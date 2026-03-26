# Phase 13: Pattern Matching Extensions - Research

**Researched:** 2026-03-26
**Domain:** F# compiler backend — pattern match extensions: when guards, OrPat, ConstPat(CharConst)
**Confidence:** HIGH

## Summary

Phase 13 extends the LangBackend pattern matching compiler with three features that are already represented in the LangThree AST but not yet handled in the backend: `when` guards (PAT-06), `OrPat` (PAT-07), and `ConstPat(CharConst)` (PAT-08).

The AST type `MatchClause = Pattern * Expr option * Expr` already includes the guard as the second element (`Expr option`). The current `Elaboration.fs` Match handler extracts `_guard` but ignores it (line 783). Similarly, `MatchCompiler.desugarPattern` raises `failwith` for both `OrPat` and `ConstPat(CharConst)` (lines 92–93, 120–121). All three features have clear, minimal implementation paths within the existing Jacobs decision tree infrastructure.

**Primary recommendation:** Handle all three extensions as desugaring passes before the decision tree, with when-guards modeled as a new `Guard` node in the `DecisionTree` DU, emitted in `Elaboration.fs` after binding variables but before the body.

---

## Current Architecture (as researched)

### AST Representation

#### MatchClause
`MatchClause = Pattern * Expr option * Expr`
- `Pattern` — the pattern to match
- `Expr option` — optional `when` guard; `None` = no guard
- `Expr` — body expression to evaluate if pattern (and guard) match

Source: `.planning/milestones/v2.0-phases/09-tuples/09-RESEARCH.md`, `.planning/research/FEATURES.md`

Example:
```
match 5 with
| n when n > 0 -> 1    (* (VarPat "n", Some (GreaterThan(Var "n", Number 0)), Number 1) *)
| _ -> 0               (* (WildcardPat, None, Number 0) *)
```

#### OrPat
`OrPat of Pattern list * Span`
- Contains a list of alternative patterns; all share the same body in the source match arm.

Source: `.planning/research/FEATURES.md` line 44

Example:
```
match 3 with | 1 | 2 | 3 -> 10 | _ -> 0
```
At the AST level this is a SINGLE match arm with pattern `OrPat([ConstPat(IntConst 1); ConstPat(IntConst 2); ConstPat(IntConst 3)])` and body `10`. The other arm is `(WildcardPat, None, Number 0)`.

#### ConstPat(CharConst)
`ConstPat of Constant * Span` where `Constant` includes `CharConst of char`.

Characters are represented as `i64` at runtime (same as integers). The char value is its Unicode code point. Pattern `'A'` is equivalent to `ConstPat(CharConst 'A')` → integer 65.

Source: `MatchCompiler.fs` lines 92–93 (the stub that currently fails).

---

## Current Code Entry Points

### Elaboration.fs — Match handler
```fsharp
// Line 777–787
| Match(scrutineeExpr, clauses, _) ->
    let (scrutVal, scrutOps) = elaborateExpr env scrutineeExpr
    ...
    // Build arms for MatchCompiler: (Pattern * bodyIndex) list
    let arms = clauses |> List.mapi (fun i (pat, _guard, _body) -> (pat, i))
    ...
    let tree = MatchCompiler.compile rootAcc arms
    ...
    // Leaf handler (lines 953–971) uses bodyIdx to index into clauses
    let (_pat, _guard, bodyExpr) = clauses.[bodyIdx]
```

The guard is already extracted in the `Leaf` handler (`_guard`) but not used. This is the precise insertion point for PAT-06.

### MatchCompiler.fs — desugarPattern stubs
```fsharp
// Line 92–93
| ConstPat (CharConst _, _) ->
    failwith "MatchCompiler: CharConst pattern not supported in backend"

// Line 120–121
| OrPat _ ->
    failwith "MatchCompiler: OrPat not yet supported in backend"
```

---

## Implementation Designs

