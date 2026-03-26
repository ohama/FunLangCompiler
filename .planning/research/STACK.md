# Technology Stack: LangBackend v4.0 — ADT/GADT, Records, Exceptions

**Project:** LangBackend — LangThree MLIR → LLVM → native binary compiler
**Milestone:** v4.0 ADT/GADT, Records (mutable fields), Exception handling
**Researched:** 2026-03-26
**Confidence:** HIGH for ADT tagged-union layout and MatchCompiler extension; HIGH for record struct layout and mutable field mutation via GEP+store; MEDIUM for setjmp/longjmp exception strategy (implementation straightforward; interaction with Boehm GC requires one explicit safeguard)

---

## Scope of This Document

This document covers ONLY stack additions and changes required for v4.0. The existing v3.0 stack
(F# / .NET 10, LLVM 20 / MLIR 20, func/arith/cf/llvm dialects, Boehm GC 8.2.12, lang_runtime.c,
45 passing E2E tests) remains unchanged. Each section states precisely what changes and why.

---

## What v4.0 Adds to the Stack

### Summary Table

| Category | What Changes | Why |
|----------|-------------|-----|
| Runtime library (`lang_runtime.c`) | Add `lang_try_push`, `lang_try_pop`, `lang_throw`, `lang_current_exception`; add `LangExnFrame` struct and global frame stack | setjmp/longjmp exception runtime — cannot be emitted from MLIR alone |
| MlirIR.fs — `MlirType` | No new variants needed | ADT tags are `i64`; record/ADT payloads are `!llvm.ptr` (existing types cover everything) |
| MlirIR.fs — `MlirOp` | Add `LlvmTruncIOp`; no other new ops needed | Truncate i64 tag to i1 for booleans if needed; everything else reuses existing ops |
| MatchCompiler.fs — `CtorTag` | Add `AdtCtor of name: string * arity: int` variant | The decision-tree algorithm needs to tell ADT constructors from cons cells and tuples |
| Elaboration.fs | Add match arms for `Constructor`, `ConstructorPat`, `RecordExpr`, `FieldAccess`, `SetField`, `RecordUpdate`, `RecordPat`, `Raise`, `TryWith`; add `TypeEnv`/`RecordEnv` to `ElabEnv` | New AST nodes need codegen |
| Elaboration.fs — `ElabEnv` | Add `TypeEnv: Map<string, AdtInfo>` and `RecordEnv: Map<string, RecordInfo>` | Constructor-to-tag index lookup; record field-to-index lookup at codegen time |
| Pipeline.fs | No changes | lang_runtime.c already compiled and linked; new C functions go into the same file |
| Test infrastructure | No new framework; extend E2E tests | 45 existing tests continue to pass; new `.lt` files added |

---

## 1. ADT / GADT: Tagged Union Representation

### 1.1 Runtime Layout

Every ADT value is a GC_malloc'd heap block with this layout:

```
offset  0 : i64   — constructor tag (0, 1, 2, ... in declaration order)
offset  8 : ptr   — payload pointer (null if constructor has no argument)
total size: 16 bytes (two i64-sized words, both 8 bytes on 64-bit)
```

This is a two-field struct: `!llvm.struct<(i64, !llvm.ptr)>`.

Why this layout:
- Uniform size (always 16 bytes regardless of constructor). Every ADT value at the use site
  is an opaque `!llvm.ptr` — no type parameterisation at the MLIR level.
- The tag field is `i64` (not `i8` or `i32`). Reasons: (a) all existing integer operations in the
  codegen use `i64`; (b) alignment — an i64 at offset 0 avoids padding before the payload pointer;
  (c) no savings from smaller types because the next field (ptr) is 8-byte aligned anyway.
- The payload pointer is null for constructors with no argument (e.g., `None`, `True`-style nullary
  ctors). Pattern matching checks the tag first; a null payload is never dereferenced.
- GADT constructors with multiple fields: wrap multiple arguments into a GC_malloc'd tuple, store
  the tuple pointer as the single payload field. This keeps the ADT struct layout constant at 16
  bytes regardless of constructor arity.

### 1.2 Constructor Expression Codegen

`Constructor("Some", Some argExpr, span)` compiles to:

```mlir
%arg = ...                          ; elaborate argExpr
%sz = arith.constant 16 : i64
%adt = llvm.call @GC_malloc(%sz) : (i64) -> !llvm.ptr
%tagSlot = llvm.getelementptr inbounds %adt[0, 0] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, !llvm.ptr)>
%tagVal = arith.constant 1 : i64   ; tag for "Some" = 1 (0-based ctor index)
llvm.store %tagVal, %tagSlot : i64, !llvm.ptr
%paySlot = llvm.getelementptr inbounds %adt[0, 1] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, !llvm.ptr)>
llvm.store %arg, %paySlot : !llvm.ptr, !llvm.ptr
```

For nullary constructors (`None`, `Red`, etc.) the payload store is omitted; the payload slot
remains uninitialized (Boehm GC conservative scan may see it but `null` is safer — initialize to
`llvm.mlir.zero` to make null-checks safe and to suppress false pointer retentions):

