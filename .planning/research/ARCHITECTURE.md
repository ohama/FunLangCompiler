# Architecture Patterns: LangBackend v2.0
**Domain:** Functional language compiler backend — MLIR text generation + LLVM pipeline
**Researched:** 2026-03-26
**Scope:** ADT/GADT + Records (mutable fields) + Exception handling — integration with existing architecture
**Confidence:** HIGH (based on direct code analysis of all five backend modules + LangThree AST)

---

## Existing Architecture Snapshot

The compiler emits MLIR as text strings via F# discriminated unions, then shells out to `mlir-opt → mlir-translate → clang`. This text-generation approach is the unchanging constraint everything below must respect.

```
Source File (.lt)
       |
       v
  [ LangThree Frontend ]
    Lexer / Parser / TypeChecker → Ast.Expr / Ast.Decl
       |
       v
  [ Elaboration.fs ]
    elaborateExpr: Expr → ElabEnv → (MlirValue × MlirOp list)
    elaborateModule: Expr → MlirModule           ← needs replacing with elaborateProgram
    ElabEnv { Vars, Counter, LabelCounter, Blocks,
              KnownFuncs, Funcs, ClosureCounter,
              Globals, GlobalCounter }
       |
       v
  MlirModule (F# DU)
  MlirType | MlirOp (DU, ~17 cases) | MlirBlock | FuncOp | MlirModule
       |
       v
  [ Printer.fs ]
    printModule: MlirModule → string   (pure, no I/O)
       |
       v
  [ Pipeline.fs ]  (shell subprocesses, unchanged)
    mlir-opt → mlir-translate → clang
       |
       v
  [ lang_runtime.c ]  (compiled as object file by Pipeline.fs/clang)
    GC_malloc wrapper, string ops, list range, match_failure, failwith
```

### Current Component Inventory

| Component | Purpose | Status |
|-----------|---------|--------|
| `MlirIR.fs` | F# DU: MlirType, MlirOp, MlirBlock, FuncOp, MlirModule | Complete |
| `Elaboration.fs` | AST → MlirIR: elaborateExpr, ElabEnv | Complete for int/bool/string/tuple/list/closure/match |
| `MatchCompiler.fs` | Jacobs decision tree: CtorTag, Accessor, DecisionTree | Complete for Int/Bool/String/Nil/Cons/Tuple; ConstructorPat and RecordPat explicitly fail |
| `Printer.fs` | MlirIR → MLIR text, pure serializer | Complete |
| `Pipeline.fs` | Shell pipeline: mlir-opt / mlir-translate / clang | Complete, unchanged for this milestone |
| `lang_runtime.c` | C runtime: GC_malloc, string ops, lang_range, lang_match_failure | Complete; needs exception additions |

---

## v3 Integration Strategy: What Changes and What Stays

### What stays unchanged

- `Pipeline.fs` — lowering passes are unchanged
- `MlirModule`, `FuncOp`, `MlirBlock`, `MlirRegion` structural shape — additive only
- `Printer.fs` architecture — add new `printOp` match cases; existing cases untouched
- Uniform `Ptr` representation for all heap types — no new MlirType variants needed
- `LlvmGEPLinearOp` / `LlvmStoreOp` / `LlvmLoadOp` reused for record fields (identical to tuple fields)
- `CfCondBrOp` / `CfBrOp` reused for exception dispatch blocks

### What extends

| Component | Extension |
|-----------|-----------|
| `MlirOp` | Add `LlvmSetjmpOp` and `LlvmLongjmpOp` cases |
| `Printer.fs` | Add `printOp` arms for the two new cases |
| `ElabEnv` | Add 5 new fields: `CtorTags`, `CtorArities`, `RecordFields`, `ExnTags`, `JmpBufPtr` |
| `MatchCompiler.fs` | Add `AdtCtor` and `RecordCtor` to `CtorTag` DU; implement `desugarPattern` for `ConstructorPat` and `RecordPat` |
| `Elaboration.fs` | New `elaborateExpr` arms for `Constructor`, `RecordExpr`, `FieldAccess`, `RecordUpdate`, `SetField`, `Raise`, `TryWith`; new `elaborateProgram` entry point |
| `lang_runtime.c` | Add `lang_raise`, `lang_current_exception` and static exception value cell |