### PAT-08: ConstPat(CharConst) — Trivial

**Insight:** Chars are `i64` at runtime (their Unicode code point). The existing `IntLit` path in `MatchCompiler.CtorTag` and `Elaboration.emitCtorTest` already handles `i64` equality tests with `arith.cmpi eq`.

**Change (MatchCompiler.fs, `desugarPattern`):**
```fsharp
| ConstPat (CharConst c, _) ->
    let n = int (c)  // char to int code point
    ([{ Scrutinee = acc; Pattern = CtorTest(IntLit n, []) }], [])
```

No changes needed to `Elaboration.fs` or `CtorTag`. This reuses the `IntLit` test path entirely.

**Verification:** `match 'A' with | 'A' -> 1 | _ -> 0` compiles and exits 1. `'A'` has code point 65; the scrutinee is already `i64`-typed.

---

### PAT-07: OrPat — Desugar Before Decision Tree

**Insight:** `OrPat([p1; p2; p3], _)` in a clause means "any of p1, p2, p3 matches → same body." The correct desugaring is to expand one clause containing `OrPat(pats)` into `List.length pats` separate clauses, each pointing to the same body.

**Change location:** `Elaboration.fs`, in the Match handler, before calling `MatchCompiler.compile`. Add a preprocessing step:

```fsharp
// Expand OrPat arms: one arm with OrPat(pats) becomes len(pats) arms
let expandOrPats (clauses: (Pattern * Expr option * Expr) list) =
    clauses |> List.collect (fun (pat, guard, body) ->
        match pat with
        | OrPat (alts, _) -> alts |> List.map (fun altPat -> (altPat, guard, body))
        | _ -> [(pat, guard, body)]
    )
let clauses = expandOrPats clauses
```

After this expansion, `clauses` is a standard `(Pattern * Expr option * Expr) list` with no `OrPat` entries. The existing `MatchCompiler.compile` call works unchanged.

**No change to MatchCompiler.fs required for OrPat** — we desugar before reaching it. The `failwith` stub for `OrPat` in `desugarPattern` becomes unreachable (can be changed to an assert or kept as a safety net).

**Note on variable bindings and guards with OrPat:** When desugaring `OrPat`, each alternative arm inherits the same guard and body. If an alternative pattern binds variables (e.g., `| (a, _) | (_, a) -> a`), each expanded arm gets its own binding. This is correct behavior since they share the body expression which references those bindings.

---

### PAT-06: when Guard — Guard Node in DecisionTree

**Insight:** After pattern variables are bound (i.e., at a `Leaf` node in the decision tree), the guard must be evaluated. If false, execution must fall through to the next arm, not execute the body.

**Strategy:** Extend the `DecisionTree` DU with a `Guard` node:

```fsharp
/// Decision tree — the output of compilation.
type DecisionTree =
    | Leaf of bindings: (string * Accessor) list * bodyIndex: int
    | Fail
    | Switch of scrutinee: Accessor * constructor: CtorTag * args: Accessor list
               * ifMatch: DecisionTree * ifNoMatch: DecisionTree
    | Guard of bodyIndex: int * ifTrue: DecisionTree * ifFalse: DecisionTree
    // Guard: evaluate the when-guard for arm bodyIndex;
    //        if true → ifTrue tree (execute body); if false → ifFalse tree (try next arms)
```

**MatchCompiler changes:** In `genMatch`, at the `Leaf` base case, check if the body has a guard. If yes, wrap the Leaf in a Guard node that falls through to `genMatch` of the remaining clauses:

```fsharp
// In genMatch, when first clause has no tests:
| first :: rest ->
    if first.Tests.IsEmpty then
        // Check if this arm has a guard
        if first.HasGuard then
            // Guard: if guard passes → Leaf; if guard fails → try remaining clauses
            Guard(first.BodyIndex, Leaf(first.Bindings, first.BodyIndex), genMatch rest)
        else
            Leaf(first.Bindings, first.BodyIndex)
```

