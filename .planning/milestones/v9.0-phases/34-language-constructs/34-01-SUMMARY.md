---
phase: 34-language-constructs
plan: 01
subsystem: compiler
tags: [string-slice, elaboration, C-runtime, MLIR, FunLang]

# Dependency graph
requires:
  - phase: 31-string-char-io-builtins
    provides: lang_string_sub C function used as basis for lang_string_slice
provides:
  - lang_string_slice(s, start, stop) C runtime function with stop<0 open-ended sentinel
  - StringSliceExpr elaboration arm in Elaboration.fs
  - externalFuncs entries in both lists (elaborateModule + elaborateProgram)
  - E2E tests 34-01 (bounded) and 34-02 (open-ended)
affects: [34-02, 34-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - stop=-1 sentinel for open-ended slices avoids two separate C functions
    - StringSliceExpr strVal coerced to Ptr if I64 (defensive, strings always arrive as Ptr)

key-files:
  created:
    - tests/compiler/34-01-string-slice-bounded.flt
    - tests/compiler/34-02-string-slice-open.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "lang_string_slice uses stop<0 sentinel for open-ended slices (not a separate lang_string_slice_open function)"
  - "StringSliceExpr elaboration arm placed before the failwithf catch-all at end of elaborateExpr"
  - "externalFuncs entry added to both lists in Elaboration.fs (elaborateModule ~line 3060 and elaborateProgram ~line 3287)"

patterns-established:
  - "Pattern: sentinel value (-1L) for open-ended range in C function avoids function proliferation"

# Metrics
duration: 8min
completed: 2026-03-29
---

# Phase 34 Plan 01: String Slicing Summary

**F#-style string slicing via lang_string_slice C wrapper: s.[0..4] and s.[6..] both compile and produce correct substrings using stop<0 open-ended sentinel**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-03-29T00:00:00Z
- **Completed:** 2026-03-29T00:08:00Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Added `lang_string_slice(s, start, stop)` in lang_runtime.c wrapping `lang_string_sub` with inclusive-stop semantics; stop<0 treated as open-ended sentinel
- Added `StringSliceExpr` elaboration arm in Elaboration.fs: emits LlvmCallOp for @lang_string_slice with stop=-1L when stopOpt=None
- Added @lang_string_slice to both externalFuncs lists (elaborateModule and elaborateProgram)
- All 167 E2E tests pass (165 pre-existing + 2 new string slice tests)

## Task Commits

1. **Task 1: Add lang_string_slice C function + header declaration** - `adcdb69` (feat)
2. **Task 2: Add StringSliceExpr elaboration arm + externalFuncs + E2E tests** - `73cc607` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added lang_string_slice after lang_string_sub
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added lang_string_slice prototype near other lang_string_* declarations
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added StringSliceExpr arm + @lang_string_slice in both externalFuncs lists
- `tests/compiler/34-01-string-slice-bounded.flt` - E2E test: s.[0..4] = "hello", s.[6..10] = "world"
- `tests/compiler/34-02-string-slice-open.flt` - E2E test: s.[6..] = "world", t.[2..] = "cdef"

## Decisions Made
- Used stop<0 sentinel (stop=-1L from Elaboration, handled by `if (stop < 0) stop = s->length - 1` in C) rather than a separate `lang_string_slice_open` function. Single function, simpler externalFuncs entry.
- Added @lang_string_slice to BOTH externalFuncs lists as required by established pattern (Phase 32-03 decision).

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- Running the .flt file directly with `dotnet run` passed the flt file as the source program causing "unbound variable 'hello'" error. Resolution: test runner must be `fslit tests/compiler/34-01-string-slice-bounded.flt` or use a temporary .lt file. Not a code bug; test verification was done correctly with both methods.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- String slicing complete, 167/167 tests pass
- Ready for Plan 34-02 (ListCompExpr) and Plan 34-03 (ForInExpr tuple destructuring + new collection for-in)
- No blockers

---
*Phase: 34-language-constructs*
*Completed: 2026-03-29*
