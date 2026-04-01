---
phase: 33-collection-types
plan: 01
subsystem: compiler-runtime
tags: [c-runtime, fsharp, elaboration, gc_malloc, stringbuilder, hashset, collections]

# Dependency graph
requires:
  - phase: 32-hashtable-list-array-builtins
    provides: lang_ht_hash static function (murmurhash3) for HashSet reuse; established externalFuncs x2 pattern

provides:
  - LangStringBuilder C struct + lang_sb_create/append/tostring functions
  - LangHashSet C struct + lang_hashset_create/add/contains/count functions
  - Elaboration arms for all 7 new builtins
  - externalFuncs entries in both lists for all 7 functions
  - E2E tests 33-01-stringbuilder.flt and 33-02-hashset.flt

affects: [33-02-queue-mutablelist, FunLexYacc compilation, any code using StringBuilder or HashSet builtins]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Struct typedefs live only in lang_runtime.h — never redefined in lang_runtime.c (prevents redefinition errors)"
    - "HashSet reuses existing lang_ht_hash (static murmurhash3) by placing lang_hashset_* after hashtable block"
    - "hashset_add returns I64 (1=new, 0=duplicate) using LlvmCallOp — not void"

key-files:
  created:
    - tests/compiler/33-01-stringbuilder.flt
    - tests/compiler/33-02-hashset.flt
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Struct typedefs defined only in lang_runtime.h — the .c file includes the header so no redefinition; same as LangHashtable pattern"
  - "hashset_add uses LlvmCallOp (returning I64) not LlvmCallVoidOp — matches LangThree bool return for membership tracking"
  - "StringBuilder uses GC_malloc + memcpy for buffer growth (no realloc); Boehm GC does not track realloc'd pointers"

patterns-established:
  - "New collection struct typedefs go in lang_runtime.h only — not repeated in lang_runtime.c"
  - "HashSet placement after lang_ht_hash block in lang_runtime.c maintains static visibility"

# Metrics
duration: 7min
completed: 2026-03-29
---

# Phase 33 Plan 01: StringBuilder + HashSet Summary

**LangStringBuilder (append/tostring) and LangHashSet (add/contains/count with murmurhash3) C runtime structs + Elaboration arms + 163/163 E2E tests passing**

## Performance

- **Duration:** 7 min
- **Started:** 2026-03-29T15:13:14Z
- **Completed:** 2026-03-29T15:20:39Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

- LangStringBuilder with GC_malloc-based buffer growth (no realloc) and 3 C functions
- LangHashSet reusing existing lang_ht_hash murmurhash3 with 4 C functions; hashset_add returns I64 (1/0)
- 7 elaboration arms and 7 externalFuncs entries in BOTH lists in Elaboration.fs
- 163/163 E2E tests passing (161 prior + 2 new)

## Task Commits

1. **Task 1: Add StringBuilder and HashSet C runtime + header declarations** - `6aa8d63` (feat)
2. **Task 2: Add Elaboration arms + externalFuncs for StringBuilder and HashSet, plus E2E tests** - `a1ca148` (feat)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/lang_runtime.c` - LangStringBuilder and LangHashSet function implementations
- `src/FunLangCompiler.Compiler/lang_runtime.h` - Struct typedefs + 7 function declarations
- `src/FunLangCompiler.Compiler/Elaboration.fs` - 7 elaboration arms + 7 entries in both externalFuncs lists
- `tests/compiler/33-01-stringbuilder.flt` - E2E: append "hello" + " world" → "hello world"
- `tests/compiler/33-02-hashset.flt` - E2E: add/contains/count with duplicate (1 returns 1/0/0/2)

## Decisions Made

- Struct typedefs defined only in lang_runtime.h — consistent with LangHashtable/LangHashEntry pattern; putting them in .c too causes redefinition errors since the .c file includes the header
- hashset_add uses LlvmCallOp returning I64 (not LlvmCallVoidOp) — matches LangThree's bool return, needed for callers that check membership changes
- StringBuilder buffer growth uses GC_malloc(new_cap) + memcpy — Boehm GC doesn't track pointers returned by realloc

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Removed duplicate struct typedef from lang_runtime.c**

- **Found during:** Task 1 first test run
- **Issue:** Struct typedefs for LangStringBuilder, LangHashSetEntry, LangHashSet added to both lang_runtime.h and lang_runtime.c, causing clang "typedef redefinition" errors
- **Fix:** Removed typedef blocks from lang_runtime.c — struct definitions stay only in the header (which the .c file includes)
- **Files modified:** src/FunLangCompiler.Compiler/lang_runtime.c
- **Verification:** Compilation succeeded; 163/163 tests pass
- **Committed in:** a1ca148 (Task 2 commit includes the fix)

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug)
**Impact on plan:** Essential correctness fix. No scope creep.

## Issues Encountered

- First compilation attempt failed with "typedef redefinition" because the plan specified adding structs to lang_runtime.c without noting they also go in the header (and the .c includes the header). Fixed by moving struct definitions to header only.

## Next Phase Readiness

- StringBuilder and HashSet builtins fully functional; ready for Phase 33-02 (Queue + MutableList)
- All 163 prior tests pass; no regressions

---
*Phase: 33-collection-types*
*Completed: 2026-03-29*
