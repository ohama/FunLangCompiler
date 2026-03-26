# Phase 3: Booleans, Comparisons, Control Flow - Research

**Researched:** 2026-03-26
**Domain:** MLIR arith.cmpi / cf dialect, multi-block SSA, F# compiler pass with block accumulator
**Confidence:** HIGH

---

## Summary

Phase 3 extends the compiler in two separable halves. The first half (Plan 03-01) adds `ArithCmpIOp` to MlirIR, teaches the Printer to emit `arith.cmpi`, and teaches Elaboration to handle `Bool`, `Equal`, `NotEqual`, `LessThan`, `GreaterThan`, `LessEqual`, `GreaterEqual` AST nodes — all producing `i1` values into the existing single-block structure. The second half (Plan 03-02) introduces multi-block elaboration: `If`, `And`, and `Or` all require emitting multiple `MlirBlock`s into the region, which demands a new "builder" pattern in Elaboration.

All six MLIR predicates (`eq`, `ne`, `slt`, `sgt`, `sle`, `sge`) have been verified end-to-end on this system. The `cf.cond_br` / `cf.br` / block-argument pattern for merge points is confirmed working. The `--convert-cf-to-llvm` pass is already in `Pipeline.loweringPasses` from Phase 1. The `MlirBlock` record already has `Label` and `Args` fields from Phase 1 design — the Printer already emits them.

The key architectural challenge is the transition from single-block elaboration (`elaborateExpr` returns `MlirValue * MlirOp list`) to multi-block elaboration where `If`/`And`/`Or` need to emit named side-blocks. The recommended solution is a mutable `ElabEnv.Blocks` accumulator (`MlirBlock list ref`) plus a separate `freshLabel` counter, so `elaborateExpr` can "emit" completed blocks and return only the current-block ops.

**Primary recommendation:** Split into two plans: 03-01 (bool/cmpi, single-block) and 03-02 (cond_br, multi-block). Add `ElabEnv.Blocks: MlirBlock list ref` in 03-02 so elaboration of `If`/`And`/`Or` can emit side blocks while returning `MlirValue * MlirOp list` unchanged for the current block segment.

---

## Standard Stack

### Core

| Tool / Module | Version | Purpose | Why Standard |
|---------------|---------|---------|--------------|
| `arith` MLIR dialect | LLVM 20.1.4 | `arith.cmpi` for comparisons, `arith.constant 1 : i1` for bool literals | Already used in Phase 1+2; zero new dialect dependencies |
| `cf` MLIR dialect | LLVM 20.1.4 | `cf.cond_br`, `cf.br`, block arguments for merge points | Required for control flow; `--convert-cf-to-llvm` already in Pipeline.loweringPasses |
| `MlirIR.fs` (this project) | Phase 3 additions | Adds `ArithCmpIOp`, `CfCondBrOp`, `CfBrOp` to `MlirOp` DU | Same extensible DU pattern from Phases 1+2 |
| `Elaboration.fs` | Phase 3 extensions | Handles `Bool`, comparison ops, `If`, `And`, `Or` | Extended in place; no new files needed |
| `LangThree Ast.fs` | project ref | `Bool`, `Equal`/`NotEqual`/`LessThan`/`GreaterThan`/`LessEqual`/`GreaterEqual`, `If`, `And`, `Or` | All cases already defined in Phase 4 section of Ast.Expr DU |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `FsLit (fslit)` | — | E2E test runner | New `.flt` tests for each success criterion |
| `mlir-opt` LLVM 20 | 20.1.4 | Validates and lowers MLIR; pass order matters | Already used; no change to Pipeline.loweringPasses |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `ElabEnv.Blocks: MlirBlock list ref` accumulator | Return `MlirValue * MlirOp list * MlirBlock list` from elaborateExpr | Returning side blocks changes signature; accumulator keeps signature stable and is idiomatic for mutable state in F# |
| `arith.constant 1 : i1` for `true` | `arith.constant true` (no type suffix) | Both accepted by mlir-opt; `1 : i1` is consistent with `ArithConstantOp(result, 1L)` where `result.Type = I1`; `true` requires special-casing the printer |
| Separate `CfCondBrOp` and `CfBrOp` MlirOp cases | A generic `TerminatorOp of string` | Typed DU cases catch mistakes at compile time; they also tell the Printer exactly which format to use |

