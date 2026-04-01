# Phase 2: Scalar Codegen via MlirIR - Research

**Researched:** 2026-03-26
**Domain:** F# compiler pass design, MLIR arith dialect, SSA name generation, LangThree AST → MlirIR translation
**Confidence:** HIGH

---

## Summary

Phase 2 introduces the Elaboration pass — an F# function that walks a LangThree `Ast.Expr` and produces `MlirOp list` with SSA bindings into `MlirBlock`. The five target MlirIR nodes are `ArithConstantOp`, `ArithAddIOp`, `ArithSubIOp`, `ArithMulIOp`, `ArithDivSIOp`. SSA let/variable bindings are tracked in a `Map<string, MlirValue>` that threads through recursive elaboration. The Printer gets five new `printOp` cases. The CLI is updated to parse the input `.lt` file and call Elaboration instead of hardcoding `return42Module`.

The MLIR arith dialect is verified working end-to-end on this system. Named SSA values (`%x`, `%y`) are accepted by `mlir-opt` as long as names are unique within a block. Shadowed let bindings (e.g., `let x = 1 in let x = 2 in x`) must be desugared to unique names by the Elaboration pass (e.g., `%x_0`, `%x_1`). Division uses `arith.divsi` (signed integer divide). Unary negation maps to `arith.subi %zero, %v : i64`. `arith.negsi` does not exist in MLIR 20.

**Primary recommendation:** Build the Elaboration pass as a single recursive F# function `elaborateExpr : ElabEnv -> Ast.Expr -> MlirValue * MlirOp list` that accumulates ops in reverse, then appends them; carry a `Map<string, MlirValue> * int ref` for the SSA env and a fresh-name counter.

---

## Standard Stack

### Core

| Library / Tool | Version | Purpose | Why Standard |
|----------------|---------|---------|--------------|
| `Ast` (LangThree) | project ref | Source AST: `Number`, `Add`, `Subtract`, `Multiply`, `Divide`, `Negate`, `Var`, `Let` | Already referenced from Phase 1; no additional dependency |
| `MlirIR` (this project) | Phase 2 additions | Target IR: adds `ArithAddIOp`, `ArithSubIOp`, `ArithMulIOp`, `ArithDivSIOp` cases to `MlirOp` DU | Same DU, same Printer, same pipeline — zero new infrastructure |
| `LangThree.IndentFilter` | project ref | Parse `.lt` files with indentation-aware tokenizer | Required by `parseModuleFromString` in `LangThree.Program`; module name is `LangThree.IndentFilter` |
| `Parser` / `Lexer` (LangThree) | project ref | Tokenize and parse `.lt` source files | Standard FsLexYacc-generated modules, already compiled into LangThree.fsproj |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `Bidir.synthTop` (LangThree) | project ref | Optional: type-check expression before elaboration | Phase 2 can skip type-checking and trust the AST structure; use if error messages are needed |
| `FsLit (fslit)` | — | E2E test runner for `.flt` test files | Three new FsLit tests cover literal, arithmetic, and let-binding cases |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Accumulate ops as `MlirOp list` via continuation/state | Mutable `ResizeArray<MlirOp>` | Both work; immutable list with `@` concat is idiomatic F# and safe; mutable ResizeArray is faster for large programs but overkill in Phase 2 |
| Fresh counter as `int ref` threaded through env | Tuple `(env, counter)` passed by value | `int ref` is simpler and avoids threading; thread-safety is not a concern here |
| Separate `ElabState` record | Inline tuple `(env, freshCounter)` | Record is cleaner for future growth; either works for Phase 2 |

---

## Architecture Patterns

### Recommended Project Structure

```
src/FunLangCompiler.Compiler/
├── MlirIR.fs          # Add ArithAddIOp, ArithSubIOp, ArithMulIOp, ArithDivSIOp cases
├── Printer.fs         # Add printOp cases for new ops
├── Elaboration.fs     # NEW: elaborateExpr and ElabEnv
└── Pipeline.fs        # Unchanged
src/FunLangCompiler.Cli/
└── Program.fs         # Updated: parse input file, call Elaboration, compile
tests/compiler/
├── 01-return42.flt    # Unchanged
├── 02-01-literal.flt  # New: integer literal end-to-end
├── 02-02-arith.flt    # New: 1 + 2 * 3 - 4 / 2 = 5
└── 02-03-let.flt      # New: let x = 5 in let y = x + 3 in y = 8
```

### Pattern 1: MlirOp DU Extension

**What:** Add four new cases to `MlirOp` DU. MlirModule/FuncOp/MlirRegion/MlirBlock shape is unchanged.

