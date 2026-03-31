---
phase: 37-hashtable-string-keys
verified: 2026-03-30T00:00:00Z
status: passed
score: 5/5 must-haves verified
---

# Phase 37: Hashtable String Keys — Verification Report

**Phase Goal:** Hashtable works with string keys, not just integer keys
**Verified:** 2026-03-30T00:00:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `hashtable_create_str ()` creates a string-key hashtable with tag=-2 | VERIFIED | `lang_runtime.c` line 491-499: `ht->tag = -2`, capacity=16, buckets zeroed. Test 37-01 exercises create+set+get and passes. |
| 2 | `hashtable_set/get/containsKey/remove/trygetvalue` dispatch to `_str` variants when key is Ptr-typed | VERIFIED | `Elaboration.fs` lines 1199-1363: all 6 builtin arms match on `keyVal.Type = Ptr` and call `@lang_hashtable_*_str`. IndexGet (line 1244-1246) and IndexSet (line 1266-1268) also dispatch to `@lang_index_get_str`/`@lang_index_set_str` when `idxVal.Type = Ptr`. |
| 3 | `hashtable_keys_str` is a separate builtin arm (keys has no key arg to dispatch on) | VERIFIED | `Elaboration.fs` lines 1333-1339: dedicated `App (Var ("hashtable_keys_str", _), ...)` arm. Test 37-01 verifies keysStr returns correct count (1) via recursive `len`. |
| 4 | `ht.["hello"] <- 42` and `ht.["hello"]` use `lang_index_set_str`/`lang_index_get_str` when index is Ptr | VERIFIED | Direct invocation test: `hashtable_create_str`, `ht2.["foo"] <- 77`, `println (to_string ht2.["foo"])` produced output `77` with exit 0. |
| 5 | String keys with identical content but different allocations hash to the same bucket | VERIFIED | Test 37-02 passes: `s1 = string_concat "hel" "lo"` then `hashtable_get ht s1` returns 99 (same as value stored under `"hello"`). FNV-1a hash + memcmp content equality confirmed. |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/LangBackend.Compiler/lang_runtime.h` | `LangHashEntryStr`, `LangHashtableStr` structs + 9 function declarations | VERIFIED | Lines 58-79: both structs present; all 9 declarations present (create, get, set, containsKey, remove, keys, trygetvalue, index_get, index_set). 195 lines total. |
| `src/LangBackend.Compiler/lang_runtime.c` | 3 static helpers + 9 public functions | VERIFIED | Lines 447-592: FNV-1a hash, memcmp find, rehash, plus all 9 public functions. 146 lines of new code. `grep` count: 11 occurrences of `_str` function definitions. |
| `src/LangBackend.Compiler/Elaboration.fs` | Key-type dispatch in all hashtable builtins + IndexGet/IndexSet + 2 new builtin arms + 9 entries in both externalFuncs lists | VERIFIED | Ptr dispatch confirmed in set (line 1209), get (1229), IndexGet (1246), IndexSet (1268), containsKey (1287), remove (1314), trygetvalue (1363). Both externalFuncs lists at lines 3377-3385 and 3628-3636 each contain all 9 `_str` entries. `inferCollectionKind` at line 80 recognizes `hashtable_create_str`. |
| `Prelude/Hashtable.fun` | `createStr` and `keysStr` wrappers | VERIFIED | Lines 3 and 8: `let createStr () = hashtable_create_str ()` and `let keysStr ht = hashtable_keys_str ht`. |
| `tests/compiler/37-01-hashtable-string-keys.flt` | E2E: CRUD + keysStr, expected output matches | VERIFIED | fslit PASS. Output: 42, 1, 0, 2, 0, 1, 1, 0 — covers set, get, containsKey (found), containsKey (missing), count (2), remove, containsKey after remove, keysStr length. |
| `tests/compiler/37-02-hashtable-string-content-equality.flt` | E2E: content equality (different allocations), expected output matches | VERIFIED | fslit PASS. Output: 99, 1 — `string_concat "hel" "lo"` lookup returns same value as stored under `"hello"` literal, confirming FNV-1a + memcmp. |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `Elaboration.fs` builtin arms | `@lang_hashtable_*_str` in `lang_runtime.c` | `LlvmCallOp` with `@lang_hashtable_*_str` names; both externalFuncs lists declare parameter/return types | WIRED | All 9 `@lang_hashtable_*_str` names appear in both externalFuncs lists AND in builtin arm dispatch code. `dotnet build` succeeded (0 errors, 0 warnings) confirming MLIR validation passes. |
| `Elaboration.fs IndexGet` | `@lang_index_get_str` | `Ptr` branch at line 1244-1246 | WIRED | Direct test confirms correct output (77) for `ht2.["foo"]` after `ht2.["foo"] <- 77`. |
| `Elaboration.fs IndexSet` | `@lang_index_set_str` | `Ptr` branch at line 1266-1268 | WIRED | Same direct test confirms IndexSet stores value correctly. |
| `lang_ht_str_find` | `memcmp` | `e->key->length == key->length && memcmp(e->key->data, key->data, (size_t)key->length) == 0` | WIRED | E2E test 37-02 confirms content equality holds across different string allocations. |

### Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| RT-01: String keys use content equality, not pointer identity | SATISFIED | `lang_ht_str_find` uses `memcmp` (line 464-465). Test 37-02 verifies: `string_concat "hel" "lo"` lookup finds value stored under `"hello"` literal. |
| RT-02: get/set/containsKey/remove work with string keys | SATISFIED | Test 37-01 exercises all operations. All produce correct output. |

### Anti-Patterns Found

None. No TODO/FIXME/placeholder patterns found in the modified files. All function bodies have substantive implementations.

### Integer Hashtable Regression Check

| Test | Status |
|------|--------|
| `32-01-hashtable-trygetvalue.flt` | PASS |
| `32-02-hashtable-count.flt` | PASS |
| `35-02-hashtable-module.flt` | PASS |

The new `LlvmPtrToIntOp` branch in `IndexSet` for Ptr-typed values (line 1261-1263) does not affect the existing integer/bool paths — integer-key tests continue to pass.

### Human Verification Required

None. All success criteria are verifiable programmatically and confirmed by E2E tests.

## Gaps Summary

No gaps. All 5 observable truths are verified. Both E2E tests pass. Build succeeds with 0 errors and 0 warnings. Integer hashtable regression tests pass.

---

_Verified: 2026-03-30T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
