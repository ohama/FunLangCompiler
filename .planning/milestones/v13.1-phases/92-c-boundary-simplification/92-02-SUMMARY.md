---
phase: 92-c-boundary-simplification
plan: 02
subsystem: compiler-runtime-boundary
tags: [tagged-int, c-runtime, elaboration, inline-gep, array-access, index-dispatch]
dependency-graph:
  requires: [92-01-simple-function-groups]
  provides: [c-wrapper-length-count, c-array-access, zero-c-boundary-untag]
  affects: []
tech-stack:
  added: []
  patterns: [c-wrapper-for-struct-field-access, c-array-element-access]
file-tracking:
  key-files:
    created: []
    modified:
      - src/FunLangCompiler.Compiler/lang_runtime.c
      - src/FunLangCompiler.Compiler/Elaboration.fs
      - src/FunLangCompiler.Compiler/ElabProgram.fs
decisions:
  - id: array-val-coerce
    summary: "array_set coerces value to I64 via coerceToI64 for C function compatibility"
    date: 2026-04-07
metrics:
  duration: ~8 min
  completed: 2026-04-07
---

# Phase 92 Plan 02: Structural GEP Replacement + Array Access Summary

**One-liner:** Replace all inline GEP+load/store patterns with C wrapper functions, completing elimination of C-boundary emitUntag/emitRetag from Elaboration.fs.

## What Was Done

### Task 1: New C wrappers + inline GEP replacement + array_create/init
- Added 3 new C wrapper functions: `lang_string_length`, `lang_array_length`, `lang_hashtable_count` -- each returns `LANG_TAG_INT(field_value)` directly
- Replaced 3 inline GEP+load+retag patterns in Elaboration.fs with single C function calls
- Modified `lang_array_create` and `lang_array_init` to accept tagged count and untag internally
- Removed 2 emitUntag sites for array count parameters
- Added extern declarations for all 3 new functions in both ElabProgram.fs declaration lists
- **Commit:** aeb2382

### Task 2: New array_get/set + index dispatch fix
- Added 2 new C functions: `lang_array_get` (untag index, bounds check, load) and `lang_array_set` (untag index, bounds check, store)
- Fixed `lang_index_get` and `lang_index_set`: removed `LANG_TAG_INT(index)` wrapper since index now arrives already tagged
- Replaced inline GEP+bounds_check+arithmetic patterns in `array_set`/`array_get` with single C calls
- Removed emitUntag from IndexGet/IndexSet int-index paths
- Added extern declarations for `lang_array_get` and `lang_array_set`
- **Commit:** 85f263b

## Remaining emitUntag/emitRetag Sites

After this plan, the following sites remain (all expected/necessary):

| File | Lines | Category | Purpose |
|------|-------|----------|---------|
| Elaboration.fs | 42-61 | Arithmetic | mul/div/mod operations (3 sites) |
| Elaboration.fs | 963, 1034 | I1 retag | Bool-to-string zext+retag (2 sites) |
| ElabHelpers.fs | 166, 172 | Definitions | emitUntag/emitRetag function definitions |
| ElabHelpers.fs | 378 | I1 retag | coerceToI64 I1 zext+retag |
| ElabProgram.fs | 28, 458 | Exit code | @main return value untag |

**Zero C-boundary emitUntag/emitRetag remaining in Elaboration.fs.**

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing] Added extern declarations in ElabProgram.fs**
- **Found during:** Task 1
- **Issue:** Plan did not mention that new C functions need extern declarations in ElabProgram.fs for MLIR codegen
- **Fix:** Added declarations for all 5 new functions (string_length, array_length, hashtable_count, array_get, array_set) in both declaration blocks
- **Files modified:** src/FunLangCompiler.Compiler/ElabProgram.fs

**2. [Rule 2 - Missing] Added coerceToI64 for array_set value parameter**
- **Found during:** Task 2
- **Issue:** The C function `lang_array_set` takes `int64_t value`, but valVal might be I1 or Ptr
- **Fix:** Added `coerceToI64 env valVal` to handle I1/Ptr cases before passing to C

## Test Results

257/257 E2E tests pass.

## Phase 92 Completion Summary

Combined with Plan 01 (simple function groups), Phase 92 achieved:
- ~37 C-boundary emitUntag/emitRetag sites removed from Elaboration.fs
- ~27 from Plan 01 (simple function groups)
- ~10 from Plan 02 (structural GEP + array access + index dispatch)
- 5 new C wrapper/access functions created
- 4 existing C functions modified (array_create, array_init, index_get, index_set)
- All C functions now accept tagged ints and untag internally (CB-01)
- All C functions return tagged ints directly (CB-02)
