---
phase: 32-hashtable-list-array-builtins
plan: 01
subsystem: compiler
tags: [fsharp, mlir, hashtable, builtins, c-runtime, gep, tuple]

# Dependency graph
requires:
  - phase: 31-string-char-io-builtins
    provides: string/char/IO builtins, eprintfn elaboration pattern
provides:
  - lang_hashtable_trygetvalue C runtime function (returns 2-slot GC-allocated tuple)
  - hashtable_trygetvalue elaboration arm (two-arg curried, calls C function)
  - hashtable_count elaboration arm (one-arg, inline GEP at LangHashtable.size field index 2)
  - externalFuncs entries for @lang_hashtable_trygetvalue in both elaboration paths
  - E2E tests 32-01 and 32-02 verifying trygetvalue and count
affects: [33-list-array-builtins, dfamin-compilation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Inline GEP pattern: use LlvmGEPLinearOp + LlvmLoadOp to read struct fields without C function"
    - "Tuple return from C: GC_malloc(N*8), fill slots, return int64_t*; tuple destructuring via GEP+load works transparently"

key-files:
  created:
    - tests/compiler/32-01-hashtable-trygetvalue.flt
    - tests/compiler/32-02-hashtable-count.flt
  modified:
    - src/LangBackend.Compiler/lang_runtime.c
    - src/LangBackend.Compiler/lang_runtime.h
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "hashtable_count uses inline GEP at LangHashtable field index 2 (size), no C function needed"
  - "E2E tests use println (to_string ...) pattern instead of printfn (which does not exist in elaboration)"
  - "Two-sequential-if MLIR limitation avoided by chaining with let _ = ... in"

patterns-established:
  - "Inline GEP for struct field reads: LlvmGEPLinearOp(ptr, base, fieldIndex) + LlvmLoadOp"

# Metrics
duration: 8min
completed: 2026-03-29
---

# Phase 32 Plan 01: Hashtable TryGetValue & Count Summary

**hashtable_trygetvalue returns GC-allocated (bool, value) tuple via C runtime; hashtable_count reads size field inline via GEP — both needed for DfaMin.fun compilation**

## Performance

- **Duration:** 8 min
- **Started:** 2026-03-29T14:18:24Z
- **Completed:** 2026-03-29T14:26:53Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

- Added `lang_hashtable_trygetvalue` C function after `lang_ht_find` (static helper visible), allocates 16-byte GC tuple with bool flag and value
- Added hashtable_trygetvalue elaboration arm (two-arg curried) with key coercion to I64
- Added hashtable_count elaboration arm using inline GEP at field index 2 (LangHashtable.size), zero C overhead
- Added externalFuncs entries for @lang_hashtable_trygetvalue in both elaboration paths
- 157/157 tests pass (2 new E2E tests + 155 existing)

## Task Commits

Each task was committed atomically:

1. **Task 1: C runtime function for hashtable_trygetvalue + header** - `02a410a` (feat)
2. **Task 2: Elaboration arms + externalFuncs + E2E tests** - `55fcd61` (feat)

**Plan metadata:** (pending)

## Files Created/Modified

- `src/LangBackend.Compiler/lang_runtime.c` - Added lang_hashtable_trygetvalue after lang_ht_find
- `src/LangBackend.Compiler/lang_runtime.h` - Added lang_hashtable_trygetvalue declaration
- `src/LangBackend.Compiler/Elaboration.fs` - Added two elaboration arms + externalFuncs entries
- `tests/compiler/32-01-hashtable-trygetvalue.flt` - E2E test: trygetvalue found/not-found cases
- `tests/compiler/32-02-hashtable-count.flt` - E2E test: hashtable_count after 2 inserts

## Decisions Made

- **hashtable_count uses inline GEP, no C function:** LangHashtable.size is at field index 2; LlvmGEPLinearOp(ptr, base, 2) + LlvmLoadOp reads it directly without any C runtime overhead
- **E2E tests use `println (to_string ...)` not `printfn "%d"`:** The `printfn` builtin with format strings does not exist in the elaborator; all integer printing must go through `to_string`
- **Sequential let-in chaining avoids two-sequential-if limitation:** Tests use `let _ = println ... in` chains instead of bare sequential `if` expressions which would trigger the empty-entry-block MLIR bug

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] E2E test used non-existent `printfn "%d"` builtin**

- **Found during:** Task 2 (E2E test execution)
- **Issue:** Plan specified `if found then printfn "%d" v else printfn "%d" (0 - 1)` but `printfn` with `%d` format does not exist in the elaborator
- **Fix:** Rewrote tests to use `println (to_string ...)` pattern, which works and also avoids two-sequential-if MLIR limitation
- **Files modified:** tests/compiler/32-01-hashtable-trygetvalue.flt, tests/compiler/32-02-hashtable-count.flt
- **Verification:** Both tests pass with fslit runner
- **Committed in:** 55fcd61 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug in test spec)
**Impact on plan:** Test rewrite necessary for correctness. No scope creep. Both must-haves fully verified.

## Issues Encountered

None beyond the printfn deviation above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- hashtable_trygetvalue and hashtable_count builtins complete and tested
- Ready for remaining Phase 32 builtins (list/array builtins for DfaMin.fun)
- No blockers

---
*Phase: 32-hashtable-list-array-builtins*
*Completed: 2026-03-29*