---

## Architecture Patterns

### Recommended Project Structure

```
src/LangBackend.Compiler/
├── MlirIR.fs          # Add ArithCmpIOp, CfCondBrOp, CfBrOp cases
├── Printer.fs         # Add printOp cases for new ops
├── Elaboration.fs     # Extend: bool/cmpi in 03-01; multi-block + if/and/or in 03-02
└── Pipeline.fs        # Unchanged (--convert-cf-to-llvm already there)
tests/compiler/
├── 03-01-bool-literal.flt     # true -> exits 1; false -> exits 0
├── 03-02-comparison.flt       # comparison returning via if-else
├── 03-03-short-circuit-and.flt
├── 03-04-short-circuit-or.flt
└── 03-05-if-else.flt          # if n <= 0 then 0 else 1
```

### Pattern 1: New MlirOp Cases

**What:** Add three new DU cases. `ArithCmpIOp` carries the predicate as a string (the MLIR keyword: `"eq"`, `"ne"`, `"slt"`, `"sgt"`, `"sle"`, `"sge"`). `CfCondBrOp` carries condition value + two branch targets. `CfBrOp` carries one branch target. Branch targets carry the label name + optional typed argument list.

```fsharp
// MlirIR.fs additions for Phase 3
type MlirOp =
    // ... existing cases ...
    | ArithCmpIOp  of result: MlirValue * predicate: string * lhs: MlirValue * rhs: MlirValue
    | CfCondBrOp   of cond: MlirValue * trueLabel: string * trueArgs: MlirValue list
                                      * falseLabel: string * falseArgs: MlirValue list
    | CfBrOp       of label: string * args: MlirValue list
```

Note: `result.Type` for `ArithCmpIOp` is always `I1`. `CfCondBrOp` and `CfBrOp` have no result value (they are terminators).

### Pattern 2: Printer Cases for New Ops

**What:** Each new op serializes to a specific MLIR text format. Branch arg lists use `(%name : %type, ...)` syntax.

```fsharp
// Printer.fs — add to printOp match
| ArithCmpIOp(result, predicate, lhs, rhs) ->
    sprintf "%s%s = arith.cmpi %s, %s, %s : %s"
        indent result.Name predicate lhs.Name rhs.Name (printType lhs.Type)
    // Note: type suffix is the OPERAND type (i64), not the result type (i1)

| CfCondBrOp(cond, trueLabel, trueArgs, falseLabel, falseArgs) ->
    let printArgs args =
        if args = [] then ""
        else
            let s = args |> List.map (fun v -> sprintf "%s : %s" v.Name (printType v.Type)) |> String.concat ", "
            sprintf "(%s)" s
    sprintf "%scf.cond_br %s, ^%s%s, ^%s%s"
        indent cond.Name trueLabel (printArgs trueArgs) falseLabel (printArgs falseArgs)

| CfBrOp(label, args) ->
    let argStr =
        if args = [] then ""
        else
            let s = args |> List.map (fun v -> sprintf "%s : %s" v.Name (printType v.Type)) |> String.concat ", "
            sprintf "(%s)" s
    sprintf "%scf.br ^%s%s" indent label argStr
```

**Critical:** `arith.cmpi` type suffix is the operand type (`i64`), NOT the result type (`i1`). The result is always `i1`.

### Pattern 3: Plan 03-01 Elaboration (Single-Block, Bool + Comparisons)

**What:** `Bool` maps to `ArithConstantOp` with `I1` type. Comparison ops map to `ArithCmpIOp`. All results are `I1`-typed `MlirValue`s. No new blocks needed — these can appear anywhere in the current block.

