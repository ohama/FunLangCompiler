# Phase 11: Pattern Matching - Research

**Researched:** 2026-03-26
**Domain:** MLIR cf.cond_br decision chains, pattern type dispatch, runtime failure fallback
**Confidence:** HIGH

---

## Summary

Phase 11 implements full pattern matching compilation in LangBackend. The phase extends the `elaborateExpr` `Match` case (currently limited to the two-arm `EmptyListPat + ConsPat` form from Phase 10) into a general-purpose sequential decision chain that handles all six v2 pattern types: `VarPat`, `WildcardPat`, `ConstPat` (int/bool/string), `EmptyListPat`, `ConsPat`, and `TuplePat`. A `@lang_match_failure` fallback block is emitted as the final branch target when no arm matches.

The compilation strategy is a linear decision chain — each arm is compiled to a condition test followed by `cf.cond_br`: true branch goes to the arm's body block, false branch falls through to the next arm's test. The last arm either accepts unconditionally (wildcard/variable) or falls through to the `@lang_match_failure` block. This reuses the same multi-block `cf.cond_br` mechanism already working for `if-else`, `&&`, `||`, and list match from Phases 3 and 10.

The `@lang_match_failure` function will be added to `lang_runtime.c` as a C function that prints an error message to stderr and calls `exit(1)`. It is declared as an external func in the module and called via `LlvmCallVoidOp` in the terminal failure block. This is the simplest approach and consistent with existing builtin patterns.

**Primary recommendation:** Replace the Phase 10 `Match` case in Elaboration with a full `compileMatchArms` recursive function that generates a decision chain. Implement pattern condition test via a `testPattern` helper (returns an I1 condition + ops and an env with variable bindings). Add `LlvmUnreachableOp` to MlirIR for the post-failure terminator.

---

## Standard Stack

### Core

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| `cf.cond_br` chain | MLIR 20 | Sequential pattern arm dispatch | Already used for if-else and list match; extends naturally to N-arm match |
| `arith.cmpi eq` | MLIR 20 | Constant int/bool pattern comparison | Same as comparison ops from Phase 3; `eq` predicate |
| `llvm.icmp "eq"` on `!llvm.ptr` | MLIR 20 | EmptyListPat null check | Same as Phase 10 list match; pointer equality to llvm.mlir.zero |
| `strcmp` + `arith.cmpi eq` on I32 | libc | String constant pattern | Same pattern as Phase 8 string equality; @strcmp already declared |
| `llvm.getelementptr` | MLIR 20 | TuplePat and ConsPat field extraction | `LlvmGEPLinearOp` (slot) and `LlvmGEPStructOp` (struct field) already exist |
| `@lang_match_failure` | lang_runtime.c | Non-exhaustive match error | New C function in lang_runtime.c; prints to stderr + exit(1) |
| `llvm.unreachable` | MLIR 20 | Terminator after void call | New `LlvmUnreachableOp` needed in MlirIR for post-failure block |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `LlvmCallVoidOp` | existing | Call `@lang_match_failure` | Already used for `@GC_init`; void return |
| `LlvmNullOp` | existing (Phase 10) | EmptyListPat null comparand | Reuse from Phase 10 |
| `LlvmIcmpOp` | existing (Phase 10) | Pointer null comparison | Reuse from Phase 10 |
| `LlvmLoadOp` / `LlvmStoreOp` | existing | TuplePat and ConsPat field load | Same as Phase 9/10 GEP+load |
| `ArithExtuIOp` | existing (Phase 8) | I1 bool const extend for ABI | Already used; not needed for pattern tests (I1 direct for cond_br) |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Sequential `cf.cond_br` chain | Decision tree with sharing | Decision tree is more efficient but far more complex; sequential chain is correct and sufficient for v2 |
| `llvm.unreachable` after failure call | `ReturnOp` with dummy value | Unreachable is semantically correct after noreturn call; dummy return would mislead optimizer |
| `@lang_match_failure` in runtime.c | Inline `printf` + `exit` | Runtime function is cleaner; keeps IR smaller; consistent with other builtins |
| `abort()` | `exit(1)` in lang_match_failure | `abort()` produces SIGABRT (non-zero); `exit(1)` produces clean exit code 1 which is easier to test |

