---
phase: 32-hashtable-list-array-builtins
plan: 03
subsystem: compiler
tags: [array, sort, qsort, builtin, elaboration, c-runtime, mlir]

# Dependency graph
requires:
  - phase: 32-02
    provides: list_sort_by + list_of_seq; established LlvmCallVoidOp pattern for void-return builtins
  - phase: 32-01
    provides: hashtable builtins; established println (to_string ...) E2E test pattern
provides:
  - lang_array_sort C function (qsort wrapper with static comparator, in-place int64_t array sort)
  - lang_array_of_seq C function (delegates to lang_array_of_list)
  - Elaboration arms for array_sort (void return) and array_of_seq (Ptr return)
  - externalFuncs entries for both new functions in both elaborateModule and elaborateProgram
  - E2E tests 32-05 and 32-06 (161 total tests passing)
affects: [32-04, 33, dfa-compilation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "array_sort uses LlvmCallVoidOp pattern (void C return -> unit I64 = 0)"
    - "array_of_seq delegates to existing array_of_list at C level, one-line wrapper"
    - "static comparator placed before the function that uses it in lang_runtime.c"

key-files:
  created:
    - tests/compiler/32-05-array-sort.flt
    - tests/compiler/32-06-array-of-seq.flt
  modified:
    - src/LangBackend.Compiler/lang_runtime.c
    - src/LangBackend.Compiler/lang_runtime.h
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "array_sort elaboration uses LlvmCallVoidOp (not LlvmCallOp) — C function returns void"
  - "array_of_seq delegates entirely to lang_array_of_list at C level — no separate logic needed"
  - "externalFuncs updated in both elaborateModule and elaborateProgram (two identical lists exist)"

patterns-established:
  - "Void-return array builtins: LlvmCallVoidOp + ArithConstantOp(unitVal, 0L)"

# Metrics
duration: 6min
completed: 2026-03-29
---

# Phase 32 Plan 03: Array Sort and Array-of-Seq Builtins Summary

**qsort-backed array_sort (in-place ascending) and array_of_seq (list-to-array) builtins added to C runtime and elaborator, bringing E2E test count to 161**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-29T14:40:00Z
- **Completed:** 2026-03-29T14:46:38Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- lang_array_sort: uses qsort with static lang_compare_i64 comparator; arr[0]=length, arr[1..n]=elements
- lang_array_of_seq: one-line wrapper delegating to lang_array_of_list (lists are already seqs)
- Elaboration arms follow established void-return pattern (LlvmCallVoidOp + ArithConstantOp unit result)
- Both externalFuncs lists (elaborateModule + elaborateProgram) updated correctly

## Task Commits

Each task was committed atomically:

1. **Task 1: C runtime functions for array_sort and array_of_seq + header** - `28af7d2` (feat)
2. **Task 2: Elaboration arms + externalFuncs + E2E tests for array builtins** - `7515674` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/LangBackend.Compiler/lang_runtime.c` - Added static lang_compare_i64, lang_array_sort, lang_array_of_seq
- `src/LangBackend.Compiler/lang_runtime.h` - Added declarations for lang_array_sort and lang_array_of_seq
- `src/LangBackend.Compiler/Elaboration.fs` - Added elaboration arms and externalFuncs entries for both functions
- `tests/compiler/32-05-array-sort.flt` - E2E test: [3;1;2] sorts to [1;2;3] in place
- `tests/compiler/32-06-array-of-seq.flt` - E2E test: list [10;20;30] converts to array with length 3

## Decisions Made
- array_sort elaboration uses LlvmCallVoidOp: C function returns void, unit result is ArithConstantOp(0L). Mirrors the array_iter and array_iter patterns.
- array_of_seq delegates to lang_array_of_list at C level: no separate logic needed since lists are the seq representation.
- externalFuncs updated in both elaborateModule and elaborateProgram (two identical lists coexist in Elaboration.fs), using replace_all to update both simultaneously.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- externalFuncs list appears twice in Elaboration.fs (once in elaborateModule ~line 2916, once in elaborateProgram ~line 3119). Used replace_all to update both simultaneously. Not a deviation — both lists are kept in sync as a pre-existing pattern.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- array_sort and array_of_seq are ready for use by Phase 32-04 and Dfa.fun compilation
- 161 E2E tests all passing, no regressions

---
*Phase: 32-hashtable-list-array-builtins*
*Completed: 2026-03-29*
