---
phase: 23-hashtable
verified: 2026-03-27T13:00:00Z
status: passed
score: 5/5 must-haves verified
re_verification: false
---

# Phase 23: Hashtable Verification Report

**Phase Goal:** Hashtable creation, key-value insertion/lookup/removal, and key enumeration all work correctly
**Verified:** 2026-03-27T13:00:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth                                                                  | Status     | Evidence                                                                                          |
| --- | ---------------------------------------------------------------------- | ---------- | ------------------------------------------------------------------------------------------------- |
| 1   | `hashtable_create ()` returns a usable empty hashtable                 | VERIFIED   | `lang_hashtable_create` in lang_runtime.c L239-248: GC_malloc, cap=16, size=0; test 23-01 passes |
| 2   | `hashtable_set` then `hashtable_get` round-trips correctly             | VERIFIED   | `lang_hashtable_set` updates or inserts; `lang_hashtable_get` returns `e->val`; test 23-02 passes |
| 3   | `hashtable_get` on missing key raises a catchable exception            | VERIFIED   | `lang_hashtable_get` calls `lang_throw((void*)msg)` on NULL find; test 23-03 catches it via try/with |
| 4   | `hashtable_containsKey` returns true after insert, false after remove  | VERIFIED   | `lang_ht_find != NULL ? 1 : 0`; elaboration emits ArithCmpIOp("ne") for I1 bool; tests 23-04, 23-05 pass |
| 5   | `hashtable_keys` returns a cons-cell list of all stored keys           | VERIFIED   | `lang_hashtable_keys` builds `LangCons*` chain; tests 23-06 and 23-08 count 2 keys correctly       |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact                                              | Expected                                              | Status      | Details                                               |
| ----------------------------------------------------- | ----------------------------------------------------- | ----------- | ----------------------------------------------------- |
| `src/LangBackend.Compiler/lang_runtime.h`             | LangHashEntry + LangHashtable typedefs + 6 decls      | VERIFIED    | L34-51: both structs + 6 function declarations present |
| `src/LangBackend.Compiler/lang_runtime.c`             | 6 C functions, ~120 LOC, GC_malloc discipline         | VERIFIED    | L239-339: 101 LOC of hashtable code, 30 GC_malloc/hash usages |
| `src/LangBackend.Compiler/Elaboration.fs`             | 6 match arms + coercion + 2x ExternalFuncDecl lists   | VERIFIED    | 24 lang_hashtable references; arms at L885-969; ExternalFuncDecls at L2228-2233 and L2371-2376 |
| `tests/compiler/23-01-ht-create.flt`                  | E2E test for hashtable_create                         | VERIFIED    | Exists, 4 lines, output "42", test passes             |
| `tests/compiler/23-02-ht-set-get.flt`                 | E2E test for set+get round-trip                       | VERIFIED    | Exists, 5 lines, output "42", test passes             |
| `tests/compiler/23-03-ht-missing-key.flt`             | E2E test for missing key exception                    | VERIFIED    | Exists, 5 lines, output "99", test passes             |
| `tests/compiler/23-04-ht-contains-true.flt`           | E2E test for containsKey true                         | VERIFIED    | Exists, 5 lines, output "1", test passes              |
| `tests/compiler/23-05-ht-remove.flt`                  | E2E test for containsKey false after remove           | VERIFIED    | Exists, 6 lines, output "0", test passes              |
| `tests/compiler/23-06-ht-keys.flt`                    | E2E test for keys enumeration                         | VERIFIED    | Exists, 10 lines, output "2" (len of cons list), passes |
| `tests/compiler/23-07-ht-overwrite.flt`               | E2E test for overwriting existing key                 | VERIFIED    | Exists, 6 lines, output "20", test passes             |
| `tests/compiler/23-08-ht-multi-ops.flt`               | E2E test for combined operations                      | VERIFIED    | Exists, 12 lines, set 3/remove 1/keys-len=2, passes  |

### Key Link Verification

