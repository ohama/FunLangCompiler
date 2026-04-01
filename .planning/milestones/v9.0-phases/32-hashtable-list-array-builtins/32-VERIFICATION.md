---
phase: 32-hashtable-list-array-builtins
verified: 2026-03-29T14:56:38Z
status: passed
score: 5/5 must-haves verified
re_verification: false
---

# Phase 32: Hashtable & List/Array Builtins Verification Report

**Phase Goal:** Users can compile programs that use hashtable_trygetvalue, hashtable_count, list_sort_by, list_of_seq, array_sort, and array_of_seq.
**Verified:** 2026-03-29T14:56:38Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth                                                                                            | Status     | Evidence                                                                                       |
| --- | ------------------------------------------------------------------------------------------------ | ---------- | ---------------------------------------------------------------------------------------------- |
| 1   | A program calling hashtable_trygetvalue receives a (bool, value) tuple and can pattern-match on the bool | ✓ VERIFIED | 32-01 test: `let (found, v) = hashtable_trygetvalue ht 42` matches and prints `1\n100\n0`; fslit PASS |
| 2   | A program calling hashtable_count returns the correct integer element count                      | ✓ VERIFIED | 32-02 test: count after 2 inserts prints `2`; fslit PASS                                       |
| 3   | A program calling list_sort_by with a comparison closure produces a correctly ordered list        | ✓ VERIFIED | 32-03 test: `[3;1;2]` sorted with identity key yields `[1;2;3]`, encoded sum `321`; fslit PASS |
| 4   | A program calling list_of_seq and array_of_seq on a collection returns an equivalent list/array  | ✓ VERIFIED | 32-04 test: `list_of_seq [10;20]` -> sum `30`; 32-06 test: `array_of_seq` len 3, elements correct; fslit PASS |
| 5   | A program calling array_sort produces a sorted array in place                                    | ✓ VERIFIED | 32-05 test: `[3;1;2]` array sorted in place, `array_get` returns `1`, `2`, `3`; fslit PASS    |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact                                             | Expected                                                   | Status      | Details                                                                                    |
| ---------------------------------------------------- | ---------------------------------------------------------- | ----------- | ------------------------------------------------------------------------------------------ |
| `src/FunLangCompiler.Compiler/lang_runtime.c`            | `lang_hashtable_trygetvalue` C function                    | ✓ VERIFIED  | Line 323: 16-byte GC-allocated tuple, bool flag + value, substantive (12 lines)           |
| `src/FunLangCompiler.Compiler/lang_runtime.c`            | `lang_list_sort_by` C function                             | ✓ VERIFIED  | Line 551: insertion sort with LangClosureFn ABI, 37 lines, no stubs                       |
| `src/FunLangCompiler.Compiler/lang_runtime.c`            | `lang_list_of_seq` C function                              | ✓ VERIFIED  | Line 590: identity cast, minimal but correct                                               |
| `src/FunLangCompiler.Compiler/lang_runtime.c`            | `lang_array_sort` C function                               | ✓ VERIFIED  | Line 603: qsort wrapper with static comparator, correct `arr[0]=length` layout             |
| `src/FunLangCompiler.Compiler/lang_runtime.c`            | `lang_array_of_seq` C function                             | ✓ VERIFIED  | Line 610: delegates to `lang_array_of_list`, one-line wrapper                             |
| `src/FunLangCompiler.Compiler/lang_runtime.h`            | Declarations for all 5 new functions                       | ✓ VERIFIED  | Lines 53, 97-101: all 5 declarations present                                              |
| `src/FunLangCompiler.Compiler/Elaboration.fs`            | `hashtable_trygetvalue` elaboration arm                    | ✓ VERIFIED  | Line 1095: two-arg curried, key coercion to I64, `LlvmCallOp` to `@lang_hashtable_trygetvalue` |
| `src/FunLangCompiler.Compiler/Elaboration.fs`            | `hashtable_count` elaboration arm (inline GEP)             | ✓ VERIFIED  | Line 1107: `LlvmGEPLinearOp(sizePtr, htVal, 2)` + `LlvmLoadOp`; no C function needed, correct field index (struct: tag=0, capacity=1, size=2) |
| `src/FunLangCompiler.Compiler/Elaboration.fs`            | `list_sort_by` elaboration arm                             | ✓ VERIFIED  | Line 1192: two-arg curried, closure coercion mirrors array_map, `LlvmCallOp` to `@lang_list_sort_by` |
| `src/FunLangCompiler.Compiler/Elaboration.fs`            | `list_of_seq` elaboration arm                              | ✓ VERIFIED  | Line 1207: one-arg, `LlvmCallOp` to `@lang_list_of_seq`                                  |
| `src/FunLangCompiler.Compiler/Elaboration.fs`            | `array_sort` elaboration arm                               | ✓ VERIFIED  | Line 1213: `LlvmCallVoidOp` + `ArithConstantOp(unitVal, 0L)` void-return pattern         |
| `src/FunLangCompiler.Compiler/Elaboration.fs`            | `array_of_seq` elaboration arm                             | ✓ VERIFIED  | Line 1219: one-arg, `LlvmCallOp` to `@lang_array_of_seq`                                 |
| `tests/compiler/32-01-hashtable-trygetvalue.flt`     | E2E test for hashtable_trygetvalue (found + not-found)     | ✓ VERIFIED  | found=1, v=100, found2=0 — fslit PASS                                                     |
| `tests/compiler/32-02-hashtable-count.flt`           | E2E test for hashtable_count                               | ✓ VERIFIED  | count=2 after 2 inserts — fslit PASS                                                      |
| `tests/compiler/32-03-list-sort-by.flt`              | E2E test for list_sort_by                                  | ✓ VERIFIED  | `[3;1;2]` -> `[1;2;3]`, sum encoding 321 — fslit PASS                                    |
| `tests/compiler/32-04-list-of-seq.flt`               | E2E test for list_of_seq                                   | ✓ VERIFIED  | `[10;20]` identity, sum=30 — fslit PASS                                                   |
| `tests/compiler/32-05-array-sort.flt`                | E2E test for array_sort                                    | ✓ VERIFIED  | `[3;1;2]` sorted in place to `[1;2;3]` — fslit PASS                                      |
| `tests/compiler/32-06-array-of-seq.flt`              | E2E test for array_of_seq                                  | ✓ VERIFIED  | list `[10;20;30]` to array, length=3, arr[0]=10, arr[2]=30 — fslit PASS                  |