---

## Architecture Patterns

### Pattern: Sequential Decision Chain

Each match arm becomes a test block + body block. The chain looks like:

```
entry block:
  %cond0 = <test pattern 0 against scrutinee>
  cf.cond_br %cond0, ^body0, ^test1

^body0:
  <arm 0 body ops>
  cf.br ^merge(%result0 : T)

^test1:
  %cond1 = <test pattern 1>
  cf.cond_br %cond1, ^body1, ^test2

^body1:
  <arm 1 body ops>
  cf.br ^merge(%result1 : T)

^test2:
  // wildcard — unconditional true, so no test needed:
  cf.br ^body2

^body2:
  <arm 2 body ops>
  cf.br ^merge(%result2 : T)

^merge(%result : T):
  // use %result

// If no wildcard/variable arm and all const tests failed:
^match_fail:
  llvm.call @lang_match_failure() : () -> ()
  llvm.unreachable
```

The merge block has a block argument typed to match all arm result types. This is identical to the existing `If` elaboration block structure.

### Pattern: Pattern Condition Test

A `testPattern` helper takes (env, scrutVal, pattern) and returns (condVal: MlirValue option, testOps: MlirOp list, bindEnv: ElabEnv):

- **WildcardPat / VarPat**: `condVal = None` (unconditional match). For VarPat, add scrutVal to bindEnv under the variable name. No test ops.
- **ConstPat(IntConst n)**: emit `ArithConstantOp(%k, n)` + `ArithCmpIOp(%cond, "eq", scrutVal, %k)`. condVal = Some %cond.
- **ConstPat(BoolConst b)**: emit `ArithConstantOp(%k, 0|1)` + `ArithCmpIOp(%cond, "eq", scrutVal, %k)`. condVal = Some %cond. scrutVal.Type = I1.
- **ConstPat(StringConst s)**: elaborate the string literal to get a string struct ptr, GEP field 1 to get data ptr, load data ptr, call `@strcmp`, compare result to 0 with `arith.cmpi eq`. condVal = Some %cond.
- **EmptyListPat**: emit `LlvmNullOp(%null)` + `LlvmIcmpOp(%cond, "eq", scrutVal, %null)`. condVal = Some %cond.
- **ConsPat(hPat, tPat)**: emit null check (isNotNull = llvm.icmp "ne"), then GEP head/tail, load, recursively bind hPat and tPat. condVal = Some isNotNull. Head at slot 0 (direct load), tail at slot 1 (GEP + load).
- **TuplePat(pats)**: condVal = None (tuples always match at this level). GEP each field, load with correct type (Ptr for nested TuplePat, I64 otherwise), add to bindEnv. TuplePat is unconditionally matching — the structure check is implicit.

### Pattern: @lang_match_failure Block

The failure fallback is the final `false` target in the last arm's `cf.cond_br`. It is a dedicated basic block:

```
^match_fail:
  llvm.call @lang_match_failure() : () -> ()
  llvm.unreachable
```

`@lang_match_failure` must be declared in `ExternalFuncs` with `ExtReturn = None, IsVarArg = false, ExtParams = []`.

`LlvmUnreachableOp` is a new zero-field MlirOp case. It prints as `llvm.unreachable`. It is needed because MLIR requires every basic block to have a terminator, and `LlvmCallVoidOp` is not a terminator.

### Pattern: Wildcard/Variable Arms Always Succeed

When the arm pattern is `WildcardPat` or `VarPat`, the test always passes. For such arms, emit a direct `CfBrOp` to the body block instead of a `CfCondBrOp`. This means the failure block is unreachable (never emitted if there is a wildcard/variable arm), but for safety it is always emitted anyway. The optimizer will remove it.

Actually: if the arm is wildcard/var, emit `cf.br ^bodyN` (not `cf.cond_br`). Then NO failure block is needed after it. Still always emit the failure block for correctness when there is no wildcard arm.