This requires `Clause` to carry a `HasGuard: bool` flag (or the actual guard expression index, since the guard expression is accessed by bodyIndex in `clauses`). The simplest approach: add `HasGuard: bool` to `Clause`.

**Elaboration.fs changes:** In `emitDecisionTree`, handle the `Guard` node:

```fsharp
| MatchCompiler.Guard (bodyIdx, ifTrue, ifFalse) ->
    // Get bindings from the ifTrue Leaf (they were already applied before this guard)
    // Actually: bindings come from the parent Leaf context. We need bindings here.
    // Design: Guard node carries bindings too, OR we restructure Leaf to contain Guard.
    ...
```

**Simpler alternative (no new DU node):** Instead of extending the `DecisionTree` DU in `MatchCompiler.fs`, handle guards entirely in `Elaboration.fs` at the `Leaf` case. When we reach a `Leaf`, check if `clauses.[bodyIdx]` has a guard. If yes:
1. Resolve bindings (set up variable environment)
2. Emit guard expression → `i1` condition value
3. `cf.cond_br(guardCond, guardTrueLabel, guardFalseLabel)`
4. Guard-true block: emit body → branch to merge
5. Guard-false block: branch to the NEXT ARM's entry block

The challenge with option 5 is "next arm's entry block" — the decision tree doesn't have this concept since it's already compiled. We need a fallthrough target.

**Recommended approach:** Extend `MatchCompiler.DecisionTree` with `Guard` AND pass a "fallthrough continuation" when compiling guards. Concretely:

1. Add `Guard of bindings: (string * Accessor) list * bodyIndex: int * ifFalse: DecisionTree` to `DecisionTree`.
   - `bindings`: same as `Leaf` — variables to bind before evaluating the guard.
   - `bodyIndex`: which clause's guard and body to use.
   - `ifFalse`: what to do if guard fails (try next arm = `genMatch rest`).
2. `genMatch` at the leaf base case: if `first.HasGuard`, emit `Guard(first.Bindings, first.BodyIndex, genMatch rest)` instead of `Leaf`.
3. In `Elaboration.emitDecisionTree`:
   - `Leaf`: unchanged (no guard, emit body directly).
   - `Guard(bindings, bodyIdx, ifFalse)`:
     - Resolve bindings → `bindEnv`.
     - Evaluate `clauses.[bodyIdx]`'s guard expression under `bindEnv` → `(guardVal, guardOps)`.
     - Emit `ifFalse` as a separate block → `falseLabel`.
     - Emit body (same as `Leaf`) under `bindEnv` → `bodyLabel`.
     - Return `bindOps @ guardOps @ [CfCondBrOp(guardVal, bodyLabel, [], falseLabel, [])]`.

---

## Architecture Patterns

### Existing Pattern: Leaf emitting body block

```fsharp
| MatchCompiler.Leaf (bindings, bodyIdx) ->
    let mutable bindOps = []
    let mutable bindEnv = env
    for (varName, acc) in bindings do
        let (v, ops) = resolveAccessor acc
        bindOps <- bindOps @ ops
        bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
    let (_pat, _guard, bodyExpr) = clauses.[bodyIdx]
    let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
    resultType.Value <- bodyVal.Type
    let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some bodyLabel; Args = []; Body = bodyOps @ [CfBrOp(mergeLabel, [bodyVal])] } ]
    bindOps @ [ CfBrOp(bodyLabel, []) ]
```

The `Guard` case mirrors this with an added conditional branch before the body block.

### File Change Summary

| File | Change |
|------|--------|
| `MatchCompiler.fs` | (1) Add `HasGuard: bool` to `Clause`. (2) Add `Guard` case to `DecisionTree`. (3) `genMatch` leaf case: emit `Guard` when `first.HasGuard`. (4) `desugarPattern` CharConst case: map to `IntLit`. |
| `Elaboration.fs` | (1) `expandOrPats` preprocessing in Match handler. (2) Pass `HasGuard` when building `Clause` list in `MatchCompiler.compile` call (or expose via `compile` API). (3) Handle `Guard` node in `emitDecisionTree`. |

