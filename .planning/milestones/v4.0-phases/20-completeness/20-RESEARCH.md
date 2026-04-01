# Phase 20: Completeness - Research

**Researched:** 2026-03-27
**Domain:** F# compiler backend — first-class ADT constructors, nested ADT pattern matching (GEP chains), exception re-raise correctness, handler-body exception propagation
**Confidence:** HIGH

## Summary

Phase 20 closes four remaining edge cases in the existing compiler. All required infrastructure (closure mechanics, MatchCompiler decision trees, setjmp/longjmp exception runtime) is already present from Phases 5, 17, and 19. This phase is purely `Elaboration.fs` bug fixes and one new elaboration path. No new MLIR ops, no C runtime changes, no new NuGet packages.

Three sub-tasks map to four requirements (ADT-11, ADT-12, EXN-07, EXN-08):

**20-01 (ADT-11): First-class constructors.** When a unary ADT constructor like `Some` is passed as a higher-order function argument (e.g., `map Some xs`), the parser produces `Constructor("Some", None, span)` as the function expression in `App`. The current `App` case in `elaborateExpr` handles `Var(name)` as funcExpr but not `Constructor(name, None, _)`. The fix: add a pattern in `App` (before the general `| _ -> failwith` branch) that rewrites `App(Constructor(name, None, _), argExpr, _)` as `Constructor(name, Some argExpr, _)`. This is a one-line pattern match addition. For broader first-class usage (binding `let f = Some` and then calling `f 42`), the constructor must be wrapped as a closure; the existing `Lambda` elaboration (Phase 12 bare-lambda path) already produces a closure struct, so the fix is to also handle bare `Constructor(name, None, _)` in non-application position by wrapping it as `Lambda("__x", Constructor(name, Some(Var("__x", s)), s), s)` and re-elaborating.