### Anti-Patterns to Avoid

- **Merging test ops into the body block**: Test ops for pattern condition must be in the TEST block (the block that falls through to the next test on failure). Mixing them into the body block means the failure path cannot skip them.
- **Using `arith.cmpi` on pointers**: Always use `llvm.icmp` for pointer comparisons (`EmptyListPat` null check, `ConsPat` non-null check). `arith.cmpi` works only on integer types.
- **Missing `llvm.unreachable` after void call**: MLIR verifier rejects blocks with no terminator. `LlvmCallVoidOp` is not a terminator. Always follow it with `LlvmUnreachableOp`.
- **Using `ReturnOp` from the failure block**: The failure block calls `exit(1)` which never returns; using `ReturnOp` with a dummy value is incorrect and confusing to the optimizer.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| String pattern comparison | custom inline strcmp | `@strcmp` already declared in ExternalFuncs | Already used for string equality in Phase 8 |
| List null check | custom icmp | `LlvmNullOp` + `LlvmIcmpOp` from Phase 10 | Already exists; reuse exactly |
| Tuple field extraction | new GEP variant | `LlvmGEPLinearOp` (slot index) from Phase 9 | Already used for tuple destructuring in LetPat |
| Match failure signal | inline printf+exit | `@lang_match_failure` in lang_runtime.c | Keeps IR clean; consistent with other runtime helpers |

**Key insight:** Phase 11 is almost entirely a refactor and extension of the existing Match case. All the primitives (CfCondBrOp, LlvmIcmpOp, LlvmNullOp, LlvmGEPLinearOp, strcmp, arith.cmpi) already exist. The only new IR-level addition is `LlvmUnreachableOp`.

---

## Common Pitfalls

### Pitfall 1: Block Counter Leakage Between Arms
**What goes wrong:** Each arm elaboration calls `freshLabel` and `freshName` on the shared env, so SSA name counters advance across arms. If the same `env` is used for test ops, body ops, and subsequent arm ops, a shared counter is fine. But if test ops reference SSA names that were computed AFTER the names emitted in body ops (due to elaboration side effects), the emitted MLIR will have name-ordering problems.
**Why it happens:** `elaborateExpr` for the arm body mutates `env.Counter` and `env.Blocks`. If the test condition for arm N+1 is elaborated BEFORE the body of arm N is committed to a block, the SSA names may be out of order.
**How to avoid:** Elaborate each arm's test condition ops and body ops in a single pass (test first, body second), committing blocks immediately. Do not pre-elaborate all tests or all bodies separately.
**Warning signs:** MLIR verifier error "use of undefined value" or SSA name appearing in a block before it is defined.

### Pitfall 2: Missing Return Type for Failure Block
**What goes wrong:** The merge block requires all predecessor blocks (including arm body blocks) to pass a value of the correct type. If the failure block calls `@lang_match_failure` and emits `llvm.unreachable` (no block argument to merge), the merge block's predecessor count is off.
**Why it happens:** `llvm.unreachable` is a legitimate terminator that has NO successors. The merge block does NOT need to accept a value from the failure block.
**How to avoid:** The failure block does NOT branch to the merge block at all. It is a dead-end: `llvm.call @lang_match_failure() + llvm.unreachable`. The merge block only has arm body blocks as predecessors.

### Pitfall 3: TuplePat Sub-Pattern Type Inference
**What goes wrong:** When a `TuplePat` contains a nested `TuplePat` sub-pattern, the field must be loaded as `Ptr` (pointer to inner tuple). Loading as `I64` produces a wrong value.
**Why it happens:** All heap-allocated values use uniform `!llvm.ptr`; only leaf integer/bool values are `I64`/`I1`. The load type must be inferred from the sub-pattern.
**How to avoid:** Use the same `loadTypeOfPat` heuristic from Phase 9 LetPat: `TuplePat sub-pattern → load as Ptr; VarPat/WildcardPat → load as I64`.
**Warning signs:** Incorrect arithmetic results from tuple pattern match; segfault when accessing nested tuple fields.