```mlir
%null = llvm.mlir.zero : !llvm.ptr
llvm.store %null, %paySlot : !llvm.ptr, !llvm.ptr
```

### 1.3 ConstructorPat Pattern Matching (MatchCompiler.fs extension)

The existing `CtorTag` DU in `MatchCompiler.fs` currently covers `IntLit`, `BoolLit`, `StringLit`,
`ConsCtor`, `NilCtor`, `TupleCtor`. Add one case:

```fsharp
type CtorTag =
    | IntLit of int
    | BoolLit of bool
    | StringLit of string
    | ConsCtor
    | NilCtor
    | TupleCtor of int
    | AdtCtor of name: string * tag: int   // NEW: named ADT constructor with its integer tag
```

`ctorArity` returns 1 for `AdtCtor` with a payload, 0 for nullary. The decision tree algorithm
(Jacobs) works unchanged — the new `AdtCtor` integrates identically to `TupleCtor`.

In `desugarPattern`, the `ConstructorPat` branch (currently `failwith`) becomes:

```fsharp
| ConstructorPat(name, argPatOpt, _) ->
    let tag = lookupCtorTag env name   // from ElabEnv.TypeEnv
    let subPats = match argPatOpt with Some p -> [p] | None -> []
    ([{ Scrutinee = acc; Pattern = CtorTest(AdtCtor(name, tag), subPats) }], [])
```

In `Elaboration.fs`, when the decision tree emits a `Switch` node for `AdtCtor(_, tag)`:

1. Load the tag field: `LlvmGEPStructOp` at field index 0 + `LlvmLoadOp` → `i64` value.
2. Compare: `ArithCmpIOp("eq", loadedTag, ArithConstantOp(tag))` → `i1`.
3. Branch: `CfCondBrOp` on the `i1` to match block / no-match block.
4. In the match block, load the payload pointer: `LlvmGEPStructOp` at field index 1 + `LlvmLoadOp`.
   The payload pointer becomes the accessor for the sub-pattern.

This reuses all existing ops. No new `MlirOp` cases are needed for ADT pattern matching.

### 1.4 ElabEnv Addition: TypeEnv

```fsharp
type CtorInfo = {
    Tag:     int       // 0-based index within the type declaration
    HasArg:  bool      // true if constructor takes an argument
}

type AdtInfo = {
    TypeName:     string
    Constructors: Map<string, CtorInfo>   // ctor name → tag + arity
}

type ElabEnv = {
    // ... existing fields ...
    TypeEnv:   Map<string, AdtInfo>    // NEW: ctor name → AdtInfo (keyed by ctor name for fast lookup)
    RecordEnv: Map<string, RecordInfo> // NEW: record type name → field-name-to-index map
}
```

`TypeEnv` is populated when the elaborator processes a `TypeDecl` (top-level declaration). The
mapping is keyed by **constructor name** (not type name) so `lookupCtorTag "Some"` works directly.

### 1.5 GADT Constructors

`GadtConstructorDecl` has multiple `argTypes`. Codegen strategy: wrap all arguments into a GC_malloc'd
flat struct (same as tuple), store the tuple pointer as the ADT payload. From the MLIR side this
is identical to a regular ADT with a tuple argument. The arity is the number of argTypes.

No new MlirOp variants are needed; GADT constructors compile as `AdtCtor` with a tuple payload.

---

## 2. Records: Named Field Structs with Mutable Fields

### 2.1 Runtime Layout

A record is a GC_malloc'd heap block with one field per record field declaration, in declaration order:

```
offset 0     : first field value (i64 or ptr, 8 bytes)
offset 8     : second field value
...
offset n*8   : nth field value
total size   : numFields * 8 bytes
```

This is the same layout as a tuple. Every record value at the use site is an opaque `!llvm.ptr`.

Why the same layout as tuples:
- The existing `LlvmGEPLinearOp` (GEP with linear integer index) is the correct op for a flat
  array-of-words layout. Record fields are accessed by name, but the elaborator resolves names to
  integer indices at compile time using `RecordEnv`.
- No discriminant tag is needed — records are not sum types.
- Mutable fields are no different from immutable fields in the layout. Mutability only affects
  whether a `SetField` expression is valid (enforced by the type checker). At the MLIR level,
  a `SetField` simply emits a `LlvmStoreOp` through the field's pointer.

### 2.2 RecordExpr Codegen

`RecordExpr(typeName, [("x", e1); ("y", e2)], span)` compiles to:

```mlir
; elaborate e1, e2
%sz = arith.constant 16 : i64         ; 2 fields * 8 bytes
%rec = llvm.call @GC_malloc(%sz) : (i64) -> !llvm.ptr
%slot0 = llvm.getelementptr %rec[0] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %v1, %slot0 : i64, !llvm.ptr    ; or !llvm.ptr for pointer fields
%slot1 = llvm.getelementptr %rec[1] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %v2, %slot1 : i64, !llvm.ptr
; result = %rec (the !llvm.ptr to the record block)
```

The field-to-index mapping comes from `RecordEnv`.

### 2.3 FieldAccess Codegen

`FieldAccess(recExpr, "x", span)` compiles to:

