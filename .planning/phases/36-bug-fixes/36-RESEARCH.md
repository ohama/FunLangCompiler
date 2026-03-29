# Phase 36: Bug Fixes - Research

**Researched:** 2026-03-30
**Domain:** MLIR codegen correctness — sequential if blocks, bool coercion, for-in mutable capture
**Confidence:** HIGH

## Summary

Phase 36 fixes three known compiler bugs that block real-world FunLexYacc code patterns. All three bugs were documented during v9.0 (Phases 31–35) and explicitly deferred. The root causes are understood from direct codebase investigation, and fixes are clear and mechanical.

FIX-01 (for-in mutable capture segfault) was likely resolved by the Phase 35 `freeVars` fix that added Cons/List/EmptyList cases (commit `01c173f`). Manual testing confirms the patterns that previously segfaulted now work correctly. Phase 36 must verify this and write the required test.

FIX-02 (two consecutive `if` expressions produce invalid MLIR) is a real active bug. Root cause: the `Let`/`LetPat` "terminator patching" logic captures `blocksAfterBind` AFTER `bodyExpr` is elaborated, meaning it patches the WRONG (innermost) merge block instead of the outer if's merge block. Fix: capture `blocksAfterBind` BEFORE elaborating `bodyExpr`.

FIX-03 (Bool module function in conditions) is partially fixed. `if Bool_mod_fn x then ...` works due to Phase 35's I64 coercion in the `If` case. But `Bool_mod_fn x && ...`, `Bool_mod_fn x || ...`, and `while Bool_mod_fn x do ...` still fail because `And`, `Or`, and `WhileExpr` elaboration do not coerce I64 operands to I1 before `CfCondBrOp`.

**Primary recommendation:** Fix FIX-02 first (it blocks the most code patterns), then FIX-03 (And/Or/While I64 coercion), then FIX-01 (verify + test).

## Standard Stack

This phase operates entirely within the existing compiler stack. No new dependencies.

### Core
| Component | Location | Purpose | Role |
|-----------|----------|---------|------|
| Elaboration.fs | `src/LangBackend.Compiler/` | MLIR codegen from AST | All three fixes are here |
| MlirIR.fs | `src/LangBackend.Compiler/` | MLIR op types | No changes needed |
| Printer.fs | `src/LangBackend.Compiler/` | MLIR text output | No changes needed |
| lang_runtime.c/h | `src/LangBackend.Compiler/` | C runtime | No changes needed |

### Supporting
| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| mlir-opt | LLVM 20 | Validates generated MLIR | Run-time validation; fails with "empty block" or type mismatch errors |
| dotnet run | .NET 9 | Build and run compiler | All testing |

**Build and test:**
```bash
dotnet run --project src/LangBackend.Cli/LangBackend.Cli.fsproj -- <input.flt> -o /tmp/out
```

## Architecture Patterns

### Compiler Pipeline
```
src/
├── LangBackend.Compiler/
│   ├── Elaboration.fs    # AST -> MlirModule (all bug fixes go here)
│   ├── MlirIR.fs         # IR data types
│   ├── Printer.fs        # MlirModule -> text
│   ├── Pipeline.fs       # mlir-opt + translate + clang
│   ├── lang_runtime.c    # C runtime
│   └── lang_runtime.h    # C runtime headers
├── LangBackend.Cli/
│   └── Program.fs        # Entry point
tests/compiler/           # .flt E2E tests (shell script + input + expected output)
```

### Pattern 1: FIX-02 — Terminator Patching Fix

**What:** When `Let(name, bindExpr, bodyExpr)` elaboration detects that `bops` (from bindExpr) ends with a terminator (CfCondBrOp from `If`), it must place `rops` (from bodyExpr) into the merge block of the OUTER if — not the globally last side block.

**Root cause (confirmed by MLIR dump):**

Generated MLIR from `let _ = if c1 then t1 in let _ = if c2 then t2 in 0`:

```
^merge2(%t5: i64):
  <empty — missing second if's ops>

^then3:
  ...
  cf.br ^merge5(...)
^else4:
  ...
  cf.br ^merge5(...)
^merge5(%t11: i64):
  %t6 = arith.constant 1 : i1
  cf.cond_br %t6, ^then3, ^else4  ← second if's CfCondBrOp here (WRONG block)
  %t12 = arith.constant 0 : i64
  return %t12 : i64  ← two terminators in same block
```

The `^merge2` block is empty (mlir-opt fails: "empty block: expect at least a terminator").

