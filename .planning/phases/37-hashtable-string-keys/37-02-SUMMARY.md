---
phase: 37-hashtable-string-keys
plan: 02
subsystem: compiler
tags: [fsharp, elaboration, hashtable, string-keys, mlir, dispatch, ptr, prelude, e2e-tests]

# Dependency graph
requires:
  - phase: 37-hashtable-string-keys (plan 01)
    provides: LangHashtableStr C runtime with all 9 _str functions (create, get, set, containsKey, remove, keys, trygetvalue) + lang_index_get_str/lang_index_set_str

provides:
  - Ptr-type dispatch in all hashtable builtin arms (get/set/containsKey/remove/trygetvalue)
  - IndexGet/IndexSet dispatch to _str variants when index is Ptr (string key)
  - hashtable_create_str and hashtable_keys_str as separate builtin arms
  - Both externalFuncs lists updated with all 9 _str entries
  - detectCollectionKind recognizes hashtable_create_str as Hashtable
  - Prelude.Hashtable.createStr and Prelude.Hashtable.keysStr wrappers
  - E2E tests: 37-01 (CRUD + keysStr) and 37-02 (content equality, RT-01 fix)

affects:
  - phase 38 (future phases using string-key hashtables)
  - phase 40 (final v10.0 validation)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Ptr-type dispatch: match keyVal.Type with | Ptr -> _str variant | _ -> existing code"
    - "Separate builtin arm for hashtable_keys_str (no key arg to dispatch on)"
    - "IndexSet Ptr value coercion: LlvmPtrToIntOp for storing Ptr-typed values as int64_t"

key-files:
  created:
    - tests/compiler/37-01-hashtable-string-keys.flt
    - tests/compiler/37-02-hashtable-string-content-equality.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs
    - Prelude/Hashtable.fun

key-decisions:
  - "hashtable_keys_str is a SEPARATE builtin arm — keys has no key argument to dispatch on"
  - "IndexSet Ptr-value coercion uses LlvmPtrToIntOp (not ArithExtuIOp) — consistent with hashtable_set"
  - "Test 37-01 uses recursive len function (not list_length builtin) to check keysStr result length"
  - "Test 37-02 uses string_concat not ^ operator (^ not supported by parser)"
  - "Both externalFuncs lists updated atomically with replace_all to ensure symmetry"

patterns-established:
  - "Key-type dispatch: elaborator inspects keyVal.Type and dispatches to _str variant when Ptr"
  - "Value-type dispatch in IndexSet: Ptr -> LlvmPtrToIntOp, I1 -> ArithExtuIOp, I64 -> identity"
  - "Separate named builtin for operations with no key arg (keys_str separate from keys)"

# Metrics
duration: 12min
completed: 2026-03-30
---

# Phase 37 Plan 02: Hashtable String Keys Elaboration Summary

**Ptr-type dispatch in all hashtable builtin arms and IndexGet/IndexSet, wiring C runtime _str functions into MLIR elaboration and verified by two E2E tests confirming content-equality string hashing**

## Performance

- **Duration:** 12 min
- **Started:** 2026-03-29T21:22:35Z
- **Completed:** 2026-03-29T21:34:50Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments

- Elaboration.fs now dispatches all 6 hashtable builtins + IndexGet + IndexSet to _str variants when key/index is Ptr-typed
- hashtable_create_str and hashtable_keys_str added as new builtin arms; both externalFuncs lists updated with 9 new entries
- Prelude/Hashtable.fun extended with `createStr` and `keysStr` wrappers
- Two E2E tests verify: basic CRUD + keysStr (37-01), content equality across different allocations (37-02), resolving RT-01

## Task Commits

Each task was committed atomically:

1. **Task 1: Add key-type dispatch to Elaboration.fs** - `bbaa5e9` (feat)
2. **Task 2: Update Prelude + E2E tests** - `2b8c9ec` (feat)