```mlir
; elaborate recExpr → %rec : !llvm.ptr
%fieldIdx = 0    ; resolved at compile time from RecordEnv
%slot = llvm.getelementptr %rec[0] : (!llvm.ptr) -> !llvm.ptr, i64
%val = llvm.load %slot : !llvm.ptr -> i64    ; or !llvm.ptr for pointer fields
```

The load type (I64 vs Ptr) is determined from the field's declared type in `RecordEnv`.

### 2.4 SetField Codegen (Mutable Field Mutation)

`SetField(recExpr, "x", newValExpr, span)` compiles to:

```mlir
; elaborate recExpr → %rec : !llvm.ptr
; elaborate newValExpr → %newVal : i64 (or !llvm.ptr)
%slot = llvm.getelementptr %rec[0] : (!llvm.ptr) -> !llvm.ptr, i64
llvm.store %newVal, %slot : i64, !llvm.ptr
; result = () → represent as arith.constant 0 : i64 (unit value)
```

`SetField` is a void-valued expression. It returns the unit value (i64 = 0), consistent with how
void operations are handled elsewhere in the existing codegen.

### 2.5 RecordUpdate Codegen

`RecordUpdate(sourceExpr, [("x", e1)], span)` — structural copy with field override:

1. Elaborate `sourceExpr` → `%src`.
2. `GC_malloc(numFields * 8)` → `%copy`.
3. For each field in declaration order: `GEP + load` from `%src`, then `GEP + store` to `%copy`.
4. For each overridden field: elaborate the new expression and store it into `%copy` (overwrites
   the copy from step 3). OR alternatively: copy all non-overridden fields, store new values for
   overridden fields directly without loading the old values first.

Strategy 2 is more efficient (avoids loading the old value for overridden fields). Use strategy 2.

No new ops needed; this is `LlvmGEPLinearOp` + `LlvmLoadOp` + `LlvmStoreOp` sequences.

### 2.6 RecordPat Pattern Matching

`RecordPat([("x", xPat); ("y", yPat)], span)` — always structurally matches (no discriminant test).

In `desugarPattern`:

```fsharp
| RecordPat(fields, _) ->
    // RecordPat is an always-matching irrefutable pattern like TuplePat
    // Resolve field names to indices, desugar sub-patterns
    let subResults =
        fields |> List.map (fun (fieldName, subPat) ->
            let idx = lookupFieldIndex env fieldName
            desugarPattern (Field(acc, idx)) subPat
        )
    (subResults |> List.collect fst, subResults |> List.collect snd)
```

Emits no condition (unconditional match). Field access in the codegen side uses `LlvmGEPLinearOp`.

### 2.7 ElabEnv Addition: RecordEnv

```fsharp
type FieldInfo = {
    Index:     int       // 0-based position in the record struct
    IsMutable: bool      // from RecordFieldDecl.isMutable
    FieldType: MlirType  // I64 for int/bool/char; Ptr for string/list/tuple/ADT/record
}

type RecordInfo = {
    TypeName: string
    Fields:   Map<string, FieldInfo>   // field name → index + mutability + type
    NumFields: int
}
```

`RecordEnv` is keyed by record type name. Populated when the elaborator processes a `RecordTypeDecl`.

The type-name-to-record lookup happens via the `typeName: string option` field of `RecordExpr`. When
`typeName = None` (type inferred by type checker), the elaborator must use the field name set to
disambiguate. Since the LangThree type checker has already run and the AST is typed, the type name
can be recovered from the type annotation carried on the expression. Simpler approach for v4.0:
require `typeName = Some _` at MLIR codegen time (the type checker should have resolved this).

---

## 3. Exception Handling: setjmp/longjmp via C Runtime

### 3.1 Strategy: C Runtime Wrapper (Recommended)

The recommended strategy is a C runtime wrapper (`lang_runtime.c` extension) that encapsulates
all `setjmp`/`longjmp` calls. The MLIR/LLVM layer never calls `setjmp` or `longjmp` directly.

**Why not `llvm.intr.eh.sjlj.setjmp` / `llvm.eh.sjlj.longjmp`:**
These are LLVM's structured-exception-handling intrinsics, designed for use with the DWARF
EH personality function and landing pads. Using them requires `llvm.invoke` instead of `llvm.call`,
landing pad blocks (`llvm.landingpad`), and an EH personality registration. This is the full C++
exception mechanism — a significant undertaking. For a language where exceptions are recoverable
but do not need destructors, setjmp/longjmp through C wrappers is simpler and correct.

**Why not calling `setjmp` directly from MLIR:**
`setjmp` is special in the C standard and in LLVM: its return address must be in the same stack
frame as the `if (setjmp(...))` check. If the call were wrapped in an MLIR function that calls
a C wrapper that calls `setjmp`, and the wrapper returns, the jmp_buf is stale. `setjmp` must be
called directly in the frame that contains the handler code. The C wrapper approach exploits the
fact that `lang_try_push` calls `setjmp` in the same frame as the TryWith body.

Specifically, `lang_try_push` is implemented as a macro/inline that expands in the caller's frame,
OR — more practically — as a non-inline C function that uses `setjmp` in its own frame and returns
a `longjmp` indicator. The codegen calls `lang_try_push` and checks its return value to branch
into the try body or exception handler body:

