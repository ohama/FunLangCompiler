# Architecture Patterns: LangBackend v2.0

**Domain:** Functional language compiler backend — MLIR text generation + LLVM pipeline
**Researched:** 2026-03-26
**Scope:** v2.0 — GC runtime, heap-allocated strings/tuples/lists, pattern matching
**Confidence:** HIGH

---

## Actual v1 Architecture (What Was Built)

The compiler emits MLIR as text strings — NOT via MLIR C API P/Invoke. This is the key
architectural fact that all v2 decisions must respect.

```
Source File (.lt)
       |
       v
  [ LangThree Frontend ] (project reference, not modified)
    Lexer + Parser (FsLexYacc)
    Type Checker (H-M / Bidir)
       |
       v
  Ast.Expr (typed)
       |
       v
  [ Elaboration.fs ]
    elaborateExpr: Expr -> ElabEnv -> (MlirValue * MlirOp list)
    ElabEnv: {Vars, Counter, LabelCounter, Blocks, KnownFuncs, Funcs, ClosureCounter}
       |
       v
  MlirModule (F# discriminated union)
  MlirType | MlirValue | MlirOp | MlirBlock | MlirRegion | FuncOp
       |
       v
  [ Printer.fs ]
    printModule: MlirModule -> string
    Pure serializer, no side effects
       |
       v
  .mlir text file (temp)
       |
       v
  [ Pipeline.fs ] (shell subprocess)
    mlir-opt --convert-arith-to-llvm --convert-cf-to-llvm
             --convert-func-to-llvm --reconcile-unrealized-casts
       |
       v
  .mlir (LLVM dialect only, temp)
       |
       v
    mlir-translate --mlir-to-llvmir
       |
       v
  .ll LLVM IR (temp)
       |
       v
    clang -Wno-override-module
       |
       v
  Native Binary
```

### Current Component Inventory

| Component | File | Status | Purpose |
|-----------|------|--------|---------|
| `MlirIR.fs` | `MlirType`, `MlirOp` (DU), `MlirBlock`, `FuncOp`, `MlirModule` | v1 complete | Typed internal IR |
| `Printer.fs` | `printModule: MlirModule -> string` | v1 complete | IR → MLIR text |
| `Elaboration.fs` | `elaborateExpr`, `ElabEnv` | v1 complete | AST → MlirIR pass |
| `Pipeline.fs` | `compile: MlirModule -> string -> Result` | v1 complete | Shell pipeline |
| `Program.fs` | CLI entry point | v1 complete | Orchestrates pipeline |

---

## v2 Integration Strategy: What Changes and What Stays

### What stays unchanged

- `Pipeline.fs` — the lowering passes are unchanged; `llvm` dialect ops lower transparently
- `Printer.fs` architecture — add new `printOp` match cases, existing cases untouched
- `ElabEnv` shape — extend with new fields (GcDecls flag); existing fields unchanged
- `FuncOp` / `MlirBlock` / `MlirRegion` / `MlirModule` — no structural change needed

### What extends

| Component | v2 Extension |
|-----------|-------------|
| `MlirType` | No new cases needed — `Ptr` covers all heap pointers |
| `MlirOp` | ~8 new cases: `LlvmCallOp`, `LlvmMallocOp`, `LlvmBitcastOp` (or reuse `LlvmCallOp` with `@GC_malloc`), plus match dispatch ops |
| `Printer.fs` | New `printOp` match arms for each new op case |
| `Elaboration.fs` | New `elaborateExpr` match arms for `String`, `Tuple`, `List`, `Cons`, `EmptyList`, `LetPat`, `Match` |
| `ElabEnv` | Add `NeedsGcDecl: bool ref` flag to trigger runtime declaration emission |
| `Pipeline.fs` | Add `-lgc` link flag to the clang invocation |

### What is new

| New Component | Purpose | Notes |
|---------------|---------|-------|
| GC runtime declarations (inline in emitted MLIR) | Declare `@GC_malloc` as `llvm.func @GC_malloc` | Emitted once at module top by `Printer.fs` |
| `RuntimeDecls.fs` (optional) | Centralise all extern function declarations | Can be a helper module or inline in Printer |

---

## Heap Allocation Strategy

### Boehm GC integration model

The compiler does NOT link against `libgc` via P/Invoke. It emits MLIR text that declares
`GC_malloc` as an external function, then links the resulting binary against `-lgc`. This
matches the text-generation approach: everything is textual MLIR + clang linker flags.

