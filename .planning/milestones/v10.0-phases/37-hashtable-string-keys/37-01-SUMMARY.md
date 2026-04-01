---
phase: 37-hashtable-string-keys
plan: 01
subsystem: runtime
tags: [c, hashtable, string-keys, fnv1a, memcmp, boehm-gc]

# Dependency graph
requires:
  - phase: 36-bug-fixes
    provides: stable runtime with corrected for-in capture, sequential-if MLIR, and bool condition handling
provides:
  - LangHashEntryStr struct (LangString* key, int64_t val, next pointer)
  - LangHashtableStr struct (tag=-2, capacity, size, buckets)
  - 3 static helpers: lang_ht_str_hash (FNV-1a), lang_ht_str_find (memcmp), lang_ht_str_rehash
  - 9 public C functions for string-key hashtable operations
  - lang_index_get_str / lang_index_set_str dispatch functions
affects:
  - 38-hashtable-string-keys-codegen (codegen will call these functions via MLIR)
  - any phase generating string-indexed hashtable IR

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "FNV-1a hash over LangString content bytes for string-keyed hashtables"
    - "memcmp with length check for string content equality (never pointer identity)"
    - "(int64_t)(uintptr_t)ptr pattern for storing LangString* as int64_t in LangCons.head"
    - "tag=-2 distinguishes LangHashtableStr from LangHashtable (tag=-1) and arrays (tag>=0)"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/lang_runtime.c

key-decisions:
  - "tag=-2 for LangHashtableStr — separates it from int-key hashtable (tag=-1) so existing index_get/index_set dispatch is unaffected"
  - "LangHashEntryStr uses struct LangString_s* in header (forward declaration already present)"
  - "lang_index_get_str/lang_index_set_str do not need tag dispatch — string index always implies string hashtable"

patterns-established:
  - "String key hashtable pattern: FNV-1a + memcmp, same chained-bucket structure as int-key variant"

# Metrics
duration: 8min
completed: 2026-03-30
---

# Phase 37 Plan 01: Hashtable String Keys (C Runtime) Summary

**FNV-1a string-key hashtable in C runtime: LangHashtableStr with tag=-2, memcmp content equality, and 9 public functions matching the int-key ABI**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-03-30T00:00:00Z
- **Completed:** 2026-03-30T00:08:00Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- Added LangHashEntryStr and LangHashtableStr struct types to lang_runtime.h with all 9 function declarations
- Implemented 3 static helpers (hash, find, rehash) and 9 public functions in lang_runtime.c
- All allocations via Boehm GC (GC_malloc); string comparison uses memcmp on content, never pointer identity
- `dotnet build` succeeds with 0 warnings and 0 errors

## Task Commits

1. **Task 1: Add structs and declarations to lang_runtime.h** - `f30401a` (feat)
2. **Task 2: Implement all functions in lang_runtime.c** - `831d1f4` (feat)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/lang_runtime.h` - Added LangHashEntryStr, LangHashtableStr structs and 9 _str function declarations
- `src/FunLangCompiler.Compiler/lang_runtime.c` - Added 147 lines: 3 static helpers + 9 public string-key hashtable functions

## Decisions Made

- Used `tag = -2` for LangHashtableStr so existing `lang_index_get`/`lang_index_set` (which dispatch on tag < 0) remain unchanged; string index dispatch is separate (`lang_index_get_str`/`lang_index_set_str`)
- `lang_index_get_str`/`lang_index_set_str` don't check tag — a string-indexed collection is always a string hashtable, no array branch needed
- Stored `LangString*` as `int64_t` in `LangCons.head` using `(int64_t)(uintptr_t)ptr` — same GC-safe pattern as existing int-key `lang_hashtable_keys`

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- C runtime is complete: all 9 string-key hashtable functions are available
- Phase 38 can proceed with MLIR codegen that calls these functions
- The tag=-2 ABI is stable: codegen must emit `tag = -2` when creating LangHashtableStr instances

---
*Phase: 37-hashtable-string-keys*
*Completed: 2026-03-30*
