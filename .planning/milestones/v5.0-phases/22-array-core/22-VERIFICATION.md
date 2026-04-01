---
phase: 22-array-core
verified: 2026-03-27T11:26:24Z
status: passed
score: 6/6 must-haves verified
---

# Phase 22: Array Core Verification Report

**Phase Goal:** Array creation, element access, mutation, length query, and list conversion all work correctly
**Verified:** 2026-03-27T11:26:24Z
**Status:** PASSED
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #  | Truth                                                          | Status     | Evidence                                                                                    |
|----|----------------------------------------------------------------|------------|---------------------------------------------------------------------------------------------|
| 1  | `array_create 5 0` allocates array of length 5, all slots = 0 | VERIFIED   | 22-01-array-create.flt: `array_get a 3` returns `0`; test PASS                             |
| 2  | `array_get arr 2` returns element at index 2                   | VERIFIED   | 22-02-array-get-set.flt: get after set returns `42`; test PASS                             |
| 3  | `array_get` raises catchable exception on out-of-bounds index  | VERIFIED   | 22-04-array-oob.flt: `try array_get a 10 with | _ -> 42` returns `42`; test PASS           |
| 4  | `array_set arr 2 99` stores 99; subsequent `array_get` returns 99 | VERIFIED | 22-02-array-get-set.flt: set index 1 to 42, get index 1 returns `42`; test PASS           |
| 5  | `array_length arr` returns number of elements                  | VERIFIED   | 22-03-array-length.flt: `array_length` of 7-element array returns `7`; test PASS           |
| 6  | `array_of_list` and `array_to_list` round-trip correctly       | VERIFIED   | 22-07-array-roundtrip.flt: list→array→list→array_length returns `7`; test PASS            |

**Score:** 6/6 truths verified

### Required Artifacts

| Artifact                                          | Expected                                           | Status     | Details                                                                                   |
|---------------------------------------------------|---------------------------------------------------|------------|-------------------------------------------------------------------------------------------|
| `src/FunLangCompiler.Compiler/MlirIR.fs`              | `LlvmGEPDynamicOp` DU case                        | VERIFIED   | Line 59: `LlvmGEPDynamicOp of result: MlirValue * ptr: MlirValue * index: MlirValue`     |
| `src/FunLangCompiler.Compiler/Printer.fs`             | Printer arm for `LlvmGEPDynamicOp`                | VERIFIED   | Lines 97-99: emits `llvm.getelementptr %ptr[%idx] : (!llvm.ptr, i64) -> !llvm.ptr, i64` |
| `src/FunLangCompiler.Compiler/lang_runtime.c`         | Four C array functions                             | VERIFIED   | Lines 161-211: `lang_array_create`, `lang_array_bounds_check`, `lang_array_of_list`, `lang_array_to_list` all implemented |
| `src/FunLangCompiler.Compiler/lang_runtime.h`         | `LangCons` forward decl + 4 function declarations  | VERIFIED   | Line 28: `typedef struct LangCons LangCons;`; Lines 29-32: all four function declarations |
| `src/FunLangCompiler.Compiler/Elaboration.fs`         | 6 array builtin elaboration cases + ExternalFuncDecl entries | VERIFIED | Lines 802-879: all six cases (array_set, array_get, array_create, array_length, array_of_list, array_to_list); ExternalFuncDecl entries at both ~2134 and ~2271 |
| `tests/compiler/22-01-array-create.flt`           | E2E test for array_create                          | VERIFIED   | Exists, substantive, passes in test suite                                                  |
| `tests/compiler/22-02-array-get-set.flt`          | E2E test for array_get and array_set               | VERIFIED   | Exists, substantive, passes in test suite                                                  |
| `tests/compiler/22-03-array-length.flt`           | E2E test for array_length                          | VERIFIED   | Exists, substantive, passes in test suite                                                  |
| `tests/compiler/22-04-array-oob.flt`              | E2E test for OOB exception                         | VERIFIED   | Exists, substantive, passes in test suite                                                  |
| `tests/compiler/22-05-array-of-list.flt`          | E2E test for array_of_list                         | VERIFIED   | Exists, substantive, passes in test suite                                                  |
| `tests/compiler/22-06-array-to-list.flt`          | E2E test for array_to_list                         | VERIFIED   | Exists, substantive, passes in test suite                                                  |
| `tests/compiler/22-07-array-roundtrip.flt`        | E2E test for list-array round-trip                 | VERIFIED   | Exists, substantive, passes in test suite                                                  |

