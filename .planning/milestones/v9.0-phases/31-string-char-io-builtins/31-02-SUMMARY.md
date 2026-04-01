---
phase: 31-string-char-io-builtins
plan: 02
subsystem: compiler
tags: [mlir, char, ctype, elaboration, builtins, e2e-tests]

# Dependency graph
requires:
  - phase: 31-01
    provides: string builtins pattern (bool-wrapping, pass-through, externalFuncs lists)
provides:
  - 6 char C runtime functions (is_digit, is_letter, is_upper, is_lower, to_upper, to_lower) using ctype.h
  - 6 Elaboration.fs pattern match arms + externalFuncs entries in both lists
  - 6 E2E tests (31-05 through 31-10)
affects: [31-03, FunLexYacc lexer compilation phase]

# Tech tracking
tech-stack:
  added: [ctype.h (isdigit, isalpha, isupper, islower, toupper, tolower)]
  patterns:
    - "Bool-wrapping char predicate: LlvmCallOp(I64) -> ArithConstantOp(0) -> ArithCmpIOp(ne) -> I1"
    - "Pass-through char transformer: LlvmCallOp(I64) -> I64 (same as char_to_int pattern)"
    - "Char predicate E2E test: use to_string(bool) with println; avoids two-sequential-if limitation"
    - "Char transformer E2E test: compare result with char_to_int; use exit code as assertion"

key-files:
  created:
    - tests/compiler/31-05-char-is-digit.flt
    - tests/compiler/31-06-char-is-letter.flt
    - tests/compiler/31-07-char-is-upper.flt
    - tests/compiler/31-08-char-is-lower.flt
    - tests/compiler/31-09-char-to-upper.flt
    - tests/compiler/31-10-char-to-lower.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Char predicate E2E tests use to_string(bool) + println pattern (not if/then/else + %d) to avoid two-sequential-if MLIR limitation"
  - "Char transformer E2E tests use exit-code comparison: result = char_to_int 'X' returns 1 on success"

patterns-established:
  - "Char predicate bool-wrapping: same as string_contains / string_endswith / string_startswith"
  - "Char transformer pass-through: same as char_to_int (identity pattern with LLVM call)"

# Metrics
duration: 13min
completed: 2026-03-29
---

# Phase 31 Plan 02: Char Builtins Summary

**Six char builtins (is_digit, is_letter, is_upper, is_lower, to_upper, to_lower) via ctype.h in lang_runtime.c + Elaboration arms; 154/154 tests pass**

## Performance

- **Duration:** 13 min
- **Started:** 2026-03-29T13:03:00Z
- **Completed:** 2026-03-29T13:16:03Z
- **Tasks:** 2
- **Files modified:** 9

## Accomplishments

- Added `#include <ctype.h>` and 6 char C runtime functions to lang_runtime.c
- Added declarations for all 6 functions in lang_runtime.h
- Added 6 pattern match arms in Elaboration.fs elaborateExpr (4 bool-wrapping predicates + 2 pass-through transformers)
- Added externalFuncs entries in BOTH lists (all 6 with ExtParams=[I64], ExtReturn=Some I64)
- Created 6 E2E tests (31-05 through 31-10); all pass; 154/154 total tests pass

## Task Commits

Each task was committed atomically:

1. **Task 1: C runtime char functions + ctype.h include** - `4bf31c1` (feat)
2. **Task 2: Elaboration arms + externalFuncs + E2E tests** - `3cb012c` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added #include <ctype.h> + 6 char functions
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added 6 char function declarations
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added 6 elaboration arms + 12 externalFuncs entries (6 per list)
- `tests/compiler/31-05-char-is-digit.flt` - E2E test: true/false cases via to_string
- `tests/compiler/31-06-char-is-letter.flt` - E2E test: true/false cases via to_string
- `tests/compiler/31-07-char-is-upper.flt` - E2E test: true/false cases via to_string
- `tests/compiler/31-08-char-is-lower.flt` - E2E test: true/false cases via to_string
- `tests/compiler/31-09-char-to-upper.flt` - E2E test: char comparison via exit code
- `tests/compiler/31-10-char-to-lower.flt` - E2E test: char comparison via exit code

## Decisions Made

- **Char predicate E2E tests use `to_string(bool) + println`** instead of plan-specified `printfn "%d" (if ... then 1 else 0)`. The compiler does not support `printfn`, and `if ... then 1 else 0` with two sequential ifs triggers the MLIR empty-block limitation. Using `to_string` on the bool directly (same as 31-01 tests) is the correct pattern.
- **Char transformer E2E tests use exit-code comparison** (`char_to_upper 'a' = char_to_int 'A'` exits 1 on success). The compiler has no `%c` format printing support, so direct char value output requires this workaround.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] E2E test format corrected from plan spec**
- **Found during:** Task 2 (running 31-05-char-is-digit.flt)
- **Issue:** Plan specified `printfn "%d" (if char_is_digit '5' then 1 else 0)` but (a) compiler does not support `printfn`, and (b) two sequential `if` expressions trigger the MLIR empty-block limitation
- **Fix:** Predicate tests use `to_string(bool) + println` pattern (same as 31-01). Transformer tests use exit-code comparison with `char_to_int`
- **Files modified:** tests/compiler/31-05 through 31-10
- **Verification:** All 6 tests pass; 154/154 total pass
- **Committed in:** `3cb012c` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 2 - missing critical correctness)
**Impact on plan:** Required to produce runnable tests; no scope change. Test behavior validates the same semantic properties the plan specified.

## Issues Encountered

None beyond the E2E test format deviation above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- All 6 char builtins working; 154 tests pass
- Phase 31-03 (eprintfn) ready to execute
- FunLexYacc lexer compilation unblocked for char classification

---
*Phase: 31-string-char-io-builtins*
*Completed: 2026-03-29*