| From                              | To                                      | Via                                              | Status      | Details                                                      |
| --------------------------------- | --------------------------------------- | ------------------------------------------------ | ----------- | ------------------------------------------------------------ |
| `lang_runtime.c`                  | `lang_throw`                            | `lang_hashtable_get` calls `lang_throw` on NULL  | WIRED       | L261: `lang_throw((void*)msg)` after LangString* construction |
| `lang_runtime.c`                  | `GC_malloc`                             | All allocations use GC_malloc                    | WIRED       | 30 usages confirmed across hashtable functions               |
| `lang_runtime.c`                  | `LangCons`                              | `lang_hashtable_keys` builds cons-cell list      | WIRED       | L326-338: GC_malloc(sizeof(LangCons)), cell->head/tail       |
| `Elaboration.fs`                  | `@lang_hashtable_create`                | LlvmCallOp in hashtable_create match arm         | WIRED       | L969: `LlvmCallOp(result, "@lang_hashtable_create", [])`     |
| `Elaboration.fs`                  | `@lang_hashtable_set`                   | LlvmCallVoidOp in hashtable_set match arm        | WIRED       | L904: `LlvmCallVoidOp("@lang_hashtable_set", [htVal; keyI64; valI64])` |
| `Elaboration.fs`                  | ExternalFuncDecl (both lists)           | 6 entries added after `@lang_array_to_list`      | WIRED       | L2228-2233 (elaborateModule) and L2371-2376 (elaborateProgram) both updated |
| `Elaboration.fs` containsKey arm  | ArithCmpIOp I1 conversion               | I64 result from C compared ne 0 → I1 bool        | WIRED       | L936-940: rawVal:I64 → ArithCmpIOp("ne", rawVal, zero) → boolResult:I1 |

### Requirements Coverage

| Requirement | Status      | Evidence / Notes                                                                                                       |
| ----------- | ----------- | ---------------------------------------------------------------------------------------------------------------------- |
| HT-01       | SATISFIED   | LangHashtable struct with chained buckets, GC_malloc throughout                                                        |
| HT-02       | SATISFIED   | hashtable_create builtin: App(Var "hashtable_create") arm calls @lang_hashtable_create → Ptr                          |
| HT-03       | SATISFIED   | hashtable_get raises exception on missing key (lang_throw), caught by try/with in test 23-03                          |
| HT-04       | SATISFIED   | hashtable_set inserts/updates; round-trip verified in test 23-02; overwrite in test 23-07                             |
| HT-05       | SATISFIED   | hashtable_containsKey returns I1 bool; tested in 23-04 (true after insert) and 23-05 (false after remove)            |
| HT-06       | SATISFIED   | hashtable_keys returns LangCons* list; len counted in tests 23-06 and 23-08                                           |
| HT-07       | SATISFIED   | hashtable_remove unlinks entry with prev-pointer technique; size decremented; tested in 23-05 and 23-08               |
| HT-08       | PARTIAL     | Hash function covers i64 keys only (Option A, conscious design decision). String/tuple/ADT content-equality deferred. Tests use integer keys exclusively. Documented in 23-RESEARCH.md as acceptable scope reduction. |

**Note on HT-08:** The requirement specifies "hash function for boxed int, string, tuple, ADT values." The implementation uses i64-identity hashing (murmurhash3 fmix64 on the i64 key value) per the research recommendation (Option A). String keys are passed as pointer values and compared by identity, not by string content. This is a documented design choice that satisfies all 8 E2E tests which use integer keys. Full string/ADT content-equality hashing is out of scope for this phase per the research decision.

### Anti-Patterns Found

None. Scanned `lang_runtime.c` (L230-339) and all 8 test files for TODO/FIXME/placeholder/stub patterns — clean.

### Human Verification Required

None. All success criteria are verifiable through the test suite, which passed 88/88.

### Test Suite Result

```
Results: 88/88 passed
```

All 8 new hashtable tests (23-01 through 23-08) pass, and all 80 pre-existing regression tests continue to pass.

---

## Gaps Summary

No gaps. All 5 observable truths are verified by passing E2E tests. All required artifacts exist, are substantive, and are wired. The only noteworthy scope item is HT-08 (string/ADT value hashing) which is explicitly documented as a deferred concern per the research phase — it does not affect any test and is not a blocker.

---

_Verified: 2026-03-27T13:00:00Z_
_Verifier: Claude (gsd-verifier)_
