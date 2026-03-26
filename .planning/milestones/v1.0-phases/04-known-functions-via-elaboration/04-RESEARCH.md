# Phase 4: Known Functions via Elaboration - Research

**Researched:** 2026-03-26
**Domain:** MLIR func dialect (func.func / func.call), recursive function elaboration, LangThree LetRec AST
**Confidence:** HIGH

---

## Summary

Phase 4 extends the compiler with recursive function support via MLIR's `func` dialect. The key additions are: `DirectCallOp` in `MlirIR`, a `KnownFuncs` map in `ElabEnv`, and handling for `LetRec` and `App` in `Elaboration.fs`. The `MlirModule.Funcs` field already supports multiple `FuncOp`s (it's a list), and the `Printer.fs` already iterates over them — so the module structure requires no changes.

All MLIR patterns have been verified end-to-end on this system (LLVM 20.1.4): multi-function modules with `@main` + `@fact`, recursive `func.call` inside a multi-block function body, and the full pipeline to binary. The `func` dialect requires `--convert-func-to-llvm` which is already present in `Pipeline.loweringPasses`. The only constraint is exit-code range: `fact(10) = 3628800` wraps to 0 mod 256, so FsLit tests must use `fact 5` (exits 120) and `fib 10` (exits 55).

The LangThree parser's `LetRec` case (single-parameter recursive binding) is already implemented. Parsing requires the explicit `in` keyword on one line with no trailing newline in the input file (the IndentFilter rejects trailing newlines for top-level expressions). The elaboration gap is entirely in `Elaboration.fs` — currently falls through to `failwithf "unsupported expression LetRec"` and `failwithf "unsupported expression App"`.

**Primary recommendation:** Add `DirectCallOp` to `MlirOp`, add `KnownFuncs` + `Funcs` to `ElabEnv`, and implement `LetRec` + `App` in `elaborateExpr`. All in one plan (04-01) — these changes are tightly coupled.

---

## Standard Stack

### Core

| Tool / Module | Version | Purpose | Why Standard |
|---------------|---------|---------|--------------|
| `func` MLIR dialect | LLVM 20.1.4 | `func.func` function definitions, `func.call` direct calls | Already used for `@main`; `--convert-func-to-llvm` already in Pipeline |
| `arith` + `cf` MLIR dialects | LLVM 20.1.4 | Recursive function body uses if-else (cf) and arithmetic (arith) | Already in Pipeline.loweringPasses |
| `MlirIR.fs` (this project) | Phase 4 additions | Add `DirectCallOp` case to `MlirOp` DU | Same extensible DU pattern from Phases 1-3 |
| `Elaboration.fs` | Phase 4 extensions | Handle `LetRec` and `App`; extend `ElabEnv` with `KnownFuncs` + `Funcs` | Extended in place; no new files needed |
| `LangThree Ast.fs` | project ref | `LetRec(name, param, body, inExpr, span)` and `App(func, arg, span)` already defined | Phase 5 section of Ast.Expr DU |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `FsLit (fslit)` | — | E2E test runner | FsLit `.flt` tests for factorial and fibonacci |
| `mlir-opt` LLVM 20 | 20.1.4 | Validates lowered MLIR; already in Pipeline | No pipeline change needed |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `KnownFuncs: Map<string, FuncSignature>` in ElabEnv | Bind function name to a sentinel `MlirValue` in `Vars` | Sentinel approach pollutes `Vars` with non-SSA entries; separate map is explicit and type-safe |
| `Funcs: FuncOp list ref` in ElabEnv | Return `FuncOp list` from `elaborateExpr` | Changing `elaborateExpr`'s return type breaks all existing call sites; mutable ref preserves the `MlirValue * MlirOp list` signature |
| I64 -> I64 hardcoded assumption for Phase 4 | Full type inference | Phase 4 only handles numeric recursion; all known functions take and return i64; deferring type inference is correct scope |

---

## Architecture Patterns

### Recommended Project Structure

```
src/LangBackend.Compiler/
├── MlirIR.fs          # Add DirectCallOp to MlirOp DU
├── Printer.fs         # Add printOp case for DirectCallOp
├── Elaboration.fs     # Extend ElabEnv; handle LetRec and App
└── Pipeline.fs        # Unchanged
tests/compiler/
├── 04-01-fact.flt     # let rec fact n = ... in fact 5  -> exits 120
└── 04-02-fib.flt      # let rec fib n = ... in fib 10  -> exits 55
```