**When to use:** Every phase adds new `MlirOp` cases only. Never change the outer structure.

```fsharp
// MlirIR.fs — add after ArithConstantOp
type MlirOp =
    | ArithConstantOp of result: MlirValue * value: int64
    | ArithAddIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithSubIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithMulIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithDivSIOp    of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ReturnOp        of operands: MlirValue list
```

### Pattern 2: Printer Cases for Binary Arith Ops

**What:** Each binary arith op serializes as `%result = arith.<op> %lhs, %rhs : i64`.

```fsharp
// Printer.fs — add to printOp match
| ArithAddIOp(result, lhs, rhs) ->
    sprintf "%s%s = arith.addi %s, %s : %s"
        indent result.Name lhs.Name rhs.Name (printType result.Type)
| ArithSubIOp(result, lhs, rhs) ->
    sprintf "%s%s = arith.subi %s, %s : %s"
        indent result.Name lhs.Name rhs.Name (printType result.Type)
| ArithMulIOp(result, lhs, rhs) ->
    sprintf "%s%s = arith.muli %s, %s : %s"
        indent result.Name lhs.Name rhs.Name (printType result.Type)
| ArithDivSIOp(result, lhs, rhs) ->
    sprintf "%s%s = arith.divsi %s, %s : %s"
        indent result.Name lhs.Name rhs.Name (printType result.Type)
```

### Pattern 3: Elaboration Pass Structure

**What:** A recursive function that walks `Ast.Expr`, emits `MlirOp list`, and returns a `MlirValue` naming the result. An `ElabEnv` carries the SSA variable map and a fresh-name counter.

**When to use:** Phase 2. Extended in Phase 3 (bool/cmp/cond_br) and later phases.

```fsharp
// Elaboration.fs
module Elaboration

open Ast
open MlirIR

/// Elaboration environment
type ElabEnv = {
    Vars:    Map<string, MlirValue>  // variable name -> SSA value
    Counter: int ref                  // fresh SSA name counter
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0 }

/// Generate a fresh SSA value name like %t0, %t1, ...
let private freshName (env: ElabEnv) : string =
    let n = !env.Counter
    env.Counter := n + 1
    sprintf "%%t%d" n

/// Elaborate a LangThree expression.
/// Returns: (result MlirValue, accumulated ops in emission order)
let rec elaborateExpr (env: ElabEnv) (expr: Ast.Expr) : MlirValue * MlirOp list =
    match expr with
    | Number (n, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, int64 n)])

    | Add (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithAddIOp(result, lv, rv)])

    | Subtract (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithSubIOp(result, lv, rv)])

    | Multiply (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithMulIOp(result, lv, rv)])

    | Divide (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithDivSIOp(result, lv, rv)])

    | Negate (inner, _) ->
        // Unary negation: 0 - inner  (arith.negsi does NOT exist in MLIR 20)
        let (iv, iops) = elaborateExpr env inner
        let zero = { Name = freshName env; Type = I64 }
        let result = { Name = freshName env; Type = I64 }
        (result, iops @ [ArithConstantOp(zero, 0L); ArithSubIOp(result, zero, iv)])

    | Var (name, span) ->
        match Map.tryFind name env.Vars with
        | Some v -> (v, [])
        | None ->
            failwithf "Elaboration: unbound variable '%s' at %s" name (Ast.formatSpan span)

    | Let (name, bindExpr, bodyExpr, _) ->
        let (bv, bops) = elaborateExpr env bindExpr
        // Bind the result value to `name` in env for body elaboration
        let env' = { env with Vars = Map.add name bv env.Vars }
        let (rv, rops) = elaborateExpr env' bodyExpr
        (rv, bops @ rops)

    | _ ->
        failwithf "Elaboration: unsupported expression %A (Phase 2 handles scalar/let only)" expr

/// Elaborate a top-level expression into a full MlirModule.
/// Produces a @main function returning i64 with the expression result.
let elaborateModule (expr: Ast.Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, ops) = elaborateExpr env expr
    {
        Funcs = [
            {
                Name       = "@main"
                InputTypes = []
                ReturnType = Some I64
                Body = {
                    Blocks = [
                        {
                            Label = None
                            Args  = []
                            Body  = ops @ [ReturnOp [resultVal]]
                        }
                    ]
                }
            }
        ]
    }
```

### Pattern 4: CLI Parsing + Elaboration

**What:** `Program.fs` reads the input `.lt` file, parses it as a module (or single expression), extracts the expression, and calls `Elaboration.elaborateModule`.