```c
// lang_runtime.c additions

typedef struct LangExnFrame {
    jmp_buf             env;
    struct LangExnFrame *parent;
    void*               exn_value;   // GC_malloc'd exception payload (or NULL)
    char*               exn_name;    // constructor name of the raised exception (or NULL)
} LangExnFrame;

// Global (or thread-local) exception frame stack
static LangExnFrame* lang_exn_top = NULL;

// Push a new frame, call setjmp, return 0 on first entry, non-zero after longjmp.
// The caller MUST treat this as an expanded-inline setjmp idiom:
// the frame struct is allocated in the caller's stack frame, not here.
// Implementation: caller allocates LangExnFrame on the heap (GC_malloc),
// fills env via lang_setjmp_helper, pushes frame, checks return.
int lang_try_enter(LangExnFrame* frame) {
    frame->parent    = lang_exn_top;
    frame->exn_value = NULL;
    frame->exn_name  = NULL;
    lang_exn_top     = frame;
    return setjmp(frame->env);   // returns 0 normally; non-zero after longjmp
}

void lang_try_exit(void) {
    if (lang_exn_top != NULL)
        lang_exn_top = lang_exn_top->parent;
}

// Throw: set exception data and longjmp to nearest handler.
// exn_name: the name of the exception constructor (e.g., "MyError")
// exn_value: the payload (may be NULL for nullary exceptions)
_Noreturn void lang_throw(const char* exn_name, void* exn_value) {
    if (lang_exn_top == NULL) {
        fprintf(stderr, "Fatal: unhandled exception: %s\n", exn_name);
        exit(1);
    }
    lang_exn_top->exn_name  = (char*)exn_name;
    lang_exn_top->exn_value = exn_value;
    LangExnFrame* frame = lang_exn_top;
    lang_exn_top = frame->parent;
    longjmp(frame->env, 1);   // always jumps with value 1
}

// Query current exception (called inside handler blocks)
const char* lang_exn_name(void)  { return lang_exn_top == NULL ? NULL : lang_exn_top->exn_name; }
void*       lang_exn_value(void) { return lang_exn_top == NULL ? NULL : lang_exn_top->exn_value; }
```

The critical architectural point: `LangExnFrame` is **GC_malloc'd** by the MLIR-generated code
before calling `lang_try_enter`. This ensures Boehm GC can scan the `exn_value` pointer inside the
frame even during a collection triggered mid-exception. If `LangExnFrame` were stack-allocated and
`longjmp` unwinds past that frame, the frame would be gone before `lang_exn_top` is updated.
GC_malloc-allocating the frame eliminates this hazard.

### 3.2 TryWith Codegen

`TryWith(bodyExpr, handlers, span)` compiles to:

```mlir
; 1. Allocate an LangExnFrame on the GC heap
%frameSz = arith.constant 40 : i64     ; sizeof(LangExnFrame) = 4 ptrs + jmp_buf (~36-40 bytes on x86-64)
%frame = llvm.call @GC_malloc(%frameSz) : (i64) -> !llvm.ptr

; 2. Call lang_try_enter — returns 0 on first entry, 1 after longjmp
%rc = llvm.call @lang_try_enter(%frame) : (!llvm.ptr) -> i32
%zero32 = arith.constant 0 : i32
%isExn = arith.cmpi "ne", %rc, %zero32 : i32
cf.cond_br %isExn, ^handler_dispatch, ^try_body

^try_body:
  ; elaborate bodyExpr here
  %bodyVal = ...
  llvm.call @lang_try_exit() : () -> ()   ; pop frame on normal exit
  cf.br ^try_join(%bodyVal : <type>)

^handler_dispatch:
  ; check exception name against each handler pattern
  %ename = llvm.call @lang_exn_name() : () -> !llvm.ptr
  ; for each | ExnPat(...) -> handlerExpr: string-compare %ename against pattern name
  ...
  cf.br ^try_join(%handlerResult : <type>)

^try_join(%result : <type>):
  ; continue with %result
```

The `setjmp` ABI constraint is satisfied: `lang_try_enter` calls `setjmp` and returns in the same
C call frame where `longjmp` will restore execution (jumping to the `rc = llvm.call @lang_try_enter`
instruction and returning with value 1). This is the canonical way to wrap `setjmp` in a function.

### 3.3 Raise Codegen

`Raise(Constructor("MyError", Some payloadExpr, span), span)` compiles to:

```mlir
; elaborate Constructor("MyError", Some payloadExpr) → %exnVal : !llvm.ptr
; (this is just a normal ADT constructor allocation — tag + payload)
%namePtr = llvm.mlir.addressof @__str_myerror : !llvm.ptr   ; global string constant "MyError"
llvm.call @lang_throw(%namePtr, %exnVal) : (!llvm.ptr, !llvm.ptr) -> ()
llvm.unreachable   ; lang_throw is _Noreturn
```

`lang_throw` receives two arguments: the exception name string (for handler dispatch by name) and
the ADT-valued exception payload pointer (which carries the constructor tag + value, same layout as
any other ADT value). The handler reads the value back via `lang_exn_value()`.

