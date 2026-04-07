---
phase: 93-generic-equality-hash
plan: 03
subsystem: compiler-equality
tags: [equality, hashtable, structural-comparison, testing]
dependency_graph:
  requires: ["93-02"]
  provides: ["generic-equality-operator", "tuple-hashtable-keys", "record-hashtable-keys"]
  affects: []
tech_stack:
  added: []
  patterns: ["lang_generic_eq wrapper for = operator dispatch"]
file_tracking:
  key_files:
    created:
      - tests/compiler/93-01-tuple-key-hashtable.sh
      - tests/compiler/93-01-tuple-key-hashtable.flt
      - tests/compiler/93-02-record-key-hashtable.sh
      - tests/compiler/93-02-record-key-hashtable.flt
      - tests/compiler/93-03-generic-equality.sh
      - tests/compiler/93-03-generic-equality.flt
    modified:
      - src/FunLangCompiler.Compiler/lang_runtime.c
      - src/FunLangCompiler.Compiler/Elaboration.fs
      - src/FunLangCompiler.Compiler/ElabProgram.fs
decisions:
  - id: "93-03-01"
    decision: "Replace strcmp with lang_generic_eq for = operator on Ptr types"
    context: "= operator used strcmp (string-only); now calls lang_generic_eq for structural equality on all heap types"
metrics:
  duration: "~8 min"
  completed: "2026-04-07"
---

# Phase 93 Plan 03: E2E Tests for Generic Equality and Hash Summary

Generic structural equality via lang_generic_eq replaces strcmp-based = operator, enabling tuple/record hashtable keys and structural comparison for all heap types.

## What Was Done

### Task 1: Generic equality compiler change + E2E tests

**Compiler changes (deviation Rule 2: missing critical functionality):**
- Added `lang_generic_eq` non-static wrapper in `lang_runtime.c` that calls `lang_ht_eq` and returns 0/1
- Updated `Elaboration.fs` Equal/NotEqual/eq cases: replaced `strcmp` via GEP with `lang_generic_eq(lv, rv)` call for all Ptr types
- Added `@lang_generic_eq` extern declaration in both `ElabProgram.fs` function lists

**Test 93-01: Tuple as hashtable key**
- hashtable_set/get with tuple keys `(1, "a")`, `(2, "b")`
- Structurally equal tuple constructed separately retrieves same value
- Different tuple `(1, "b")` returns containsKey=false
- Overwrite with structurally equal key works

**Test 93-02: Record as hashtable key**
- Record type `Point = { x: int; y: int }` as key
- Structural equality for record lookup
- containsKey, overwrite, count verification

**Test 93-03: Generic structural equality**
- String equality (backward compat)
- Tuple equality: `(1, "a") = (1, "a")` true, `(1, "a") = (1, "b")` false
- List equality: `[1;2;3] = [1;2;3]` true, length mismatch false
- ADT equality: nullary `Red = Red` true, `Red = Green` false
- ADT with data: `Some 1 = Some 1` true, `Some 1 = Some 2` false
- Nested: `(1, [2;3]) = (1, [2;3])` true
- Inequality operator `<>` on strings and lists

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical functionality] = operator needed compiler change for structural equality**
- **Found during:** Task 1 analysis
- **Issue:** The `=` operator compiled Ptr equality via `strcmp` (extracting data pointer via GEP), which only works for strings. Tuples, records, lists, and ADTs would crash or give wrong results.
- **Fix:** Replaced all 3 strcmp-based comparison sites in Elaboration.fs with `lang_generic_eq` calls. Added non-static wrapper in lang_runtime.c and extern declaration in ElabProgram.fs.
- **Files modified:** Elaboration.fs, ElabProgram.fs, lang_runtime.c
- **Commit:** 16f3aa8

## Verification

- 260/260 E2E tests pass (257 existing + 3 new)
- All 3 new tests pass individually
- String equality backward compatibility confirmed

## Next Phase Readiness

Phase 93 (Generic Equality and Hash) is now complete:
- 93-01: Heap tagging for tuples, records, lists, ADTs
- 93-02: Generic hash/equality in C runtime
- 93-03: E2E tests + = operator structural equality