```fsharp
// Elaboration.fs — additions for 03-01 (single-block expressions)

| Bool (b, _) ->
    let v = { Name = freshName env; Type = I1 }
    let n = if b then 1L else 0L
    (v, [ArithConstantOp(v, n)])

| Equal (lhs, rhs, _) ->
    elaborateCmp env "eq" lhs rhs
| NotEqual (lhs, rhs, _) ->
    elaborateCmp env "ne" lhs rhs
| LessThan (lhs, rhs, _) ->
    elaborateCmp env "slt" lhs rhs
| GreaterThan (lhs, rhs, _) ->
    elaborateCmp env "sgt" lhs rhs
| LessEqual (lhs, rhs, _) ->
    elaborateCmp env "sle" lhs rhs
| GreaterEqual (lhs, rhs, _) ->
    elaborateCmp env "sge" lhs rhs

// Helper: elaborate a binary comparison
let private elaborateCmp (env: ElabEnv) (predicate: string) (lhs: Expr) (rhs: Expr)
    : MlirValue * MlirOp list =
    let (lv, lops) = elaborateExpr env lhs
    let (rv, rops) = elaborateExpr env rhs
    let result = { Name = freshName env; Type = I1 }
    (result, lops @ rops @ [ArithCmpIOp(result, predicate, lv, rv)])
```

### Pattern 4: Plan 03-02 Elaboration (Multi-Block, If / And / Or)

**What:** When `If`, `And`, or `Or` is elaborated, the current block must be "split" — the ops so far become the entry block (terminated by a `CfCondBrOp`), then-/else-/right-side-eval blocks are emitted, and a merge block with a block argument collects the result. A mutable `Blocks` accumulator in `ElabEnv` collects all completed blocks.

**ElabEnv extension:**

```fsharp
type ElabEnv = {
    Vars:    Map<string, MlirValue>
    Counter: int ref          // SSA value name counter (%t0, %t1, ...)
    LabelCounter: int ref     // Block label counter (bb0, bb1, ...)
    Blocks:  MlirBlock list ref  // Accumulator for completed blocks (in emission order)
}

let private freshLabel (env: ElabEnv) : string =
    let n = env.LabelCounter.Value
    env.LabelCounter.Value <- n + 1
    sprintf "bb%d" n
```

**If elaboration strategy:**

```fsharp
| If (condExpr, thenExpr, elseExpr, _) ->
    // 1. Elaborate condition in the current block
    let (condVal, condOps) = elaborateExpr env condExpr

    // 2. Fresh labels for then, else, merge blocks
    let thenLabel  = freshLabel env
    let elseLabel  = freshLabel env
    let mergeLabel = freshLabel env

    // 3. Elaborate then-branch (into its own op sequence)
    let (thenVal, thenOps) = elaborateExpr env thenExpr

    // 4. Elaborate else-branch (into its own op sequence)
    let (elseVal, elseOps) = elaborateExpr env elseExpr

    // 5. Merge block result type comes from branch results
    let mergeArg = { Name = freshName env; Type = thenVal.Type }

    // 6. Emit then block: thenOps + CfBrOp to merge with thenVal
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some thenLabel
            Args  = []
            Body  = thenOps @ [CfBrOp(mergeLabel, [thenVal])] } ]

    // 7. Emit else block: elseOps + CfBrOp to merge with elseVal
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some elseLabel
            Args  = []
            Body  = elseOps @ [CfBrOp(mergeLabel, [elseVal])] } ]

    // 8. Emit merge block (with block arg, no terminator yet — elaborateModule adds ReturnOp)
    // Note: the merge block becomes the "continuation" of elaboration
    // Return the merge block arg as the result, with no additional ops for the current segment
    // BUT: the current segment must be terminated with CfCondBrOp
    // So: return (mergeArg, condOps @ [CfCondBrOp(condVal, thenLabel, [], elseLabel, [])])
    // and emit the merge block separately
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some mergeLabel
            Args  = [mergeArg]
            Body  = []  } ]  // Body filled by elaborateModule continuation or next elaboration

    (mergeArg, condOps @ [CfCondBrOp(condVal, thenLabel, [], elseLabel, [])])
```

**Important subtlety:** When `If` appears as the top-level expression (not nested), `elaborateModule` adds `ReturnOp [mergeArg]` to the merge block's body. When `If` appears inside another expression (e.g., `let x = if ... in x + 1`), the merge block's body is empty after elaboration — and the continuation ops (from `x + 1`) should go into the merge block. This requires either:
- Making elaborateModule smarter about appending to the last block
- Or requiring that `If` is only ever the tail expression (Phase 3 scope is limited to top-level if-else, so this is acceptable)

**Simplification for Phase 3:** Since Phase 3 success criteria only require top-level if-else (not nested), the merge block's body is empty and `elaborateModule` appends `ReturnOp [mergeArg]` to it. This is correct for Phase 3 scope.