**MLIR declaration (emitted once per module):**
```
llvm.func private @GC_malloc(i64) -> !llvm.ptr
```

**Allocation call for a struct of N words:**
```
%size = arith.constant N : i64           ; bytes = N * 8 for i64-width fields
%ptr  = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr
```

**Pipeline.fs clang flag addition:**
```fsharp
let clangArgs = sprintf "-Wno-override-module %s -lgc -o %s" llFile outputPath
```

### When to use GC_malloc vs alloca

| Allocation Site | Mechanism | Reason |
|-----------------|-----------|--------|
| Closure env struct (existing) | `llvm.alloca` | Stack-scoped, v1 decision validated |
| String literal | `GC_malloc` | Heap-allocated, may outlive stack frame |
| Tuple value | `GC_malloc` | Returned from functions, must outlive frame |
| List cons cell | `GC_malloc` | Recursive structure, indefinite lifetime |

### MlirOp extensions for heap allocation

Two approaches; **recommended: add `LlvmCallOp`** (general external call):

```fsharp
// Recommended: general external call — covers GC_malloc and future runtime calls
| LlvmCallOp of result: MlirValue * callee: string * args: MlirValue list

// Printer case:
| LlvmCallOp(result, callee, args) ->
    let argStr = args |> List.map (fun v -> sprintf "%s : %s" v.Name (printType v.Type)) |> String.concat ", "
    sprintf "%s%s = llvm.call @%s(%s) : (%s) -> %s"
        indent result.Name callee argStr
        (args |> List.map (fun v -> printType v.Type) |> String.concat ", ")
        (printType result.Type)
```

This single op handles `@GC_malloc`, and future calls like `@strcmp`, `@strlen`.

---

## String Representation

### Memory layout

A string is represented as a GC-managed block with:
- Field 0 (i64): byte length (not including null terminator)
- Field 1+ (i8 array): UTF-8 bytes

In MLIR text: `!llvm.ptr` (opaque pointer — the field layout is implicit, accessed via GEP).

### String literal codegen

For `String("hello", _)` in Elaboration:

```
; 1. Allocate struct: 8 bytes (length field) + len(s) bytes (chars) + 1 (null)
%size = arith.constant <total_bytes> : i64
%ptr  = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr

; 2. Store length at field 0
%len_const = arith.constant <len> : i64
llvm.store %len_const, %ptr : i64, !llvm.ptr

; 3. Store bytes: use global constant + memcpy, or individual byte stores for small strings
; For simplicity in v2: llvm.mlir.global for string data + llvm.call @memcpy
```

**Recommended approach for v2:** Use `llvm.mlir.global` for the byte content (static data),
then at runtime: allocate header struct via `GC_malloc`, copy pointer or inline bytes.
Alternatively, store a pointer-to-global as the string's data pointer (two-field layout: `{i64 length, ptr data}`).

Simplest v2 layout — two-field heap struct:
```
Field 0: i64 — byte length
Field 1: ptr — points to global or GC'd byte array
```

Emitted as:
```
; global byte array (module top level)
llvm.mlir.global private constant @str_data_0("\68\65\6c\6c\6f\00") : !llvm.array<6 x i8>

; at use site
%size = arith.constant 16 : i64          ; 2 fields × 8 bytes
%hdr  = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr
%len_val = arith.constant 5 : i64
llvm.store %len_val, %hdr : i64, !llvm.ptr
%data_slot = llvm.getelementptr %hdr[1] : (!llvm.ptr) -> !llvm.ptr, i64
%data_ptr = llvm.mlir.addressof @str_data_0 : !llvm.ptr
llvm.store %data_ptr, %data_slot : !llvm.ptr, !llvm.ptr
; %hdr is the string value (type Ptr)
```

### MlirOp additions for strings

```fsharp
| LlvmGlobalConstantOp of name: string * value: string * numBytes: int
  // emits: llvm.mlir.global private constant @name("...") : !llvm.array<N x i8>
  // Printer: top-level op, not inside a function body
```

`LlvmGlobalConstantOp` is a **module-level op** — it must be added to `MlirModule` as a
`GlobalDecls: MlirGlobal list` field, or represented as a new variant in a module-level
declaration type. This is the only structural change to `MlirModule` needed in v2.

---

## Tuple Representation

### Memory layout

A tuple `(v1, v2, ..., vN)` is a GC-managed array of N `i64` words (all values are boxed
to `i64` or represented as `!llvm.ptr`; Ptr values are stored as pointers in i64 slots).

