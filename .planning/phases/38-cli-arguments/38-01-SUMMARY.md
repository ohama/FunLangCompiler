---
phase: 38-cli-arguments
plan: 01
subsystem: compiler
tags: [cli-arguments, argc, argv, lang_runtime, elaboration, mlir, get_args]

# Dependency graph
requires:
  - phase: 37-hashtable-string-keys
    provides: string-key hashtable runtime + elaboration patterns
provides:
  - lang_init_args/lang_get_args C runtime functions
  - get_args() builtin in Elaboration.fs returning LangCons* of CLI args
  - "@main with (i64, ptr) signature (argc, argv)"
  - E2E test proving ./prog foo bar baz prints each argument
affects:
  - 39-next-phase
  - any phase using CLI argument access

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "initArgsOp prepended before gcInitOp in both elaborateModule/elaborateProgram @main entry blocks"
    - "get_args mirrors stdin_read_all pattern: unit-arg builtin returning Ptr"
    - "lang_get_args uses forward-cursor pattern (same as lang_read_lines/lang_range) for list building"
    - "argv stored as int64_t/(uintptr_t) in LangCons.head — same pattern as hashtable string keys"

key-files:
  created:
    - tests/compiler/38-01-cli-args.flt
  modified:
    - src/LangBackend.Compiler/lang_runtime.h
    - src/LangBackend.Compiler/lang_runtime.c
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "lang_get_args starts from argv[1] to skip program name — argv[0] excluded per RT-03 spec"
  - "Both elaborateModule and elaborateProgram updated — missing one causes MLIR validation failures"
  - "%arg0/%arg1 names in initArgsOp match Printer's func param naming convention"
  - "ExternalFuncDecl entries added to BOTH externalFuncs lists in Elaboration.fs"

patterns-established:
  - "Phase 38: CLI argument pattern — lang_init_args called before GC_init, get_args returns LangCons*"

# Metrics
duration: ~8min
completed: 2026-03-30
---

# Phase 38 Plan 01: CLI Arguments Summary

**@main changed to (i64, ptr) -> i64 with lang_init_args/lang_get_args runtime; get_args() builtin returns LangCons* of argv[1..]; E2E verified with foo/bar/baz output**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-03-30T21:51:00Z
- **Completed:** 2026-03-30T21:59:35Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- Added lang_init_args/lang_get_args to C runtime (header + implementation)
- Updated both @main constructions in Elaboration.fs to InputTypes=[I64;Ptr] with initArgsOp prepended before GC_init
- Added get_args builtin arm in elaborateExpr (unit-arg, returns Ptr pattern)
- Added ExternalFuncDecl entries in both externalFuncs lists
- E2E test 38-01-cli-args.flt passes: ./prog foo bar baz prints foo/bar/baz, exit 0
- Full 189/189 test suite passes — no regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: C Runtime + Elaboration changes for CLI arguments** - `52feabf` (feat)
2. **Task 2: E2E test for CLI argument passing** - `4485510` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/lang_runtime.h` - Added lang_init_args and lang_get_args declarations
- `src/LangBackend.Compiler/lang_runtime.c` - Implemented lang_init_args (stores argc/argv) and lang_get_args (builds LangCons* from argv[1..])
- `src/LangBackend.Compiler/Elaboration.fs` - InputTypes=[I64;Ptr] in both mainFunc; initArgsOp prepend; get_args arm; ExternalFuncDecl entries in both lists
- `tests/compiler/38-01-cli-args.flt` - E2E test: compile get_args program, run with "foo bar baz", verify output

## Decisions Made
- lang_get_args starts from i=1 to skip argv[0] (program name) — per RT-03 spec
- Both elaborateModule and elaborateProgram must be updated identically — missing one causes MLIR validation failures
- %arg0/%arg1 names chosen to match Printer's func param naming convention
- forward-cursor pattern used in lang_get_args (consistent with lang_read_lines, lang_range)

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None — build succeeded first attempt, 38-01-cli-args.flt passed on first run, full suite 189/189.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- RT-03/RT-04 requirements satisfied: get_args() returns string list of CLI arguments
- @main is now (i64, ptr) -> i64 — compiled binaries accept argc/argv
- Phase 39 can build on get_args for more advanced CLI argument handling

---
*Phase: 38-cli-arguments*
*Completed: 2026-03-30*
