---
phase: 22-array-core
plan: 02
subsystem: compiler
tags: [mlir, llvm, arrays, elaboration, builtins, e2e-tests, gep-dynamic]

# Dependency graph
requires:
  - phase: 22-01
    provides: LlvmGEPDynamicOp, C runtime array functions (create/bounds_check/of_list/to_list)
  - phase: 19-exception-handling
    provides: lang_throw/setjmp/longjmp for catchable OOB errors
provides:
  - 6 array builtin elaboration cases: array_create, array_get, array_set, array_length, array_of_list, array_to_list
  - 4 ExternalFuncDecl entries for C runtime array functions
  - 7 E2E tests covering all array operations including OOB exception handling
affects:
  - phase-23-hashtable (same builtin elaboration pattern)
  - any future phase using array operations

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Three-arg builtins (array_set) must appear before two-arg and one-arg App patterns in elaborateExpr
    - Bounds check emitted via LlvmCallVoidOp before GEP+load/store - longjmp-catchable
    - Slot arithmetic: logical index 0..n-1 maps to GEP index 1..n (slot 0 = length)
    - Exit codes for try/with tests must fit in 0-255 (shell $? truncates to 8 bits)

key-files:
  created:
    - tests/compiler/22-01-array-create.flt
    - tests/compiler/22-02-array-get-set.flt
    - tests/compiler/22-03-array-length.flt
    - tests/compiler/22-04-array-oob.flt
    - tests/compiler/22-05-array-of-list.flt
    - tests/compiler/22-06-array-to-list.flt
    - tests/compiler/22-07-array-roundtrip.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "OOB test uses exit 42 (not 999) because shell $? truncates exit codes to 8 bits; 999 & 0xFF = 231 which looks like a crash"
  - "array_create coerces defVal to I64 with ArithExtuIOp when type is I1 (bool literal default)"
  - "Three-arg array_set match arm placed before two-arg array_get and two-arg array_create to avoid shadowing"

patterns-established:
  - "Bounds check call (void) placed first in array_get/set sequence; MLIR doesn't know it's conditional-noreturn but longjmp handles it at runtime"
  - "ExternalFuncDecl entries must be added to BOTH externalFuncs lists in elaborateModule and elaborateProgram"

# Metrics
duration: 16min
completed: 2026-03-27
---

# Phase 22 Plan 02: Array Builtin Elaboration and E2E Tests Summary

**Six array builtin elaboration cases wired in Elaboration.fs with bounds-checked GEP+load/store, plus 7 E2E tests covering create/get/set/length/of_list/to_list and OOB exception handling (80 tests total)**

## Performance

- **Duration:** 16 min
- **Started:** 2026-03-27T11:05:11Z
- **Completed:** 2026-03-27T11:21:37Z
- **Tasks:** 2
- **Files modified:** 8 (1 .fs + 7 .flt)

## Accomplishments
- Added 6 array builtin elaboration cases covering all array operations
- Added 4 ExternalFuncDecl entries in both externalFuncs lists for C runtime array functions
- Created 7 E2E tests: all pass, including OOB exception catching with wildcard try/with
- Total test count: 80/80 passing (73 existing + 7 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add array builtin elaboration cases and ExternalFuncDecl entries** - `78928bc` (feat)
2. **Task 2: Add 7 E2E tests for all array operations** - `6a8e998` (feat)

**Plan metadata:** (see docs commit below)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added 6 builtin cases + 4 ExternalFuncDecl entries in both lists
- `tests/compiler/22-01-array-create.flt` - array_create + array_get (exit 0)
- `tests/compiler/22-02-array-get-set.flt` - array_set + array_get roundtrip (exit 42)
- `tests/compiler/22-03-array-length.flt` - array_length (exit 7)
- `tests/compiler/22-04-array-oob.flt` - OOB exception caught by wildcard try/with (exit 42)
- `tests/compiler/22-05-array-of-list.flt` - array_of_list + element access (exit 2)
- `tests/compiler/22-06-array-to-list.flt` - array_to_list + head extraction (exit 10)
- `tests/compiler/22-07-array-roundtrip.flt` - list->array->list->array length check (exit 7)

## Decisions Made
- OOB test (22-04) uses exit 42 not 999: shell `$?` truncates exit codes to 8 bits, so 999 & 0xFF = 231 which looks like a crash rather than the intended handler result
- `array_create` coerces defVal to I64 with ArithExtuIOp when type is I1 (for bool literals as default values)
- Three-arg `array_set` case placed before two-arg and one-arg patterns to prevent premature App matching

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Exit code for OOB test corrected from 999 to 42**
- **Found during:** Task 2 (E2E test for OOB)
- **Issue:** Plan specified `| _ -> 999` with "exit 999" as expected output; shell `$?` truncates to 8 bits giving 231 instead
- **Fix:** Changed handler return value from 999 to 42 (fits in 0-255)
- **Files modified:** tests/compiler/22-04-array-oob.flt
- **Verification:** fslit reports PASS, exit code 42 printed correctly
- **Committed in:** 6a8e998 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug - exit code truncation)
**Impact on plan:** Necessary correctness fix; no scope change.

## Issues Encountered
- Wildcard pattern `| _ -> 999` in try/with compiled correctly (confirmed via assembly: `mov w8, #0x3e7` = 999) but exit code 999 & 0xFF = 231 making the test appear to fail. Root cause: POSIX exit codes are 8-bit; plan's expected output was wrong.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All 6 array builtins operational: create, get, set, length, of_list, to_list
- OOB exceptions catchable via try/with wildcard pattern
- 80 E2E tests passing
- Ready to proceed with Phase 23 (Hashtable)
- No blockers

---
*Phase: 22-array-core*
*Completed: 2026-03-27*
