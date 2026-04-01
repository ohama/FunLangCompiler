---
phase: 24-array-hof-builtins
plan: 02
subsystem: compiler
tags: [mlir, llvm-dialect, array, hof, closures, elaboration, fsharp, fslit, e2e-tests]

# Dependency graph
requires:
  - phase: 24-01
    provides: C runtime HOF functions (lang_array_iter, lang_array_map, lang_array_fold, lang_array_init)
provides:
  - Elaboration.fs HOF match arms for array_iter, array_map, array_fold, array_init
  - ExternalFuncDecl entries in both externalFuncs lists
  - 4 E2E test files verifying all HOF builtins end-to-end
  - 92 total E2E tests passing (88 prior + 4 new)
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Closure coerce-to-ptr: if fVal.Type = I64 then LlvmIntToPtrOp else use Ptr value directly
    - 3-arg HOF arms placed before 2-arg arms to avoid ambiguous partial matches
    - E2E test closures use explicit currying (fun acc -> fun x -> ...) not multi-param fun

key-files:
  created:
    - tests/compiler/24-01-array-iter.flt
    - tests/compiler/24-02-array-map.flt
    - tests/compiler/24-03-array-fold.flt
    - tests/compiler/24-04-array-init.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "FunLang parser does not support multi-param fun (fun x y -> ...); use fun x -> fun y -> ... in test closures"
  - "Closure coerce: check fVal.Type = I64 and emit LlvmIntToPtrOp; Ptr values used directly"
  - "array_fold arm placed before array_iter/array_map/array_init (3-arg before 2-arg)"
  - "ExternalFuncDecl entries added to BOTH externalFuncs lists (elaborateModule + elaborateProgram)"

patterns-established:
  - "HOF closure arg pattern: elaborate closure expr, if I64 emit LlvmIntToPtrOp to get Ptr, pass Ptr to C runtime"

# Metrics
duration: 12min
completed: 2026-03-27
---

# Phase 24 Plan 02: Array HOF Builtins — Elaboration Layer Summary

**4 array HOF builtins (iter/map/fold/init) wired in Elaboration.fs with ExternalFuncDecl entries and closure-to-Ptr coercion; 4 E2E tests verifying each builtin, 92/92 total tests passing**

## Performance

- **Duration:** 12 min
- **Started:** 2026-03-27T16:32:41Z
- **Completed:** 2026-03-27T16:45:03Z
- **Tasks:** 2
- **Files modified:** 5 (1 modified, 4 created)

## Accomplishments
- Added 4 ExternalFuncDecl entries to BOTH externalFuncs lists in elaborateModule and elaborateProgram
- Added 4 elaboration match arms: array_fold (3-arg), array_iter, array_map, array_init (2-arg)
- Closure coercion pattern: LlvmIntToPtrOp when fVal.Type = I64, direct Ptr otherwise
- 4 E2E tests covering every HOF builtin, all 92 tests pass

## Task Commits

Each task was committed atomically:

1. **Task 1: Add ExternalFuncDecl entries and HOF elaboration cases** - `c173982` (feat)
2. **Task 2: Create 4 E2E test files for HOF builtins** - `60141aa` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - 4 HOF match arms + 8 ExternalFuncDecl entries (both lists)
- `tests/compiler/24-01-array-iter.flt` - array_iter E2E: prints each element in order
- `tests/compiler/24-02-array-map.flt` - array_map E2E: double elements, fold-sum = 12
- `tests/compiler/24-03-array-fold.flt` - array_fold E2E: sum [1..5] = 15
- `tests/compiler/24-04-array-init.flt` - array_init E2E: squares 0..4, fold-sum = 30

## Decisions Made
- **FunLang multi-param fun not supported:** `fun x y -> ...` causes parse error; test closures use explicit `fun acc -> fun x -> ...` currying
- **Closure coerce pattern:** Inline per-arm (consistent with existing hashtable/array patterns) — check fVal.Type = I64 and emit LlvmIntToPtrOp to convert to Ptr
- **Arm ordering:** array_fold (3-arg) placed before 2-arg arms to avoid mis-matching during partial application resolution
- **Both externalFuncs lists:** Both elaborateModule and elaborateProgram lists must be kept in sync (pre-existing project constraint)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] FunLang parser does not support multi-param fun syntax**
- **Found during:** Task 2 (creating E2E test files)
- **Issue:** Plan specified `fun acc x -> acc + x` style closures, but FunLang parser only supports single-param fun; `fun acc x ->` causes "parse error"
- **Fix:** Rewrote all binary closures in test files as explicitly curried `fun acc -> fun x -> acc + x`
- **Files modified:** 24-02-array-map.flt, 24-03-array-fold.flt, 24-04-array-init.flt
- **Verification:** All 4 tests pass after fix
- **Committed in:** 60141aa (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - parser syntax bug in test design)
**Impact on plan:** Fix required for correctness; no scope change.

## Issues Encountered
- None beyond the parser syntax issue documented above.

## Next Phase Readiness
- Phase 24 complete: all 4 array HOF builtins (iter/map/fold/init) available in compiled FunLang programs
- 92/92 E2E tests passing, v5.0 milestone complete
- No blockers or concerns

---
*Phase: 24-array-hof-builtins*
*Completed: 2026-03-27*