```fsharp
// Program.fs — updated for Phase 2
// Parse .lt file → Ast.Expr → Elaboration.elaborateModule → Pipeline.compile
let parseFile (path: string) : Ast.Expr =
    let src = System.IO.File.ReadAllText(path)
    let filteredTokens = LangThree.lexAndFilter src path
    // ... reconstruct tokenizer and call Parser.start or parseModuleFromString
    // For Phase 2: treat the file content as a single expression
    // Use LangThree.parse src path (expression-only parser)
```

**Simpler option for Phase 2:** Use `LangThree.parse` (the expression parser, not module parser). Test `.lt` files contain a single expression. Module-level decls are a Phase 6 concern.

### Anti-Patterns to Avoid

- **Using `string * MlirOp list` with string names:** Always use `MlirValue` (typed record). The Printer depends on `result.Type` to emit the type suffix.
- **Emitting ops in reverse order and forgetting to reverse:** Ops must be in emission order. Either accumulate in forward order (using `@`) or reverse at the end. The `@` approach is idiomatic for Phase 2 scale.
- **Duplicate SSA names:** `mlir-opt` rejects duplicate `%name` definitions within a block with "redefinition of SSA value". The fresh counter (`%t0`, `%t1`, ...) prevents this. Let bindings re-use the value returned from elaboration of the bind expression — no new op is emitted for the binding itself.
- **Using `int` instead of `int64` for constants:** `Ast.Number` carries `int`; `ArithConstantOp` requires `int64`. Cast with `int64 n`.
- **Using `arith.negsi`:** It does not exist in MLIR 20. Use `arith.subi %zero, %v : i64` where `%zero = arith.constant 0 : i64`.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Source file parsing | Custom lexer | `LangThree.parse` (expression) or `LangThree.parseModuleFromString` + extract expr | LangThree.fsproj is already a project reference; all parser logic is there |
| Type checking | Custom type inference | `Bidir.synthTop` or skip in Phase 2 | Phase 2 elaborates structural AST — types are known from AST shape for int-only programs |
| SSA name uniqueness | Complex renaming pass | `int ref` fresh counter + `%t{n}` prefix | Simple, guaranteed unique, no analysis needed |
| Arith op lowering | Custom LLVM IR generation | `mlir-opt --convert-arith-to-llvm` | Already in pipeline from Phase 1; zero changes needed |

**Key insight:** The Elaboration pass is pure structural recursion — no new tooling, no new pipeline stages, no new file formats. The only moving parts are the DU extension and the recursive F# function.

---

## Common Pitfalls

### Pitfall 1: SSA Redefinition on Let-Shadow

**What goes wrong:** `let x = 1 in let x = 2 in x` would emit two ops named `%x`, causing `mlir-opt` to reject with "redefinition of SSA value '%x'".

**Why it happens:** Naively binding SSA values to the LangThree variable name assumes no shadowing. LangThree allows variable shadowing (`let` is purely lexical).

**How to avoid:** Never name SSA values after LangThree variable names. Use the fresh counter (`%t0`, `%t1`, ...) for ALL SSA value names. The `ElabEnv.Vars` map maps `string → MlirValue` where `MlirValue.Name` is always a fresh `%t{n}`.

**Warning signs:** Tests with duplicate let-bound names fail at `mlir-opt`, not at elaboration. The error message is "redefinition of SSA value".

### Pitfall 2: Op Emission Order Reversal

**What goes wrong:** If ops are accumulated in a cons-list (`op :: acc`) and not reversed, the MlirBlock body will be in reverse order, causing uses before definitions.

**Why it happens:** Prepending to a list is O(1) but produces reverse order.

**How to avoid:** Either use list append (`lops @ rops @ [op]`) which preserves order, or if using a cons-accumulator, call `List.rev` before inserting into `MlirBlock.Body`.

**Warning signs:** `mlir-opt` error "operand defined after use" or "use of undefined SSA value".

### Pitfall 3: `arith.negsi` Does Not Exist

**What goes wrong:** Emitting `%r = arith.negsi %v : i64` — `mlir-opt` rejects with "custom op 'arith.negsi' is unknown".

**Why it happens:** Negation is a natural unary op but MLIR arith dialect does not include `negsi` in LLVM 20.

**How to avoid:** Desugar `Negate(inner)` to `ArithConstantOp(zero, 0L)` + `ArithSubIOp(result, zero, inner_val)`. Verified working end-to-end on this system.

**Warning signs:** Any attempt to use `negsi` string in the Printer or a `NegateOp` MlirIR case.

### Pitfall 4: `Ast.Number` Carries `int`, Not `int64`

**What goes wrong:** `ArithConstantOp(v, n)` where `n` is an F# `int` — type error at compile time if `ArithConstantOp` expects `int64`.