**Note on compile API:** Currently `MatchCompiler.compile` takes `(Pattern * int) list`. To thread guard info, change to `(Pattern * bool * int) list` (pattern, hasGuard, bodyIdx), or expose a separate `compileWithGuards` entry point.

---

## Common Pitfalls

### Pitfall 1: OrPat variable binding inconsistency
**What goes wrong:** If two alternatives of an OrPat bind different variable names, the body expression may reference an unbound variable in one branch.
**Why it happens:** F# requires all alternatives in an OrPat to bind exactly the same variable names with the same types.
**How to avoid:** LangThree presumably enforces this in its type checker. The backend can trust the input is well-typed.

### Pitfall 2: Guard fall-through loses bindings from next arm
**What goes wrong:** When a guard fails, we fall through to the next arm. The next arm's pattern has not yet been matched, so we must NOT re-use the binding environment from the failed arm.
**How to avoid:** `ifFalse` subtree in the `Guard` node is the result of `genMatch rest` — it starts fresh from the scrutinee. Bindings from the failed arm are not in scope in `ifFalse`.

### Pitfall 3: OrPat with guards
**What goes wrong:** `| 1 | 2 when x > 0 -> body` — the guard applies to ALL alternatives after desugaring.
**How to avoid:** The `expandOrPats` desugaring correctly propagates the guard to each expanded arm:
```fsharp
OrPat([p1; p2], _), Some guard, body
→ [(p1, Some guard, body); (p2, Some guard, body)]
```

### Pitfall 4: CharConst scrutinee type
**What goes wrong:** If a `match` expression's scrutinee is a `Char` value and the existing code assumes all scrutinees start as `I64`, this is fine — chars ARE i64. But if the frontend emits chars as a different type, the equality test will fail.
**How to avoid:** Confirm that `Char` literals in expressions elaborate to `I64` values (they should, as `Number` for chars). This should already work.

### Pitfall 5: Shared body block for OrPat alternatives
**Note:** Our desugaring creates SEPARATE clauses for each OrPat alternative. Each will emit its own body block (since `bodyIdx` differs after expansion). This is correct but slightly less efficient than sharing one body block. For correctness, separate clauses with separate `bodyIdx` values is easiest.
**After expansion:** `bodyIdx` is re-assigned by `List.mapi`, so each expanded arm gets a unique index. This is correct.

---

## Code Examples

### PAT-08 Implementation (MatchCompiler.fs)
```fsharp
| ConstPat (CharConst c, _) ->
    let n = int c
    ([{ Scrutinee = acc; Pattern = CtorTest(IntLit n, []) }], [])
```

### PAT-07 OrPat Expansion (Elaboration.fs, before MatchCompiler.compile)
```fsharp
let expandOrPats (clauses: (Pattern * Expr option * Expr) list) =
    clauses |> List.collect (fun (pat, guard, body) ->
        match pat with
        | OrPat (alts, _) -> alts |> List.map (fun altPat -> (altPat, guard, body))
        | _ -> [(pat, guard, body)]
    )
let clauses = expandOrPats clauses
```

### PAT-06 Guard Node in DecisionTree (MatchCompiler.fs)
```fsharp
// Extended DecisionTree DU
type DecisionTree =
    | Leaf of bindings: (string * Accessor) list * bodyIndex: int
    | Fail
    | Switch of Accessor * CtorTag * Accessor list * DecisionTree * DecisionTree
    | Guard of bindings: (string * Accessor) list * bodyIndex: int * ifFalse: DecisionTree

// Extended Clause
type Clause = {
    Tests: Test list
    Bindings: (string * Accessor) list
    BodyIndex: int
    HasGuard: bool
}

// In genMatch leaf case:
if first.Tests.IsEmpty then
    if first.HasGuard then
        Guard(first.Bindings, first.BodyIndex, genMatch rest)
    else
        Leaf(first.Bindings, first.BodyIndex)
```