**Why it happens:** In `Let`/`LetPat` at line 651–658 and 673–679, the terminator-patching code:
1. Elaborates `bindExpr` → adds blocks [then0, else1, merge2]
2. Elaborates `bodyExpr` → adds MORE blocks [then3, else4, merge5] (inner if)
3. Patches `List.last innerBlocks` = merge5 with rops (WRONG)
4. merge2 stays empty

**Fix:** Capture `blocksAfterBind` (the block count AFTER `bindExpr`, BEFORE `bodyExpr`) and use `blocksAfterBind - 1` as the target index:

```fsharp
// In Let case (line 626), after elaborating bindExpr:
let blocksAfterBind = env.Blocks.Value.Length  // NEW: capture BEFORE bodyExpr
let (rv, rops) = elaborateExpr env' bodyExpr
let isTerminator op = match op with CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true | _ -> false
match List.tryLast bops with
| Some op when isTerminator op && blocksAfterBind > blocksBeforeBind ->
    let innerBlocks = env.Blocks.Value
    let targetIdx = blocksAfterBind - 1  // merge block from OUTER if
    let targetBlock = innerBlocks.[targetIdx]
    let patchedTarget = { targetBlock with Body = rops @ targetBlock.Body }
    env.Blocks.Value <- innerBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
    (rv, bops)
| _ ->
    (rv, bops @ rops)
```

**Apply same fix to:** `LetPat (WildcardPat _, ...)` case (line 665), which has identical patching logic.

### Pattern 2: FIX-03 — I64→I1 Coercion in And/Or/While

**What:** `And`, `Or`, and `WhileExpr` use `leftVal`/`condVal` directly in `CfCondBrOp`, but module-returning Bool functions return I64 (not I1). The `If` case already has the right coercion pattern.

**Existing fix in `If` case (line 807–813, reference pattern):**
```fsharp
| If (condExpr, thenExpr, elseExpr, _) ->
    let (condVal, condOps) = elaborateExpr env condExpr
    let (i1CondVal, coerceCondOps) =
        if condVal.Type = I64 then
            let zeroVal = { Name = freshName env; Type = I64 }
            let boolVal = { Name = freshName env; Type = I1  }
            (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", condVal, zeroVal)])
        else (condVal, [])
    // ... uses i1CondVal in CfCondBrOp
```

**Apply same pattern to `And` (line 834):**
```fsharp
| And (lhsExpr, rhsExpr, _) ->
    let (leftVal, leftOps) = elaborateExpr env lhsExpr
    let (i1LeftVal, coerceLeftOps) =           // NEW
        if leftVal.Type = I64 then
            let zeroVal = { Name = freshName env; Type = I64 }
            let boolVal = { Name = freshName env; Type = I1  }
            (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", leftVal, zeroVal)])
        else (leftVal, [])
    let evalRightLabel = freshLabel env "and_right"
    let mergeLabel     = freshLabel env "and_merge"
    let (rightVal, rightOps) = elaborateExpr env rhsExpr
    let (i1RightVal, coerceRightOps) =         // NEW
        if rightVal.Type = I64 then
            let zeroVal = { Name = freshName env; Type = I64 }
            let boolVal = { Name = freshName env; Type = I1  }
            (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", rightVal, zeroVal)])
        else (rightVal, [])
    let mergeArg = { Name = freshName env; Type = I1 }
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some evalRightLabel; Args = [];
            Body = rightOps @ coerceRightOps @ [CfBrOp(mergeLabel, [i1RightVal])] } ]  // use i1RightVal
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
    (mergeArg, leftOps @ coerceLeftOps @ [CfCondBrOp(i1LeftVal, evalRightLabel, [], mergeLabel, [i1LeftVal])])  // use i1LeftVal
```

**Apply same pattern to `Or` (line 845):** identical structure.

**Apply same pattern to `WhileExpr` (line 2947):** coerce `condVal` and `condVal2` before `CfCondBrOp`.

### Pattern 3: FIX-01 — For-in Mutable Capture Verification

**What:** The segfault that was documented in Phase 34-03 was: `let mut sum = 0 in for i in (1::[]) do sum <- i` crashes at runtime.

**Status (confirmed by manual testing 2026-03-30):** All variants tested work correctly:
- `for x in list do mut_var <- x` — works (returns correct value)
- `for x in xs do mut_var <- mut_var + x` — works (accumulation)
- `for (k,v) in ht do mut_var <- mut_var + v` — works
- `for x in hs do mut_var <- mut_var + x` — works

**Root cause of original segfault (Phase 35 `01c173f` fixed it):** `freeVars` did not handle `Cons`, `List`, `EmptyList` expressions — so a `Lambda` body containing inline list cons expressions (`1::[]`) did not correctly capture outer mutable variable refs. The closure struct was built with zero captures, and the mutable cell pointer was never stored. At runtime, the closure tried to GEP into an invalid/uninitialized slot → segfault.