**`elaborateModule` update for multi-block:**

```fsharp
let elaborateModule (expr: Ast.Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, entryOps) = elaborateExpr env expr
    // Entry block: the ops we got back (may end with CfCondBrOp for If)
    let entryBlock = { Label = None; Args = []; Body = entryOps @ (if containsTerminator entryOps then [] else [ReturnOp [resultVal]]) }
    // Side blocks accumulated by elaboration
    let sideBlocks = env.Blocks.Value
    // For multi-block: append ReturnOp to last block (merge block) if needed
    let allBlocks = entryBlock :: sideBlocks |> appendReturnToLastBlock resultVal
    { Funcs = [ { Name = "@main"; InputTypes = []; ReturnType = Some I64; Body = { Blocks = allBlocks } } ] }
```

**Simpler approach for Phase 3:** Distinguish "has cond_br" from "doesn't" at the elaborateModule level:

```fsharp
let elaborateModule (expr: Ast.Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, entryOps) = elaborateExpr env expr
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            // Single-block case (Phase 1/2/3-01)
            [ { Label = None; Args = []; Body = entryOps @ [ReturnOp [resultVal]] } ]
        else
            // Multi-block case (Phase 3-02): entry block ends with CfCondBrOp
            // Last side block (merge) needs ReturnOp appended
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [ReturnOp [resultVal]] }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    { Funcs = [ { Name = "@main"; InputTypes = []; ReturnType = Some I64; Body = { Blocks = allBlocks } } ] }
```

### Pattern 5: Short-Circuit And / Or

**And (`&&`):** If left is `false`, short-circuit — result is `false`. If left is `true`, result is the right side.

```
cf.cond_br %left, ^eval_right, ^merge(%left : i1)
^eval_right:
  <right ops>
  cf.br ^merge(%right_val : i1)
^merge(%result : i1):
  ...
```

**Or (`||`):** If left is `true`, short-circuit — result is `true`. If left is `false`, result is the right side.

```
cf.cond_br %left, ^merge(%left : i1), ^eval_right
^eval_right:
  <right ops>
  cf.br ^merge(%right_val : i1)
^merge(%result : i1):
  ...
```

Both patterns pass the left value directly as a block argument to the short-circuit branch — verified working on this system.

### Pattern 6: Return Type for Bool-Valued Programs

**What:** When the top-level expression is a bool (e.g., `true`, `1 < 2`), the `MlirValue.Type` is `I1`. The `@main` function must return `I1`, not `I64`. On Linux/macOS, exit code for `i1` return: `true` exits 1, `false` exits 0. Both are valid for FsLit testing.

**Current `elaborateModule` hardcodes `ReturnType = Some I64`** — this must be changed in Phase 3 to use `Some resultVal.Type` so bool-valued programs return `i1`.

```fsharp
ReturnType = Some resultVal.Type  // I64 for int programs, I1 for bool programs
```

Verified: `func.func @main() -> i1` compiles and runs correctly (exits 1 for true, 0 for false).

### Anti-Patterns to Avoid

- **`arith.constant true : i1` (with `: i1` suffix):** Rejected by `mlir-opt` with "expected operation name in quotes". Use `arith.constant true` (no suffix) OR `arith.constant 1 : i1`. Recommendation: use `arith.constant 1 : i1` (consistent with existing `ArithConstantOp` printer format).
- **`arith.cmpi` type suffix is operand type, not result type:** `%r = arith.cmpi slt, %a, %b : i64` — the `: i64` is the type of `%a` and `%b`, NOT `%r`. The result `%r` is implicitly `i1`. Do NOT emit `: i1` after the operands.
- **Emitting `CfCondBrOp` without also emitting side blocks:** The branch labels referenced in `CfCondBrOp` MUST exist as blocks in the region. Failure causes `mlir-opt` to error with "reference to an undefined block".
- **Emitting multiple terminators in a block:** A MLIR basic block must end with exactly one terminator (`CfCondBrOp`, `CfBrOp`, `ReturnOp`). The Printer must NOT emit `ReturnOp` after a `CfCondBrOp`.
- **Hardcoded `ReturnType = Some I64` in `elaborateModule`:** Must become `Some resultVal.Type`. Bool programs need `-> i1`.
- **Nested if-else in Phase 3:** Phase 3 tests only require top-level if-else. Do NOT attempt to handle `If` inside a `Let` binding or as an operand of arithmetic in Phase 3. Catch unsupported nesting with `failwithf` and defer.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Phi nodes / value merging | Custom phi-node IR type | Block arguments (`MlirBlock.Args`) + `CfBrOp` with value list | MLIR uses block arguments as its phi-node equivalent; already modeled in `MlirBlock` |
| Branch target representation | A `BranchTarget` record type | `string * MlirValue list` (label + args) inlined in `CfCondBrOp`/`CfBrOp` | Simpler; the only consumers are the Printer and Elaboration |
| SSA dominance analysis | Custom dominance checker | Trust `mlir-opt` to verify | mlir-opt reports "use of undefined SSA value" if something is wrong; no need to pre-verify |