### Pattern 1: New MlirOp Case — DirectCallOp

**What:** `DirectCallOp` represents `func.call @callee(args) : (argTypes) -> retType`. The result is the SSA value produced by the call. The callee is a string (the `@name` including the `@` sigil).

```fsharp
// MlirIR.fs addition for Phase 4
type MlirOp =
    // ... existing cases ...
    | DirectCallOp of result: MlirValue * callee: string * args: MlirValue list
```

The `result.Type` encodes the return type. The arg types are derived from `args` at print time.

### Pattern 2: Printer Case for DirectCallOp

**What:** Emit `func.call @callee(%arg0, %arg1) : (i64, i64) -> i64`.

```fsharp
// Printer.fs — add to printOp match
| DirectCallOp(result, callee, args) ->
    let argNames = args |> List.map (fun v -> v.Name) |> String.concat ", "
    let argTypes = args |> List.map (fun v -> printType v.Type) |> String.concat ", "
    sprintf "%s%s = func.call %s(%s) : (%s) -> %s"
        indent result.Name callee argNames argTypes (printType result.Type)
```

**Verified MLIR output format:**
```mlir
%r = func.call @fact(%arg0) : (i64) -> i64
%r = func.call @add(%a, %b) : (i64, i64) -> i64
```

### Pattern 3: Extended ElabEnv

**What:** Add `KnownFuncs` for looking up function signatures and `Funcs` for accumulating FuncOps discovered during elaboration.

```fsharp
// FuncSignature: enough to elaborate a direct call
type FuncSignature = {
    MlirName:   string      // e.g. "@fact" (with @ sigil)
    ParamTypes: MlirType list
    ReturnType: MlirType
}

type ElabEnv = {
    Vars:         Map<string, MlirValue>
    Counter:      int ref
    LabelCounter: int ref
    Blocks:       MlirBlock list ref
    KnownFuncs:   Map<string, FuncSignature>   // NEW: function name -> signature
    Funcs:        FuncOp list ref              // NEW: accumulated FuncOps
}

let emptyEnv () : ElabEnv =
    { Vars         = Map.empty
      Counter      = ref 0
      LabelCounter = ref 0
      Blocks       = ref []
      KnownFuncs   = Map.empty
      Funcs        = ref [] }
```

### Pattern 4: elaborateExpr for LetRec