**20-02 (ADT-12): Nested ADT pattern matching.** Multi-level patterns like `Node(Node(Leaf, v, Leaf), root, _)` fail with an MLIR type error: a payload slot is loaded as `i64` by `ensureAdtFieldTypes`, but when the inner `AdtCtor` test tries to use it as a pointer (for GEP into the inner node's tag slot), the SSA value has type `i64` not `!llvm.ptr`. The ptr-retype guard in `resolveAccessor` (line 976-978) correctly re-emits a Ptr load, but the previously-cached I64 value is still referenced by the already-emitted GEP in `emitCtorTest`. Fix: in `ensureAdtFieldTypes`, pre-load ADT payload at slot 1 as **Ptr** (not I64), because a payload that is itself an ADT block is always a pointer. The challenge is knowing at pre-load time whether the payload will be a primitive (I64) or a nested ADT block (Ptr). The correct fix is to pre-load as Ptr unconditionally (since `resolveAccessorTyped` handles re-typing from Ptr to I64 for primitives by re-loading). Alternatively: update `emitCtorTest` for `AdtCtor` to call `resolveAccessorTyped scrutAcc Ptr` (which is already done at line 1191) and ensure the cache is updated before any GEP; and update `ensureAdtFieldTypes` to load payload as Ptr. The tested failure mode is: `%t45 = llvm.load %t44 : !llvm.ptr -> i64` then `%t46 = llvm.getelementptr %t45[0]` — type conflict.

**20-03 (EXN-07 + EXN-08):** Investigation shows EXN-07 (re-raise on handler miss) is **already implemented and passing** (test 19-06-try-fallthrough.flt). The `exn_fail` block already calls `@lang_throw(exnPtrVal)` + `LlvmUnreachableOp`. EXN-08 (exception raised inside handler body propagates to outer handler) has a **real bug**: in the Leaf case of `emitDecisionTree` (line 1169-1187), `bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]` appends a branch after `LlvmUnreachableOp` when the body expression is a `Raise`. MLIR requires that `llvm.unreachable` is the last op in its block. Fix: mirror `appendReturnIfNeeded` — only append `CfBrOp` if `List.tryLast bodyOps <> Some LlvmUnreachableOp`. The same fix is needed in the Guard case (line 1228-1229) and in the duplicate `resolveAccessor2` / `emitDecisionTree2` code in the TryWith handler dispatch section (lines 1591-1810). Additionally, the empty-block error seen when TryWith nesting occurs at Let level suggests the `Let` terminator-patching logic may need extension for the case where the outer TryWith's body is itself multi-block.

**Primary recommendation:** Implement in order: 20-03 (EXN-08 bug is simplest and unblocked), 20-01 (ADT-11 one-liner), 20-02 (ADT-12 cache-type fix). Each task is independently verifiable. Do not conflate the tasks — the EXN-07/EXN-08 requirement in 20-03 is partially done (EXN-07) and partially buggy (EXN-08).

## Standard Stack

This phase adds no new libraries. All tooling is from prior phases.

### Core (already present)
| Component | Location | Purpose |
|-----------|----------|---------|
| `Elaboration.fs` | `src/FunLangCompiler.Compiler/` | Main fix target: `elaborateExpr` App/Lambda/Constructor cases, `emitDecisionTree` Leaf/Guard cases, `ensureAdtFieldTypes` |
| `MatchCompiler.fs` | `src/FunLangCompiler.Compiler/` | No changes needed — `splitClauses`/`desugarPattern` already handle nested ADtCtor correctly |
| `MlirIR.fs` | `src/FunLangCompiler.Compiler/` | No changes needed — all required ops exist |
| `lang_runtime.c` | `src/FunLangCompiler.Compiler/` | No changes needed — `lang_try_exit`/`lang_throw` already correct |

### Installation
No new packages. Build with `dotnet build`.

## Architecture Patterns

### Pattern 1: Constructor-as-Value — Rewrite in App case

**What:** When the parser sees `map Some xs`, the funcExpr of the outer App is `App(funcExpr=App(Var("map"), Constructor("Some", None), _), Var("xs"), _)`. The inner application of `map` to `Some` produces `App(Var("map"), Constructor("Some", None), _)`. In the general App case, the funcExpr is `Var("map")` which is handled, and argExpr is `Constructor("Some", None, _)`. But `Constructor` as an argument passes through `elaborateExpr`, which currently has no case for bare `Constructor(name, None, _)` (it would fall to `| _ -> failwith`).

Actually the current flow is: `Constructor("Some", None, _)` is passed as argExpr in `elaborateExpr env argExpr`. The bare `Constructor(name, None, _)` case currently matches the existing `| Constructor(name, None, _) ->` at line 1332 (nullary ctor allocator), which allocates a 16-byte block with tag and null payload. This works when `Some` is nullary (e.g., `None`), but for a unary constructor `Some`, we want a closure `fun x -> Some x`, not the nullary allocation.

The correct model: a bare `Constructor(name, None, _)` where the constructor has `Arity = 1` in `TypeEnv` should produce a closure, not a nullary block. The existing `| Constructor(name, None, _) ->` case allocates nullary regardless of arity — this is wrong for unary constructors used as values.

**Fix strategy:** In the `Constructor(name, None, _)` case, look up `TypeEnv`:
- If `arity = 0`: current behavior (allocate nullary block) — correct
- If `arity >= 1`: wrap as `Lambda("__ctor_arg", Constructor(name, Some(Var("__ctor_arg", s)), s), s)` and re-elaborate — produces a proper closure

**When to use:** Any unary+ constructor referenced without its argument (first-class use).

**Example:**
```fsharp
// In elaborateExpr, Constructor(name, None, _) case — existing code:
| Constructor(name, None, _) ->
    let info = Map.find name env.TypeEnv
    if info.Arity = 0 then
        // Nullary: allocate 16-byte block with tag and null payload
        let sizeVal     = { Name = freshName env; Type = I64 }
        // ... (existing code)
        (blockPtr, ops)
    else
        // Unary or multi-arg: wrap as lambda closure
        let s = Ast.unknownSpan
        let paramName = sprintf "__ctor_%d" env.Counter.Value
        env.Counter.Value <- env.Counter.Value + 1
        let wrapperExpr = Ast.Lambda(paramName, Ast.Constructor(name, Some(Ast.Var(paramName, s)), s), s)
        elaborateExpr env wrapperExpr
```

### Pattern 2: Nested ADT Type Fix in `ensureAdtFieldTypes`

**What:** `ensureAdtFieldTypes` pre-loads the payload slot (slot 1) of an ADT block into the accessor cache before the decision tree recurses into nested sub-patterns. Currently it pre-loads as I64 (default). When the sub-pattern is an AdtCtor (nested ADT block), the payload is a Ptr, not I64. The conflict: `emitCtorTest` for the inner `AdtCtor` calls `resolveAccessorTyped scrutAcc Ptr`, which detects the cached I64 and re-issues a GEP+load — but the old I64 SSA name is still in scope and the already-emitted code may reference it incorrectly.

**Fix strategy:** Change `ensureAdtFieldTypes` to pre-load as Ptr:

```fsharp
let ensureAdtFieldTypes (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
    let mutable ops = []
    if argAccs.Length >= 1 then
        // Pre-load payload as Ptr: ADT payloads that are nested ADT blocks ARE ptrs.
        // For integer payloads, resolveAccessorTyped will re-load as I64 when needed.
        let (_, payOps) = resolveAccessorTyped argAccs.[0] Ptr
        ops <- ops @ payOps
    ops
```

**Why Ptr is safe:** If the payload is actually an integer (e.g., `Some 42` where payload = raw i64), then when the pattern variable `n` is bound via `resolveAccessor acc`, it will call `resolveAccessorTyped acc I64` which re-issues a fresh GEP+load with i64 type. No correctness problem. The cache retype mechanism already handles this.

**Note on duplicate resolveAccessor2:** The TryWith case (exception handler dispatch) at lines 1591-1810 contains a duplicate `resolveAccessor2`/`resolveAccessorTyped2`/`ensureAdtFieldTypes2` set. The same fix must be applied to `ensureAdtFieldTypes2`.

### Pattern 3: EXN-08 Fix — Guard Raise in Leaf/Guard cases

**What:** When a handler arm body raises an exception (e.g., `| NotFound -> raise (Failure "msg")`), the body produces ops ending in `LlvmUnreachableOp`. The `emitDecisionTree` Leaf case appends `CfBrOp(mergeLabel, [bodyVal])` unconditionally, placing it after `LlvmUnreachableOp` — an MLIR validity error.

**Fix strategy:** In the Leaf case (and Guard case), only append `CfBrOp` if the body doesn't end with a noreturn terminator:

```fsharp
| MatchCompiler.Leaf (bindings, bodyIdx) ->
    // ... resolve bindings, elaborate body ...
    let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
    resultType.Value <- bodyVal.Type
    let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
    // Only branch to merge if body doesn't already terminate (e.g., Raise)
    let bodyTerminatedOps =
        match List.tryLast bodyOps with
        | Some LlvmUnreachableOp ->
            bodyOps  // already terminated — no merge branch
        | Some (CfBrOp _) | Some (CfCondBrOp _) ->
            bodyOps  // multi-block body, inner merge already handled
        | _ ->
            bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some bodyLabel; Args = []; Body = bodyTerminatedOps } ]
    bindOps @ [ CfBrOp(bodyLabel, []) ]
```

The same fix applies to the Guard case's body block emission (line 1228-1240) and to the duplicate `emitDecisionTree2` in the TryWith exception handler section.

### Anti-Patterns to Avoid

- **Changing MatchCompiler.fs for ADT-12:** The MatchCompiler correctly produces `Field(scrutAcc, 1)` for the payload accessor. The bug is in `ensureAdtFieldTypes` (the type hint passed to the cache), not in the decision tree structure.
- **Adding new MlirOp DU cases:** All required ops exist. No new cases needed.
- **Changing `lang_runtime.c`:** EXN-07 and EXN-08 are purely elaboration bugs, not runtime bugs.
- **Modifying the `exn_fail` block:** EXN-07 is already implemented and passing. Do not touch `exn_fail`.
- **Changing `resolveAccessor`/`resolveAccessorTyped` general logic:** The ptr-retype guard is correct. The fix is upstream (what type is pre-loaded into the cache).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Closure wrapping for ctors | Custom closure struct emission | Existing `Lambda` elaboration path (line 1255) | Phase 12 already produces correct closure structs for bare lambdas; re-using avoids divergence |
| Type inference for nested ADT payload | Type analysis pass | Cache retype mechanism in `resolveAccessorTyped` | The retype guard (lines 990-1017) already handles I64↔Ptr mismatches; just feed the right initial type |
| Block terminator checking | New `isTerminator2` function | `List.tryLast bodyOps` pattern from existing Let case | Same pattern used at line 481 — consistent |

**Key insight:** Every problem in Phase 20 has a micro-fix using the existing infrastructure. The solutions are 5-20 lines each, not architectural changes.

## Common Pitfalls

### Pitfall 1: Duplicate resolveAccessor2/emitDecisionTree2 code
**What goes wrong:** The TryWith case has a second copy of all the accessor and decision-tree emission code (lines 1591-1810) with `2` suffix names. Fixes applied to the Match case (resolveAccessor, ensureAdtFieldTypes) must also be applied to the TryWith handler dispatch code (resolveAccessor2, ensureAdtFieldTypes2).
**Why it happens:** The Match and TryWith cases can't share the closures because the accessor cache and environment differ.
**How to avoid:** Apply every fix to both the `_` suffix and `2` suffix versions.
**Warning signs:** Nested ADT patterns in exception handler arms fail while in normal match they work.

### Pitfall 2: Nullary vs. unary constructor confusion
**What goes wrong:** The `Constructor(name, None, _)` case covers BOTH actual nullary constructors (like `None`, `Leaf`) AND unary constructors used as values (like `Some`, `Node`). Current code always allocates a nullary block, which is wrong for unary.
**Why it happens:** The parser produces the same AST for `None` and bare `Some` (both are `Constructor(name, None, span)`).
**How to avoid:** Look up `TypeEnv` arity before deciding whether to allocate nullary or wrap as lambda.
**Warning signs:** `let f = Some in f 42` compiles but returns a garbage value (got a nullary ADT block instead of a closure).

### Pitfall 3: EXN-07 vs EXN-08 scope confusion
**What goes wrong:** The ROADMAP lists EXN-07 and EXN-08 together in 20-03, but EXN-07 is already done (19-06-try-fallthrough.flt passes). Implementing EXN-07 again or "fixing" the exn_fail block will break passing tests.
**Why it happens:** Phase 19 plan 03 implemented re-raise but the requirement wasn't officially ticked off in the roadmap.
**How to avoid:** Run the existing 19-06 test first to confirm it passes. Only fix EXN-08 (Raise inside handler arm produces invalid MLIR).
**Warning signs:** Touching `exn_fail` block construction causes 19-06 to fail.

### Pitfall 4: Multi-block TryWith body patching for Let case
**What goes wrong:** When `let _ = try <expr> with ...` is elaborated and `<expr>` itself is a multi-block expression (another TryWith), the outer TryWith body block patching (lines 1543-1556) may conflict with the Let case's `blocksBeforeBind` patching.
**Why it happens:** Two different levels of "patch the last block" logic interact.
**How to avoid:** Write targeted E2E tests for exactly these nesting combinations before and after the fix. Confirm the MLIR output is valid before running the binary.
**Warning signs:** "empty block" MLIR error or "expect at least a terminator" on @main.

### Pitfall 5: Guard case also needs the Raise-body fix
**What goes wrong:** The Guard case (line 1210-1240) also emits a body block and appends to it, with the same issue as Leaf.
**Why it happens:** The Guard case is structurally identical to Leaf but is a separate code path.
**How to avoid:** Apply the `appendIfNotTerminated` pattern to the Guard body block too.
**Warning signs:** Tests with `when`-guard expressions that raise compile fail with same MLIR error as EXN-08.

## Code Examples

### Existing Lambda closure emission (reuse for constructor wrapping)
```fsharp
// Source: Elaboration.fs line 1255 (Phase 12 bare Lambda case)
// This is the pattern to REUSE for ADT-11.
// Instead of writing new closure emission code, re-elaborate as Lambda:
| Constructor(name, None, _) ->
    let info = Map.find name env.TypeEnv
    if info.Arity = 0 then
        // Nullary ctor: existing allocate-16-byte-block logic
        (blockPtr, allocOps)
    else
        // Unary ctor as first-class value: wrap as lambda and re-elaborate
        let s = unknownSpan
        let paramName = sprintf "__ctor_%d_%s" env.Counter.Value name
        env.Counter.Value <- env.Counter.Value + 1
        elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))
```

### Fixed ensureAdtFieldTypes (pre-load as Ptr)
```fsharp
// Source: Elaboration.fs ~line 1122 — ensureAdtFieldTypes fix
let ensureAdtFieldTypes (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
    let mutable ops = []
    if argAccs.Length >= 1 then
        // Load payload as Ptr. ADT payloads that are nested ADT/tuple blocks are ptrs.
        // For scalar (i64) payloads, the caller will re-load via resolveAccessorTyped I64.
        let (_, payOps) = resolveAccessorTyped argAccs.[0] Ptr
        ops <- ops @ payOps
    ops
```

### Fixed Leaf case — skip merge branch when body noreturn
```fsharp
// Source: Elaboration.fs ~line 1165 (emitDecisionTree Leaf case) — EXN-08 fix
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
    // Only append merge branch if body is not already terminated (noreturn/multi-block)
    let terminatedOps =
        match List.tryLast bodyOps with
        | Some LlvmUnreachableOp -> bodyOps          // Raise in arm body — noreturn
        | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps  // already branched
        | _ -> bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some bodyLabel; Args = []; Body = terminatedOps } ]
    bindOps @ [ CfBrOp(bodyLabel, []) ]
```

### Test: ADT-11 (first-class constructor)
```
// New test file: 20-01-firstclass-ctor.flt
type Option = None | Some of int

let rec map f lst =
  match lst with
  | [] -> 0
  | h :: t -> (map f t)

let _ = map Some [1; 2; 3]
// Expected output: 0
// (doesn't matter what map returns, just verifies it compiles and runs)
```

A simpler targeted test:
```
type Option = None | Some of int
let apply f x = f x
let _ =
  match apply Some 42 with
  | Some n -> n
  | None -> 0
// Expected output: 42
```

### Test: ADT-12 (nested constructor pattern)
```
type Tree = Leaf | Node of Tree * int * Tree
let inner = Node(Leaf, 5, Leaf) in
let outer = Node(inner, 10, Leaf) in
match outer with
| Node(Node(l, v, r), root, _) -> root + v
| _ -> 0
// Expected output: 15  (10 + 5)
```

### Test: EXN-08 (raise inside handler arm)
```
exception Failure of string
exception NotFound

let inner =
  try raise NotFound
  with
  | NotFound -> raise (Failure("from handler"))

let _ =
  try inner
  with
  | Failure msg -> 42
// Expected output: 42
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Constructor as nullary block | Constructor as closure when arity > 0 | Phase 20 | Enables HOF patterns like `List.map Some` |
| I64 pre-load for ADT payload | Ptr pre-load for ADT payload | Phase 20 | Enables multi-level nested ADT pattern matching |
| Unconditional merge branch in Leaf | Conditional merge branch (skip if noreturn) | Phase 20 | Enables Raise inside handler arms |

## Open Questions

1. **Guard + Raise interaction in exception handler dispatch**
   - What we know: The Guard case (match with `when` clause) has the same Leaf-body pattern and would suffer the same EXN-08 bug.
   - What's unclear: Is there a test that exercises `when` guards in TryWith handler arms with a Raise in the guard-success body?
   - Recommendation: Apply the same fix to Guard case as to Leaf; add a test if one doesn't exist.

2. **Multi-block TryWith body nested in Let**
   - What we know: The "empty block" error appeared in one test configuration where TryWith was bound in a Let and the outer context was also a TryWith.
   - What's unclear: Exact conditions under which `Let`'s `blocksBeforeBind` logic interacts badly with nested TryWith.
   - Recommendation: After fixing EXN-08, run the full test suite and investigate any remaining empty-block errors with targeted MLIR inspection.

3. **Multi-arg constructor arity in TypeEnv**
   - What we know: `GadtConstructorDecl` with multiple `argTypes` still gets `arity = 1` in `prePassDecls` (line 1912-1913), matching the "multi-arg = one Tuple arg" convention.
   - What's unclear: If a GADT constructor has 3 type args, does the first-class wrapping `fun x -> C x` work correctly?
   - Recommendation: Treat as out-of-scope for Phase 20; standard ADTs with multi-arg are handled via Tuple argument.

## Sources

### Primary (HIGH confidence)
- Direct code inspection of `Elaboration.fs` (2025 lines) — all patterns verified by reading actual implementation
- Direct code inspection of `MatchCompiler.fs` (270 lines) — `splitClauses` and `desugarPattern` verified
- Live testing: nested ADT patterns confirmed broken with MLIR type error, first-class constructors confirmed broken with elaboration error, EXN-07 confirmed working (19-06 passes), EXN-08 confirmed broken with `llvm.unreachable not last op` error

### Secondary (MEDIUM confidence)
- Parser.fsy inspection: confirmed `Constructor(name, None, span)` is produced for bare uppercase identifiers (line 283-284), and constructor application becomes `Constructor(name, Some arg, span)` (lines 259-261)
- Phase 17 RESEARCH.md and Phase 19 RESEARCH.md: confirmed prior design decisions (16-byte ADT block, slot-0 tag / slot-1 payload, setjmp/longjmp scheme)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new dependencies, all tools verified present
- Architecture: HIGH — root causes identified by reading code and running failing tests live
- Pitfalls: HIGH — all pitfalls discovered by actual test failures and code trace

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable internal codebase; no external dependencies)