**Key insight:** MLIR's block argument system directly replaces phi nodes. The `MlirBlock.Args` field already exists and the Printer already emits it. No new IR types needed for merge points.

---

## Common Pitfalls

### Pitfall 1: `arith.cmpi` Type Annotation is Operand Type

**What goes wrong:** Emitting `%r = arith.cmpi slt, %a, %b : i1` or `%r = arith.cmpi slt, %a, %b : i1 i64` — `mlir-opt` rejects with a parse error.

**Why it happens:** The type suffix after the operands in `arith.cmpi` annotates the *operand* types, not the result. The result is always inferred as `i1`.

**How to avoid:** Printer emits `arith.cmpi %predicate, %lhs, %rhs : %lhs.Type` — use `lhs.Type` (which is `I64`) for the type annotation.

**Warning signs:** `mlir-opt` error "custom op 'arith.cmpi' expects same type for all operands".

### Pitfall 2: Block Labels Referenced Before Definition

**What goes wrong:** `CfCondBrOp` references `^bb_then` and `^bb_else`, but those blocks appear later in the `MlirBlock list`. `mlir-opt` rejects with "reference to an undefined block" if a block is missing entirely, but forward references within a function ARE allowed.

**Why it matters:** The order of blocks in `MlirRegion.Blocks` does NOT need to match control flow order — MLIR accepts any order. However, ALL referenced block labels MUST exist.

**How to avoid:** Ensure the block accumulator always emits then/else/merge blocks before `elaborateModule` finalizes the region.

**Warning signs:** `mlir-opt` error "reference to an undefined block '^bb_then'".

### Pitfall 3: ReturnOp Emitted After a Terminator

**What goes wrong:** `elaborateModule` always appends `ReturnOp` to `entryOps`. For single-block programs this is correct, but for multi-block programs (where `entryOps` ends with `CfCondBrOp`), the entry block would have TWO terminators.

**Why it happens:** `elaborateModule` currently appends `ReturnOp [resultVal]` unconditionally.

**How to avoid:** Use the `sideBlocks.IsEmpty` check: if empty, add `ReturnOp` to entry block; if non-empty, entry block already ends with `CfCondBrOp`, and `ReturnOp` goes into the last side block (merge block).

**Warning signs:** `mlir-opt` error "block with multiple terminators" or "operations after a terminator".

### Pitfall 4: `true` Bool Constant Syntax

**What goes wrong:** Emitting `arith.constant true : i1` — `mlir-opt` rejects with "expected operation name in quotes" because it parses `true` as a string attribute, then fails on `: i1`.

**Correct syntax options:**
- `arith.constant true` (type inferred as `i1`)
- `arith.constant 1 : i1` (explicit)
- `arith.constant 0 : i1` (false)

**Recommendation:** Use `arith.constant 1 : i1` and `arith.constant 0 : i1` because it fits the existing `ArithConstantOp` printer which always emits `value : type`. The `I1` type case in `printType` already returns `"i1"`.

### Pitfall 5: Hardcoded I64 Return Type

**What goes wrong:** `if true then 1 else 0` returns `I64` (fine), but `true` alone returns `I1`. If `ReturnType = Some I64` is hardcoded, the Printer emits `-> i64` while the `ReturnOp` carries an `I1` value — `mlir-opt` rejects with "return operand types don't match the function signature".