### PAT-06 Guard Emission (Elaboration.fs, emitDecisionTree)
```fsharp
| MatchCompiler.Guard (bindings, bodyIdx, ifFalse) ->
    // Set up bindings
    let mutable bindOps = []
    let mutable bindEnv = env
    for (varName, acc) in bindings do
        let (v, ops) = resolveAccessor acc
        bindOps <- bindOps @ ops
        bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
    // Evaluate guard
    let (_pat, guardOpt, bodyExpr) = clauses.[bodyIdx]
    let guard = guardOpt.Value  // HasGuard=true guarantees Some
    let (guardVal, guardOps) = elaborateExpr bindEnv guard
    // Emit false (fallthrough) branch
    let falseLbl = freshLabel env (sprintf "guard_fail%d" bodyIdx)
    let falseOps = emitDecisionTree ifFalse
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some falseLbl; Args = []; Body = falseOps } ]
    // Emit body block
    let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
    resultType.Value <- bodyVal.Type
    let bodyLbl = freshLabel env (sprintf "match_body%d" bodyIdx)
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some bodyLbl; Args = [];
            Body = bodyOps @ [CfBrOp(mergeLabel, [bodyVal])] } ]
    bindOps @ guardOps @ [CfCondBrOp(guardVal, bodyLbl, [], falseLbl, [])]
```

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead |
|---------|-------------|-------------|
| OrPat shared code block | Shared MLIR basic block for all alternatives | Simple per-alternative clause expansion — easier, correct, slightly larger IR |
| Guard type coercion | Custom bool-to-i1 conversion | Guard expressions already elaborate to `I1` (comparison operators return `I1`) |

---

## Open Questions

1. **MatchCompiler.compile API change**
   - What we know: `compile` currently takes `(Pattern * int) list`; guards live in `clauses` in Elaboration.fs
   - Options: (a) pass `(Pattern * bool * int) list` to `compile`; (b) wrap in a new `compileWithGuards`; (c) post-process the tree in Elaboration after calling `compile`
   - Recommendation: Option (a) — cleanest, single change to public API

2. **TuplePat single-arm desugar case**
   - The existing `Match (scrutinee, [(TuplePat(...), None, body)], span)` special case (line 744) runs BEFORE the general Match case. After OrPat expansion, check that this special case still fires correctly (it should, since we expand OrPat → multiple clauses, so a single-arm tuple match won't have an OrPat).

---

## Sources

### Primary (HIGH confidence)
- Direct code analysis: `src/LangBackend.Compiler/MatchCompiler.fs` — full Jacobs algorithm, all stubs
- Direct code analysis: `src/LangBackend.Compiler/Elaboration.fs` lines 777–1007 — Match handler, emitDecisionTree, Leaf case
- `.planning/research/FEATURES.md` — AST types for MatchClause, OrPat, ConstPat
- `.planning/milestones/v2.0-phases/09-tuples/09-RESEARCH.md` — MatchClause type definition

### Secondary (MEDIUM confidence)
- `.planning/REQUIREMENTS.md` — PAT-06, PAT-07, PAT-08 requirement specs
- `.planning/ROADMAP.md` — Phase 13 success criteria

## Metadata

**Confidence breakdown:**
- CharConst implementation: HIGH — trivial reuse of IntLit path, confirmed by code inspection
- OrPat desugaring: HIGH — well-understood expansion, all expansion points identified in code
- Guard implementation: HIGH — Leaf pattern is fully understood; Guard node design follows naturally

**Research date:** 2026-03-26
**Valid until:** Stable (compiler architecture doesn't change between phases)
