---
phase: 33-collection-types
verified: 2026-03-29T15:32:36Z
status: passed
score: 4/4 must-haves verified
re_verification: false
---

# Phase 33: Collection Types Verification Report

**Phase Goal:** Users can compile programs that create and manipulate the four new collection types: StringBuilder, HashSet, Queue, and MutableList.
**Verified:** 2026-03-29T15:32:36Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth                                                                                                     | Status     | Evidence                                                                      |
| --- | --------------------------------------------------------------------------------------------------------- | ---------- | ----------------------------------------------------------------------------- |
| 1   | A program creating a StringBuilder, calling add multiple times, and calling toString compiles and returns the concatenated string | ✓ VERIFIED | Compiled and ran: output "hello world", exit 0                                |
| 2   | A program creating a HashSet, calling add and contains, compiles and returns correct membership results   | ✓ VERIFIED | Compiled and ran: contains(1)=1, contains(3)=0, count=2, exit 0              |
| 3   | A program creating a Queue, calling enqueue and dequeue, compiles and returns elements in FIFO order      | ✓ VERIFIED | Compiled and ran: dequeue→10, dequeue→20, count=1, exit 0                    |
| 4   | A program creating a MutableList, calling add, indexed get/set, and count, compiles and returns correct values | ✓ VERIFIED | Compiled and ran: count=2, get(0)=999 (after set), get(1)=200, exit 0         |

**Score:** 4/4 truths verified

### Required Artifacts

| Artifact                                              | Expected                                          | Status      | Details                                                                                         |
| ----------------------------------------------------- | ------------------------------------------------- | ----------- | ----------------------------------------------------------------------------------------------- |
| `src/FunLangCompiler.Compiler/lang_runtime.c`             | LangStringBuilder + LangHashSet + LangQueue + LangMutableList C functions | ✓ VERIFIED  | 1024 lines; all 16 functions present (lines 615–756); no realloc                                |
| `src/FunLangCompiler.Compiler/lang_runtime.h`             | Struct typedefs + all 16 function declarations     | ✓ VERIFIED  | 161 lines; all typedefs (LangStringBuilder, LangHashSetEntry, LangHashSet, LangQueueNode, LangQueue, LangMutableList) and 16 declarations present |
| `src/FunLangCompiler.Compiler/Elaboration.fs`             | Elaboration arms for all 16 builtins + externalFuncs in both lists | ✓ VERIFIED  | All 16 functions have exactly 3 occurrences each (1 arm + 2 externalFuncs lists); mutablelist_set (line 1289) before mutablelist_get (line 1299) |
| `tests/compiler/33-01-stringbuilder.flt`              | E2E test for StringBuilder                         | ✓ VERIFIED  | File exists; compiled and executed; output matches expected "hello world\n0"   |
| `tests/compiler/33-02-hashset.flt`                    | E2E test for HashSet                               | ✓ VERIFIED  | File exists; compiled and executed; output matches expected "1\n0\n2\n0"       |
| `tests/compiler/33-03-queue.flt`                      | E2E test for Queue                                 | ✓ VERIFIED  | File exists; compiled and executed; output matches expected "10\n20\n1\n0"     |
| `tests/compiler/33-04-mutablelist.flt`                | E2E test for MutableList                           | ✓ VERIFIED  | File exists; compiled and executed; output matches expected "2\n999\n200\n0"   |

### Key Link Verification

| From                  | To                    | Via                                          | Status      | Details                                                                                          |
| --------------------- | --------------------- | -------------------------------------------- | ----------- | ------------------------------------------------------------------------------------------------ |
| Elaboration.fs        | lang_runtime.c        | @lang_sb_create in externalFuncs (both lists) | ✓ WIRED     | lang_sb_create appears 3 times (1 arm + 2 ext lists); same for all 7 StringBuilder/HashSet fns  |
| Elaboration.fs        | lang_runtime.c        | @lang_queue_create in externalFuncs (both lists) | ✓ WIRED  | lang_queue_create appears 3 times; same for all 9 Queue/MutableList fns                          |
| mutablelist_set arm   | mutablelist_get arm   | Pattern ordering in Elaboration.fs           | ✓ WIRED     | mutablelist_set at line 1289, mutablelist_get at line 1299; three-arg before two-arg             |
| queue_dequeue arm     | lang_queue_dequeue    | Two-arg curried: App(App(Var "queue_dequeue", q), unit) | ✓ WIRED | unit arg elaborated and discarded; C function receives only queue pointer                      |