### What is new

| New Item | Purpose |
|----------|---------|
| `elaborateProgram` (in `Elaboration.fs`) | Replaces `elaborateModule` for multi-decl programs; processes `TypeDecl`/`RecordTypeDecl`/`ExceptionDecl` before expressions |
| `@lang_raise` / `@lang_current_exception` (in `lang_runtime.c`) | C runtime exception delivery using `setjmp`/`longjmp` |

---

## ADT/GADT Heap Representation

### Memory Layout

Every ADT constructor application is a 16-byte GC-managed struct:

```
Field 0 (offset 0, i64):  integer tag — assigned 0, 1, 2, ... in constructor declaration order
Field 1 (offset 8, i64 or ptr): payload — the constructor argument, or 0 if no argument
```

Zero-argument constructors still allocate 16 bytes; field 1 is stored as 0 (unused).
One-argument constructors store the argument value (i64 for scalars, ptr for heap values) at field 1.

This is structurally identical to a two-field tuple and reuses all existing GEP/load infrastructure.

### Example: `type Option = None | Some of int`

Tags assigned at TypeDecl processing time: `None → 0`, `Some → 1`.

Elaboration of `None`:
```
%sz  = arith.constant 16 : i64
%ptr = llvm.call @GC_malloc(%sz) : (i64) -> !llvm.ptr
%t   = arith.constant 0 : i64            ; tag for None
llvm.store %t, %ptr : i64, !llvm.ptr
%f1  = llvm.getelementptr %ptr[1] : (!llvm.ptr) -> !llvm.ptr, i64
%z   = arith.constant 0 : i64
llvm.store %z, %f1 : i64, !llvm.ptr
; %ptr : !llvm.ptr — the None value
```

Elaboration of `Some 42`:
```
%sz  = arith.constant 16 : i64
%ptr = llvm.call @GC_malloc(%sz) : (i64) -> !llvm.ptr
%t   = arith.constant 1 : i64            ; tag for Some
llvm.store %t, %ptr : i64, !llvm.ptr
%f1  = llvm.getelementptr %ptr[1] : (!llvm.ptr) -> !llvm.ptr, i64
%v   = arith.constant 42 : i64
llvm.store %v, %f1 : i64, !llvm.ptr
; %ptr : !llvm.ptr — the Some 42 value
```

### ConstructorPat Matching

Tag comparison: load field 0 from the scrutinee pointer, compare with `ArithCmpIOp "eq"`.
Payload extraction (for one-argument constructors): `LlvmGEPLinearOp(slotVal, scrutPtr, 1)` + `LlvmLoadOp`.

`MatchCompiler.CtorTag` gains:
```fsharp
| AdtCtor of tag: int * arity: int
  // arity: 0 = no payload, 1 = single payload at field 1
```

`emitCtorTest` for `AdtCtor(tag, _)`:
```
%f0   = llvm.getelementptr %ptr[0] : (!llvm.ptr) -> !llvm.ptr, i64
%actual_tag = llvm.load %f0 : !llvm.ptr -> i64
%expected   = arith.constant <tag> : i64
%cond       = arith.cmpi eq, %actual_tag, %expected : i64
; %cond : i1 — true if this constructor matches
```

---

## Record Representation

### Memory Layout

Records use the same linear array layout as tuples. Field order matches the `RecordDecl` declaration order.

```
GC_malloc(n * 8) bytes
Field 0 (offset 0):   first declared field (i64 or ptr)
Field 1 (offset 8):   second declared field
...
Field n-1 (offset (n-1)*8): last declared field
```

For `type Point = { mutable x: int; y: int }`: `x` at index 0, `y` at index 1.

### FieldAccess

```fsharp
// FieldAccess(expr, "x", _) where x is at index 0 in ElabEnv.RecordFields["Point"]
%slot = llvm.getelementptr %rec_ptr[0] : (!llvm.ptr) -> !llvm.ptr, i64
%val  = llvm.load %slot : !llvm.ptr -> i64
; %val : i64
```