Simpler: treat all values as `i64`-width (i64 and Ptr fit in 8 bytes on 64-bit).

Layout: `[i64 field0, i64 field1, ..., i64 fieldN-1]`

In MLIR: `!llvm.ptr` pointing to a GC'd array of `i64`.

### Tuple construction codegen

For `Tuple([e1; e2; e3], _)`:

```
; allocate N*8 bytes
%size = arith.constant 24 : i64   ; 3 fields * 8 bytes
%ptr  = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr

; store each field
%v1 = <elaborate e1>
llvm.store %v1, %ptr : i64, !llvm.ptr
%slot1 = llvm.getelementptr %ptr[1] : (!llvm.ptr) -> !llvm.ptr, i64
%v2 = <elaborate e2>
llvm.store %v2, %slot1 : i64, !llvm.ptr
...
; %ptr is the tuple value (type Ptr)
```

### Tuple element extraction

For pattern `let (x, y) = tup`:
```
; load field 0
%x = llvm.load %ptr : !llvm.ptr -> i64
; load field 1
%slot1 = llvm.getelementptr %ptr[1] : (!llvm.ptr) -> !llvm.ptr, i64
%y = llvm.load %slot1 : !llvm.ptr -> i64
```

This reuses existing `LlvmGEPLinearOp` and `LlvmLoadOp` — no new MlirOp cases needed for
field extraction. New cases needed only for construction (`LlvmCallOp` for GC_malloc).

---

## List Representation

### Memory layout

A list is a singly-linked cons cell list:
- `[]` (EmptyList): represented as null pointer (`i64 0` cast to `!llvm.ptr`)
- `h :: t` (Cons): GC'd two-field struct `{i64 head, ptr tail}`

Layout of a cons cell:
```
Field 0: i64  — head value (or ptr to heap-allocated head)
Field 1: ptr  — tail (next cons cell, or null for last element)
```

Size: 16 bytes per cell.

### List codegen

For `EmptyList`:
```
%null = arith.constant 0 : i64
; use %null as the list pointer (i64 0 = null ptr treated as Ptr type)
```

For `Cons(head, tail, _)`:
```
%size = arith.constant 16 : i64
%cell = llvm.call @GC_malloc(%size) : (i64) -> !llvm.ptr
%h = <elaborate head>
llvm.store %h, %cell : i64, !llvm.ptr
%tail_slot = llvm.getelementptr %cell[1] : (!llvm.ptr) -> !llvm.ptr, i64
%t = <elaborate tail>
llvm.store %t, %tail_slot : !llvm.ptr, !llvm.ptr
; %cell is the list value
```

For `List([e1; e2; e3])`: desugar to nested Cons with EmptyList at end (in Elaboration,
not as a separate MlirOp).

### The null-pointer issue

Using `i64 0` as the null/empty list requires a bitcast or `inttoptr` when stored into a
`ptr` slot. LLVM dialect provides `llvm.inttoptr`:

```fsharp
| LlvmIntToPtrOp of result: MlirValue * value: MlirValue
// emits: %result = llvm.inttoptr %value : i64 to !llvm.ptr
```

Alternatively, use `llvm.mlir.zero` (LLVM 20 null pointer constant):
```
%null = llvm.mlir.zero : !llvm.ptr
```

This is cleaner. Add `LlvmZeroOp of result: MlirValue` to `MlirOp`.

---

## Pattern Matching Codegen

### AST nodes to handle

```
Match(scrutinee, clauses, _)
  where each clause: (Pattern * Expr option * Expr)  [pattern, guard, body]

Patterns in scope for v2:
  VarPat(name)           — always succeeds, binds name
  WildcardPat            — always succeeds, binds nothing
  TuplePat([p1; p2])     — match arity, then recurse on fields
  ConsPat(hPat, tPat)    — check non-null, then recurse on head/tail
  EmptyListPat           — check null/zero
  ConstPat(IntConst n)   — compare value == n
  ConstPat(BoolConst b)  — compare value == 0/1

LetPat(TuplePat([p1;p2]), expr, body) — irrefutable pattern let binding
```

### Compilation strategy: chain of if-then tests

Each pattern clause becomes a sequence of conditional branches in the basic block graph.
Pattern matching compiles to:

