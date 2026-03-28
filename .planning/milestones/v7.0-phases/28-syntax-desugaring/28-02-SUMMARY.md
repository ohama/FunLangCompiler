---
phase: 28-syntax-desugaring
plan: 02
subsystem: compiler
tags: [indexing, array, hashtable, runtime-dispatch, elaboration, c-runtime]

# Dependency graph
requires:
  - phase: 22-arrays
    provides: lang_array_create, lang_array_bounds_check, array_get/set elaboration
  - phase: 23-hashtables
    provides: lang_hashtable_create/get/set, hashtable elaboration
  - phase: 28-01
    provides: SEQ/ITE desugaring (plan 01)

provides:
  - lang_index_get/lang_index_set C runtime dispatch functions
  - LangHashtable tag field (-1) for array vs hashtable distinction
  - IndexGet/IndexSet elaboration in elaborateExpr
  - freeVars cases for IndexGet/IndexSet
  - 5 E2E tests covering IDX-01..IDX-04 + roundtrip

affects: [29-loops, future phases using .[...] indexing syntax]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Runtime dispatch by inspecting first word of collection pointer (tag=-1 for hashtable, >=0 for array length)"
    - "LangHashtable tag field as first field enables uniform void* collection dispatch"

key-files:
  created:
    - src/LangBackend.Compiler/lang_runtime.h (modified - added tag + declarations)
    - src/LangBackend.Compiler/lang_runtime.c (modified - tag init + dispatch funcs)
    - tests/compiler/28-06-idx-array-get.flt
    - tests/compiler/28-07-idx-array-set.flt
    - tests/compiler/28-08-idx-ht-get.flt
    - tests/compiler/28-09-idx-ht-set.flt
    - tests/compiler/28-10-idx-array-roundtrip.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs (IndexGet/IndexSet + freeVars + externals)

key-decisions:
  - "LangHashtable tag field added as FIRST field (before capacity) so void* dispatch works by checking [0] offset"
  - "tag = -1 for hashtable; arrays use non-negative length at offset 0 — values never overlap"
  - "freeVars cases added for IndexGet/IndexSet to ensure closure capture works correctly"
  - "Both elaborateModule and elaborateTopLevel externalFuncs lists updated with lang_index_get/set"

patterns-established:
  - "IndexGet elaboration: collOps @ idxOps @ idxCoerce @ [LlvmCallOp(result, lang_index_get, [coll; idx])]"
  - "IndexSet elaboration: void call + ArithConstantOp(unitVal, 0L) for unit result"

# Metrics
duration: 15min
completed: 2026-03-28
---

# Phase 28 Plan 02: IDX Desugaring Summary

**arr.[i] and ht.[key] indexing syntax compiles via C runtime dispatch using LangHashtable tag=-1 sentinel**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-03-28T00:46:00Z
- **Completed:** 2026-03-28T01:01:38Z
- **Tasks:** 2
- **Files modified:** 8

## Accomplishments

- Added `int64_t tag` as first field in `LangHashtable` struct; initialized to -1 in `lang_hashtable_create`
- Added `lang_index_get`/`lang_index_set` C dispatch functions that branch on first word of collection pointer
- Added `IndexGet`/`IndexSet` elaboration arms in `elaborateExpr` and `freeVars` cases
- 5 new E2E tests (28-06 through 28-10) covering IDX-01 through IDX-04 plus roundtrip
- All 128 tests pass (121 pre-existing + 5 new + 2 from plan 01 SEQ tests)

## Task Commits

Each task was committed atomically:

1. **Task 1: C runtime tag + dispatch functions** - `3f99d25` (feat)
2. **Task 2: IndexGet/IndexSet elaboration + E2E tests** - `90835ef` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/lang_runtime.h` - Added tag field to LangHashtable + lang_index_get/set declarations
- `src/LangBackend.Compiler/lang_runtime.c` - tag=-1 init in lang_hashtable_create + dispatch functions
- `src/LangBackend.Compiler/Elaboration.fs` - IndexGet/IndexSet elaboration + freeVars cases + 2 external decl lists updated
- `tests/compiler/28-06-idx-array-get.flt` - IDX-01: arr.[i] read
- `tests/compiler/28-07-idx-array-set.flt` - IDX-02: arr.[i] <- v write
- `tests/compiler/28-08-idx-ht-get.flt` - IDX-03: ht.[key] read
- `tests/compiler/28-09-idx-ht-set.flt` - IDX-04: ht.[key] <- v write
- `tests/compiler/28-10-idx-array-roundtrip.flt` - Multi-index roundtrip

## Decisions Made

- Test format omits trailing "0" exit code line (per existing test conventions in the repo)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Removed trailing "0" from test output sections**

- **Found during:** Task 2 (E2E test creation)
- **Issue:** Plan specified "0" as last line in test Output sections (echo $?), but existing passing tests do not include this line — FsLit only matches printed output, not exit code echo
- **Fix:** Removed "0" from all 5 test Output sections
- **Files modified:** tests/compiler/28-06 through 28-10
- **Verification:** All 5 tests pass after fix
- **Committed in:** 90835ef (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - incorrect test format in plan)
**Impact on plan:** Minor format fix. No scope creep.

## Issues Encountered

None beyond the test format deviation above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- IDX desugaring complete: arr.[i], arr.[i] <- v, ht.[key], ht.[key] <- v all compile
- Phase 28 plan 01 (SEQ/ITE) and plan 02 (IDX) both done — Phase 28 complete
- Phase 29 (loops: WhileExpr/ForExpr codegen) can proceed

---
*Phase: 28-syntax-desugaring*
*Completed: 2026-03-28*