For nullary exceptions (`Raise(Constructor("Exn", None, span), span)`):
- The exception payload is a null pointer (`llvm.mlir.zero`).
- `lang_throw` is called with null as the value argument.

### 3.4 Exception Handler Pattern Matching

The `handlers` clauses in `TryWith` are `MatchClause list`. Each clause has a pattern.
Exception patterns in LangThree are `ConstructorPat` patterns (the exception name is the constructor
name from `ExceptionDecl`). The handler dispatch block:

1. Calls `lang_exn_name()` → `!llvm.ptr` (C string pointer to exception name).
2. For each handler clause: emit a `strcmp` call against the exception's constructor name.
3. If `strcmp` returns 0: enter handler body; bind the payload value if the pattern has an argument
   (call `lang_exn_value()`, cast/use as the appropriate type).
4. If no handler matches: re-throw by calling `lang_throw` again with the same name and value
   (or calling `lang_match_failure` — unhandled exceptions should abort with a diagnostic).

This reuses the existing `strcmp` + `ArithCmpIOp` pattern already used for string literal pattern
matching in `ConstPat(StringConst ...)`.

### 3.5 ExceptionDecl Codegen

`ExceptionDecl("MyError", Some TEString, span)` declares an exception constructor.

Compilation: emit a global string constant for the exception name (e.g., `@__exn_MyError`), and
register the constructor in `TypeEnv` as if it were a single-ctor ADT with `tag = 0` and
`hasArg = (dataType <> None)`. At construction time (`Constructor("MyError", Some ..., span)`),
the tag field is always 0 (exceptions have exactly one constructor); the payload is the value.

No dedicated IR structure for exceptions beyond what ADTs already provide. Exception name lookup
at handler dispatch uses the string constant, not the tag.

### 3.6 Boehm GC + setjmp/longjmp Interaction

**The key constraint:** Boehm GC internally uses `setjmp` to flush CPU registers to the stack before
scanning the stack for roots. This is independent of the application's use of `setjmp`/`longjmp`.

**Concern:** if a `longjmp` from `lang_throw` skips stack frames that hold pointers to
GC-managed objects, those pointers are lost before the next GC cycle can scan the dead frames.
Boehm GC is a conservative collector: it scans the **live** stack. After `longjmp`, the unwound
frames are gone, so any pointers they held are no longer on the stack. If those were the only roots
for some live objects, those objects could be collected.