```
; for Match(scrut, [clause1; clause2; clause3])

%v = <elaborate scrut>

; Test clause1
<pattern test ops for clause1>
cf.cond_br %match1, ^clause1_body, ^try_clause2

^clause1_body:
  <bind pattern variables>
  <elaborate body1>
  cf.br ^match_merge(%result1 : i64)

^try_clause2:
  <pattern test ops for clause2>
  cf.cond_br %match2, ^clause2_body, ^try_clause3
  ...

^match_exhausted:
  ; runtime error — unreachable in well-typed program
  llvm.unreachable   ; or call @abort

^match_merge(%result : i64):
  ; %result is the match expression value
```

This reuses the existing `CfCondBrOp` / `CfBrOp` mechanism — the same pattern as `If`.
No new control-flow MlirOp cases are needed.

### Pattern test emission

Each pattern type generates test ops:

| Pattern | Test ops |
|---------|----------|
| `VarPat` / `WildcardPat` | None — always succeeds; emit `arith.constant 1 : i1` |
| `ConstPat(IntConst n)` | `arith.constant n; arith.cmpi eq` → `i1` |
| `ConstPat(BoolConst b)` | `arith.constant 0/1; arith.cmpi eq` |
| `EmptyListPat` | `arith.constant 0; arith.cmpi eq` (compare ptr-as-i64 to 0) — needs `ptrtoint` |
| `ConsPat(hp, tp)` | Check `ptr != 0` (non-null), then load head and tail |
| `TuplePat([p1;p2])` | Always succeeds at top level (types match); load fields, test sub-patterns |

#### EmptyListPat check — ptrtoint

To compare a list pointer to null, we need `llvm.ptrtoint`:
```fsharp
| LlvmPtrToIntOp of result: MlirValue * value: MlirValue
// emits: %result = llvm.ptrtoint %value : !llvm.ptr to i64
```

Pattern check for `EmptyListPat`:
```
%as_int = llvm.ptrtoint %list_ptr : !llvm.ptr to i64
%zero   = arith.constant 0 : i64
%is_nil = arith.cmpi eq, %as_int, %zero : i64
```

#### Irrefutable pattern let binding

`LetPat(TuplePat([VarPat "x"; VarPat "y"]), expr, body)` compiles exactly like tuple
field extraction: elaborate expr, GEP+load field 0 into x, GEP+load field 1 into y,
elaborate body with x and y in env. This is just a special case of `elaborateExpr` with
no conditional branching — treat it as a degenerate `Match` with one always-succeeding clause.

---

## ElabEnv Extensions

### New fields needed

```fsharp
type ElabEnv = {
    // --- existing v1 fields ---
    Vars:           Map<string, MlirValue>
    Counter:        int ref
    LabelCounter:   int ref
    Blocks:         MlirBlock list ref
    KnownFuncs:     Map<string, FuncSignature>
    Funcs:          FuncOp list ref
    ClosureCounter: int ref
    // --- new v2 fields ---
    StringGlobals:  (string * string) list ref  // (globalName, value) pairs, module-level
    NeedsGcDecl:    bool ref                    // true once any GC_malloc is emitted
}
```

`StringGlobals` accumulates `llvm.mlir.global` entries. `Printer.fs` emits them before
the first function. `NeedsGcDecl` controls whether `llvm.func private @GC_malloc(i64) -> !llvm.ptr`
is emitted; set to `true` on first heap allocation.

### MlirModule extension

Add a `GlobalDecls: string list` field to hold pre-function declarations emitted verbatim:

```fsharp
type MlirModule = {
    GlobalDecls: string list   // raw MLIR lines emitted before all FuncOps
    Funcs:       FuncOp list
}
```

`Printer.printModule` emits `GlobalDecls` lines first, then functions. This is the minimal
change needed; avoids new DU cases for global constants.

---

## Component Interaction Map (v2)

```
Ast.Expr
   |
   | new match arms in elaborateExpr
   v
Elaboration.fs
   |--- String  → LlvmGlobalConstantOp (→ ElabEnv.StringGlobals)
   |             + LlvmCallOp(@GC_malloc) + LlvmGEPLinearOp + LlvmStoreOp
   |--- Tuple   → LlvmCallOp(@GC_malloc) + LlvmGEPLinearOp + LlvmStoreOp
   |--- EmptyList → LlvmZeroOp (null ptr)
   |--- Cons    → LlvmCallOp(@GC_malloc) + LlvmGEPLinearOp + LlvmStoreOp
   |--- List    → desugared to EmptyList + Cons
   |--- LetPat  → GEP+load (tuple fields) → no new ops
   |--- Match   → CfCondBrOp + CfBrOp (existing) + pattern tests (existing arith + new ptrtoint)
   |
   v
MlirModule {GlobalDecls; Funcs}
   |
   v
Printer.fs
   |--- emit GlobalDecls (GC_malloc decl, llvm.mlir.global entries)
   |--- emit FuncOps (existing + new op cases)
   |
   v
.mlir text
   |
   v
Pipeline.fs (add -lgc to clang args)
   |
   v
Native binary with Boehm GC
```