**What:** When `LetRec(name, param, body, inExpr, _)` is encountered:
1. Build a **fresh** `ElabEnv` for the function body (fresh counters, Blocks, BUT shares the parent's `Funcs` ref so nested let-rec accumulates into the same list).
2. Bind `param` to `%arg0: i64` (Phase 4: all params are I64).
3. Forward-declare `name` in `KnownFuncs` so the body can make recursive calls.
4. Elaborate the body with the fresh env.
5. Assemble `MlirRegion` from the fresh env's blocks (same multi-block logic as `elaborateModule`).
6. Create `FuncOp` and append to the parent env's `Funcs` ref.
7. Add `name` to the **parent** env's `KnownFuncs`.
8. Elaborate `inExpr` with the updated parent env.
9. Return result of `inExpr`.

```fsharp
| LetRec (name, param, body, inExpr, _) ->
    // 1. Build fresh env for function body — shares Funcs accumulator with parent
    let arg0 = { Name = "%arg0"; Type = I64 }
    let funcSig = { MlirName = "@" + name; ParamTypes = [I64]; ReturnType = I64 }
    let bodyEnv = {
        Vars         = Map.ofList [ (param, arg0) ]
        Counter      = ref 0
        LabelCounter = ref 0
        Blocks       = ref []
        KnownFuncs   = Map.ofList [ (name, funcSig) ]  // forward-declare for recursion
        Funcs        = env.Funcs   // share parent's accumulator
    }
    // 2. Elaborate function body
    let (bodyVal, bodyEntryOps) = elaborateExpr bodyEnv body
    let bodySideBlocks = bodyEnv.Blocks.Value
    // 3. Assemble MlirRegion (same multi-block logic as elaborateModule)
    let allBodyBlocks =
        if bodySideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = bodyEntryOps @ [ReturnOp [bodyVal]] } ]
        else
            let entryBlock = { Label = None; Args = []; Body = bodyEntryOps }
            let lastBlock = List.last bodySideBlocks
            let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [ReturnOp [bodyVal]] }
            let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    // 4. Create FuncOp and add to accumulator
    let funcOp = {
        Name       = "@" + name
        InputTypes = [I64]
        ReturnType = Some bodyVal.Type
        Body       = { Blocks = allBodyBlocks }
    }
    env.Funcs.Value <- env.Funcs.Value @ [funcOp]
    // 5. Add to parent env's KnownFuncs for use in inExpr
    let env' = { env with KnownFuncs = Map.add name funcSig env.KnownFuncs }
    // 6. Elaborate inExpr with updated env
    elaborateExpr env' inExpr
```

**Key design decision:** The function body env has **no outer Vars** — only `param`. This enforces "no free variables beyond the recursion variable". If the body references other outer variables, elaboration fails with "unbound variable" (correct behavior for Phase 4 scope).

### Pattern 5: elaborateExpr for App

**What:** `App(func, arg, _)` where `func` is `Var(name)` and `name` is in `KnownFuncs`.

```fsharp
| App (funcExpr, argExpr, _) ->
    match funcExpr with
    | Var (name, _) when Map.containsKey name env.KnownFuncs ->
        let sig = env.KnownFuncs.[name]
        let (argVal, argOps) = elaborateExpr env argExpr
        let result = { Name = freshName env; Type = sig.ReturnType }
        (result, argOps @ [DirectCallOp(result, sig.MlirName, [argVal])])
    | _ ->
        failwithf "Elaboration: unsupported App (only direct calls to known functions supported)"
```

### Pattern 6: elaborateModule Update

**What:** Initialize `Funcs` ref in the env, then collect extra FuncOps after elaborating the expression.

```fsharp
let elaborateModule (expr: Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, entryOps) = elaborateExpr env expr
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = entryOps @ [ReturnOp [resultVal]] } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [ReturnOp [resultVal]] }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    let mainFunc = {
        Name       = "@main"
        InputTypes = []
        ReturnType = Some resultVal.Type
        Body       = { Blocks = allBlocks }
    }
    // Collect extra FuncOps (from LetRec elaboration) + @main last
    { Funcs = env.Funcs.Value @ [mainFunc] }
```

### Pattern 7: FsLit Test Syntax

**What:** FsLit tests must use single-line `let rec ... in ...` syntax without trailing newlines. The LangThree IndentFilter rejects trailing newlines for top-level expressions.

```
// --- Input:
let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 5
// --- Output:
120
```

```
// --- Input:
let rec fib n = if n <= 1 then n else fib (n - 1) + fib (n - 2) in fib 10
// --- Output:
55
```

**Note:** The input in FsLit `.flt` files does NOT have a trailing newline — the test harness provides the content as-is via `%input`.

### Anti-Patterns to Avoid

- **Using `fact 10` as a success criterion exit code:** `fact(10) = 3628800`, and `3628800 mod 256 = 0`, so the binary exits 0 — indistinguishable from a crash. Use `fact 5 = 120` or `fib 10 = 55` instead.
- **Sharing `Blocks` ref between parent env and function body env:** The `Blocks` accumulator is per-function. A fresh body env must have `Blocks = ref []`. Only `Funcs` is shared.
- **Adding outer `Vars` to the function body env:** Phase 4 constraint is "no free variables". Keeping body env `Vars` to only `{ param -> %arg0 }` enforces this and avoids accidentally compiling closures.
- **Forgetting to forward-declare `name` in the body env's `KnownFuncs`:** Without this, recursive calls (`fact (n-1)`) fall through to the `_ -> failwithf` in App elaboration.
- **Emitting `@fact` functions AFTER `@main` is needed for a call:** MLIR accepts any function declaration order in a module — this is NOT a problem. `env.Funcs.Value @ [mainFunc]` puts helper functions before `@main`, which is the conventional order.
- **Trailing newlines in FsLit input:** `let rec fact n = ... in fact 5\n` (with newline) causes a parse error from the IndentFilter. The FsLit test input section should not end with a blank line.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Function forward declarations | Custom "declare" op type | Just put `@fact` FuncOp before `@main` in `MlirModule.Funcs` | MLIR modules allow forward references; declaration order doesn't matter for `func.call` |
| Type checking LetRec body | A type inference pass | Trust elaboration: if body returns wrong type, Printer/mlir-opt catches it | Phase 4 only needs I64 -> I64; dynamic `bodyVal.Type` from elaboration is sufficient |
| Multi-block region assembly | A separate assembler function | Extract the existing `elaborateModule` multi-block logic into a helper (`assembleRegion`) | The same `sideBlocks.IsEmpty` logic works for both @main and helper functions |

**Key insight:** The existing `MlirModule.Funcs: FuncOp list` field already supports multiple functions — the Printer already iterates them. No structural changes to MlirModule or FuncOp are needed; only new elaboration logic and a new `MlirOp` case.

---

## Common Pitfalls

### Pitfall 1: Exit Code Overflow for Factorial

**What goes wrong:** Test asserts `fact 10` exits with 3628800, but Unix exit codes are mod 256. `fact(10) mod 256 = 0`, so it exits 0 — test passes trivially (wrong).

**Why it happens:** The success criteria in the phase description was written aspirationally without checking exit code range.

**How to avoid:** Use `fact 5 = 120` (fits in 0-255) and `fib 10 = 55` (fits in 0-255) for FsLit tests. Document that larger factorials would require stdout output (deferred to a later phase).

**Warning signs:** FsLit test for `fact 10` always passes even when the function is broken.

### Pitfall 2: Sharing Blocks Accumulator Between Parent and Function Body

**What goes wrong:** If the body env reuses `env.Blocks`, blocks emitted during body elaboration (from `if-else` in the function body) get mixed into the `@main` function's block list.

**Why it happens:** `ElabEnv` is a record, and `Blocks` is a `ref`. A shallow copy shares the ref.

**How to avoid:** Always initialize `Blocks = ref []` in the function body env. Only `Funcs` is shared.

**Warning signs:** `@main` unexpectedly has `^then0`, `^else0`, `^merge0` blocks from the helper function's body.

### Pitfall 3: App Elaboration Hits the Fallthrough Case

**What goes wrong:** `App(Var("fact"), arg)` fails with "unsupported App" even though `fact` is a known function.

**Why it happens:** `KnownFuncs` wasn't updated in the outer env before elaborating `inExpr`, or the `App` match in `elaborateExpr` checks `env.Vars` instead of `env.KnownFuncs`.

**How to avoid:** Ensure the `App` case checks `KnownFuncs` first. Ensure `elaborateExpr` for `LetRec` updates the env passed to `elaborateExpr env' inExpr`.

**Warning signs:** `Elaboration: unsupported App` exception when program has `fact 5` in the `inExpr`.

### Pitfall 4: Parse Error Due to Trailing Newline in Test Input

**What goes wrong:** FsLit test with `let rec fact n = ... in fact 5\n` (trailing newline) causes "parse error" at runtime.

**Why it happens:** The LangThree IndentFilter treats a trailing newline at column 0 as a `DEDENT` that terminates the expression prematurely.

**How to avoid:** FsLit test `.flt` files should have the Input section end immediately with `// --- Output:` on the next line, not a blank line. The FsLit command passes the file content via a temp file — check exactly what `%input` contains.

**Warning signs:** `parse error` exception in the compiler, even for syntactically valid programs.

### Pitfall 5: Function Name in Body Env Vars vs KnownFuncs

**What goes wrong:** Adding `name` to body env's `Vars` (as a `MlirValue`) instead of `KnownFuncs`. Then `App(Var("fact"), ...)` finds it in `Vars`, tries to use it as a `MlirValue` in an arithmetic op, and produces invalid MLIR.

**Why it happens:** Existing Var handling just returns the bound `MlirValue` — no check that it's function-typed.

**How to avoid:** Never add function names to `Vars`. Function names go in `KnownFuncs` only. The `App` case explicitly checks `KnownFuncs`.

---

## Code Examples

Verified patterns from end-to-end tests on this system (LLVM 20.1.4, 2026-03-26):

### Multi-function MLIR module (fact 5 = 120)

```mlir
module {
  func.func @fact(%arg0: i64) -> i64 {
    %t0 = arith.constant 1 : i64
    %t1 = arith.cmpi sle, %arg0, %t0 : i64
    cf.cond_br %t1, ^base0, ^recurse0
  ^base0:
    cf.br ^merge0(%t0 : i64)
  ^recurse0:
    %t2 = arith.subi %arg0, %t0 : i64
    %t3 = func.call @fact(%t2) : (i64) -> i64
    %t4 = arith.muli %arg0, %t3 : i64
    cf.br ^merge0(%t4 : i64)
  ^merge0(%t5 : i64):
    return %t5 : i64
  }
  func.func @main() -> i64 {
    %t0 = arith.constant 5 : i64
    %t1 = func.call @fact(%t0) : (i64) -> i64
    return %t1 : i64
  }
}
// Compiles, runs, exits with 120
```

### Fibonacci MLIR module (fib 10 = 55)

```mlir
module {
  func.func @fib(%arg0: i64) -> i64 {
    %t0 = arith.constant 1 : i64
    %t1 = arith.cmpi sle, %arg0, %t0 : i64
    cf.cond_br %t1, ^base0, ^recurse0
  ^base0:
    cf.br ^merge0(%arg0 : i64)
  ^recurse0:
    %t2 = arith.subi %arg0, %t0 : i64
    %t3 = arith.constant 2 : i64
    %t4 = arith.subi %arg0, %t3 : i64
    %t5 = func.call @fib(%t2) : (i64) -> i64
    %t6 = func.call @fib(%t4) : (i64) -> i64
    %t7 = arith.addi %t5, %t6 : i64
    cf.br ^merge0(%t7 : i64)
  ^merge0(%t8 : i64):
    return %t8 : i64
  }
  func.func @main() -> i64 {
    %t0 = arith.constant 10 : i64
    %t1 = func.call @fib(%t0) : (i64) -> i64
    return %t1 : i64
  }
}
// Compiles, runs, exits with 55
```

### func.call syntax — verified

```mlir
// Single argument:
%result = func.call @fact(%arg0) : (i64) -> i64

// Two arguments:
%result = func.call @add(%a, %b) : (i64, i64) -> i64

// No arguments (not needed in Phase 4 but valid):
%result = func.call @getval() : () -> i64
```

### Function declaration order — any order works

```mlir
module {
  func.func @main() -> i64 {         // @main BEFORE @fact
    %n = arith.constant 10 : i64
    %r = func.call @fact(%n) : (i64) -> i64
    return %r : i64
  }
  func.func @fact(%arg0: i64) -> i64 { ... }  // declared AFTER use
}
// mlir-opt accepts this — forward references are fine
```

### LangThree test syntax — confirmed parseable

```
// This parses correctly (single line, no trailing newline):
let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 5

// This FAILS (trailing newline confuses IndentFilter):
let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 5
[empty line]
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Single FuncOp `@main` only | Multiple FuncOps (helper funcs + `@main`) | Phase 4 | `elaborateModule` collects from `env.Funcs` ref |
| `MlirOp` has arith + cf + return | Adds `DirectCallOp` | Phase 4 | Printer gains one new case |
| `ElabEnv` has Vars, Counter, LabelCounter, Blocks | Adds `KnownFuncs` + `Funcs` | Phase 4 | `emptyEnv()` gains two new fields |
| `elaborateExpr` handles Let (simple binding) | Also handles LetRec and App | Phase 4 | Recursive functions with no free variables compile |
| Success criteria: `fact 10 = 3628800` | Use `fact 5 = 120` and `fib 10 = 55` | Phase 4 planning | Exit code constraint requires values < 256 |

**Deprecated/outdated:**
- The success criterion "binary exits with 3628800" in the context description is incorrect — `3628800 mod 256 = 0`, so `fact 10` would exit 0. Replace with `fact 5` (exits 120).

---

## Open Questions

1. **Multi-parameter functions (curried)**
   - What we know: `LetRec(name, param, body, inExpr)` has one parameter. Multi-param functions in LangThree use currying: `let rec f x y = ...` produces nested Lambdas inside LetRec body.
   - What's unclear: Whether Phase 4 should support curried multi-param functions (would require App chains and nested Lambda handling).
   - Recommendation: Phase 4 scope is single-parameter only. Add `failwithf` for `App` cases where the function is not a simple `Var` in `KnownFuncs`. Document limitation.

2. **Type inference for return type**
   - What we know: `bodyVal.Type` is used dynamically — if the body is an if-else returning I64, it works. If for some reason the body returns I1 (bool), the FuncOp would have `ReturnType = Some I1`.
   - What's unclear: Whether Phase 4 tests ever produce bool-returning known functions.
   - Recommendation: The dynamic approach (`ReturnType = Some bodyVal.Type`) is correct and handles I64 and I1 equally. No hardcoding needed.

3. **Nested LetRec (two recursive functions in scope)**
   - What we know: `let rec f n = ... in let rec g n = ... in g (f 10)` would require two separate FuncOps, with `g` able to call `f`.
   - What's unclear: Whether Phase 4 tests exercise this pattern.
   - Recommendation: The `env.Funcs` sharing approach supports nested LetRec automatically — each LetRec adds to the same `Funcs` accumulator and extends the outer env's `KnownFuncs`. No extra work needed, but verify with a test if it arises.

---

## Sources

### Primary (HIGH confidence — verified e2e on this system, 2026-03-26)

All MLIR patterns tested with `mlir-opt 20.1.4` + `mlir-translate` + `clang`, full pipeline to binary, and executed:

- Multi-function MLIR module (`@main` + `@fact`) — compiles and runs correctly
- Function declaration order (any order) — confirmed: `@main` before `@fact` works
- Recursive `func.call @fact(%nm1) : (i64) -> i64` inside multi-block body — verified
- `fact 5 = 120` exits 120; `fact 10 mod 256 = 0` exits 0 — confirmed
- `fib 10 = 55` exits 55 — confirmed
- `func.call @add(%a, %b) : (i64, i64) -> i64` (multi-arg) — verified exits 7 for 3+4
- LangThree parser accepts `let rec fact n = ... in fact 5` (single line, no trailing newline) and produces `LetRec("fact", "n", If(...), App(Var("fact"), Number(5,...), ...), ...)`
- LangThree parser produces `Elaboration: unsupported expression LetRec` (not parse error) — confirmed the parsing works correctly
- `let rec fib n = ... in fib 10` parses correctly and fails at elaboration only

### Secondary (HIGH confidence — project source)

- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MlirIR.fs` — `MlirModule.Funcs: FuncOp list` already supports multiple funcs; `FuncOp.InputTypes: MlirType list` already supports params; `Printer.printFuncOp` already generates `%arg0`, `%arg1`, etc.
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Printer.fs` — `printModule` already iterates `m.Funcs`; `printFuncOp` already handles `InputTypes`
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — `ElabEnv` structure, `freshName`/`freshLabel` helpers, multi-block assembly in `elaborateModule`, `LetRec` falls to `failwithf`
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Pipeline.fs` — `--convert-func-to-llvm` confirmed in `loweringPasses`; no changes needed
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — `LetRec of name * param * body * inExpr * span` and `App of func * arg * span` confirmed
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Parser.fs` — LetRec grammar rule confirmed: `let rec name params = body in inExpr`; Parser.start expects single `Expr`

---

## Metadata

**Confidence breakdown:**
- func.func / func.call MLIR syntax: HIGH — verified e2e with factorial and fibonacci
- MlirIR DirectCallOp design: HIGH — follows established pattern of other MlirOp cases
- ElabEnv KnownFuncs + Funcs design: HIGH — straightforward extension; mutable ref pattern used in Blocks already
- LetRec elaboration strategy: HIGH — forward-declaration + fresh body env is a standard technique
- App elaboration: HIGH — trivial once KnownFuncs is in place
- Exit code limitation (fact 10 = 0): HIGH — verified with python and binary execution
- FsLit test syntax: HIGH — confirmed parser accepts single-line let rec ... in ... without trailing newline

**Research date:** 2026-03-26
**Valid until:** 2026-04-25 (MLIR 20.x stable; LangThree AST and parser locked)
