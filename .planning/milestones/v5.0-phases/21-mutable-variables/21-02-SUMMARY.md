---
phase: 21-mutable-variables
plan: 02
subsystem: compiler
tags: [fsharp, mlir, closure, mutable, ref-cell, free-variables]

# Dependency graph
requires:
  - phase: 21-01
    provides: GC ref cell approach for mutable variables, MutableVars field in ElabEnv

provides:
  - Correct closure capture of mutable ref cells (captures Ptr not I64)
  - MutableVars propagated into closure innerEnv so inner body auto-dereferences
  - Fixed freeVars to detect captured mutable vars through LetPat(WildcardPat/VarPat)
  - 2 new E2E tests (21-05, 21-06) for closure+mutable scenarios
  - Phase 21 (Mutable Variables) fully complete

affects: [22-array-core, 23-hashtable, 24-array-hofs]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Closure capture of mutable vars: store Ptr (ref cell) in closure struct slot, load Ptr on capture load, MutableVars membership drives auto-deref at Var site"
    - "freeVars must handle LetPat(WildcardPat) and LetPat(VarPat) to correctly identify all free variables in closure bodies"

key-files:
  created:
    - tests/compiler/21-05-closure-capture-mut.flt
    - tests/compiler/21-06-closure-mut-counter.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "freeVars LetPat fix is a correctness bug: without it, let _ = mutVar <- ... patterns inside closures fail to detect captured vars"
  - "capType conditional on MutableVars: Ptr for mutable captures, I64 for immutable — consistent with how outer env stores them"

patterns-established:
  - "When propagating env into closure innerEnv, always copy MutableVars (not Set.empty)"

# Metrics
duration: 14min
completed: 2026-03-27
---

# Phase 21 Plan 02: Closure Capture of Mutable Variables Summary

**Closure capture of GC ref cells: Ptr-typed capture loads + MutableVars propagation + freeVars LetPat fix, enabling counter closures and mutation-visible-through-closure patterns**

## Performance

- **Duration:** ~14 min
- **Started:** 2026-03-27T09:42:45Z
- **Completed:** 2026-03-27T09:57:00Z
- **Tasks:** 2
- **Files modified:** 1 (+ 2 test files created)

## Accomplishments

- Propagated `MutableVars` from outer `env` into closure `innerEnv` (both LetFn and bare Lambda cases)
- Changed capture load type from always `I64` to conditional `Ptr` for mutable captures, so the ref cell pointer is correctly recovered inside the closure
- Fixed `freeVars` to handle `LetPat(WildcardPat, ...)` and `LetPat(VarPat, ...)` — the pre-existing gap caused `let _ = c <- c + 1 in c` bodies to return `Set.empty` for free variables, making mutable captures invisible
- 2 E2E tests: read-after-mutation through closure (exit 99) and counter closure with 3 calls (exit 3)
- Phase 21 complete: 73/73 tests passing

## Task Commits

1. **Task 1: Fix closure capture for mutable variables** - `a559850` (fix)
2. **Task 2: Add E2E tests for closure capture of mutable variables** - `288fa74` (test)

**Plan metadata:** (see final commit below)

## Files Created/Modified

- `src/LangBackend.Compiler/Elaboration.fs` - Three changes: MutableVars propagation (2 sites), conditional capType (2 sites), freeVars LetPat cases
- `tests/compiler/21-05-closure-capture-mut.flt` - E2E test: `let mut x = 5 in let f = fun z -> x in let _ = x <- 99 in f 0` → exit 99
- `tests/compiler/21-06-closure-mut-counter.flt` - E2E test: counter closure with 3 increments → exit 3

## Decisions Made

- `freeVars` fix included in Task 1 commit as a Rule 1 (Bug) auto-fix: it was a pre-existing correctness bug blocking the feature. Without it, `LetPat(WildcardPat, Assign("c", ...), ...)` returned `Set.empty` for free vars, so `c` was never added to captures.
- `capType` is conditional on `MutableVars` membership at the outer env scope (where the lambda is defined), not the inner env — this is correct because the capture list is derived from outer env.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed freeVars missing LetPat(WildcardPat) and LetPat(VarPat) cases**

- **Found during:** Task 1 (fix closure capture for mutable variables)
- **Issue:** `freeVars` function fell through to `_ -> Set.empty` for `LetPat(WildcardPat, ...)` and `LetPat(VarPat, ...)`. This caused closures whose bodies contained `let _ = mutVar <- expr in rest` to compute empty capture sets, leaving `mutVar` unbound inside the closure.
- **Fix:** Added explicit cases for both patterns before the TuplePat case
- **Files modified:** `src/LangBackend.Compiler/Elaboration.fs`
- **Verification:** Counter closure test now produces correct output (3)
- **Committed in:** a559850 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug)
**Impact on plan:** The fix was essential for the feature to work. The plan's description of the fix was accurate about what needed to change but didn't anticipate the secondary `freeVars` bug. No scope creep.

## Issues Encountered

- Initial test revealed captures were empty despite MutableVars fix — debug eprintfn showed `captures=[]` even with `MutableVars=[c]`. Root cause was `freeVars` returning `Set.empty` for `LetPat(WildcardPat, ...)` expressions used in mutation patterns.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 22 (Array Core) can proceed. All mutable variable infrastructure is complete.
- Phase 22 still requires `LlvmGEPDynamicOp` (dynamic SSA-value index GEP) — new IR op.
- 73 E2E tests passing as baseline for Phase 22.

---
*Phase: 21-mutable-variables*
*Completed: 2026-03-27*
