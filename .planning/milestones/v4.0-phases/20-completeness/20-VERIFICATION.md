---
phase: 20-completeness
verified: 2026-03-27T08:20:01Z
status: passed
score: 5/5 must-haves verified
re_verification:
  previous_status: gaps_found
  previous_score: 4/5
  gaps_closed:
    - "Truth #1: apply Some 42 compiles — I64 closure dispatch via inttoptr + general App case added in Elaboration.fs (commits 5e93de9, 575254e)"
  gaps_remaining: []
  regressions: []
---

# Phase 20: Completeness Verification Report

**Phase Goal:** First-class constructors, nested ADT pattern matching, exception re-raise, and handler-internal exceptions all work correctly, closing the remaining edge cases
**Verified:** 2026-03-27T08:20:01Z
**Status:** passed
**Re-verification:** Yes — after gap closure plan 20-03

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `List.map Some [1; 2; 3]` compiles — constructor as first-class function argument | VERIFIED | `20-04-ho-ctor.flt` (`apply Some 42`) PASS. I64 dispatch arm at lines 863-872 and general App case at lines 881-902 of Elaboration.fs confirmed present. |
| 2 | Nested ADT pattern `Node(Node(_, v, _), root, _)` extracts values at depth 2+ | VERIFIED | `20-02-nested-adt-pat.flt` PASS. resolveAccessorTyped parent Ptr fix at lines 978, 1009, 1021, 1657, 1678, 1690. |
| 3 | Handler miss propagates the exception — lang_throw called from Fail branch | VERIFIED | `19-06-try-fallthrough.flt` PASS. exn_fail block at line 1901 emits `LlvmCallVoidOp("@lang_throw", [exnPtrVal]); LlvmUnreachableOp`. |
| 4 | Exception raised inside handler arm propagates to outer handler correctly | VERIFIED | `20-03-raise-in-handler.flt` PASS. emitDecisionTree/2 conditional merge branch + dead block detection confirmed. |
| 5 | All 67 E2E tests pass (REG-01 gate) | VERIFIED | `fslit tests/compiler/` reports 67/67 passed. |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/LangBackend.Compiler/Elaboration.fs` | I64 closure dispatch arm in App Var(name) branch | VERIFIED | Lines 863-872: `closureVal.Type = I64` arm with `LlvmIntToPtrOp` + `LlvmLoadOp` + `IndirectCallOp`. Commit 5e93de9. |
| `src/LangBackend.Compiler/Elaboration.fs` | General App case for non-Var function expressions | VERIFIED | Lines 881-902: `| _ ->` arm evaluates funcExpr, dispatches by type (Ptr direct, I64 inttoptr). Commit 575254e. |
| `src/LangBackend.Compiler/Elaboration.fs` | Ptr-to-I64 coercion in closure-making DirectCallOp | VERIFIED | Lines 844-850: `if argVal.Type = Ptr then LlvmPtrToIntOp` coercion before DirectCallOp. Commit 575254e. |
| `src/LangBackend.Compiler/Elaboration.fs` | Arity-aware Constructor(name, None, _) case | VERIFIED | Lines 1361-1369: `if info.Arity >= 1 then elaborateExpr env (Lambda(...))`. Present from 20-01. |
| `src/LangBackend.Compiler/Elaboration.fs` | resolveAccessorTyped parent Ptr in Field branches | VERIFIED | Lines 978, 1009, 1021, 1657, 1678, 1690 all call `resolveAccessorTyped parent Ptr`. Present from 20-02. |
| `src/LangBackend.Compiler/Elaboration.fs` | Conditional merge branch in emitDecisionTree/2 Leaf+Guard | VERIFIED | Lines 1192-1201 (Match Leaf), 1245-1253 (Match Guard), 1834-1843 (TryWith Leaf), 1880-1888 (TryWith Guard). Present from 20-02. |
| `src/LangBackend.Compiler/MlirIR.fs` | LlvmIntToPtrOp and LlvmPtrToIntOp DU cases | VERIFIED | Lines 73-76: both DU cases present. Present from 20-01. |
| `src/LangBackend.Compiler/Printer.fs` | llvm.inttoptr and llvm.ptrtoint emission | VERIFIED | Lines 135-138: both match arms emit correct MLIR text. Present from 20-01. |
| `tests/compiler/20-01-firstclass-ctor.flt` | E2E test for first-class constructor via let-binding | VERIFIED | PASS. `let s = Some in match s 42 with`, exits 42. |
| `tests/compiler/20-02-nested-adt-pat.flt` | E2E test for nested ADT pattern | VERIFIED | PASS. Nested Node pattern, exits 15. |
| `tests/compiler/20-03-raise-in-handler.flt` | E2E test for raise inside handler arm | VERIFIED | PASS. Nested try-with with raise, exits 42. |
| `tests/compiler/20-04-ho-ctor.flt` | E2E test for higher-order constructor passing | VERIFIED | PASS. `apply Some 42` exits 42. Commit ca55a62. |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| App `Var(name)`, `closureVal.Type = I64` | `IndirectCallOp` | `LlvmIntToPtrOp` cast before `LlvmLoadOp` + call | WIRED | Lines 863-872: I64 dispatch arm confirmed present |
| General `App _ ->` arm | `IndirectCallOp` | Elaborates funcExpr, branches on `funcVal.Type = I64` | WIRED | Lines 881-902: general case confirmed; handles `App(App(Var "apply", Var "Some"), 42)` outer App |
| Closure-making `DirectCallOp` | Uniform ABI I64 arg | `LlvmPtrToIntOp` when `argVal.Type = Ptr` | WIRED | Lines 844-850: coercion confirmed |
| `Constructor(name, None, _) arity>=1` | Lambda elaboration | Re-elaborates as `Lambda(__ctor_N_Name, Constructor(...))` | WIRED | Line 1369: confirmed from 20-01 |
| `resolveAccessorTyped Field branches` | GEP base as Ptr | `resolveAccessorTyped parent Ptr` at all field sub-branches | WIRED | 6 call sites confirmed from 20-02 |
| `emitDecisionTree Fail case` | `exn_fail block` → `lang_throw` | `CfBrOp(exnFailLabel, [])` → `LlvmCallVoidOp("@lang_throw", ...)` | WIRED | Lines 1822 + 1901 confirmed from 20-01/19 |

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| ADT-11 (first-class constructors) | SATISFIED | Both let-bound (`let s = Some in s 42`) and higher-order passing (`apply Some 42`) verified. I64 dispatch arm + general App case close the remaining path. |
| ADT-12 (nested ADT patterns) | SATISFIED | resolveAccessorTyped parent Ptr fix enables depth-2+ GEP chains. Test 20-02 confirms. |
| EXN-07 (handler miss re-raise) | SATISFIED | exn_fail block calls lang_throw. Test 19-06 passes. |
| EXN-08 (raise inside handler body) | SATISFIED | emitDecisionTree/2 conditional merge branch + dead block detection. Test 20-03 confirms. |

### Anti-Patterns Found

No stub patterns, TODO/FIXME markers, or empty implementations found in the modified source files.

### Human Verification Required

None. All success criteria are mechanically verifiable and confirmed by the 67/67 test suite pass.

### Re-verification Summary

**Gap closed:** Truth #1 (higher-order constructor passing) was the single gap in the initial verification. Plan 20-03 added two changes to Elaboration.fs:

1. **I64 dispatch arm** (lines 863-872, commit `5e93de9`): When `Map.tryFind name env.Vars` returns an I64-typed value, emit `LlvmIntToPtrOp` to cast it to Ptr, then `LlvmLoadOp` + `IndirectCallOp`. This handles constructor closures passed as lambda parameters (uniform ABI stores them as I64).

2. **General App case** (lines 881-902, commit `575254e`): The `| _ ->` arm handles arbitrary function expressions (e.g., `App(App(...), 42)` where the outer App's funcExpr is itself an App). Elaborates funcExpr recursively, dispatches by type. Also added `LlvmPtrToIntOp` coercion in closure-making `DirectCallOp` when arg is Ptr-typed.

3. **E2E test** (`20-04-ho-ctor.flt`, commit `ca55a62`): `apply Some 42` compiles and exits 42.

**No regressions:** All 67 tests pass (66 pre-existing + 1 new). Key regression gates 17-05-unary-match.flt and 19-06-try-fallthrough.flt confirmed passing.

---

_Verified: 2026-03-27T08:20:01Z_
_Verifier: Claude (gsd-verifier)_
_Re-verification after: 20-03 gap closure_