Reuses `LlvmGEPLinearOp` + `LlvmLoadOp` — no new MlirOp cases.

### SetField (Mutable Field Assignment)

```fsharp
// SetField(expr, "x", value, _) — r.x <- 5
%slot = llvm.getelementptr %rec_ptr[0] : (!llvm.ptr) -> !llvm.ptr, i64
%v    = arith.constant 5 : i64
llvm.store %v, %slot : i64, !llvm.ptr
%unit = arith.constant 0 : i64            ; unit value
; %unit : i64
```

Reuses `LlvmStoreOp` — no new MlirOp cases.

**Mutation semantics note:** After a `SetField`, any previously-elaborated SSA value for that field is stale. The elaborator must re-emit a fresh `LlvmLoadOp` on subsequent `FieldAccess`. This is automatically correct because `elaborateExpr (FieldAccess ...)` always emits a new GEP+load — it does not cache SSA values across expression elaborations.

### RecordPat Matching

Records always match structurally (unconditional, like tuples). `MatchCompiler.CtorTag` gains:
```fsharp
| RecordCtor of fields: (string * int) list
  // unconditional match; each field name maps to a field index
```

`emitCtorTest` for `RecordCtor(fields)`: emits `arith.constant 1 : i1` (always true). Sub-patterns bind field values via `LlvmGEPLinearOp(slotVal, scrutPtr, fieldIndex)` + `LlvmLoadOp`.

---

## Exception Handling: setjmp / longjmp Pattern

### Design Decision: C setjmp rather than LLVM Exceptions

Use C `setjmp`/`longjmp` via `llvm.call` in `LlvmCallOp`/`LlvmCallVoidOp`. This avoids LLVM exception personality, invoke instructions, landing pads, and `scf.if` — none of which exist in the current MLIR emission pipeline. C setjmp integrates cleanly with the existing `LlvmCallOp` pattern.

### New MlirOp Cases

```fsharp
// In MlirIR.fs:
| LlvmSetjmpOp  of result: MlirValue * jmpBuf: MlirValue
  // result.Type = I32; jmpBuf.Type = Ptr
  // emits: %result = llvm.call @setjmp(%jmpBuf) : (!llvm.ptr) -> i32

| LlvmLongjmpOp of jmpBuf: MlirValue * value: MlirValue
  // jmpBuf.Type = Ptr; value.Type = I32
  // emits: llvm.call @longjmp(%jmpBuf, %value) : (!llvm.ptr, i32) -> ()
  // always followed by LlvmUnreachableOp (longjmp never returns)
```

### jmp_buf Allocation

The `jmp_buf` must live on the C stack — not GC heap — because setjmp writes the stack frame address into it. The elaborator emits `llvm.alloca` for the buffer.

```fsharp
// LlvmAllocaOp is already in MlirIR.fs (used for closures):
// | LlvmAllocaOp of result: MlirValue * count: MlirValue * numCaptures: int
// For jmp_buf: emit a raw jmp_buf-sized alloca.
// Simplest approach: declare jmp_buf as an opaque !llvm.ptr via a fixed-size alloca.
// jmp_buf is platform-specific; use a generous 200-byte alloca (sufficient on all platforms).
```

Emitted sequence:
```
%jbuf_size = arith.constant 200 : i64
%one       = arith.constant 1 : i64
%jbuf      = llvm.alloca %one x !llvm.array<200 x i8> : (i64) -> !llvm.ptr
; %jbuf : !llvm.ptr — points to stack-allocated jmp_buf
```

### TryWith Elaboration

For `TryWith(body, handlers, _)`:

```
; 1. Allocate jmp_buf on stack
%jbuf_sz  = arith.constant 200 : i64
%one      = arith.constant 1 : i64
%jbuf     = llvm.alloca %one x !llvm.array<200 x i8> : (i64) -> !llvm.ptr

; 2. Call setjmp — returns 0 on initial entry, 1 after longjmp
%sjret    = llvm.call @setjmp(%jbuf) : (!llvm.ptr) -> i32
%zero32   = arith.constant 0 : i32
%no_exn   = arith.cmpi eq, %sjret, %zero32 : i32    ; i1
cf.cond_br %no_exn, ^try_body, ^catch_dispatch

^try_body:
  ; elaborate body with JmpBufPtr = Some %jbuf in ElabEnv
  ... body ops ...
  cf.br ^try_merge(%body_val : <T>)

^catch_dispatch:
  ; load exception value
  %exn_ptr = llvm.call @lang_current_exception() : () -> !llvm.ptr
  ; dispatch on exception tag — same Jacobs decision tree as regular Match
  ... decision tree for handlers ...
  cf.br ^try_merge(%handler_val : <T>)

^try_merge(%result : <T>):
  ; %result is the TryWith expression value
```

The handler dispatch is a nested `Match` on `%exn_ptr` using `ConstructorPat` patterns for exception names — reusing the full ADT pattern matching pipeline.

### Raise Elaboration

For `Raise(expr, _)`:

```
%exn_val = ... elaborate expr ...   ; type: !llvm.ptr (exception is an ADT value)
%jbuf    = ElabEnv.JmpBufPtr.Value  ; the current in-scope jmp_buf ptr
llvm.call @lang_raise(%exn_val, %jbuf) : (!llvm.ptr, !llvm.ptr) -> ()
llvm.unreachable                    ; lang_raise never returns (it calls longjmp)
%unit    = arith.constant 0 : i64   ; dummy SSA result to satisfy type system
; %unit : i64 (never reached)
```

When `ElabEnv.JmpBufPtr = None` (no enclosing `TryWith`), `Raise` becomes an uncaught exception — call `@lang_failwith` with the exception message and treat it the same as an unhandled error.

### Nested TryWith

Each `TryWith` saves the outer `ElabEnv.JmpBufPtr` and restores it after. The inner elaboration uses the new `%jbuf`. On `Raise`, the most recently pushed `%jbuf` is used, which is the innermost handler — correct LIFO semantics.

### C Runtime Support

```c
// In lang_runtime.c:
#include <setjmp.h>

// Global exception value storage (single-threaded)
static void* __lang_exc_value = NULL;

// Called by Raise: store exception value, then longjmp to handler
// jmpbuf: pointer to the jmp_buf allocated on the caller's stack frame
void lang_raise(void* exn, void* jmpbuf) {
    __lang_exc_value = exn;
    longjmp(*(jmp_buf*)jmpbuf, 1);
    // never returns
}

// Called at start of catch_dispatch to retrieve the thrown exception
void* lang_current_exception(void) {
    return __lang_exc_value;
}
```

---

## ElabEnv Changes

```fsharp
type ElabEnv = {
    // --- existing fields (unchanged) ---
    Vars:           Map<string, MlirValue>
    Counter:        int ref
    LabelCounter:   int ref
    Blocks:         MlirBlock list ref
    KnownFuncs:     Map<string, FuncSignature>
    Funcs:          FuncOp list ref
    ClosureCounter: int ref
    Globals:        (string * string) list ref
    GlobalCounter:  int ref
    // --- new fields ---
    CtorTags:       Map<string, int>         // constructor name → integer tag (per TypeDecl + ExceptionDecl)
    CtorArities:    Map<string, int>         // constructor name → arity: 0 or 1
    RecordFields:   Map<string, (string * int) list>  // record type → [(fieldName, fieldIndex)]
    ExnTags:        Map<string, int>         // exception name → integer tag (same scheme as ADT)
    JmpBufPtr:      MlirValue option         // Some ptr = current setjmp buffer in scope; None = no handler
}
```

`emptyEnv` initialises new fields as `Map.empty` / `None`.

---

## MatchCompiler.fs Changes

### New CtorTag Variants

```fsharp
type CtorTag =
    | IntLit of int           // existing
    | BoolLit of bool         // existing
    | StringLit of string     // existing
    | ConsCtor                // existing
    | NilCtor                 // existing
    | TupleCtor of int        // existing
    | AdtCtor of tag: int * arity: int    // NEW: ADT constructor with integer tag
    | RecordCtor of fields: (string * int) list  // NEW: record (unconditional, field indices)
```