---

## New MlirOp Cases Summary

Recommended additions to `MlirOp` DU in `MlirIR.fs`:

```fsharp
// External function call (covers GC_malloc, memcpy, etc.)
| LlvmCallOp of result: MlirValue * callee: string * args: MlirValue list

// Null pointer constant
| LlvmZeroOp of result: MlirValue
// emits: %result = llvm.mlir.zero : !llvm.ptr

// ptr-to-int conversion (for nil list check)
| LlvmPtrToIntOp of result: MlirValue * ptr: MlirValue
// emits: %result = llvm.ptrtoint %ptr : !llvm.ptr to i64

// int-to-ptr conversion (when null int needs to become ptr)
| LlvmIntToPtrOp of result: MlirValue * value: MlirValue
// emits: %result = llvm.inttoptr %value : i64 to !llvm.ptr

// Unreachable (match exhaustion in well-typed programs never reached)
| LlvmUnreachableOp
// emits: llvm.unreachable
```

Each new case requires one new match arm in `Printer.printOp`. No other Printer changes.

---

## Build Order for v2 Phases

Dependencies flow strictly from left to right. Each phase must have E2E tests passing before
the next phase starts.

```
Phase 1: Boehm GC Integration
  - Add -lgc to Pipeline.fs clang args
  - Add @GC_malloc declaration to MlirModule.GlobalDecls
  - Add LlvmCallOp to MlirIR + Printer
  - Test: hello-world that calls GC_malloc once, returns size

Phase 2: Strings
  - Requires: Phase 1 (GC_malloc)
  - Add LlvmGlobalConstantOp (or raw string to GlobalDecls)
  - Elaborate String literal: global + header alloc + field stores
  - Test: compile string literal, return its length

Phase 3: Tuples
  - Requires: Phase 1 (GC_malloc)
  - Elaborate Tuple: alloc + store fields
  - Elaborate LetPat(TuplePat): GEP+load fields, bind vars
  - Test: let (x, y) = (1, 2) in x + y → 3

Phase 4: Lists
  - Requires: Phase 1 (GC_malloc)
  - Add LlvmZeroOp (null ptr for nil)
  - Elaborate EmptyList, Cons
  - Elaborate List literal (desugar to Cons chain)
  - Test: let xs = [1; 2; 3] in ...head...

Phase 5: Pattern Matching
  - Requires: Phases 2, 3, 4 (all heap types)
  - Add LlvmPtrToIntOp for nil check
  - Add LlvmUnreachableOp for exhaustion
  - Elaborate Match with: VarPat, WildcardPat, ConstPat, TuplePat, ConsPat, EmptyListPat
  - Elaborate LetPat as degenerate match (irrefutable)
  - Test: match xs with [] -> 0 | h :: _ -> h → head of list
  - Test: match (1, 2) with (x, y) -> x + y → 3
```

**Critical path:** Phase 1 (GC) must be working and tested before Phases 2–4 start.
Phases 2–4 are independent of each other once Phase 1 is solid.
Phase 5 depends on all of 2–4 because test programs use all types in match expressions.

---

## Patterns to Follow

### Pattern 1: Extend DU, not restructure

Every v1 feature added new `MlirOp` cases without changing `MlirModule`, `FuncOp`, `MlirBlock`,
or `MlirRegion`. Follow the same rule in v2. When the compiler fails to match a new `MlirOp`
case in `Printer.printOp`, the F# compiler raises a warning — use this as a check.

### Pattern 2: GEP + Load for field extraction (existing)

Field extraction for tuples and cons cells uses `LlvmGEPLinearOp` + `LlvmLoadOp` — the same
ops used for closure capture loading. No new op cases are needed for reads.

### Pattern 3: GlobalDecls as raw strings

Rather than a typed DU for global declarations, use `string list` in `MlirModule.GlobalDecls`.
This avoids over-engineering for the few declaration types needed in v2 (GC_malloc decl,
string byte arrays). If v3 adds more, promote to a typed DU then.

