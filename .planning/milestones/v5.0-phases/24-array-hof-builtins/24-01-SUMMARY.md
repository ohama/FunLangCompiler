---
phase: 24-array-hof-builtins
plan: 01
subsystem: runtime
tags: [c-runtime, closure-abi, array, higher-order-functions, gc, mlir]

# Dependency graph
requires:
  - phase: 22-arrays
    provides: lang_array_create/bounds_check/of_list/to_list C runtime; one-block array layout (arr[0]=n, arr[1..n]=elements)
  - phase: 05-closures-via-elaboration
    provides: closure ABI — closure ptr whose first field is LangClosureFn function pointer
provides:
  - LangClosureFn typedef (int64_t (*)(void* env, int64_t arg)) in lang_runtime.h
  - lang_array_iter: applies closure to each element, discards return
  - lang_array_map: returns new GC array with closure results
  - lang_array_fold: curried binary fold using two-call pattern per element
  - lang_array_init: creates n-element array via zero-based index closure calls
affects: [24-array-hof-elaboration, future-collection-phases]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Closure ABI: fn = *(LangClosureFn*)closure; fn(closure, arg) — first field of closure struct is function pointer"
    - "Curried fold two-call pattern: partial = fn(closure, acc); fn2 = *(LangClosureFn*)partial_ptr; acc = fn2(partial_ptr, elem)"
    - "Array HOF output via GC_malloc((n+1)*8) — not GC_malloc_atomic — so closures inside arrays are scanned by GC"

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/lang_runtime.h
    - src/LangBackend.Compiler/lang_runtime.c

key-decisions:
  - "LangClosureFn typedef placed in lang_runtime.h alongside existing array/hashtable declarations for visibility across all translation units"
  - "lang_array_fold uses two-call curried pattern: first call returns partial closure as i64, cast to void*, dereference for fn2"
  - "lang_array_init uses zero-based index (i=0..n-1), stores at out[i+1] to maintain one-block layout"
  - "All HOF output arrays use GC_malloc (not GC_malloc_atomic) so GC correctly scans for interior pointers"

patterns-established:
  - "Closure invocation: cast closure ptr to LangClosureFn* and dereference for function pointer, then call fn(closure, arg)"
  - "Curried binary HOF: two calls per iteration — partial application then element application"

# Metrics
duration: 3min
completed: 2026-03-27
---

# Phase 24 Plan 01: Array HOF Builtins Summary

**4 C runtime HOF functions (iter/map/fold/init) implementing closure ABI with curried two-call fold pattern — all 88 tests pass**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-27T16:26:36Z
- **Completed:** 2026-03-27T16:30:28Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Added `LangClosureFn` typedef and 4 HOF prototypes to `lang_runtime.h`
- Implemented `lang_array_iter` (applies closure, discards returns), `lang_array_map` (returns new GC array), `lang_array_fold` (two-call curried binary fold), and `lang_array_init` (zero-based index initialization)
- All 88 existing E2E tests pass — no regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: Add LangClosureFn typedef and HOF prototypes to lang_runtime.h** - `e60e188` (feat)
2. **Task 2: Implement 4 HOF functions in lang_runtime.c** - `1c4c426` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/lang_runtime.h` - Added `LangClosureFn` typedef and 4 HOF prototypes
- `src/LangBackend.Compiler/lang_runtime.c` - Implemented `lang_array_iter`, `lang_array_map`, `lang_array_fold`, `lang_array_init`

## Decisions Made
- `lang_array_fold` uses the two-call curried pattern: `partial = fn(closure, acc)` then cast `partial` as `void*` to get `fn2` and call `fn2(partial_ptr, arr[i])`. This matches the curried binary closure ABI where fold functions are applied one argument at a time.
- All output arrays use `GC_malloc` (not `GC_malloc_atomic`) so the Boehm GC correctly scans for interior closure pointers stored in array elements.
- `lang_array_init` calls closure with zero-based index `i` (0..n-1) and stores result at `out[i+1]` to maintain the standard one-block layout.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- C runtime HOF functions ready for elaboration-layer wiring in Phase 24 Plan 02
- Elaboration needs to map `array_iter`, `array_map`, `array_fold`, `array_init` source builtins to these C functions via MLIR external call codegen
- No blockers

---
*Phase: 24-array-hof-builtins*
*Completed: 2026-03-27*