`ctorArity` for `AdtCtor(_, 0)` = 0; `AdtCtor(_, 1)` = 1; `RecordCtor(fields)` = `List.length fields`.

### desugarPattern for ConstructorPat

```fsharp
| ConstructorPat(name, argPat, _) ->
    // Look up tag and arity from ElabEnv (must be threaded to desugarPattern)
    let tag   = Map.find name ctorTags
    let arity = Map.find name ctorArities
    let subPats = match argPat with Some p -> [p] | None -> []
    ([{ Scrutinee = acc; Pattern = CtorTest(AdtCtor(tag, arity), subPats) }], [])
```

### desugarPattern for RecordPat

```fsharp
| RecordPat(fields, _) ->
    // Look up field indices from ElabEnv
    // fields: (fieldName * subPattern) list
    let orderedFields = fields |> List.sortBy (fun (name, _) ->
        // get index from RecordFields map for the relevant type
        // (type name must be resolved from context or inferred)
        fieldIndex name)
    let subPats = orderedFields |> List.map snd
    let fieldList = orderedFields |> List.mapi (fun i (name, _) -> (name, i))
    ([{ Scrutinee = acc; Pattern = CtorTest(RecordCtor(fieldList), subPats) }], [])
```

**Design note:** `desugarPattern` needs access to `ElabEnv.CtorTags` / `ElabEnv.RecordFields`. The simplest approach is to pass a `desugarCtx` record alongside, or curry the maps as parameters. The existing `compile` function in `MatchCompiler.fs` takes `(scrutinee: Accessor) (arms: (Pattern * bool * int) list)` — extend to `(ctx: DesugarCtx) (scrutinee: Accessor) (arms: ...)`.

---

## New ExternalFuncDecl Registrations

`elaborateProgram` must include these in `MlirModule.ExternalFuncs`:

| C function | MLIR declaration |
|------------|-----------------|
| `@setjmp` | `llvm.func @setjmp(!llvm.ptr) -> i32` |
| `@longjmp` | `llvm.func @longjmp(!llvm.ptr, i32) -> ()` (void) |
| `@lang_raise` | `llvm.func @lang_raise(!llvm.ptr, !llvm.ptr) -> ()` (void) |
| `@lang_current_exception` | `llvm.func @lang_current_exception() -> !llvm.ptr` |

These are added unconditionally by `elaborateProgram` (the same way `@GC_malloc` and `@printf` are already added).

---

## Top-Level Module Elaboration

Currently `elaborateModule` takes a single `Expr`. For programs with multiple declarations:

```fsharp
let elaborateProgram (decls: Decl list) : MlirModule
```

Processing order:
1. Collect all `TypeDecl`, `RecordTypeDecl`, `ExceptionDecl` — assign tags/indices, populate `ElabEnv`; no IR emitted.
2. Process `LetDecl` / `LetRecDecl` — compile to `FuncOp` (for `LetRec`) or inline into `@main`.
3. Emit the `@main` function wrapping the top-level expression sequence.
4. Return `MlirModule` with accumulated `Funcs`, `Globals`, `ExternalFuncs`.

---

## Component Interaction Map (v3)

```
Ast.Decl list
   |
   | elaborateProgram (new entry point)
   |
   |-- TypeDecl / RecordTypeDecl / ExceptionDecl
   |      → populate CtorTags, CtorArities, RecordFields, ExnTags in ElabEnv
   |      (no IR emitted at this stage)
   |
   |-- LetDecl / LetRecDecl
   |
   | elaborateExpr (extended)
   |
   |-- Constructor  → GC_malloc(16) + store tag + store payload
   |-- RecordExpr   → GC_malloc(n*8) + store each field
   |-- FieldAccess  → GEP(index) + load
   |-- RecordUpdate → GC_malloc(n*8) + copy all fields + overwrite updated fields
   |-- SetField     → GEP(index) + store
   |-- Raise        → elaborate exn + call @lang_raise + llvm.unreachable
   |-- TryWith      → alloca jmp_buf + setjmp + try/catch blocks + handler dispatch
   |
   | Match (with ConstructorPat / RecordPat)
   |
   | MatchCompiler.compile (extended)
   |-- ConstructorPat → AdtCtor test: load field 0 + cmpi eq tag
   |-- RecordPat      → RecordCtor test: unconditional + field accessors
   |
   v
MlirModule { Globals, ExternalFuncs, Funcs }
   |
   v
Printer.fs
   | + printOp for LlvmSetjmpOp, LlvmLongjmpOp
   v
.mlir text → Pipeline.fs → native binary
```