### Pitfall 4: ConsPat Head Type Hardcoded as I64
**What goes wrong:** In Phase 10, head is always loaded as `I64` (integer lists only). In Phase 11 full pattern matching, a cons list might contain non-integer values (e.g., tuple elements) but this is out of scope for v2.
**Why it happens:** The type system is untyped at the Elaboration level; all values are I64/I1/Ptr.
**How to avoid:** Keep the hardcoded `I64` for ConsPat head loads in v2 scope. Document this as a known limitation.

### Pitfall 5: @lang_match_failure Not Declared in ExternalFuncs
**What goes wrong:** The emitted MLIR references `@lang_match_failure` without a forward declaration, causing an MLIR verifier error ("undefined symbol").
**Why it happens:** ExternalFuncs are hardcoded in `elaborateModule` (not added dynamically).
**How to avoid:** Add `{ ExtName = "@lang_match_failure"; ExtParams = []; ExtReturn = None; IsVarArg = false }` to the hardcoded `externalFuncs` list in `elaborateModule` unconditionally (same pattern as `@GC_init`).

---

## Code Examples

### Example: ConstPat(IntConst 42) test
```fsharp
// In testPattern for ConstPat(IntConst n):
let kVal  = { Name = freshName env; Type = I64 }
let cond  = { Name = freshName env; Type = I1 }
let ops   = [ArithConstantOp(kVal, int64 n); ArithCmpIOp(cond, "eq", scrutVal, kVal)]
(Some cond, ops, env)
```

### Example: EmptyListPat test
```fsharp
// In testPattern for EmptyListPat:
let nullVal = { Name = freshName env; Type = Ptr }
let cond    = { Name = freshName env; Type = I1 }
let ops     = [LlvmNullOp(nullVal); LlvmIcmpOp(cond, "eq", scrutVal, nullVal)]
(Some cond, ops, env)
```

### Example: ConstPat(StringConst s) test
```fsharp
// In testPattern for ConstPat(StringConst s):
let (strVal, strOps) = elaborateStringLiteral env s    // GC_malloc'd header
let dataPtrVal = { Name = freshName env; Type = Ptr }
let dataVal    = { Name = freshName env; Type = Ptr }
let scrutDataPtrVal = { Name = freshName env; Type = Ptr }
let scrutDataVal    = { Name = freshName env; Type = Ptr }
let cmpRes     = { Name = freshName env; Type = I32 }
let zero32     = { Name = freshName env; Type = I32 }
let cond       = { Name = freshName env; Type = I1 }
let ops = strOps @ [
    LlvmGEPStructOp(dataPtrVal, strVal, 1)
    LlvmLoadOp(dataVal, dataPtrVal)
    LlvmGEPStructOp(scrutDataPtrVal, scrutVal, 1)
    LlvmLoadOp(scrutDataVal, scrutDataPtrVal)
    LlvmCallOp(cmpRes, "@strcmp", [scrutDataVal; dataVal])
    ArithConstantOp(zero32, 0L)
    ArithCmpIOp(cond, "eq", cmpRes, zero32)
]
(Some cond, ops, env)
```

### Example: VarPat binding
```fsharp
// In testPattern for VarPat(name):
let env' = { env with Vars = Map.add name scrutVal env.Vars }
(None, [], env')  // condVal = None means unconditional match
```

### Example: LlvmUnreachableOp in Printer
```fsharp
| LlvmUnreachableOp ->
    sprintf "%sllvm.unreachable" indent
```

### Example: @lang_match_failure in lang_runtime.c
```c
void lang_match_failure(void) {
    fprintf(stderr, "Fatal: non-exhaustive match\n");
    exit(1);
}
```

### Example: arm dispatch loop structure (pseudocode)
```
for each arm (pat, guard, body) in clauses:
    if pat is WildcardPat or VarPat:
        // unconditional — emit cf.br to body block; no further arms needed
        emit cf.br ^bodyN
        emit ^bodyN: [bind VarPat if needed] body ops + cf.br ^merge(%result)
        DONE (no failure block needed after this arm)
    else:
        // conditional — emit test ops + cf.cond_br
        let (cond, testOps, bindEnv) = testPattern(env, scrutVal, pat)
        emit testOps into current test block
        emit cf.cond_br cond, ^bodyN, ^testN+1
        emit ^bodyN: body ops with bindEnv + cf.br ^merge(%result)
        current test block = ^testN+1

// After all arms without a wildcard:
emit ^match_fail:
    llvm.call @lang_match_failure() : () -> ()
    llvm.unreachable
```