**Plan metadata:** (see docs commit below)

## Files Created/Modified

- `src/LangBackend.Compiler/Elaboration.fs` - Ptr-type dispatch in hashtable builtins, IndexGet/IndexSet _str dispatch, two new builtin arms, 9 entries in both externalFuncs lists
- `Prelude/Hashtable.fun` - Added createStr() and keysStr() wrappers
- `tests/compiler/37-01-hashtable-string-keys.flt` - E2E: string-key CRUD + keysStr via recursive len
- `tests/compiler/37-02-hashtable-string-content-equality.flt` - E2E: string_concat result lookup (RT-01 verified)

## Decisions Made

1. **hashtable_keys_str is a separate builtin arm** — `keys` has no key argument to dispatch on, so a new named builtin was required
2. **IndexSet Ptr-value coercion via LlvmPtrToIntOp** — consistent with hashtable_set's value handling; stores string pointer as int64_t in C runtime
3. **Test 37-01 uses recursive `len` function** — `list_length` is not a builtin; existing test pattern from 23-06-ht-keys.flt used instead; `hashtable_count` used for key count verification
4. **Test 37-02 uses `string_concat`** — `^` operator not supported by parser; `string_concat "hel" "lo"` is the correct form
5. **Both externalFuncs lists updated with replace_all=true** — both lists were identical, atomic replacement ensured symmetry

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] list_length not a recognized builtin**
- **Found during:** Task 2 (E2E test 37-01)
- **Issue:** The plan said `list_length` is "already available (from the cons list runtime)" but it is not implemented as a builtin in Elaboration.fs or the C runtime
- **Fix:** Used `hashtable_count` for count verification and defined a local `let rec len` function (following the pattern from 23-06-ht-keys.flt) to verify keysStr list length
- **Files modified:** tests/compiler/37-01-hashtable-string-keys.flt
- **Committed in:** 2b8c9ec (Task 2 commit)

**2. [Rule 1 - Bug] ^ operator not supported by parser**
- **Found during:** Task 2 (E2E test 37-02 initial test)
- **Issue:** The plan used `"hel" ^ "lo"` for string concatenation but parser returned "unrecognized input"
- **Fix:** Changed to `string_concat "hel" "lo"` — the existing string concat builtin
- **Files modified:** tests/compiler/37-02-hashtable-string-content-equality.flt
- **Committed in:** 2b8c9ec (Task 2 commit)

**3. [Rule 1 - Bug] flt file format includes expected output as raw code**
- **Found during:** Task 2 (running E2E test 37-01 directly with CLI)
- **Issue:** When running .flt files directly with the CLI, the `// --- Output:` section's raw lines (`42`, `1`, etc.) were parsed as code, causing a segfault
- **Fix:** Used `fslit` test runner (the correct tool: `~/.local/bin/fslit`) which extracts only the `// --- Input:` section before compiling
- **Files modified:** None (test runner usage corrected)

---

**Total deviations:** 3 auto-fixed (all Rule 1 - bug/incorrect assumptions in plan)
**Impact on plan:** All fixes necessary for correct test implementation. No scope creep.

## Issues Encountered

- Initial diagnostic confusion: segfault 139 appeared to be a C runtime crash, but was actually the parser trying to interpret expected output lines as code. Resolved by switching from direct CLI invocation to `fslit` test runner.

## Next Phase Readiness

- Phase 37 complete: RT-01 and RT-02 fully resolved
  - String-key hashtable create/get/set/containsKey/remove/keys/trygetvalue all working
  - Content equality confirmed (FNV-1a hash + memcmp comparison in C runtime)
  - IndexGet/IndexSet syntax `ht.["key"]` and `ht.["key"] <- v` working with string keys
  - All existing integer-key hashtable tests still pass (no regressions)
- Ready for Phase 38 (next v10.0 phase)

---
*Phase: 37-hashtable-string-keys*
*Completed: 2026-03-30*
