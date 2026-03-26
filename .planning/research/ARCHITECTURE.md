# Architecture Patterns: LangBackend Compiler

**Domain:** Functional language compiler backend (F# frontend + MLIR/LLVM codegen)
**Researched:** 2026-03-26
**Confidence:** HIGH

---

## Recommended Architecture

The pipeline is a straight-line transformation: each stage takes well-defined input, produces
well-defined output, and owns nothing from the previous stage after conversion. There is no
shared mutable state between stages.

```
Source File (.lt)
       |
       v
  [ LangThree Frontend ]
    Lexer + Parser
    Type Checker (H-M)
       |
       v
  Ast.Expr (typed)
       |
       v
  [ CodegenEnv ]
    SymbolTable (name -> MlirValue)
    ClosureCapture analysis
       |
       v
  [ MLIRGen ]
    Emit arith, func, cf, llvm dialect ops
    via MLIR C API P/Invoke
       |
       v
  MlirModule (in-memory MLIR)
       |
       v
  [ Lowering Pipeline ]
    convert-arith-to-llvm
    convert-func-to-llvm
    convert-cf-to-llvm
    reconcile-unrealized-casts
       |
       v
  MlirModule (llvm dialect only)
       |
       v
  [ LLVM Backend ]
    mlirTranslateModuleToLLVMIR
    LLVM Target Machine
    Emit object file (.o)
       |
       v
  [ Linker ]
    clang / lld
       |
       v
  Native Binary (ELF x86-64)
```

---

## Component Boundaries

| Component | Responsibility | Input | Output | Communicates With |
|-----------|---------------|-------|--------|-------------------|
| LangThree Frontend | Lex, parse, type-check source | `.lt` file | `Ast.Module` | CLI driver |
| CLI Driver | Orchestrate pipeline, handle errors | File path, flags | Exit code | Frontend, CodegenEnv |
| CodegenEnv | Manage MLIR context, symbol tables, closure analysis | `Ast.Expr` | Mutable env state | MLIRGen |
| MLIRGen | Traverse AST, emit MLIR ops via C API | `Ast.Expr` + CodegenEnv | `MlirModule` | MLIR C API bindings |
| MLIR C API Bindings | F# P/Invoke layer over `libMLIR.so` | F# calls | Opaque MLIR handles | libMLIR.so |
| Lowering Pipeline | Run built-in conversion passes | `MlirModule` | `MlirModule` (llvm-only) | MLIR C API bindings |
| LLVM Backend | Translate to LLVM IR, emit object file | `MlirModule` | `.o` file | LLVM Target API |
| Linker | Link object file into executable | `.o` files | ELF binary | System clang/lld |

**Key boundary rule:** The F# code never manipulates raw MLIR pointers directly. All interaction
with MLIR C API goes through the Bindings layer, which wraps each opaque handle in an F# struct
or discriminated union to preserve lifetime guarantees.

---

## Dialect Selection: What to Use and Why

### Recommended dialect set for v1

| Dialect | Used For | Why Not Another |
|---------|----------|-----------------|
| `arith` | Integer arithmetic (add, sub, mul, div, cmpi), boolean ops | Standard, well-supported, direct lowering to llvm available |
| `func` | Top-level function definitions, `func.call` for known calls | Clean representation; required by lowering infrastructure |
| `cf` | `cf.br`, `cf.cond_br` for if-else and recursion back-edges | Unstructured flow, works cleanly for if-else; `scf` adds overhead |
| `llvm` | Struct types for closures, `llvm.alloca`, `llvm.getelementptr`, `llvm.store/load`, `llvm.call` for closure invocation | Direct LLVM parity; closures need pointer arithmetic unavailable in higher dialects |

### Why NOT `memref`

`memref` is designed for array/tensor workloads. Its descriptor lowering generates extra struct
boilerplate in LLVM. For closures and environment structs, use `llvm.alloca` +
`llvm.getelementptr` directly. This gives full control over field layout with no hidden overhead.

### Why NOT `scf` (structured control flow)

`scf.if` would work for v1 if-else, but it requires a result value semantics that complicates
tail position handling. `cf.cond_br` is simpler to emit from a recursive AST traversal because
each branch is a separate basic block — the same model LLVM IR uses.

---

## Data Flow: Source to Binary

### Phase 1 — Frontend (reused, not written here)

```
"let f = fun x -> x + 1"
       |  (Lexer.fsl + Parser.fsy)
       v
Let("f", Lambda("x", Add(Var "x", Number 1)), ...)
       |  (Infer.fs / TypeCheck.fs)
       v
Ast.Expr (type-annotated, spans preserved)
```

### Phase 2 — CodegenEnv initialization

Before emitting any MLIR, create:
- `MlirContext` with registered dialects (arith, func, cf, llvm)
- `MlirModule` (a single compilation unit)
- `MlirLocation` (file:line:col, from AST spans)
- `SymbolTable: Map<string, MlirValue>` for SSA variable lookup
- `ClosureEnvTracker` for closure capture analysis (see below)

### Phase 3 — MLIRGen: AST traversal

Traverse `Ast.Expr` with a single recursive function `emitExpr : Expr -> CodegenEnv -> MlirValue`.

```
Number(n)     => arith.constant i64 n
Bool(b)       => arith.constant i1 (0 or 1)
Add(l, r)     => arith.addi (emit l) (emit r)
Sub(l, r)     => arith.subi
Mul(l, r)     => arith.muli
Div(l, r)     => arith.divsi
Equal(l, r)   => arith.cmpi "eq"
LessThan      => arith.cmpi "slt"
And(l, r)     => arith.andi  (boolean AND on i1)
Or(l, r)      => arith.ori
If(c, t, f)   => cf.cond_br (see if-else pattern below)
Var(name)     => look up SymbolTable
Let(n, v, b)  => emit v, bind n -> result in SymbolTable, emit b
LetRec(...)   => emit forward-declared func.func (see recursion pattern below)
Lambda(p, b)  => closure allocation (see closure pattern below)
App(fn, arg)  => indirect call through closure struct (see closure pattern below)
```

### Phase 4 — Lowering

Run the pass pipeline in order:
```
mlirRegisterAllPasses()
pipeline = "convert-arith-to-llvm,convert-func-to-llvm,convert-cf-to-llvm,reconcile-unrealized-casts"
mlirPassManagerRunOnOp(pm, module)
```

All conversion patterns ship with MLIR; no custom lowering passes needed for v1.

### Phase 5 — Object file emission

```fsharp
// Translate MLIR module to LLVM IR (in-memory)
let llvmModule = mlirTranslateModuleToLLVMIR mlirModule context

// Initialize target machine (x86-64, host CPU)
mlirTargetMachineEmitToFile tm llvmModule "output.o" MlirCodegenFileType.ObjectFile
```

### Phase 6 — Linking

```bash
clang output.o -o program
# Or: lld -flavor gnu output.o -lc -dynamic-linker /lib64/ld-linux-x86-64.so.2 -o program
```

clang is preferred because it handles the libc startup dance automatically.

---

## Closure Representation Strategy

### The Problem

`fun x -> x + captured_var` is a first-class value. It must be passable as a function argument
and callable without knowing at the call site whether it is a closure or a plain function. MLIR
has no built-in closure type.

### Recommended: Flat struct with function pointer (uniform representation)

Every lambda — even those with no captures — is represented as an LLVM struct:

```
%ClosureEnv_f = type { ptr, i64 }    ; [fn_ptr, capture_0, capture_1, ...]
```

Where:
- Field 0: `ptr` — points to the implementation function (`fn_ptr`)
- Fields 1..N: captured variable values (by copy for immutable bindings, by pointer for mutable)

The implementation function always takes the closure struct pointer as its first argument
(the "environment parameter"), followed by the actual user arguments:

```llvm
; Implementation function for: fun x -> x + y   (captures y)
define i64 @closure_impl_0(ptr %env, i64 %x) {
  %y_ptr = getelementptr %ClosureEnv_0, ptr %env, i32 0, i32 1
  %y = load i64, ptr %y_ptr
  %result = add i64 %x, %y
  ret i64 %result
}
```

Calling a closure (App node):
```llvm
; %closure_ptr is the closure struct pointer
%fn_ptr_ptr = getelementptr %ClosureType, ptr %closure_ptr, i32 0, i32 0
%fn_ptr = load ptr, ptr %fn_ptr_ptr
%result = call i64 %fn_ptr(ptr %closure_ptr, i64 %arg)
```

### Why this approach

- **Uniform calling convention**: all function values have the same type (`ptr`), making
  higher-order functions straightforward. No special-casing of closures vs. top-level functions.
- **Stack allocation by default**: `llvm.alloca` for the closure struct means no GC needed for
  v1. Closures that escape (returned from function) need heap allocation — defer to v2.
- **No MLIR closure type needed**: pure LLVM dialect structs, well-supported in lowering.
- **Matches SpeakEZ/Fidelity pattern**: the F#-to-native compiler using MLIR uses exactly this
  approach (flat closure, thunk takes `ptr` to its containing struct as first arg).

### Closure allocation in MLIR (before lowering)

```
; In llvm dialect (before lowering to LLVM IR)
%env = llvm.alloca i8 * sizeof(ClosureStruct) : !llvm.ptr
%fn_ptr_field = llvm.getelementptr %env[0, 0] : (!llvm.ptr) -> !llvm.ptr
llvm.store @closure_impl_0 : !llvm.ptr, %fn_ptr_field
%capture_field = llvm.getelementptr %env[0, 1] : (!llvm.ptr) -> !llvm.ptr
llvm.store %captured_val, %capture_field
; Now %env is the closure value — pass it around as i8*
```

### Escape analysis for v1

For v1, use a conservative rule: closures returned from functions or passed to other functions
are heap-allocated via `llvm.call @malloc`. Pure local closures (not escaping) use stack
`llvm.alloca`. Correct analysis can be improved in v2.

---

## If-Else Pattern

The standard approach using `cf` dialect basic blocks:

```
; MLIR basic blocks for: if cond then t_expr else f_expr
%cond = arith.cmpi ...         ; i1 value
cf.cond_br %cond, ^then_bb, ^else_bb

^then_bb:
  %t_result = ... (emit t_expr)
  cf.br ^merge_bb(%t_result : i64)

^else_bb:
  %f_result = ... (emit f_expr)
  cf.br ^merge_bb(%f_result : i64)

^merge_bb(%result : i64):
  ; %result is the if-else value
```

Both branches must produce a value of the same type. The merge block uses a block argument
(MLIR's equivalent of a phi node) to receive the selected value.

---

## Let and LetRec Patterns

### Let binding

`let x = expr in body` is purely an SSA binding — no stack slot needed:

```fsharp
let xVal = emitExpr expr env
let env' = { env with SymbolTable = env.SymbolTable |> Map.add name xVal }
emitExpr body env'
```

No MLIR op is emitted for `let` itself; it just extends the symbol table.

### LetRec (recursive function)

`let rec f x = body in inExpr` requires `f` to be callable from within `body`. Strategy:

1. Emit a forward-declared `func.func @f` with the correct signature (including closure ptr arg)
2. Emit the function body referencing `@f` by symbol name (not SSA value)
3. Bind `f` in the environment as a closure struct pointing to `@f`
4. Emit `inExpr` with `f` in scope

In MLIR, `func.func` is a symbol — it can reference itself by name inside its own body without
SSA capture. This makes `let rec` straightforward: the recursive call is a `func.call @f`
(not an indirect closure call), which lowers cleanly.

For the external view, `f` is still wrapped in a closure struct so it can be passed as a
first-class value. The closure's fn_ptr field points to the `func.func @f` symbol.

---

## F# P/Invoke Binding Structure

### Layer structure

```
libMLIR.so (C++ MLIR runtime)
      ^
      | DllImport P/Invoke
      |
MlirBindings.fs      -- raw extern declarations, opaque handle types
      ^
      | thin wrapper, ownership tracking
      |
MlirDsl.fs           -- F#-idiomatic builders (computation expressions optional)
      ^
      | business logic
      |
CodegenEnv.fs        -- symbol table, closure tracking, context lifecycle
      ^
      |
MLIRGen.fs           -- AST traversal, emits MLIR ops
```

### Opaque handle representation

MLIR C API exposes all objects as opaque `{ ptr }` structs in C. In F#:

```fsharp
// Raw handle type — wrap in struct to prevent confusion
[<Struct>] type MlirContext = { Ptr: nativeint }
[<Struct>] type MlirModule  = { Ptr: nativeint }
[<Struct>] type MlirValue   = { Ptr: nativeint }
[<Struct>] type MlirBlock   = { Ptr: nativeint }
[<Struct>] type MlirType    = { Ptr: nativeint }
[<Struct>] type MlirLocation = { Ptr: nativeint }
```

### Ownership rules (from MLIR C API)

- `mlirXCreate*` / `mlirXGet*`: caller owns the result, must call `mlirXDestroy`
- Operations inserted into a block are owned by that block; do not destroy separately
- Contexts own types and attributes — destroy context last
- In F#: use `IDisposable` wrappers or explicit `use` bindings for owned handles

### Key P/Invoke declarations (examples)

```fsharp
[<DllImport("libMLIR.so")>]
extern MlirContext mlirContextCreate()

[<DllImport("libMLIR.so")>]
extern void mlirContextDestroy(MlirContext ctx)

[<DllImport("libMLIR.so")>]
extern void mlirRegisterAllDialects(MlirContext ctx)

[<DllImport("libMLIR.so")>]
extern MlirModule mlirModuleCreateEmpty(MlirLocation loc)

[<DllImport("libMLIR.so")>]
extern MlirOperation mlirOperationCreate(MlirOperationState& state)

[<DllImport("libMLIR.so")>]
extern MlirPassManager mlirPassManagerCreate(MlirContext ctx)

[<DllImport("libMLIR.so")>]
extern MlirLogicalResult mlirPassManagerRunOnOp(MlirPassManager pm, MlirOperation op)
```

String arguments to MLIR C API use `MlirStringRef` (ptr + length), not null-terminated:

```fsharp
[<Struct>]
type MlirStringRef = {
    Data: nativeint   // const char*
    Length: unativeint
}
// Helper: pin an F# string and pass as MlirStringRef
```

---

## Build Order (Phase Dependencies)

The following build order is dictated by hard dependencies — each layer requires the one above
it to be working before it can be validated:

```
Phase 1: MLIR C API P/Invoke Bindings
  - MlirBindings.fs: context, module, location, block, type, op creation
  - Smoke test: create context, register dialects, destroy context
  - No AST involvement yet

Phase 2: Integer/Arithmetic Codegen
  - Requires: Phase 1
  - Emit arith.constant, arith.addi/subi/muli/divsi for Number and arithmetic Expr nodes
  - Requires lowering pipeline to LLVM and object file emission to be working
  - E2E test: compile `1 + 2` to binary, run, check exit code 3

Phase 3: Let Binding and Variables
  - Requires: Phase 2
  - Symbol table management in CodegenEnv
  - Emit for Var, Let nodes (pure SSA, no new MLIR ops)
  - E2E test: `let x = 5 in x * 2` → 10

Phase 4: Boolean, Comparisons, If-Else
  - Requires: Phase 3
  - arith.constant i1, arith.cmpi, arith.andi/ori
  - cf.cond_br + basic block structure
  - E2E test: `if 3 > 2 then 1 else 0` → 1

Phase 5: Functions, Lambda, Application
  - Requires: Phase 4
  - func.func for top-level functions
  - Closure struct (llvm dialect) for lambda values
  - App → indirect call through closure ptr
  - E2E test: `(fun x -> x + 1) 5` → 6

Phase 6: LetRec (Recursive Functions)
  - Requires: Phase 5
  - Forward-declared func.func with self-reference by symbol name
  - E2E test: `let rec fact n = if n = 0 then 1 else n * fact (n-1) in fact 5` → 120

Phase 7: CLI
  - Requires: Phase 6
  - Read .lt file, parse (LangThree), codegen, write .o, invoke clang
  - E2E test: compile file, execute, check stdout/exit code
```

**Critical path**: Phase 1 → Phase 2 → Phase 5 → Phase 6. Everything else hangs off this chain.
Phase 2 must include a working E2E emission path (lowering + object file + link) before
anything else is useful to test. Do not defer the linking step to Phase 7.

---

## Patterns to Follow

### Pattern 1: Emit-Before-Insert

Create an `MlirOperationState`, populate its operands/result types/attributes, then call
`mlirOperationCreate`, then insert into the current block. Never mutate an operation after
insertion — MLIR operations are immutable once created.

### Pattern 2: Block-Centric Emission

The `CodegenEnv` tracks a "current insertion block". All ops are appended to this block.
When emitting if-else: create then/else/merge blocks, switch insertion point, emit each branch,
restore to merge block. This mirrors how LLVM IR builders work.

```fsharp
type CodegenEnv = {
    Context: MlirContext
    Module: MlirModule
    Builder: MlirOpBuilder       // wraps current insertion point
    SymbolTable: Map<string, MlirValue>
    CurrentFunc: MlirBlock option
}
```

### Pattern 3: Type-Directed Emission

Carry type information from the type checker through codegen. The AST spans contain enough
information; the Hindley-Milner type checker resolves all types before codegen runs.
In codegen, `int` → `i64` (not i32 — avoids sign-extension issues on x86-64),
`bool` → `i1`. No boxing for v1.

### Pattern 4: Entry Point Convention

The compiled module emits a single `func.func @main() -> i64` (for expressions) or
`func.func @main(i32, ptr) -> i32` (for a full program). The CLI wraps the user expression
in a `main` function that returns the computed value. The test harness checks the exit code.

---

## Anti-Patterns to Avoid

### Anti-Pattern 1: Premature Custom Dialect

**What:** Define a custom `funlang` dialect with custom ops (e.g., `funlang.let`, `funlang.app`)
before validating the pipeline end-to-end.

**Why bad:** Custom dialect ops require custom lowering passes. Bugs in lowering are hard to
distinguish from bugs in codegen. For v1 with simple features, using standard dialects
(arith/func/cf/llvm) directly is sufficient and testable immediately.

**Instead:** Emit standard dialect ops directly from the AST. A custom dialect is a v2+
optimization concern for source-level transforms.

### Anti-Pattern 2: Heap-Only Closures

**What:** Unconditionally malloc every closure, mirroring a GC'd runtime.

**Why bad:** Introduces malloc/free into v1, requires either a memory leak or a manual free
discipline. Adds runtime complexity before the basics work.

**Instead:** Stack-allocate all closures for v1 (they don't escape in simple call-and-return
programs). Document the escape limitation; revisit in v2.

### Anti-Pattern 3: Deep P/Invoke Abstraction Too Early

**What:** Build a full F# DSL (computation expressions, CE builders) over MLIR C API before
any codegen works.

**Why bad:** The abstraction boundary is unknown until you've emitted a few real IR constructs.
Over-abstracting early produces leaky wrappers that must be refactored.

**Instead:** Start with thin `extern` declarations and direct calls in MlirGen.fs. Extract
helpers only when a pattern repeats 3+ times.

### Anti-Pattern 4: String-Based IR Construction

**What:** Build MLIR by printing textual IR into a string and calling `mlirModuleCreateParse`.

**Why bad:** Parsing textual IR is 10-100x slower than building via C API. More importantly,
it loses source location information and makes incremental IR construction impossible.

**Instead:** Use the C API builder path exclusively for production codegen. Textual IR is
useful only for debugging (pretty-print the built module to verify it).

### Anti-Pattern 5: Deferring Linking to the End

**What:** Build all codegen stages before proving the object file can be linked and run.

**Why bad:** The MLIR → LLVM → object file → link chain has multiple failure modes
(missing symbol prefixes, calling convention mismatches, startup routine issues) that are
unrelated to your IR logic. Discovering these late means debugging two problems at once.

**Instead:** In Phase 2, emit the simplest possible program (`return 42`) and verify the full
pipeline works before building more IR.

---

## Scalability Considerations

This is a compiler tool, not a service. "Scalability" means compile-time performance for
growing source files, not concurrent users.

| Concern | At 100 LOC | At 10K LOC | At 100K LOC |
|---------|-----------|-----------|------------|
| AST size | Negligible | Small | Track allocation |
| MLIR module size | Negligible | Moderate | May need module splitting |
| Pass pipeline time | < 100ms | < 1s | Profile pass costs |
| Closure env size | Stack fine | Stack fine | Escape analysis needed |
| Symbol table lookup | O(log n) Map fine | Fine | Fine |

For v1 scope (expressions + small programs), none of this matters.

---

## Sources

- [MLIR C API official documentation](https://mlir.llvm.org/docs/CAPI/) — handle ownership, naming conventions (HIGH confidence)
- [MLIR arith Dialect](https://mlir.llvm.org/docs/Dialects/ArithOps/) — integer operations available (HIGH confidence)
- [MLIR func Dialect](https://mlir.llvm.org/docs/Dialects/Func/) — function definition and call ops (HIGH confidence)
- [MLIR llvm Dialect](https://mlir.llvm.org/docs/Dialects/LLVM/) — struct types, GEP, alloca (HIGH confidence)
- [MLIR LLVM IR Target](https://mlir.llvm.org/docs/TargetLLVMIR/) — translation to LLVM IR (HIGH confidence)
- [MLIR Toy Tutorial Ch. 2 — Emitting MLIR from AST](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-2/) — visitor pattern, direct lowering approach (HIGH confidence)
- [MLIR Toy Tutorial Ch. 6 — Lowering to LLVM](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-6/) — pass pipeline pattern (HIGH confidence)
- [MLIR Dialect Conversion](https://mlir.llvm.org/docs/DialectConversion/) — built-in conversion infrastructure (HIGH confidence)
- [SpeakEZ — Gaining Closure](https://speakez.tech/blog/gaining-closure/) — flat closure representation, F#+MLIR practice (HIGH confidence)
- [SpeakEZ — Why F# is a Natural Fit for MLIR](https://speakez.tech/blog/why-fsharp-is-a-natural-fit-for-mlir/) — P/Invoke architecture (MEDIUM confidence, no source code visible)
- [fsharp-mlir-hello proof of concept](https://github.com/speakeztech/fsharp-mlir-hello) — F# + MLIR C API integration example (HIGH confidence)
- [MLIR discourse — Closure Op in MLIR](https://discourse.llvm.org/t/closure-op-in-mlir/83817) — confirms no native closure op exists (HIGH confidence)
- [Mapping High-Level Constructs to LLVM IR — Lambda Functions](https://mapping-high-level-constructs-to-llvm-ir.readthedocs.io/en/latest/advanced-constructs/lambda-functions.html) — canonical closure struct + fn pointer pattern (HIGH confidence)
- [Jeremy Kun — MLIR Lowering through LLVM](https://www.jeremykun.com/2023/11/01/mlir-lowering-through-llvm/) — practical lowering pipeline walkthrough (HIGH confidence)
- [Intro to Structures in LLVM/MLIR (Medium)](https://medium.com/@60b36t/structures-in-llvm-and-how-to-emit-a-structure-using-mlir-497f5132914e) — llvm.getelementptr usage (MEDIUM confidence)