---

## Build Order

| Step | Files Changed | What It Delivers | Prerequisite |
|------|--------------|-----------------|-------------|
| 1 | `MlirIR.fs`, `Printer.fs` | `LlvmSetjmpOp`, `LlvmLongjmpOp` cases compile and print valid MLIR | None |
| 2 | `lang_runtime.c` | `lang_raise`, `lang_current_exception` available for linking | None (parallel with step 1) |
| 3 | `Elaboration.fs` | Add 5 new `ElabEnv` fields; update `emptyEnv` | None |
| 4 | `Elaboration.fs` | `elaborateProgram` entry point: processes `TypeDecl`/`RecordTypeDecl`/`ExceptionDecl`, populates maps | Step 3 |
| 5 | `Elaboration.fs` | `Constructor` and `RecordExpr` expression elaboration | Step 4 (tags/field indices must exist) |
| 6 | `Elaboration.fs` | `FieldAccess`, `RecordUpdate`, `SetField` | Step 5 (record ptr needed) |
| 7 | `MatchCompiler.fs` | `AdtCtor` and `RecordCtor` CtorTag variants; `desugarPattern` for `ConstructorPat` and `RecordPat` | Step 4 (tag maps must exist) |
| 8 | `Elaboration.fs` | Wire `emitCtorTest` for `AdtCtor` (load tag + cmpi) and `RecordCtor` (unconditional, expose fields) | Step 7 |
| 9 | `Elaboration.fs` | `TryWith` elaboration (alloca jmp_buf, setjmp, try/catch blocks, handler match dispatch) | Steps 1, 3, 7, 8 |
| 10 | `Elaboration.fs` | `Raise` elaboration (call `@lang_raise` + `LlvmUnreachableOp`) | Steps 2, 3, 9 |

**Rationale:**
- Steps 1 and 2 are independent — validate setjmp/longjmp MLIR emission in isolation before wiring it into the elaborator.
- Step 3 before Steps 4–10 because all subsequent elaboration uses the new ElabEnv fields.
- Step 4 (elaborateProgram) before Steps 5–10 because `Constructor`/`Record` elaboration requires pre-populated tag/field maps.
- Steps 5–6 before Step 8 because pattern matching on records requires knowing the field layout.
- Step 7 (MatchCompiler) before Step 8 (emitCtorTest integration) — the decision tree must know `AdtCtor`/`RecordCtor` before the elaborator walks it.
- Steps 9 and 10 are last because they depend on JmpBufPtr (Step 3) and lang_runtime additions (Step 2), but are independent of ADT/record elaboration.
- Steps 1/2/3 can all proceed in parallel.

---

## Anti-Patterns

### Anti-Pattern 1: Allocating jmp_buf with GC_malloc

**What people do:** Call `GC_malloc(sizeof(jmp_buf))` to get the setjmp buffer, for consistency with other heap allocations.

**Why it is wrong:** `setjmp` writes the stack pointer of the call frame into the buffer. The buffer must remain valid for the duration of the `TryWith` scope — i.e., as long as the enclosing function is on the stack. If the GC moves or collects the buffer (which Boehm GC may do because jmp_buf contents are not GC roots), the stored stack state is invalid. Additionally, `longjmp` jumps to the frame that called `setjmp` — if that frame has already returned, the program crashes.

**Do this instead:** Use `llvm.alloca` — the buffer lives on the stack frame of the function containing `TryWith`, which is guaranteed to be live until the function returns or the catch branch executes.

### Anti-Pattern 2: Sharing a Single Global jmp_buf

**What people do:** Allocate one global `jmp_buf` (or one per program) and reuse it for all `TryWith` expressions.

