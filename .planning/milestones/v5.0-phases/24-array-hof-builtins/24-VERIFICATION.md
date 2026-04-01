---
phase: 24-array-hof-builtins
verified: 2026-03-27T00:00:00Z
status: passed
score: 4/4 must-haves verified
re_verification: false
---

# Phase 24: Array HOF Builtins Verification Report

**Phase Goal:** Higher-order array builtins (iter, map, fold, init) work correctly with arbitrary function arguments
**Verified:** 2026-03-27
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `array_iter (fun x -> print x) arr` calls closure on each element in order | VERIFIED | 24-01-array-iter.flt: input [1;2;3], output `123`, PASS |
| 2 | `array_map (fun x -> x * 2) arr` returns new array with doubled elements | VERIFIED | 24-02-array-map.flt: fold-sum of doubled [1;2;3] = 12, PASS |
| 3 | `array_fold (fun acc -> fun x -> acc + x) 0 arr` returns sum of all elements | VERIFIED | 24-03-array-fold.flt: sum [1..5] = 15, PASS |
| 4 | `array_init 5 (fun i -> i * i)` returns array of squares | VERIFIED | 24-04-array-init.flt: fold-sum of squares [0;1;4;9;16] = 30, PASS |

**Score:** 4/4 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/FunLangCompiler.Compiler/lang_runtime.h` | LangClosureFn typedef + 4 HOF prototypes | VERIFIED | Line 53: `typedef int64_t (*LangClosureFn)(void* env, int64_t arg);` + lines 54-57: all 4 prototypes present (59 lines total) |
| `src/FunLangCompiler.Compiler/lang_runtime.c` | 4 HOF function implementations | VERIFIED | Lines 345-391: all 4 implementations present (391 lines total), loop bodies non-trivial |
| `src/FunLangCompiler.Compiler/Elaboration.fs` | 4 HOF match arms + 8 ExternalFuncDecl entries (4 per list) | VERIFIED | Lines 974-1043: 4 match arms; lines 2308-2311 + 2455-2458: 8 ExternalFuncDecl entries across both lists; 16 total HOF references |
| `tests/compiler/24-01-array-iter.flt` | E2E test for array_iter | VERIFIED | 7 lines, uses `array_iter (fun x -> print (to_string x))`, expected output `1230` |
| `tests/compiler/24-02-array-map.flt` | E2E test for array_map | VERIFIED | 7 lines, uses `array_map (fun x -> x * 2)` + fold verification, expected output `12` |
| `tests/compiler/24-03-array-fold.flt` | E2E test for array_fold | VERIFIED | 6 lines, sums [1;2;3;4;5], expected output `15` |
| `tests/compiler/24-04-array-init.flt` | E2E test for array_init | VERIFIED | 6 lines, `array_init 5 (fun i -> i * i)` + fold verification, expected output `30` |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `lang_runtime.c` | closure slot 0 fn_ptr | `*(LangClosureFn*)closure` dereference | VERIFIED | All 4 functions use this pattern: `LangClosureFn fn = *(LangClosureFn*)closure;` |
| `lang_runtime.c` | GC_malloc for output arrays | `GC_malloc((n+1)*8)` | VERIFIED | lang_array_map (line 356) and lang_array_init (line 385) both use `GC_malloc((size_t)((n + 1) * 8))` |
| `lang_array_fold` | curried two-call pattern | `partial = fn(closure, acc); fn2(partial_ptr, arr[i])` | VERIFIED | Lines 372-375: two-call per iteration with intermediate partial application cast |
| `Elaboration.fs` | `@lang_array_iter` C function | `LlvmCallVoidOp` external call | VERIFIED | Line 1007: `LlvmCallVoidOp("@lang_array_iter", [closurePtrVal; arrVal])` + unit constant |
| `Elaboration.fs` | `@lang_array_map` C function | `LlvmCallOp` external call | VERIFIED | Line 1023: `LlvmCallOp(result, "@lang_array_map", [closurePtrVal; arrVal])` |
| `Elaboration.fs` | `@lang_array_fold` C function | `LlvmCallOp` external call | VERIFIED | Line 991: `LlvmCallOp(result, "@lang_array_fold", [closurePtrVal; initV; arrVal])` |
| `Elaboration.fs` | `@lang_array_init` C function | `LlvmCallOp` external call | VERIFIED | Line 1043: `LlvmCallOp(result, "@lang_array_init", [nV; closurePtrVal])` |
| `Elaboration.fs` | closure ptr coercion | `LlvmIntToPtrOp` when `fVal.Type = I64` | VERIFIED | All 4 match arms use identical coercion guard pattern before passing closurePtr to C runtime |
| `Elaboration.fs` | Both ExternalFuncDecl lists | `elaborateModule` + `elaborateProgram` | VERIFIED | Entries at lines 2308-2311 (elaborateModule) and 2455-2458 (elaborateProgram) — both lists identical |

### Requirements Coverage

| Requirement | Status | Blocking Issue |
|-------------|--------|----------------|
| ARR-08: `array_iter` — `(a -> unit) -> a array -> unit` | SATISFIED | None — 24-01-array-iter.flt passes |
| ARR-09: `array_map` — `(a -> b) -> a array -> b array` | SATISFIED | None — 24-02-array-map.flt passes |
| ARR-10: `array_fold` — `(acc -> a -> acc) -> acc -> a array -> acc` | SATISFIED | None — 24-03-array-fold.flt passes |
| ARR-11: `array_init` — `int -> (int -> a) -> a array` | SATISFIED | None — 24-04-array-init.flt passes |

Note: REQUIREMENTS.md still shows these as `Pending` checkboxes — documentation not updated by the implementation phase. This is a documentation gap, not a code gap; all 4 requirements are functionally satisfied.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| (none) | — | — | — | — |

No stub patterns, TODO/FIXME comments, empty returns, or placeholder content detected in any phase 24 artifacts.

### Human Verification Required

None. All success criteria are verifiable programmatically via the E2E test suite.

### Test Suite Result

**92/92 tests passed** (88 pre-existing + 4 new phase 24 tests). Zero regressions.

The 4 new tests cover every phase success criterion:
- `24-01-array-iter.flt`: iter prints elements in order (output `1230`)
- `24-02-array-map.flt`: map doubles elements, fold-verified (output `12`)
- `24-03-array-fold.flt`: fold sums [1..5] = 15 (output `15`)
- `24-04-array-init.flt`: init produces squares, fold-sum = 30 (output `30`)

### Gaps Summary

No gaps. All 4 observable truths are verified by passing E2E tests against the actual compiled binary output.

---

_Verified: 2026-03-27_
_Verifier: Claude (gsd-verifier)_