**Mitigation:** GC_malloc all exception-related values **before** the `longjmp`. In this design:
- The `LangExnFrame` itself is GC_malloc'd — so the GC can find `exn_value` through `lang_exn_top`.
- The exception payload (`%exnVal`) is the result of a normal ADT constructor allocation (GC_malloc'd)
  and stored in `frame->exn_value` by `lang_throw` before calling `longjmp`. The global
  `lang_exn_top` pointer chain keeps the payload alive across the `longjmp`.
- The handler code calls `lang_exn_value()` to recover the payload immediately and stores it in
  a local SSA value — Boehm GC scans the new live stack and finds it there.

This is the standard technique used by Chicken Scheme and similar systems. The critical invariant:
**the exception payload pointer must be reachable through the global `lang_exn_top` chain between
`lang_throw` and the handler's call to `lang_exn_value()`.**

**Do NOT** stack-allocate `LangExnFrame`. If the frame is on the stack and `longjmp` unwinds past
that frame, the frame pointer becomes dangling before `lang_exn_top` is updated, and the GC can no
longer find `exn_value`. GC_malloc allocation of the frame prevents this.

---

## 4. New MlirOp Cases

Exactly one new `MlirOp` case is needed for v4.0 (all other codegen reuses existing ops):

```fsharp
// MlirIR.fs — v4.0 addition
type MlirOp =
    // ... all existing cases unchanged ...

    // v4.0: Truncate a wider integer to a narrower one (e.g. i32 → i1 for try-enter return value)
    | LlvmTruncIOp of result: MlirValue * value: MlirValue
    // emits: %result = llvm.trunci %value : <srcType> to <dstType>
    // Used to convert i32 (lang_try_enter return) to i1 for cf.cond_br
```

Why `LlvmTruncIOp` and not `ArithTruncIOp`:
`arith.trunci` is valid for integer scalar types in the arith dialect, but `lang_try_enter` returns
`i32` (C `int`), which is an llvm dialect type at the IR level. To convert `i32` to `i1` for
`cf.cond_br`, `llvm.trunci` is more consistent with the rest of the llvm-dialect ops used in v4.0.
In practice, `arith.trunci` also works — either is acceptable. Use `ArithCmpIOp("ne", rc, zero32)`
with an `i32` zero constant instead, which avoids the need for `LlvmTruncIOp` entirely.

**Revised conclusion: zero new MlirOp cases needed.** Compare `i32 rc` against `i32 0` using
the existing `ArithCmpIOp` (arith.cmpi works on i32 as well as i64). Add `I32` as an existing
`MlirType` variant — it is already present in the DU. No new ops.

---

## 5. New ExternalFuncDecl Entries

Add to the static `externalFuncs` list in `Elaboration.fs`:

```fsharp
{ ExtName = "@lang_try_enter"; ExtParams = [Ptr]; ExtReturn = Some I32; IsVarArg = false }
{ ExtName = "@lang_try_exit";  ExtParams = [];    ExtReturn = None;     IsVarArg = false }
{ ExtName = "@lang_throw";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false }
{ ExtName = "@lang_exn_name";  ExtParams = [];    ExtReturn = Some Ptr; IsVarArg = false }
{ ExtName = "@lang_exn_value"; ExtParams = [];    ExtReturn = Some Ptr; IsVarArg = false }
```

The `lang_throw` entry has `ExtReturn = None` (void) and the codegen follows it with
`LlvmUnreachableOp` (already exists in `MlirOp`) to satisfy MLIR block terminator requirements.

---

## 6. lang_runtime.c Changes

Add the following new functions to `lang_runtime.c`. No existing functions change.

```c
// New types and globals (add at the top of lang_runtime.c after existing typedefs)
#include <setjmp.h>

typedef struct LangExnFrame {
    jmp_buf             env;           // platform-specific save area
    struct LangExnFrame *parent;       // previous frame in the exception stack
    void*               exn_value;    // GC_malloc'd exception value pointer
    char*               exn_name;     // exception constructor name (points to static string)
} LangExnFrame;

static LangExnFrame* lang_exn_top = NULL;

int lang_try_enter(LangExnFrame* frame) {
    frame->parent    = lang_exn_top;
    frame->exn_value = NULL;
    frame->exn_name  = NULL;
    lang_exn_top     = frame;
    return setjmp(frame->env);
}

void lang_try_exit(void) {
    if (lang_exn_top != NULL)
        lang_exn_top = lang_exn_top->parent;
}

_Noreturn void lang_throw(const char* exn_name, void* exn_value) {
    if (lang_exn_top == NULL) {
        fprintf(stderr, "Fatal: unhandled exception: %s\n",
                exn_name != NULL ? exn_name : "<unknown>");
        exit(1);
    }
    lang_exn_top->exn_name  = (char*)exn_name;
    lang_exn_top->exn_value = exn_value;
    LangExnFrame* frame = lang_exn_top;
    lang_exn_top = frame->parent;
    longjmp(frame->env, 1);
}

const char* lang_exn_name(void)  {
    return lang_exn_top != NULL ? lang_exn_top->exn_name : NULL;
}

void* lang_exn_value(void) {
    return lang_exn_top != NULL ? lang_exn_top->exn_value : NULL;
}
```

**sizeof(LangExnFrame) note:** `jmp_buf` size varies by platform. On x86-64 Linux, it is 200 bytes.
On macOS arm64, it is 392 bytes. To allocate the frame in the MLIR code with the correct size,
emit a call to a helper that returns the actual size, OR — simpler — emit a large fixed constant
(512 bytes) and let GC_malloc overallocate harmlessly. Alternatively, expose a `lang_exn_frame_size`
C function or a compile-time constant. **Recommended for v4.0:** add a
`lang_exn_frame_size()` function to `lang_runtime.c` that returns `(int64_t)sizeof(LangExnFrame)`
and call it once at the start of each `TryWith` elaboration to get the actual frame size.

```c
int64_t lang_exn_frame_size(void) { return (int64_t)sizeof(LangExnFrame); }
```

Add to `externalFuncs` in `Elaboration.fs`:
```fsharp
{ ExtName = "@lang_exn_frame_size"; ExtParams = []; ExtReturn = Some I64; IsVarArg = false }
```

---

## 7. Elaboration.fs Changes Summary

### 7.1 ElabEnv additions

```fsharp
type ElabEnv = {
    Vars:           Map<string, MlirValue>
    Counter:        int ref
    LabelCounter:   int ref
    Blocks:         MlirBlock list ref
    KnownFuncs:     Map<string, FuncSignature>
    Funcs:          FuncOp list ref
    ClosureCounter: int ref
    Globals:        (string * string) list ref
    GlobalCounter:  int ref
    // v4.0 additions:
    TypeEnv:        Map<string, AdtInfo>    // keyed by constructor name
    RecordEnv:      Map<string, RecordInfo> // keyed by record type name
}
```

`emptyEnv` initialises both new fields to `Map.empty`.

### 7.2 New elaborateExpr match arms (outline)

```fsharp
| Constructor(name, argOpt, _)  → allocate 16-byte ADT block, store tag + payload
| RecordExpr(_, fields, _)      → GC_malloc(numFields*8), store each field
| FieldAccess(expr, name, _)    → elaborate expr, GEP field index, load
| SetField(expr, name, val, _)  → elaborate expr + val, GEP field index, store; return unit (0L)
| RecordUpdate(src, fields, _)  → GC_malloc copy, copy all fields, overwrite changed fields
| Raise(Constructor(...))       → elaborate Constructor, call lang_throw, llvm.unreachable
| TryWith(body, handlers, _)    → allocate LangExnFrame, lang_try_enter, cond_br, elaborate body/handlers
```

### 7.3 MatchCompiler.fs: ConstructorPat and RecordPat

Both currently `failwith`. Replace with real implementations as described in sections 1.3 and 2.6.
`desugarPattern` needs `TypeEnv` / `RecordEnv` from `ElabEnv` passed as a parameter, OR the
`lookupCtorTag` / `lookupFieldIndex` lookups happen in the Elaboration layer that calls
`MatchCompiler.compile`, not inside the MatchCompiler itself (preferred — keeps MatchCompiler
pure / free of environment references).

Preferred approach: MatchCompiler receives the already-desugared test lists from Elaboration, which
pre-resolves constructor names to integer tags before calling `compile`. `ConstructorPat` desugaring
happens in the `testPattern` function in `Elaboration.fs` (alongside `TuplePat`), not in
`MatchCompiler.desugarPattern`. This requires no change to `MatchCompiler.desugarPattern` at all —
the pattern is already expanded when it reaches the tree algorithm.

---

## 8. No New MLIR Passes

The lowering pipeline does not change:

```
--convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts
```

All new ops (GEP, load, store, call, cond_br) are already in the llvm or cf dialect and pass
through the pipeline unchanged. `lang_try_enter` calls `setjmp` internally; no LLVM intrinsic
for setjmp is emitted by the MLIR code generator. This is the entire design advantage of the
C wrapper strategy.

---

## 9. No New F# / .NET Dependencies

All v4.0 features are implemented in the four compiler source files and `lang_runtime.c`. No new
NuGet packages are needed.

---

## 10. Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| ADT tag width | `i64` | `i8` / `i32` | `i64` matches all other integer values in the codegen; no padding before the payload ptr; alignment is consistent; not worth saving 4 bytes per constructor |
| ADT payload for nullary ctors | Null ptr (llvm.mlir.zero) stored at field 1 | Leave field 1 uninitialised | Conservative GC may scan uninitialised bytes and interpret garbage bits as pointers, keeping dead objects alive. Explicit null prevents this |
| Multi-arg GADT ctors | Tuple payload (wrap in GC_malloc'd tuple) | Variadic struct with N payload fields | Variadic layout requires per-type struct allocation sizes; uniform 16-byte ADT block is simpler and already proven (same as cons cell) |
| Exception runtime | C wrapper `lang_try_enter` / `lang_throw` | LLVM SJLJ intrinsics (`llvm.eh.sjlj.setjmp`) | LLVM SJLJ intrinsics require `llvm.invoke` (not `llvm.call`), landing pads, and an EH personality function — essentially the full C++ EH ABI. C wrapper avoids all of this. |
| Exception runtime | C wrapper `lang_try_enter` / `lang_throw` | LLVM invoke + landingpad | Requires `--convert-func-to-llvm` to understand `llvm.invoke` semantics; adds `landingpad` block to every TryWith; handler dispatch requires `llvm.extractvalue` on the landingpad result. Significantly more IR complexity for no benefit over C wrapper. |
| LangExnFrame allocation | GC_malloc | Stack alloca | Stack alloca: if `longjmp` unwinds the frame before `lang_exn_top` is updated, the pointer to the frame in `lang_exn_top` becomes dangling, causing undefined behaviour or GC corruption. GC_malloc is unconditionally safe. |
| Exception frame size | `lang_exn_frame_size()` C function | Hardcoded constant | `jmp_buf` size varies by platform (200 bytes on x86-64 Linux, 392 on macOS arm64). Hardcoding 512 bytes works but is wasteful and fragile. `lang_exn_frame_size()` makes it portable at the cost of one extra call per TryWith. |
| Record mutable fields | GEP + LlvmStoreOp directly | ref cells (extra indirection) | The interpreter uses `Value ref` for mutable fields (F# ref cells). At the MLIR level, the record block is already on the heap; GEP + store is sufficient and avoids a double-indirection. |
| Record field type at codegen | I64 for scalars, Ptr for heap types | Always Ptr (uniform boxing) | Scalars stored as I64 avoid extra GC_malloc per field for int/bool values. The type is known from `RecordFieldDecl.fieldType` — no runtime type dispatch needed. |
| Handler dispatch | `strcmp` against constructor name string | Integer tag comparison | Exception constructor tags are not stable across compilation units (they depend on `ExceptionDecl` order). String name comparison is the only reliable discriminant when exceptions can cross module boundaries. |

---

## 11. Version Compatibility

No version changes from v3.0. All new features use:

| Component | Version | Notes |
|-----------|---------|-------|
| Boehm GC (libgc) | 8.2.12 | `lang_exn_top` global is not thread-safe; single-threaded programs only. Thread-safe version requires `pthread_key_t` for `lang_exn_top`. |
| LLVM / MLIR | 20 | `llvm.getelementptr`, `llvm.call`, `llvm.store`, `cf.cond_br` — all present in MLIR 20. No version bump needed. |
| F# / .NET | 10 | No NuGet additions. |
| clang (compile step) | whatever version links with LLVM 20 | `lang_runtime.c` uses standard C99 + POSIX setjmp.h. No platform-specific extensions. |

---

## 12. Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| ADT tagged union layout (16-byte, i64 tag + ptr payload) | HIGH | Standard technique; uniform size is the correct trade-off for a GC language; GEP field 0/1 reuses existing `LlvmGEPStructOp` pattern |
| ADT pattern matching via decision tree extension | HIGH | `CtorTag` extension is additive; Jacobs algorithm unchanged; reuses existing `ArithCmpIOp` + `CfCondBrOp` machinery |
| Record layout = tuple layout (GEP linear) | HIGH | Already proven for tuple codegen; field-name-to-index resolution is straightforward given `RecordEnv` |
| Mutable field SetField via GEP + store | HIGH | Semantically correct; GC_malloc heap block is already mutable; LlvmStoreOp already exists |
| Exception C wrapper strategy | HIGH (design); MEDIUM (sizeof(LangExnFrame)) | Design is standard; jmp_buf portability is the main concern. Mitigated by `lang_exn_frame_size()`. |
| GC interaction with longjmp | MEDIUM | Conservative GC + GC_malloc'd frame is the standard mitigation; confirmed by Chicken Scheme / bdwgc documentation. No known issues with this pattern but requires careful frame pointer management. |
| MatchCompiler.fs: RecordPat / ConstructorPat | HIGH | Both are structurally equivalent to existing TuplePat/ConsPat; field-name-to-index lookup is the only new logic |

---

## 13. Sources

- LangThree `Ast.fs` (direct inspection, 2026-03-26) — authoritative source for `Constructor`, `ConstructorPat`, `RecordExpr`, `FieldAccess`, `SetField`, `RecordUpdate`, `RecordPat`, `Raise`, `TryWith`, `ExceptionDecl` AST definitions
- LangBackend `MlirIR.fs`, `Elaboration.fs`, `Printer.fs`, `Pipeline.fs`, `lang_runtime.c` (direct inspection, 2026-03-26) — confirmed existing op set, `LlvmGEPStructOp` syntax, cons cell layout (16 bytes = two ptrs), `LlvmUnreachableOp` existence, external function declaration pattern
- LangBackend `MatchCompiler.fs` (direct inspection, 2026-03-26) — confirmed `CtorTag` DU, `desugarPattern` structure, `ConstructorPat` and `RecordPat` currently `failwith` stubs
- [MLIR llvm Dialect documentation](https://mlir.llvm.org/docs/Dialects/LLVM/) — confirmed `llvm.getelementptr` struct syntax `[0, fieldIdx]`, `llvm.invoke` + `llvm.landingpad` (rejected for v4.0), `llvm.call`, `llvm.store`, `llvm.load` ops in MLIR 20
- [Mapping High-Level Constructs to LLVM IR — Exception Handling](https://mapping-high-level-constructs-to-llvm-ir.readthedocs.io/en/latest/exception-handling/setjmp+longjmp-exception-handling.html) — setjmp/longjmp pattern for exceptions; C wrapper technique
- [Dev.to: setjmp/longjmp and Exception Handling in C](https://dev.to/pauljlucas/setjmp-longjmp-and-exception-handling-in-c-1h7h) — `LangExnFrame` design derived from the linked-list frame stack pattern
- [LLVM Exception Handling documentation](https://llvm.org/docs/ExceptionHandling.html) — confirmed SJLJ intrinsics require landing pad ABI (rejected)
- [bdwgc Conservative GC Overview](https://www.hboehm.info/gc/gcdescr.html) — confirmed GC uses setjmp internally for stack scanning; longjmp interaction is safe when live pointers remain reachable through the global frame stack
- [Zig tagged union layout discussion](https://github.com/ziglang/zig/issues/2166) — confirmed 16-byte tag+payload is a common trade-off in compiled language implementations
- [Tagged union — Wikipedia](https://en.wikipedia.org/wiki/Tagged_union) — confirmed standard industry representation for discriminated unions

---

## Appendix: Minimal Diff to MlirIR.fs

No new `MlirType` or `MlirOp` variants are required. The only change is in `Elaboration.fs` (new
match arms and `ElabEnv` fields), `MatchCompiler.fs` (new `CtorTag.AdtCtor` variant and stub
replacements), `Printer.fs` (no changes needed — all ops already print correctly), and
`lang_runtime.c` (new functions).

The additive-only change to `MatchCompiler.fs`:

```fsharp
// MatchCompiler.fs — v4.0 addition to CtorTag
type CtorTag =
    | IntLit of int
    | BoolLit of bool
    | StringLit of string
    | ConsCtor
    | NilCtor
    | TupleCtor of int
    | AdtCtor of name: string * tag: int   // NEW

// CtorArity: AdtCtor arity is passed from Elaboration (0 for nullary, 1 for payload)
// For simplicity, use AdtCtor(name, tag) only for constructors WITH a payload;
// nullary ADT ctors produce tag-only tests (arity 0).
// Or: AdtCtor always arity 1, but the payload accessor is discarded for nullary ctors.
// Recommended: carry arity in the DU:
| AdtCtor of name: string * tag: int * arity: int   // arity: 0 (nullary) or 1 (has payload)
```

---
*Stack research for: LangBackend v4.0 — ADT/GADT, Records (mutable fields), Exception handling*
*Researched: 2026-03-26*
