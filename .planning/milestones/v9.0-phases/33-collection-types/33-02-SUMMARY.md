---
phase: 33-collection-types
plan: 02
subsystem: compiler
tags: [c-runtime, elaboration, queue, mutablelist, collection-types, gc-malloc, mlir]

# Dependency graph
requires:
  - phase: 33-01-stringbuilder-hashset
    provides: StringBuilder and HashSet C runtime + Elaboration arms; established struct-typedef-in-header-only pattern
provides:
  - LangQueue linked-list C runtime with enqueue/dequeue/count (FIFO, GC_malloc nodes)
  - LangMutableList growable int64_t array with add/get/set/count (cap-doubling via GC_malloc+memcpy)
  - Elaboration arms for queue_create/enqueue/dequeue/count and mutablelist_set/get/create/add/count
  - externalFuncs entries in both elaborateModule and elaborateProgram lists
  - E2E tests 33-03-queue.flt and 33-04-mutablelist.flt
affects: [33-03-collection-extras, FunLexYacc DFA construction, any code using Queue or MutableList builtins]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - queue_dequeue is two-arg curried (queue, unit) in Elaboration — C function takes only queue pointer
    - mutablelist_set (three-arg) must appear before mutablelist_get (two-arg) in Elaboration.fs match arms
    - struct typedefs defined only in lang_runtime.h (not redefined in .c) — prevents clang redefinition errors

key-files:
  created:
    - tests/compiler/33-03-queue.flt
    - tests/compiler/33-04-mutablelist.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "queue_dequeue elaboration: two-arg App(App(Var, q), unit) — unit elaborated and discarded; C function lang_queue_dequeue takes only the queue pointer"
  - "mutablelist_set (three-arg App(App(App(...)))) placed BEFORE mutablelist_get (two-arg App(App(...))) in Elaboration.fs — same rule as hashtable_set before hashtable_get"
  - "LangMutableList grow: GC_malloc new block + memcpy (never realloc) — Boehm GC must track all live pointers"
  - "queue_enqueue and mutablelist_add use LlvmCallVoidOp + ArithConstantOp(unit,0); queue_dequeue uses LlvmCallOp returning I64"

patterns-established:
  - "Pattern: three-arg mutating builtins (set) must appear before two-arg query builtins (get) in Elaboration.fs pattern match"
  - "Pattern: two-arg curried builtin where second arg is unit — elaborate both, discard unit val, pass only first to C"

# Metrics
duration: 4min
completed: 2026-03-29
---

# Phase 33 Plan 02: Queue and MutableList Summary

**LangQueue (linked-list FIFO) and LangMutableList (growable int64_t array) C runtime structs + Elaboration arms — 165/165 E2E tests passing**

## Performance

- **Duration:** 4 min
- **Started:** 2026-03-29T15:22:49Z
- **Completed:** 2026-03-29T15:27:29Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

- LangQueue with head/tail/count linked-list, lang_queue_dequeue calls lang_failwith on empty queue
- LangMutableList with cap-doubling growth via GC_malloc+memcpy (never realloc), bounds checks on get/set
- queue_dequeue elaborated as two-arg curried call (queue, unit) — unit discarded, C function takes only queue ptr
- mutablelist_set (three-arg) placed before mutablelist_get (two-arg) in Elaboration.fs — critical ordering
- externalFuncs updated in BOTH elaborateModule and elaborateProgram lists (9 entries each)
- 165/165 E2E tests passing (163 prior + 2 new)

## Task Commits

1. **Task 1: Add Queue and MutableList C runtime + header declarations** - `6e50e04` (feat)
2. **Task 2: Add Elaboration arms + externalFuncs for Queue and MutableList, plus E2E tests** - `7555f23` (feat)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/lang_runtime.c` - LangQueue (create/enqueue/dequeue/count) and LangMutableList (create/add/get/set/count) implementations
- `src/FunLangCompiler.Compiler/lang_runtime.h` - LangQueueNode, LangQueue, LangMutableList struct typedefs + 9 function declarations
- `src/FunLangCompiler.Compiler/Elaboration.fs` - 9 new elaboration arms + 9 externalFuncs entries in each of 2 lists
- `tests/compiler/33-03-queue.flt` - E2E: enqueue 10/20/30, dequeue twice → 10, 20, count=1
- `tests/compiler/33-04-mutablelist.flt` - E2E: add 100/200, set index 0 to 999 → count=2, get(0)=999, get(1)=200

## Decisions Made

- `queue_dequeue` elaboration uses two-arg curried pattern `App(App(Var "queue_dequeue", qExpr), unitExpr)` — the unit arg is elaborated (to avoid dangling MLIR SSA values) but discarded; the C function `lang_queue_dequeue(LangQueue* q)` takes only the queue pointer
- `mutablelist_set` (three-arg) placed before `mutablelist_get` (two-arg) in Elaboration.fs — F# pattern matching is top-to-bottom; the three-arg `App(App(App(...)))` must appear first or the two-arg arm would partially match it
- `LangMutableList` buffer growth uses GC_malloc new block + memcpy; never realloc — Boehm GC does not track realloc'd pointers
- Struct typedefs (LangQueueNode, LangQueue, LangMutableList) defined only in `lang_runtime.h` (following 33-01 pattern) — including them in .c would cause clang redefinition errors since .c includes the header

## Deviations from Plan

None — plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Queue and MutableList builtins fully available in the compiler
- Phase 33-03 (remaining collection types if any) can build on the established patterns
- FunLexYacc DFA construction can now use Queue for BFS state exploration
- All 165 prior E2E tests pass; no regressions

---
*Phase: 33-collection-types*
*Completed: 2026-03-29*