**Why it is wrong:** Nested `TryWith` expressions require a stack of jump buffers. The innermost handler must longjmp to its own buffer; the outer handler must have its buffer preserved. A single global is overwritten by the inner `TryWith` and the outer handler becomes unreachable.

**Do this instead:** Each `TryWith` allocates its own stack `jmp_buf` via `llvm.alloca`. `ElabEnv.JmpBufPtr` tracks the current innermost buffer; it is saved and restored across nested `TryWith` elaboration.

### Anti-Pattern 3: Representing Zero-Argument ADT Constructors as Null

**What people do:** Represent `None`, `False`, or any no-payload constructor as a null pointer to avoid an allocation.

**Why it is wrong:** Pattern matching for `AdtCtor` loads field 0 of the pointer. A null dereference crashes. More critically, when multiple zero-argument constructors exist in the same type (`Red | Green | Blue`), null can represent only one — the others require distinct tag values.

**Do this instead:** Always allocate 16 bytes and write the tag. The only exception is the existing list `[]` which is null by design and uses a separate `NilCtor` path that does not dereference.

### Anti-Pattern 4: Caching Record Field SSA Values Across SetField

**What people do:** When the match compiler resolves an accessor (GEP + load), it caches the result in `accessorCache`. A subsequent `FieldAccess` reuses the cached value.

**Why it is wrong:** `SetField` mutates the heap cell. The cached SSA value reflects the pre-mutation state. Any subsequent read sees the old value, which is incorrect.

**Do this instead:** The `accessorCache` in `emitDecisionTree` is scoped to a single `Match` expression evaluation — it is not shared with `elaborateExpr` for `FieldAccess`. `FieldAccess` always emits a fresh GEP + load. This is already the correct behavior in the existing code; the anti-pattern is to change it in the name of "optimization."

### Anti-Pattern 5: Encoding GADT Constraints in the IR Tag

**What people do:** For GADTs with parameterized return types, embed the type parameter index in the tag value to enable runtime type inspection.

**Why it is wrong:** The type checker handles all GADT constraints statically before elaboration. The elaborator never needs to inspect return type constraints at runtime. Adding type information to the tag conflates type checking with code generation.

**Do this instead:** Tags are dense integers (0..n-1) assigned per constructor in declaration order. GADT type parameters are erased after type checking. The elaborator treats GADT constructors identically to regular ADT constructors.

---

## Scalability Considerations

| Concern | Current state | After this milestone | Impact |
|---------|--------------|---------------------|--------|
| `MlirOp` DU cases | ~17 cases | +2 → ~19 | Negligible |
| `Printer.printOp` match arms | ~17 arms | +2 → ~19 | Negligible |
| `ElabEnv` fields | 9 fields | +5 → 14 | Negligible |
| `MatchCompiler.CtorTag` cases | 6 cases | +2 → 8 | Negligible |
| `lang_runtime.c` functions | 8 functions | +2 → 10 | Negligible |
| jmp_buf stack depth | N/A | 1 alloca per TryWith nesting level | Stack frames; fine for typical programs |
| Exception value cell | N/A | 1 global pointer | Single-threaded; fine |

---

## Sources

- Direct code analysis: `MlirIR.fs`, `Elaboration.fs`, `MatchCompiler.fs`, `Printer.fs`, `Pipeline.fs`, `lang_runtime.c` in `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/`
- Source AST nodes: `Ast.fs`, `MatchCompile.fs`, `Eval.fs` in `/Users/ohama/vibe-coding/LangThree/src/LangThree/`
- `MatchCompiler.fs` line 121–125: explicit `failwith "MatchCompiler: ConstructorPat not yet supported in backend"` and `RecordPat` — confirms both are unimplemented as of current codebase
- C standard (C11 §7.13): `setjmp`/`longjmp` behavior; buffer must be in scope at longjmp call
- Boehm GC: conservative scanner traverses all reachable pointers; stack-allocated jmp_buf is scanned correctly as part of the stack frame

---
*Architecture research for: LangBackend ADT/GADT + Records (mutable fields) + Exception handling milestone*
*Researched: 2026-03-26*