**How to avoid:** Change `elaborateModule` to `ReturnType = Some resultVal.Type`.

**Warning signs:** `mlir-opt` error "return operand types don't match function signature".

### Pitfall 6: Nested If/And/Or in Phase 3 Scope

**What goes wrong:** An elaborate-and-branch scheme that works for top-level expressions breaks silently when the if-else result is used inside a `Let` binding or arithmetic, because the merge block's continuation is not hooked up.

**How to avoid:** Phase 3 tests should only exercise top-level if-else. Add `failwithf` guard for nested cases. Document the limitation.

**Warning signs:** Wrong exit code from a program like `let x = if true then 1 else 2 in x + 1`.

---

## Code Examples

Verified patterns from end-to-end tests on this system (LLVM 20.1.4, 2026-03-26):

### arith.cmpi — all 6 predicates verified

```mlir
// LangThree =  -> arith.cmpi eq
// LangThree <> -> arith.cmpi ne
// LangThree <  -> arith.cmpi slt
// LangThree >  -> arith.cmpi sgt
// LangThree <= -> arith.cmpi sle
// LangThree >= -> arith.cmpi sge
%result = arith.cmpi slt, %a, %b : i64
// %result has type i1 (true/false)
```

All six predicates tested and confirmed correct: `eq`, `ne`, `slt`, `sgt`, `sle`, `sge`.

### if-else with merge block argument

```mlir
module {
  func.func @main() -> i64 {
    %n = arith.constant 5 : i64
    %zero = arith.constant 0 : i64
    %cond = arith.cmpi sle, %n, %zero : i64
    cf.cond_br %cond, ^bb_then, ^bb_else
  ^bb_then:
    %tv = arith.constant 0 : i64
    cf.br ^bb_merge(%tv : i64)
  ^bb_else:
    %fv = arith.constant 1 : i64
    cf.br ^bb_merge(%fv : i64)
  ^bb_merge(%result : i64):
    return %result : i64
  }
}
// n=5 > 0, so else branch: exits with 1
```

### Bool literal (true -> exits 1)

```mlir
module {
  func.func @main() -> i1 {
    %t = arith.constant 1 : i1
    return %t : i1
  }
}
// exits with 1 (true)
```

### if true then 1 else 0 (exits 1)

```mlir
module {
  func.func @main() -> i64 {
    %t = arith.constant 1 : i1    // true
    cf.cond_br %t, ^bb_then, ^bb_else
  ^bb_then:
    %one = arith.constant 1 : i64
    cf.br ^bb_merge(%one : i64)
  ^bb_else:
    %zero = arith.constant 0 : i64
    cf.br ^bb_merge(%zero : i64)
  ^bb_merge(%r : i64):
    return %r : i64
  }
}
// exits with 1
```

### Short-circuit AND (false && true -> false)

```mlir
// false && true = false (right side NOT evaluated)
%left = arith.constant 0 : i1    // false
cf.cond_br %left, ^eval_right, ^merge(%left : i1)
^eval_right:
  %right = arith.constant 1 : i1  // true (would be evaluated expression)
  cf.br ^merge(%right : i1)
^merge(%result : i1):
  ...
```

### Short-circuit OR (true || false -> true)