### Pattern 4: Match compiles like nested If-Else

Each match clause is a `cf.cond_br` followed by a body block. The existing `If` elaboration
pattern (emit condition, create branch blocks, append to env.Blocks) applies directly.
No new control-flow ops are needed.

### Pattern 5: String counter for globals

Add a `StringCounter: int ref` to `ElabEnv`. Each string literal allocates the next name
`@str_data_N`. The corresponding `llvm.mlir.global` line is appended to `ElabEnv.StringGlobals`
during elaboration, then collected by `elaborateModule` into `MlirModule.GlobalDecls`.

---

## Anti-Patterns to Avoid

### Anti-Pattern 1: Storing type tags at runtime

**What:** Embed a discriminator word (tag) in every heap object to support dynamic dispatch.
**Why bad:** LangThree is statically typed — the type checker resolves all types before codegen.
A tag adds overhead and complexity for no benefit in a monomorphic, type-safe compiler.
**Instead:** Use structural layout (fixed field offsets) directly. The elaboration pass knows
the type at compile time and generates the correct GEP index.

### Anti-Pattern 2: Boxing everything to a uniform value type

**What:** Represent all values as `{i64 tag, i64 payload}` pairs on the heap.
**Why bad:** Forces double-indirection for integers and booleans, which are perfectly
representable as unboxed `i64` and `i1`. Massively increases allocation pressure.
**Instead:** Keep integers and booleans as unboxed SSA values. Only strings, tuples, and
lists require heap allocation. Match expressions receive the unboxed value directly.

### Anti-Pattern 3: Writing a GC from scratch

**What:** Emit manual `llvm.call @malloc` + `llvm.call @free` with reference counting.
**Why bad:** Reference counting with cyclic data structures (lists of tuples) requires
cycle detection. Manual `free` requires lifetime analysis. Both are v3+ concerns.
**Instead:** Boehm GC (`-lgc`, `GC_malloc`) handles collection conservatively.
Zero code changes in the compiler for collection — only allocation matters.

### Anti-Pattern 4: Fully-general pattern match compilation

**What:** Implement decision trees, matrix compilation, or exhaustiveness checking in
the elaboration pass.
**Why bad:** These are frontend concerns. The type checker + elaboration pass only needs
to compile patterns it encounters. LangThree's type checker already validates exhaustiveness.
**Instead:** Compile each clause sequentially (linear chain of `cf.cond_br`). This is
correct and sufficient. Decision tree optimization is a v3 concern.

### Anti-Pattern 5: Changing FuncOp signature for GC init

**What:** Emit a `GC_init()` call at the start of `@main`.
**Why bad:** Boehm GC does not require explicit initialization when using `GC_malloc`
(initialization is lazy). Adding a call complicates the entry point for no benefit.
**Instead:** Link with `-lgc` and call `GC_malloc` directly. No init call needed.

---

## Scalability Considerations

| Concern | Current state | v2 change | Impact |
|---------|---------------|-----------|--------|
| `MlirOp` DU size | 17 cases | +5 cases → 22 | Negligible |
| `Printer.printOp` | 17 match arms | +5 arms → 22 | Negligible |
| `ElabEnv` fields | 7 fields | +2 fields | Negligible |
| Heap allocation count | 0 (all stack) | Per string/tuple/list | GC handles |
| Pattern match depth | N/A | Linear chain per clause | Fine for typical programs |
| String globals count | 0 | 1 per distinct literal | Small; deduplicate if needed |

---

## Sources

- Boehm GC documentation: https://www.hboehm.info/gc/ — `GC_malloc` signature, no init needed (HIGH confidence)
- MLIR llvm dialect: https://mlir.llvm.org/docs/Dialects/LLVM/ — `llvm.call`, `llvm.mlir.zero`, `llvm.ptrtoint`, `llvm.inttoptr`, `llvm.unreachable`, `llvm.mlir.global` (HIGH confidence)
- LLVM LangRef: https://llvm.org/docs/LangRef.html — getelementptr semantics for struct field access with opaque pointers (HIGH confidence)
- Existing v1 source (verified): `MlirIR.fs`, `Printer.fs`, `Elaboration.fs`, `Pipeline.fs` — actual architecture base (HIGH confidence)
- LangThree `Ast.fs` (verified): `String`, `Tuple`, `EmptyList`, `List`, `Cons`, `Match`, `LetPat`, `Pattern` variants that need compilation (HIGH confidence)