### Requirements Coverage

| Requirement | Status       | Notes                                                       |
| ----------- | ------------ | ----------------------------------------------------------- |
| COL-01      | ✓ SATISFIED  | StringBuilder: create/append/tostring functional; E2E passes |
| COL-02      | ✓ SATISFIED  | HashSet: create/add/contains/count functional; add returns I64 (1=new, 0=duplicate); E2E passes |
| COL-03      | ✓ SATISFIED  | Queue: create/enqueue/dequeue/count functional; FIFO order verified; E2E passes |
| COL-04      | ✓ SATISFIED  | MutableList: create/add/get/set/count functional; bounds check in get/set; E2E passes |

### Anti-Patterns Found

| File                   | Line | Pattern       | Severity | Impact                                                  |
| ---------------------- | ---- | ------------- | -------- | ------------------------------------------------------- |
| `lang_runtime.c`       | 331  | "placeholder" | Info     | Comment in pre-existing `lang_ht_trygetvalue` labeling a tuple field; unrelated to phase 33 code; not a stub |

No blockers or warnings found. The single "placeholder" string is a descriptive comment from an earlier phase (Phase 32 hashtable code).

### Verified Implementation Details

**StringBuilder (COL-01):**
- `lang_sb_create` (line 615): GC_malloc struct, buf cap=64, len=0
- `lang_sb_append` (line 623): GC_malloc+memcpy growth (no realloc), memcpy string data
- `lang_sb_tostring` (line 638): GC_malloc LangString from buf, null-terminated
- Struct typedef in lang_runtime.h only (no redefinition in .c)

**HashSet (COL-02):**
- `lang_hashset_create` (line 649): capacity=16, GC_malloc buckets, zero-init
- `lang_hashset_add` (line 658): returns int64_t (1=new, 0=duplicate); reuses `lang_ht_hash` static function
- `lang_hashset_contains` (line 673): returns int64_t 1 or 0
- `lang_hashset_count` (line 683): returns hs->size
- hashset_add elaborated with LlvmCallOp (ExtReturn = Some I64), not LlvmCallVoidOp

**Queue (COL-03):**
- `lang_queue_create` (line 686): head=NULL, tail=NULL, count=0
- `lang_queue_enqueue` (line 694): GC_malloc node, append at tail, count++
- `lang_queue_dequeue` (line 708): calls lang_failwith on empty queue; pops head, count--
- `lang_queue_count` (line 720): returns q->count
- Elaboration: queue_dequeue is two-arg App(App(Var "queue_dequeue", q), unit) — unit discarded

**MutableList (COL-04):**
- `lang_mlist_create` (line 723): cap=8, GC_malloc data (8*8 bytes), len=0
- `lang_mlist_add` (line 731): GC_malloc+memcpy cap-doubling growth (no realloc)
- `lang_mlist_get` (line 742): bounds check, return ml->data[index]
- `lang_mlist_set` (line 749): bounds check, set ml->data[index]
- `lang_mlist_count` (line 756): returns ml->len
- Ordering: mutablelist_set (three-arg) at Elaboration.fs:1289 before mutablelist_get (two-arg) at :1299

### Human Verification Required

None. All four success criteria were verified by compiling and executing the exact programs described in the phase goal. Each program ran without errors and produced the exact expected output.

---

_Verified: 2026-03-29T15:32:36Z_
_Verifier: Claude (gsd-verifier)_