### Key Link Verification

| From                               | To                                  | Via                                           | Status     | Details                                                                    |
| ---------------------------------- | ----------------------------------- | --------------------------------------------- | ---------- | -------------------------------------------------------------------------- |
| `Elaboration.fs` arm               | `@lang_hashtable_trygetvalue`       | `LlvmCallOp` + `externalFuncs` in both lists  | ✓ WIRED    | Lines 1103, 2882, 3087                                                     |
| `Elaboration.fs` arm               | `LangHashtable.size` (field index 2)| Inline `LlvmGEPLinearOp(sizePtr, htVal, 2)`   | ✓ WIRED    | Lines 1107-1115; struct layout confirmed: tag=0, capacity=1, size=2       |
| `Elaboration.fs` arm               | `@lang_list_sort_by`                | `LlvmCallOp` + `externalFuncs` in both lists  | ✓ WIRED    | Lines 1204, 2916, 3121                                                     |
| `Elaboration.fs` arm               | `@lang_list_of_seq`                 | `LlvmCallOp` + `externalFuncs` in both lists  | ✓ WIRED    | Lines 1210, 2917, 3122                                                     |
| `Elaboration.fs` arm               | `@lang_array_sort`                  | `LlvmCallVoidOp` + `externalFuncs` in both lists | ✓ WIRED | Lines 1216, 2918, 3123; void return handled with `ArithConstantOp` unit  |
| `Elaboration.fs` arm               | `@lang_array_of_seq`                | `LlvmCallOp` + `externalFuncs` in both lists  | ✓ WIRED    | Lines 1222, 2919, 3124                                                     |
| `lang_array_of_seq` (C)            | `lang_array_of_list` (C)            | Direct call delegation                         | ✓ WIRED    | `return lang_array_of_list((LangCons*)collection);`                        |
| `lang_hashtable_trygetvalue` (C)   | `lang_ht_find` (static helper)     | Direct call                                    | ✓ WIRED    | `LangHashEntry* e = lang_ht_find(ht, key);`                               |

### Requirements Coverage

| Requirement | Status      | Evidence                                                         |
| ----------- | ----------- | ---------------------------------------------------------------- |
| HT-01       | ✓ SATISFIED | `hashtable_trygetvalue` returns (bool, value) tuple; test 32-01 proves pattern-match on bool |
| HT-02       | ✓ SATISFIED | `hashtable_count` reads `size` field via inline GEP; test 32-02 returns correct count |
| LA-01       | ✓ SATISFIED | `list_sort_by` with closure key extractor; test 32-03 proves ordering |
| LA-02       | ✓ SATISFIED | `list_of_seq` is identity pass-through; test 32-04 proves list preserved |
| LA-03       | ✓ SATISFIED | `array_sort` in-place qsort; test 32-05 proves sorted order      |
| LA-04       | ✓ SATISFIED | `array_of_seq` delegates to `array_of_list`; test 32-06 proves correct array |

### Anti-Patterns Found

None. Reviewed all modified files:

- `lang_runtime.c`: No TODO/FIXME/placeholder in new functions. All functions have real implementations.
- `lang_runtime.h`: Declarations only — correct.
- `Elaboration.fs`: All new arms have real MLIR op sequences. No stubs.
- Test files: All have real expected outputs verified against actual execution.

### Human Verification Required

None. All goal truths were verified programmatically via the `fslit` test runner.

## Summary

All 6 builtins (hashtable_trygetvalue, hashtable_count, list_sort_by, list_of_seq, array_sort, array_of_seq) are fully implemented, wired, and verified by 6 E2E tests. 161/161 total tests pass — no regressions.

The implementation follows established patterns:
- Inline GEP for struct field reads (hashtable_count)
- LangClosureFn ABI for closure-taking builtins (list_sort_by)
- LlvmCallVoidOp + ArithConstantOp unit result for void-return C functions (array_sort)
- externalFuncs entries in both elaborateModule and elaborateProgram (both lists kept in sync)

---

_Verified: 2026-03-29T14:56:38Z_
_Verifier: Claude (gsd-verifier)_