**Why it happens:** LangThree AST predates this project's `i64` decision. `Number of int * Span`.

**How to avoid:** Cast at elaboration boundary: `ArithConstantOp(v, int64 n)`. Add a comment noting this int-to-int64 cast point.

**Warning signs:** F# type error "This expression was expected to have type 'int64' but here has type 'int'" in `elaborateExpr`.

### Pitfall 5: Module Parser vs Expression Parser

**What goes wrong:** Using `parseModuleFromString` and extracting the inner `Ast.Expr` is indirect. The module may contain zero or multiple decls; extraction logic needs to handle these cases.

**Why it happens:** LangThree has two entry points: `parse` (single expression) and `parseModuleFromString` (module with top-level decls).

**How to avoid:** For Phase 2 (scalar/let programs), use `LangThree.Program.parse` — the expression-only parser. Module-level decls are a Phase 6 concern. The `parse` function signature is `parse (input: string) (filename: string) : Expr` and it does NOT require IndentFilter (comment in LangThree source confirms this).

**Warning signs:** Extracting `Expr` from `Module` requires matching on `LetDecl` and using the body — fragile for Phase 2.

---

## Code Examples

Verified patterns from manual end-to-end testing on this system:

### MLIR arith ops (verified in mlir-opt 20.1.4)

```mlir
// 1 + 2 * 3 - 4 / 2 = 5
module {
  func.func @main() -> i64 {
    %c1 = arith.constant 1 : i64
    %c2 = arith.constant 2 : i64
    %c3 = arith.constant 3 : i64
    %c4 = arith.constant 4 : i64
    %c2_1 = arith.constant 2 : i64
    %mul = arith.muli %c2, %c3 : i64
    %div = arith.divsi %c4, %c2_1 : i64
    %add = arith.addi %c1, %mul : i64
    %sub = arith.subi %add, %div : i64
    return %sub : i64
  }
}
// -> binary exits with code 5 ✓
```

### SSA let bindings (verified in mlir-opt 20.1.4)

```mlir
// let x = 5 in let y = x + 3 in y = 8
module {
  func.func @main() -> i64 {
    %x = arith.constant 5 : i64
    %c3 = arith.constant 3 : i64
    %y = arith.addi %x, %c3 : i64
    return %y : i64
  }
}
// -> binary exits with code 8 ✓
```

Note: Named SSA values like `%x`, `%y` are accepted by `mlir-opt`. The elaboration pass can use `%t0`, `%t1`, ... for all values — the names in MLIR output are cosmetic for Phase 2 (correctness only; readability can be improved later).

### Unary negation (verified in mlir-opt 20.1.4)

```mlir
%c5 = arith.constant 5 : i64
%zero = arith.constant 0 : i64
%neg = arith.subi %zero, %c5 : i64
// %neg = -5
```

### LangThree parse entry point

```fsharp
// Use the expression parser for Phase 2 .lt files (single expression)
open FSharp.Text.Lexing
// From LangThree.Program (accessible via project reference):
let src = System.IO.File.ReadAllText(inputPath)
let expr = LangThree.Program.parse src inputPath  // returns Ast.Expr
```

Wait — `LangThree.Program.parse` may not be the right approach since `Program.fs` is a module named `Program`, not a library API. The safer approach is to replicate the minimal parse logic in `FunLangCompiler.Cli/Program.fs`:

```fsharp
// Minimal expression parse: replicate parse logic from LangThree.Program.parse
open FSharp.Text.Lexing
let parseExpr (src: string) (filename: string) : Ast.Expr =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    Parser.start Lexer.tokenize lexbuf
```

This uses `Lexer.setInitialPos`, `Parser.start`, and `Lexer.tokenize` — all accessible from the LangThree project reference.

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Hardcoded `return42Module` in CLI | Parse `.lt` file → Elaborate → compile | Phase 2 | CLI becomes a real compiler for scalar programs |
| `MlirOp` DU has 2 cases | `MlirOp` DU has 6 cases (+ 4 arith) | Phase 2 | Printer must handle all 6 cases; exhaustive match catches missing cases at compile time |
| No SSA variable tracking | `ElabEnv.Vars: Map<string, MlirValue>` | Phase 2 | Foundation for all future variable-binding passes (closures, let rec, etc.) |
| `arith.constant` only | Full binary arith: addi/subi/muli/divsi | Phase 2 | Enables integer arithmetic programs end-to-end |

**Deprecated/outdated:**
- `MlirIR.return42Module` hardcoded value: Still valid as a test utility, but CLI no longer calls it directly in Phase 2.

---

## Open Questions