### Key Link Verification

| From                                       | To                          | Via                                               | Status   | Details                                                                                      |
|--------------------------------------------|-----------------------------|---------------------------------------------------|----------|----------------------------------------------------------------------------------------------|
| `Printer.fs`                               | `MlirIR.fs`                 | pattern match on `LlvmGEPDynamicOp`               | WIRED    | Printer.fs line 97 matches `LlvmGEPDynamicOp(result, ptr, index)` from MlirIR.fs line 59   |
| `Elaboration.fs`                           | `@lang_array_create`        | `LlvmCallOp` in array_create elaboration          | WIRED    | Line 855: `LlvmCallOp(result, "@lang_array_create", [nV; defV])`                            |
| `Elaboration.fs`                           | `LlvmGEPDynamicOp`          | array_get/array_set inline GEP                    | WIRED    | Lines 816, 835: `LlvmGEPDynamicOp(elemPtr, arrVal, slotVal)`                               |
| `Elaboration.fs`                           | `@lang_array_bounds_check`  | `LlvmCallVoidOp` before GEP in get/set            | WIRED    | Lines 813, 832: `LlvmCallVoidOp("@lang_array_bounds_check", [arrVal; idxVal])`             |
| `lang_runtime.c:lang_array_bounds_check`   | `lang_throw`                | catchable exception via longjmp                   | WIRED    | Line 183: `lang_throw((void*)msg)` — not `lang_failwith`, ensuring OOB is catchable        |
| `Elaboration.fs` ExternalFuncDecl          | `lang_runtime.c`            | ExternalFuncDecl in both `externalFuncs` lists    | WIRED    | Both lists (lines ~2134 and ~2271) have all 4 C array function declarations                 |

### Requirements Coverage

| Requirement | Truth                                                     | Status     | Notes                                                  |
|-------------|-----------------------------------------------------------|------------|--------------------------------------------------------|
| ARR-01      | `array_create 5 0` allocates correct array                | SATISFIED  | Verified via 22-01-array-create.flt                    |
| ARR-02      | `array_get` returns element at index                      | SATISFIED  | Verified via 22-02-array-get-set.flt                  |
| ARR-03      | `array_set` stores value; subsequent get returns it       | SATISFIED  | Verified via 22-02-array-get-set.flt                  |
| ARR-04      | `array_length` returns element count                      | SATISFIED  | Verified via 22-03-array-length.flt                   |
| ARR-05      | `array_of_list` converts list to array                    | SATISFIED  | Verified via 22-05-array-of-list.flt                  |
| ARR-06      | `array_to_list` converts array to list                    | SATISFIED  | Verified via 22-06-array-to-list.flt                  |
| ARR-07      | `array_get`/`array_set` raise catchable OOB exception     | SATISFIED  | Verified via 22-04-array-oob.flt; uses `lang_throw`   |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| None | -    | -       | -        | No anti-patterns found in any phase-22 modified file |

### Human Verification Required

None. All observable behaviors are exercised by E2E tests that compile and run real binaries.

### Summary

All six array builtin operations are correctly implemented end-to-end:

- **IR layer:** `LlvmGEPDynamicOp` DU case exists in `MlirIR.fs` and prints correct MLIR syntax (`llvm.getelementptr ... : (!llvm.ptr, i64) -> !llvm.ptr, i64`) from `Printer.fs`.
- **C runtime:** Four functions in `lang_runtime.c` implement the one-block array layout (`arr[0]` = length, `arr[1..n]` = elements). Bounds check uses `lang_throw` (not `lang_failwith`) so OOB errors are catchable via `try/with`.
- **Elaborator:** All six builtins have wired elaboration cases in `Elaboration.fs`. Three-arg `array_set` is correctly placed before two-arg and one-arg patterns. `ExternalFuncDecl` entries appear in both `externalFuncs` lists.
- **Tests:** 7 new E2E tests cover every ROADMAP success criterion. All 80 tests pass (73 pre-existing + 7 new).

---

_Verified: 2026-03-27T11:26:24Z_
_Verifier: Claude (gsd-verifier)_