**Phase 36 action:** Write the E2E test (FIX-01 success criteria), confirm it passes, and mark as done.

### Anti-Patterns to Avoid
- **Patching `List.last`:** The bug in FIX-02 is precisely that `List.last innerBlocks` was used. Always use `blocksAfterBind - 1` (index of the OUTER expression's last block, captured BEFORE inner elaboration).
- **Multiple patches:** When FIX-02 is fixed, the inner `Let` (for the second if) will ALSO try to patch its own merge block — and it should target the inner merge block. This is correct behavior. Do not change the inner patching logic.
- **Coercing merge args:** For `And`/`Or`, the merge block arg type must be `I1`. The rightVal that branches to merge must also be coerced to `I1` before being used as `CfBrOp` arg.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| I64→I1 coercion helper | Custom function | Inline pattern from `If` case | Already established in codebase, 3 lines |
| Block index tracking | Separate data structure | Local `blocksAfterBind` variable | Simple and already pattern-consistent |
| MLIR validation | Custom validator | mlir-opt error output | mlir-opt gives precise location of violations |

**Key insight:** All three fixes follow established patterns already in Elaboration.fs. Copy the I64→I1 coercion from the `If` case. Use the same "blocks before bind" pattern already in `Let`/`LetPat`.

## Common Pitfalls

### Pitfall 1: FIX-02 — Wrong Block Count Variable
**What goes wrong:** Using `env.Blocks.Value.Length` at the time of patching (AFTER bodyExpr elaboration) instead of after bindExpr.
**Why it happens:** The current code already captures `blocksBeforeBind` before `bindExpr`, but the inner `bodyExpr` is elaborated BEFORE the patching decision. After bodyExpr, `env.Blocks.Value` has grown.
**How to avoid:** Add `let blocksAfterBind = env.Blocks.Value.Length` immediately after `elaborateExpr env bindExpr` and BEFORE `elaborateExpr env' bodyExpr`.
**Warning signs:** Empty blocks in MLIR output; "empty block" mlir-opt error.

### Pitfall 2: FIX-03 — And/Or Merge Block Arg Type
**What goes wrong:** When coercing rightVal to i1 for CfBrOp, the merge block arg must also be `I1`. If rightVal is still I64 when branching to merge, type mismatch occurs.
**Why it happens:** The merge block is declared as `Args = [mergeArg]` with `Type = I1`. Any value flowing into it via CfBrOp args must also be `I1`.
**How to avoid:** Coerce BOTH leftVal (for the short-circuit branch) AND rightVal (for the eval_right branch) to I1.
**Warning signs:** "use of value expects different type than prior uses: 'i1' vs 'i64'" from mlir-opt.

### Pitfall 3: FIX-02 — Both Let and LetPat Need Fixing
**What goes wrong:** Fixing `Let` but not `LetPat(WildcardPat)` leaves the bug for `let _ = if ... in let _ = if ...` patterns.
**Why it happens:** `Let("_", ...)` uses `LetPat(WildcardPat, ...)` in some parsing paths, or `Let("_", ...)` case may fall to LetPat.
**How to avoid:** Apply the same fix to BOTH `Let` case (line 626) AND `LetPat (WildcardPat _, ...)` case (line 665).
**Warning signs:** Some sequential-if tests pass but others fail.

### Pitfall 4: WhileExpr Has Two Condition Elaborations
**What goes wrong:** `WhileExpr` elaborates `condExpr` TWICE: once for the header (`condVal`) and once for the back-edge re-evaluation (`condVal2`). Both need I1 coercion.
**Why it happens:** The while loop needs to re-evaluate the condition after each iteration.
**How to avoid:** Apply I64→I1 coercion to both `condVal` at line 2986 and `condVal2` at line 2964.

## Code Examples

### FIX-02: Minimal Reproducer
```
// Input (tests/compiler/36-XX-sequential-if.flt):
let _ = if true then println "first"
let _ = if true then println "second"
let _ = 0
// Expected output: first\nsecond\n0
```

### FIX-03: And/Or Reproducer
```
// Input:
let _ = if String.endsWith "hello.fun" ".fun" && String.startsWith "hello" "h" then println "yes" else println "no"
// Expected output: yes
// Currently fails: "use of value expects different type: 'i1' vs 'i64'"
```

### FIX-01: Mutable Capture Test Pattern
```
// Input:
let mut count = 0
let xs = [1; 2; 3]
let _ = for x in xs do count <- count + x
let _ = println (to_string count)
// Expected output: 6
// Currently PASSES (bug was fixed in Phase 35)
```

### FIX-02: blocksAfterBind Fix Location

The key change is in `elaborateExpr` for the `Let` case (Elaboration.fs ~line 626):

```fsharp
// BEFORE (buggy):
| Let (name, bindExpr, bodyExpr, _) ->
    let blocksBeforeBind = env.Blocks.Value.Length
    let (bv, bops) = elaborateExpr env bindExpr
    // ... env' setup ...
    let (rv, rops) = elaborateExpr env' bodyExpr
    let isTerminator op = ...
    match List.tryLast bops with
    | Some op when isTerminator op && env.Blocks.Value.Length > blocksBeforeBind ->
        let innerBlocks = env.Blocks.Value
        let lastBlock = List.last innerBlocks  // WRONG: last of ALL blocks including inner if's
        ...

// AFTER (fixed):
| Let (name, bindExpr, bodyExpr, _) ->
    let blocksBeforeBind = env.Blocks.Value.Length
    let (bv, bops) = elaborateExpr env bindExpr
    let blocksAfterBind = env.Blocks.Value.Length  // CAPTURE BEFORE bodyExpr
    // ... env' setup ...
    let (rv, rops) = elaborateExpr env' bodyExpr
    let isTerminator op = ...
    match List.tryLast bops with
    | Some op when isTerminator op && blocksAfterBind > blocksBeforeBind ->
        let innerBlocks = env.Blocks.Value
        let targetIdx = blocksAfterBind - 1  // CORRECT: outer if's merge block
        let targetBlock = innerBlocks.[targetIdx]
        let patchedTarget = { targetBlock with Body = rops @ targetBlock.Body }
        env.Blocks.Value <- innerBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
        (rv, bops)
    | _ ->
        (rv, bops @ rops)
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| freeVars missing Cons/List/EmptyList | Fixed in 01c173f | Phase 35 | FIX-01 segfault resolved |
| If cond: no I64→I1 coercion | If case coerces I64→I1 | Phase 35 | if Bool_fn x works; && still broken |
| Let patching: last block | Need: outer merge block | FIX-02 this phase | Two sequential ifs work |
| And/Or/While: no I64 coercion | Need: coerce leftVal/condVal | FIX-03 this phase | Bool module fns in && work |

**Deprecated/outdated:**
- `<> 0` workaround in test files: remove once FIX-03 is done
- `to_string(bool)` pattern in tests instead of `if bool_fn`: can be replaced with direct if

## Open Questions

1. **FIX-01 already fixed?**
   - What we know: Phase 35 freeVars fix resolved the specific patterns that segfaulted
   - What's unclear: Whether there are remaining edge cases in other freeVars paths (ForExpr, WhileExpr, LetMut etc.)
   - Recommendation: Run success criteria test pattern; if it passes, mark FIX-01 done, write test, move on

2. **FIX-02 interaction with While/For/Match**
   - What we know: The Let/LetPat patching fix is needed for If; While and For also produce terminators
   - What's unclear: Whether the same blocksAfterBind fix is needed for Let after a While/For body
   - Recommendation: The same fix applies uniformly — blocksAfterBind captures ALL terminator-generating expression types, not just If

3. **FIX-03 inside closures**
   - What we know: And/Or/While in top-level function bodies fail
   - What's unclear: Whether the fix is also needed inside Lambda/LetRec inner bodies
   - Recommendation: The same Elaboration.fs code handles both — fix is universal

## Sources

### Primary (HIGH confidence)
- Direct code inspection of `Elaboration.fs` (lines 626–680, 805–855, 2947–3000)
- Direct MLIR dump from compiler run (confirmed empty block + type mismatch errors)
- Phase 35 commit `01c173f` (freeVars fix that resolved FIX-01)
- Phase 31 summary (31-01-SUMMARY.md) — documents sequential if limitation
- v9.0 ROADMAP (v9.0-ROADMAP.md) — documents deferred issues

### Secondary (MEDIUM confidence)
- STATE.md (2026-03-30) — "Root cause: closure captures stale stack pointer" (partially superseded by Phase 35 fix)
- Phase 34-03 summary — documents specific patterns that segfaulted

### Tertiary (LOW confidence)
- None

## Metadata

**Confidence breakdown:**
- FIX-01 status: HIGH — manually confirmed fixed, root cause understood
- FIX-02 root cause: HIGH — MLIR dump confirms exact block layout; fix is clear
- FIX-03 scope: HIGH — error messages pinpoint And/Or/While as broken contexts

**Research date:** 2026-03-30
**Valid until:** 2026-04-30 (compiler internals are stable)