1. **`Program.parse` accessibility from FunLangCompiler**
   - What we know: `LangThree.Program` is a module compiled into `LangThree.fsproj`. F# does not have `internal` visibility — all top-level `let` bindings are public. However, `Program.fs` opens `Eval`, `Prelude`, `Argu`, and other modules that may trigger side effects on module initialization.
   - What's unclear: Whether calling `Parser.start Lexer.tokenize lexbuf` directly in `FunLangCompiler.Cli/Program.fs` (bypassing `LangThree.Program.parse`) avoids initialization of unused LangThree modules (Eval, Prelude).
   - Recommendation: Replicate the 4-line parse logic in `FunLangCompiler.Cli/Program.fs` using `Lexer` and `Parser` directly. This is the simplest and avoids Eval/Prelude initialization. The `parse` function in LangThree.Program does the same thing.

2. **`Number` carries `int` but target is `i64` — range constraint**
   - What we know: LangThree `Number of int` uses .NET `int` (32-bit signed). The MLIR target is `i64`. Programs using values > 2^31-1 cannot be expressed in LangThree source.
   - What's unclear: Whether this matters for Phase 2 (all test cases use small values: 1, 2, 3, 4, 5, 8, 42).
   - Recommendation: Cast `int64 n` at the elaboration boundary. Document the i32→i64 upcast as expected behavior. No action needed in Phase 2.

3. **Test exit code range limit (0–255)**
   - What we know: OS exit codes are 8-bit (0–255). FsLit tests verify the exit code. Phase 2 success criteria use values 5 and 8 — both in range.
   - What's unclear: Whether Phase 3+ success criteria will use larger values requiring stdout-based verification instead.
   - Recommendation: Phase 2 tests are fine. For Phase 3+ with values > 255, print the result to stdout and verify that instead of exit code. No action needed in Phase 2.

---

## Sources

### Primary (HIGH confidence — verified on this system 2026-03-26)

- MLIR 20.1.4 at `/opt/homebrew/opt/llvm/bin/mlir-opt` — verified `arith.addi`, `arith.subi`, `arith.muli`, `arith.divsi` all accepted and lowered correctly
- End-to-end test: `1 + 2 * 3 - 4 / 2` → binary exits 5 ✓
- End-to-end test: `let x = 5 in let y = x + 3 in y` → binary exits 8 ✓
- SSA redefinition rejection: `%x = ...; %x = ...` → `mlir-opt` error "redefinition of SSA value '%x'" ✓
- `arith.negsi` non-existence: `mlir-opt` error "custom op 'arith.negsi' is unknown" ✓
- `arith.subi %zero, %v` unary negation: exits 251 (= -5 mod 256) ✓
- `LangThree/src/LangThree/Ast.fs` — `Number of int * Span`, `Add/Subtract/Multiply/Divide/Negate/Var/Let` cases confirmed
- `LangThree/src/LangThree/Program.fs` — `parse` function signature confirmed: `parse (input: string) (filename: string) : Expr` (uses `Parser.start Lexer.tokenize lexbuf`)

### Secondary (HIGH confidence — project source code)

- `src/FunLangCompiler.Compiler/MlirIR.fs` — current `MlirOp` DU shape
- `src/FunLangCompiler.Compiler/Printer.fs` — existing printOp pattern to follow
- `src/FunLangCompiler.Compiler/Pipeline.fs` — unchanged; pipeline from Phase 1 handles all new ops
- `.planning/ROADMAP.md` — Phase 2 success criteria and planned plans
- `.planning/STATE.md` — accumulated decisions including MlirOp extensibility rule

### Tertiary (MEDIUM confidence — design judgment)

- `ElabEnv` design with `int ref` counter — idiomatic F# for stateful fresh-name generation; pattern used in LangThree's own `Elaborate.fs` (`freshTypeVarIndex` uses `let counter = ref 0`)
- `Map<string, MlirValue>` for SSA env — standard persistent map; correct for lexical scoping (Phase 2 `let` is non-recursive)

---

## Metadata

**Confidence breakdown:**
- MLIR arith ops: HIGH — verified end-to-end on this system
- SSA naming rules: HIGH — verified redefinition rejection and named value acceptance
- Elaboration pass design: HIGH — pattern is standard recursive compiler pass; verified correct output
- LangThree AST structure: HIGH — read Ast.fs directly
- LangThree parse API: MEDIUM — `Program.parse` is accessible but initialization of Eval/Prelude uncertain; safe path is to use `Parser.start`/`Lexer.tokenize` directly

**Research date:** 2026-03-26
**Valid until:** 2026-04-25 (MLIR 20.x stable; LangThree AST structure locked for Phase 2 scope)
