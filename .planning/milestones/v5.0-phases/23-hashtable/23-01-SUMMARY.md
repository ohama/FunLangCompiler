---
phase: 23-hashtable
plan: 01
subsystem: runtime
tags: [c-runtime, hashtable, gc, chained-buckets, murmurhash3, lang_throw]

# Dependency graph
requires:
  - phase: 22-array-core
    provides: lang_throw for catchable runtime errors, GC_malloc pattern, LangCons struct
  - phase: 19-exception-handling
    provides: lang_throw/setjmp/longjmp infrastructure for missing-key raises
provides:
  - LangHashEntry struct (key, val, next chain node)
  - LangHashtable struct (capacity, size, GC_malloc'd buckets array)
  - lang_hashtable_create: 16-bucket chained hashtable
  - lang_hashtable_get: lookup or lang_throw LangString* on missing key
  - lang_hashtable_set: insert/update with 3/4 load factor rehash
  - lang_hashtable_containsKey: 1/0 predicate
  - lang_hashtable_remove: prev-pointer chain unlinking
  - lang_hashtable_keys: LangCons* linked list of all keys
affects:
  - phase-23-plan-02 (hashtable elaboration in compiler - will wire these C functions via ExternalFuncDecl)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "murmurhash3 finalizer (XOR-shift-multiply) for i64 key hashing — same pattern as murmur3 fmix64"
    - "lang_throw (NOT lang_failwith) for catchable runtime errors from C runtime"
    - "GC_malloc for all allocations — no manual free needed, BDW GC handles it"
    - "3/4 load factor rehash with capacity doubling — entries relinked into new buckets"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/lang_runtime.c

key-decisions:
  - "murmurhash3 fmix64 finalizer chosen for hash function — fast, good avalanche, no external dependency"
  - "lang_throw used for missing-key error so try/with can catch hashtable KeyNotFound — consistent with array OOB pattern from 22-01"
  - "Rehash threshold is size*4 > capacity*3 (i.e., load > 0.75) — standard open-addressing threshold applied to chaining"
  - "buckets array allocated as GC_malloc(capacity * sizeof(LangHashEntry*)) — fully GC-managed, no manual lifecycle"

patterns-established:
  - "C runtime functions use lang_throw for catchable errors (not lang_failwith which calls exit)"
  - "All new C heap allocations use GC_malloc — never malloc/free"

# Metrics
duration: 6min
completed: 2026-03-27
---

# Phase 23 Plan 01: Hashtable C Runtime Summary

**LangHashEntry/LangHashtable chained-bucket structs and 6 C runtime functions (create/get/set/containsKey/remove/keys) with murmurhash3 hashing, 3/4 load-factor rehash, and lang_throw on missing key**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-27T12:02:50Z
- **Completed:** 2026-03-27T12:08:51Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Added LangHashEntry and LangHashtable struct typedefs and 6 function declarations to lang_runtime.h
- Implemented all 6 C hashtable functions in lang_runtime.c with full GC_malloc discipline
- Missing-key error uses lang_throw (catchable) instead of lang_failwith (fatal) — consistent with array OOB pattern
- All 80 existing E2E tests continue to pass after the additions

## Task Commits

Each task was committed atomically:

1. **Task 1: Add hashtable struct typedefs and function declarations to lang_runtime.h** - `00faf59` (feat)
2. **Task 2: Implement 6 hashtable C functions in lang_runtime.c** - `4c1c2d8` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added LangHashEntry, LangHashtable structs and 6 function declarations
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Implemented lang_ht_hash (murmurhash3), lang_ht_find, lang_ht_rehash helpers and 6 public hashtable functions

## Decisions Made
- **murmurhash3 fmix64 finalizer** for hashing: fast, no dependencies, good avalanche properties for i64 keys
- **lang_throw for missing-key** (not lang_failwith): ensures hashtable KeyNotFound is catchable by try/with, consistent with array OOB pattern established in phase 22-01
- **Rehash at load > 3/4**: standard threshold, capacity doubles on each rehash
- **LangCons* returned by lang_hashtable_keys**: order unspecified (bucket iteration order), matches list type used throughout the runtime

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- C runtime hashtable fully implemented and compiled into lang_runtime.c
- Phase 23 Plan 02 can now wire these functions via ExternalFuncDecl in Elaboration.fs, following the same elaboration pattern as array builtins from phase 22-02
- No blockers

---
*Phase: 23-hashtable*
*Completed: 2026-03-27*