```mlir
// true || false = true (right side NOT evaluated)
%left = arith.constant 1 : i1    // true
cf.cond_br %left, ^merge(%left : i1), ^eval_right
^eval_right:
  %right = arith.constant 0 : i1  // false (would be evaluated expression)
  cf.br ^merge(%right : i1)
^merge(%result : i1):
  ...
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Single-block elaboration only | Multi-block elaboration with `ElabEnv.Blocks` accumulator | Phase 3-02 | Enables control flow; `elaborateModule` must handle multi-block regions |
| `ReturnType = Some I64` hardcoded | `ReturnType = Some resultVal.Type` | Phase 3-01 | Enables bool-valued programs |
| `MlirOp` has arith and return only | Adds `ArithCmpIOp`, `CfCondBrOp`, `CfBrOp` | Phase 3 | Printer gains three new cases; exhaustive match enforced by F# compiler |
| `--convert-cf-to-llvm` already in Pipeline | No change needed | Phase 1 (pre-included) | Zero pipeline changes for Phase 3 |

**Deprecated/outdated:**
- Nothing from prior phases is deprecated. Phase 3 extends purely additively.

---

## Open Questions

1. **Nested if-else (deferred)**
   - What we know: The block-accumulator approach works for top-level if-else but has a "dangling merge block body" problem for nested if-else (e.g., `let x = if ... in x + 1`).
   - What's unclear: Whether Phase 3 tests exercise nested if-else or only top-level.
   - Recommendation: Phase 3 plans should explicitly scope to top-level if-else only. Add `failwithf "unsupported: nested If"` for nested cases. Nested if-else is a Phase 4+ concern.

2. **Block label prefix convention**
   - What we know: `mlir-opt` accepts any alphanumeric label including underscores (e.g., `^bb_then`, `^bb0`, `^merge`).
   - What's unclear: Whether using numeric labels (`^bb0`, `^bb1`) vs. semantic labels (`^bb_then`, `^bb_else`) is preferred for readability.
   - Recommendation: Use semantic names generated from the expression kind + counter: `"then" + string n`, `"else" + string n`, `"merge" + string n`. E.g., `^then0`, `^else0`, `^merge0`. This makes the MLIR output readable in test failures.

3. **FsLit exit code range for bool tests**
   - What we know: `i1` return from `main` exits 1 (true) or 0 (false). Both are within the 0–255 exit code range.
   - What's unclear: Whether FsLit tests should use `true` / `false` programs (exiting 1/0) or wrap in `if-else` to return a more obvious integer.
   - Recommendation: Use `if true then 1 else 0` for bool literal tests — clearer intent, unambiguous exit code of 1. For standalone `true` expression tests, exit 1 is correct.

---

## Sources

### Primary (HIGH confidence — verified on this system, 2026-03-26)

All MLIR patterns below were tested with `mlir-opt 20.1.4` at `/opt/homebrew/opt/llvm/bin/mlir-opt`, full pipeline to binary, and executed:

- `arith.cmpi eq/ne/slt/sgt/sle/sge` — all 6 predicates compile and produce correct results
- `arith.constant 1 : i1` / `arith.constant 0 : i1` — valid bool constant syntax
- `arith.constant true` (no type suffix) — also valid; prefer `1 : i1` for consistency
- `arith.constant true : i1` — INVALID (mlir-opt rejects)
- `cf.cond_br %cond, ^true_label, ^false_label` — verified
- `cf.cond_br %cond, ^label(%val : type), ^label2` — block args on one or both branches verified
- `cf.br ^label(%val : type)` — unconditional branch with args verified
- Block arguments as phi-node replacements — merge point pattern verified e2e
- `func.func @main() -> i1` — valid; exits 1 for true, 0 for false
- `--convert-cf-to-llvm` omission causes `mlir-translate` failure — confirmed (cf ops remain unrecognized without lowering)
- Out-of-order blocks — MLIR accepts blocks in any order within a function
- Short-circuit AND / OR patterns — both verified e2e

### Secondary (HIGH confidence — project source)

- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MlirIR.fs` — `MlirBlock.Label` and `MlirBlock.Args` already exist; `I1` type already in `MlirType`; `MlirOp` extensibility pattern confirmed
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Printer.fs` — `printBlock` already emits label+args; `printType` already handles `I1`
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — `ElabEnv` design, `freshName` pattern, `elaborateModule` single-block structure
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Pipeline.fs` — `--convert-cf-to-llvm` confirmed in `loweringPasses`
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — `Bool`, `If`, `Equal`, `NotEqual`, `LessThan`, `GreaterThan`, `LessEqual`, `GreaterEqual`, `And`, `Or` all confirmed in `Ast.Expr` DU

---

## Metadata

**Confidence breakdown:**
- MLIR arith.cmpi predicates: HIGH — all 6 verified e2e
- cf.cond_br / cf.br / block args: HIGH — verified e2e including short-circuit patterns
- Bool literal syntax: HIGH — verified (1:i1 works, "true:i1" does not)
- ElabEnv multi-block design: MEDIUM — pattern is sound (similar to known compiler techniques); not yet implemented, may need adjustments for edge cases
- Nested if-else: LOW — explicitly deferred; known limitation documented

**Research date:** 2026-03-26
**Valid until:** 2026-04-25 (MLIR 20.x stable; LangThree AST structure locked)
