---
phase: 90-hashtable-unification
plan: 01
subsystem: runtime-compiler
tags: [hashtable, LSB-dispatch, tagged-integers, C-runtime, codegen]
dependency-graph:
  requires: [88, 89]
  provides: [unified-hashtable, no-str-variants]
  affects: []
tech-stack:
  added: []
  patterns: [LSB-dispatch-for-key-types]
key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - src/FunLangCompiler.Compiler/lang_runtime.h
    - src/FunLangCompiler.Compiler/Elaboration.fs
    - src/FunLangCompiler.Compiler/ElabHelpers.fs
    - src/FunLangCompiler.Compiler/ElabProgram.fs
    - Prelude/Hashtable.fun
    - tests/compiler/66-09-int-hashtablestr-prelude.sh
    - tests/compiler/66-09-int-hashtablestr-prelude.flt
    - tests/compiler/37-01-hashtable-string-keys.flt
    - tests/compiler/37-02-hashtable-string-content-equality.flt
decisions:
  - id: LSB-dispatch
    description: "key & 1 selects hash/equality: tagged int (LSB=1) vs string pointer (LSB=0)"
  - id: keys-stored-as-is
    description: "Hashtable stores keys as-is (tagged int or raw pointer) — no untag/retag needed"
  - id: hashset-separate-hash
    description: "HashSet gets dedicated lang_hs_hash (murmurhash only) since it stores raw untagged ints"
  - id: index-get-retag
    description: "lang_index_get/set retag index when routing to hashtable (compiler untags for array compat)"
metrics:
  duration: "~15 min"
  completed: 2026-04-07
---

# Phase 90 Plan 01: Hashtable Unification Summary

**One-liner:** Unified C hashtable with LSB key dispatch, removed all *_str function duplication from C/compiler/Prelude.

## What Was Done

### Task 1: Unify C hashtable (43fe1c5)
- Replaced `lang_ht_hash` with LSB-dispatch version: `key & 1` selects murmurhash (int) vs FNV-1a (string)
- Added `lang_ht_eq` for unified equality: tagged int direct compare vs string content memcmp
- Updated `lang_ht_find` and `lang_hashtable_remove` to use `lang_ht_eq`
- Updated `lang_for_in_hashtable` to pass `e->key` directly (no LANG_TAG_INT needed)
- Removed `LangHashtableStr`, `LangHashEntryStr` structs and all 7 `_str` C functions
- Kept `lang_index_get_str`/`lang_index_set_str` as thin wrappers coercing `LangString*` to `int64_t`

### Task 2: Unify compiler patterns (e5dcaeb)
- Removed 7 explicit `_str` match patterns from Elaboration.fs
- Simplified remaining 7 hashtable patterns: use `coerceToI64` for key (Ptr->PtrToInt, I64->as-is)
- Removed all `emitUntag` calls on int keys (keys now stored tagged)
- Removed 7 `_str` external declarations from ElabProgram.fs (both blocks)
- Removed `hashtable_create_str` from `detectCollectionKind` in ElabHelpers.fs
- Cleaned up KnownFuncs list
- Fixed `lang_index_get/set` to retag index when routing to hashtable

### Task 3: Clean Prelude and tests (3ae0686)
- Removed all 8 `*Str` functions from Prelude/Hashtable.fun
- Updated test 66-09 to use unified API
- Updated tests 37-01 and 37-02 to use `hashtable_create` instead of `_str`
- Fixed LangString forward declaration ordering in lang_runtime.h
- Added `lang_hs_hash` for HashSet (murmurhash only, no LSB dispatch)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] HashSet segfault from LSB dispatch**
- **Found during:** Task 3 (test verification)
- **Issue:** HashSet stores raw (untagged) ints. Even values have LSB=0, causing `lang_ht_hash` to dereference them as string pointers → segfault
- **Fix:** Added dedicated `lang_hs_hash` function (murmurhash only) for HashSet
- **Files modified:** lang_runtime.c
- **Commit:** 3ae0686

**2. [Rule 1 - Bug] LangString forward declaration ordering**
- **Found during:** Task 3 (compile verification)
- **Issue:** `lang_index_get_str`/`lang_index_set_str` declarations moved before `struct LangString_s` forward declaration, causing "conflicting types" compile error
- **Fix:** Moved forward declaration above the index_str wrappers
- **Files modified:** lang_runtime.h
- **Commit:** 3ae0686

**3. [Rule 2 - Critical] lang_index_get/set retag for hashtable path**
- **Found during:** Task 2 (analysis)
- **Issue:** `lang_index_get/set` receives untagged index from compiler (for array compatibility) but routes to `lang_hashtable_get` which now expects tagged keys
- **Fix:** Added `LANG_TAG_INT(index)` in the hashtable branch of `lang_index_get/set`
- **Files modified:** lang_runtime.c
- **Commit:** e5dcaeb

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| LSB dispatch (`key & 1`) | Tagged ints have LSB=1, heap pointers have LSB=0 — natural discriminator |
| Keys stored as-is | Eliminates untag/retag overhead; simplifies compiler codegen |
| Separate hash for HashSet | HashSet stores raw ints (Phase 88 untag convention), can't use LSB dispatch |
| Keep index_get/set_str wrappers | Compiler dispatches `ht.["key"]` syntax on Ptr type; simpler than changing IndexGet/IndexSet codegen |

## Verification

- 257/257 E2E tests pass
- `hashtable_create()` works for both int and string keys
- No `*Str` functions in Prelude/Hashtable.fun
- No `*_str` patterns in Elaboration.fs
- Single LangHashtable struct in C runtime

## Net Code Impact

- **C runtime:** -173 lines (removed), +43 lines (unified) = -130 net
- **Elaboration.fs:** -155 lines (removed), +36 lines (unified) = -119 net
- **ElabProgram.fs:** -14 lines (7 _str decls x 2 blocks)
- **Total:** ~260 lines removed
