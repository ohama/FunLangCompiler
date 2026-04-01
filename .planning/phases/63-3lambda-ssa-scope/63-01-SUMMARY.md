---
phase: 63-3lambda-ssa-scope
plan: 01
subsystem: compiler
tags: [fsharp, elaboration, ssa, closures, curried-functions, mlir]

# Dependency graph
requires:
  - phase: 62-bugfixes
    provides: stable compiler foundation pre-v19.0
provides:
  - Guard on 2-lambda pattern in Elaboration.fs rejecting 3+ lambda innerBody
  - E2E test for 3-arg curried function (Issue #4 reproducer)
affects: [future elaboration work, curried function optimization]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "2-lambda pattern guard: `when (match stripAnnot innerBody with Lambda _ -> false | _ -> true)` prevents SSA scope violation for 3+ arg curried functions"
    - "Shell-script .flt tests: used for tests requiring Prelude linkage"

key-files:
  created:
    - tests/compiler/63-04-3lambda-curried.flt
    - tests/compiler/63-04-3lambda-curried.sh
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Fix via guard on existing 2-lambda pattern rather than adding a dedicated 3-lambda pattern — general Let path handles N-ary chains correctly"
  - "Fall-through to general Let path is safe: standalone Lambda path creates isolated closure env per layer with no SSA cross-scope leakage"

patterns-established:
  - "2-lambda pattern guard: when innerBody is a Lambda, reject 2-lambda path so 3+ arg chains fall through to general Let path"

# Metrics
duration: 6min
completed: 2026-04-01
---

# Phase 63 Plan 01: 3-Lambda SSA Scope Fix Summary

**Single `when` guard on 2-lambda pattern in Elaboration.fs fixes Issue #4 SSA scope violation for 3+ arg curried functions, verified by 244/244 E2E tests**

## Performance

- **Duration:** 6 min
- **Started:** 2026-04-01T15:38:08Z
- **Completed:** 2026-04-01T15:44:00Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- Added `when (match stripAnnot innerBody with Lambda _ -> false | _ -> true)` guard to 2-lambda pattern at Elaboration.fs line 713-714
- 3+ arg curried functions (`add3`, `combine` with outer capture) now compile and run correctly via the general Let path
- All 244 tests pass (243 existing + 1 new), with zero regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: Add guard to 2-lambda pattern** - `34e3745` (fix)
2. **Task 2: Add E2E test for 3-arg curried function** - `a533b66` (test)

**Plan metadata:** (pending)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added `when` guard to 2-lambda pattern at line 713
- `tests/compiler/63-04-3lambda-curried.flt` - New E2E test for Issue #4 (3-arg curried function)
- `tests/compiler/63-04-3lambda-curried.sh` - Shell script running `add3` and `combine` test cases

## Decisions Made
- Fix via guard on existing pattern rather than adding a dedicated 3-lambda path — the general Let path already handles N-ary lambda chains correctly with isolated SSA scopes per closure layer
- Two test cases: `add3 10 20 30 = 60` (basic 3-arg) and `combine 10 20 30 = 160` (with outer capture) to stress both paths

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- Test 35-05-option-module.flt exhibited a flaky apphost copy race condition during the full suite run (pre-existing infrastructure issue). Re-running confirmed 244/244 pass.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Issue #4 (SSA scope violation in 3+ arg curried functions) is resolved
- v19.0 milestone complete: all three phases done (63-01 this plan, plus prior 63-01/02/03 tests)
- No blockers for future phases

---
*Phase: 63-3lambda-ssa-scope*
*Completed: 2026-04-01*