---

## Key Decisions from STATE.md

The following decisions are already locked (from STATE.md `Decisions` section):

- `[v2.0 PatMatch]`: Match compiles to sequential cf.cond_br chain (same mechanism as if-else); always emit @lang_match_failure terminal (C-10 prevention)
- Phase 9: `Match(single TuplePat arm)` already desugars to `LetPat(TuplePat)` — this desugar case must be kept above the general Match case.
- Phase 10: `Match(EmptyListPat + ConsPat)` is a special case — Phase 11 replaces this with the general chain compiler that handles all pattern types including EmptyListPat+ConsPat.

---

## Phase 11 Specific: What Phases 9 and 10 Already Handle

From `Elaboration.fs` current state:

1. **Line 632**: `Match(scrutinee, [(TuplePat(pats, patSpan), None, body)], span)` — single TuplePat arm desugars to `LetPat(TuplePat, ...)`. **Keep this case as-is above the general Match handler.**

2. **Lines 663-721**: `Match(scrutineeExpr, clauses, _)` — handles exactly EmptyListPat+ConsPat two-arm form. **Phase 11 replaces this case** with the general decision chain compiler. The replacement must handle EmptyListPat+ConsPat as part of the general algorithm.

The general handler for `Match` in Phase 11 should come AFTER the single-TuplePat desugar case but BEFORE the catch-all `| _ -> failwithf`.

---

## Plan Split

Phase 11 maps cleanly to two plans as specified in ROADMAP.md:

**Plan 11-01** (PAT-01, PAT-02, PAT-04, PAT-05):
- Add `LlvmUnreachableOp` to MlirIR + Printer
- Add `@lang_match_failure` to lang_runtime.c
- Replace Phase 10's two-arm `Match` case with a full `compileMatchArms` function that handles: VarPat, WildcardPat, ConstPat(IntConst/BoolConst), and terminates with `@lang_match_failure`
- Add `@lang_match_failure` to ExternalFuncs in `elaborateModule`
- FsLit tests: constant int pattern + wildcard, bool pattern, non-exhaustive match failure

**Plan 11-02** (PAT-03 + multi-pattern):
- Extend `testPattern` for ConstPat(StringConst) via strcmp
- Extend for EmptyListPat (reuse LlvmIcmpOp null check from Phase 10)
- Extend for ConsPat with recursive sub-pattern binding
- Extend for TuplePat in match (GEP+load, unconditional match)
- FsLit tests: string pattern, list pattern (sum), tuple pattern match, multi-arm comprehensive

---

## Sources

### Primary (HIGH confidence)
- Direct code reading of `Elaboration.fs` (Phase 10 Match case at lines 663-721) — pattern chain mechanism
- Direct code reading of `MlirIR.fs` — existing MlirOp cases
- Direct code reading of `Printer.fs` — existing serialization patterns
- Direct code reading of `lang_runtime.c` — existing runtime helper pattern

### Secondary (MEDIUM confidence)
- `Ast.fs` Pattern DU — all pattern variants confirmed (VarPat, WildcardPat, ConstPat, TuplePat, ConsPat, EmptyListPat)
- `STATE.md` Decisions section — locked design choices for pattern matching
- `ROADMAP.md` Phase 11 plan split — two plans as defined

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all primitives confirmed in existing code; only LlvmUnreachableOp is new
- Architecture: HIGH — sequential cf.cond_br chain is proven by Phase 3 if-else and Phase 10 list match
- Pitfalls: HIGH — identified from direct code analysis of existing Phase 9/10 elaboration

**Research date:** 2026-03-26
**Valid until:** Stable (no external dependencies; all findings from local codebase)
