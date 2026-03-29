---
phase: 32-hashtable-list-array-builtins
plan: 02
subsystem: compiler
tags: [fsharp, mlir, list, builtins, c-runtime, closure, insertion-sort]

# Dependency graph
requires:
  - phase: 32-01
    provides: hashtable_trygetvalue + hashtable_count, E2E test patterns
  - phase: 31-01
    provides: LangCons typedef placement rule, LangClosureFn ABI
provides:
  - lang_list_sort_by C runtime function (insertion sort with closure key extractor)
  - lang_list_of_seq C runtime function (identity cast to LangCons*)
  - list_sort_by elaboration arm (two-arg curried, closure coercion mirrors array_map)
  - list_of_seq elaboration arm (one-arg, identity pass-through)
  - externalFuncs entries for both functions in both elaboration paths
  - E2E tests 32-03 and 32-04 verifying sort and identity semantics
affects: [33-list-array-builtins, dfamin-compilation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Closure key extractor pattern: lang_list_sort_by uses LangClosureFn ABI same as array_map"
    - "Identity pass-through builtin: lang_list_of_seq just casts void* to LangCons*"

key-files:
  created:
    - tests/compiler/32-03-list-sort-by.flt
    - tests/compiler/32-04-list-of-seq.flt
  modified:
    - src/LangBackend.Compiler/lang_runtime.c
    - src/LangBackend.Compiler/lang_runtime.h
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "list_sort_by uses parallel int64_t arrays + insertion sort for simplicity and correctness"
  - "E2E tests use println (to_string ...) pattern — printfn with %d does not exist in elaborator"
  - "list_of_seq is a simple void* cast — no C logic needed, just type coercion"

patterns-established:
  - "Two-arg curried closure arm pattern: mirrors array_map exactly (fOps @ closureOps @ secondOps @ [LlvmCallOp])"

# Metrics
duration: 9min
completed: 2026-03-29
---

# Phase 32 Plan 02: list_sort_by and list_of_seq Summary

**list_sort_by (insertion sort with LangClosureFn key extractor) and list_of_seq (identity cast) added to C runtime and elaborator — required for DfaMin.fun compilation**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-03-29T14:29:16Z
- **Completed:** 2026-03-29T14:37:51Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

- Added `lang_list_sort_by` in lang_runtime.c after existing list/array higher-order functions: uses parallel elems/keys arrays + insertion sort; invokes closure via LangClosureFn ABI
- Added `lang_list_of_seq` in lang_runtime.c: simple void* cast to LangCons* (identity)
- Added header declarations for both functions in lang_runtime.h
- Added list_sort_by elaboration arm: two-arg curried, closure coercion to Ptr if I64 (mirrors array_map pattern exactly)
- Added list_of_seq elaboration arm: one-arg, returns Ptr result
- Added externalFuncs entries to both elaboration paths (two lists in Elaboration.fs)
- 159/159 tests pass (2 new E2E tests + 157 existing)

## Task Commits

Each task was committed atomically:

1. **Task 1: C runtime functions for list_sort_by and list_of_seq + header** - `1a60c28` (feat)
2. **Task 2: Elaboration arms + externalFuncs + E2E tests** - `c03d1d7` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/lang_runtime.c` - Added lang_list_sort_by and lang_list_of_seq after lang_array_init
- `src/LangBackend.Compiler/lang_runtime.h` - Added declarations for both new functions
- `src/LangBackend.Compiler/Elaboration.fs` - Added two elaboration arms + externalFuncs entries in both lists
- `tests/compiler/32-03-list-sort-by.flt` - E2E test: sort [3;1;2] with identity key -> [1;2;3], verify a+b*10+c*100=321
- `tests/compiler/32-04-list-of-seq.flt` - E2E test: list_of_seq [10;20] returns same list, a+b=30

## Decisions Made

- **Insertion sort for list_sort_by:** Simple, correct for small lists (DfaMin use case), no stdlib dependency
- **Parallel arrays for sort:** Copy list elements and keys into int64_t arrays, sort in place, rebuild cons list in reverse — clean and GC-safe
- **list_of_seq is identity cast:** In this compiler, lists are already sequences (LangCons chains); list_of_seq is just a type-level coercion with no runtime work
- **E2E tests use `println (to_string ...)` not `printfn "%d"`:** Established pattern from Phase 32-01

## Deviations from Plan

None - plan executed exactly as written. The `println (to_string ...)` pattern was already specified in the plan per Phase 32-01 decisions.

## Next Phase Readiness

Plan 32-03 (if it exists) should be able to build on the list builtins established here. The LangClosureFn ABI is stable. Two-sequential-if limitation still applies for future E2E tests.
