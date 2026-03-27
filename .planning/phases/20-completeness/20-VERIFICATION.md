---
phase: 20-completeness
verified: 2026-03-27T07:48:37Z
status: gaps_found
score: 4/5 must-haves verified
gaps:
  - truth: "List.map Some [1; 2; 3] compiles — unary constructor Some is wrapped as a lambda and passed as a first-class function"
    status: failed
    reason: "apply Some 42 fails with 'unsupported App — f is not a known function or closure value'. Constructor closures only work when bound directly via let-binding (let s = Some in s 42), not when passed through a higher-order function parameter. Lambda parameters have type I64 in the uniform closure ABI; passing a constructor closure as an argument causes it to lose its Ptr type, making it unrecognizable as a closure at the indirect call site."
    artifacts:
      - path: "src/LangBackend.Compiler/Elaboration.fs"
        issue: "Constructor(name, None, _) arity>=1 correctly wraps as Lambda closure, but the resulting closure value (Ptr) loses its type when passed through a higher-order function parameter (typed I64). The App elaboration dispatches on Type=Ptr for closures, but the I64-typed parameter prevents dispatch."
    missing:
      - "Type tracking for closure-typed parameters so that a closure passed as function argument remains callable via IndirectCallOp at the callee site"
      - "OR: a special App dispatch path that handles I64-typed function values by attempting indirect call"
      - "E2E test for apply Some 42 or map Some [1;2;3] passing with exit code 1 (first element)"
---

# Phase 20: Completeness Verification Report

**Phase Goal:** First-class constructors, nested ADT pattern matching, exception re-raise, and handler-internal exceptions all work correctly, closing the remaining edge cases
**Verified:** 2026-03-27T07:48:37Z
**Status:** gaps_found
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `List.map Some [1; 2; 3]` compiles — constructor as first-class function argument | FAILED | `apply Some 42` elaboration fails: "unsupported App — 'f' is not a known function or closure value". Only `let s = Some in s 42` works (direct let-binding). |
| 2 | Nested ADT pattern `Node(Node(_, v, _), root, _)` extracts values at depth 2+ | VERIFIED | Test 20-02-nested-adt-pat.flt: input with depth-2 Node pattern exits 15 (= 10 + 5). resolveAccessorTyped parent Ptr fix confirmed in Elaboration.fs at lines 978, 1009, 1021. |
| 3 | Handler miss propagates the exception — lang_throw called from Fail branch | VERIFIED | exn_fail block at line 1901 emits `[LlvmCallVoidOp("@lang_throw", [exnPtrVal]); LlvmUnreachableOp]`. Test 19-06-try-fallthrough.flt passes (EXN-07 regression gate). |
| 4 | Exception raised inside handler arm propagates to outer handler correctly | VERIFIED | Test 20-03-raise-in-handler.flt exits 42. emitDecisionTree/2 Leaf+Guard cases conditionally skip CfBrOp when body ends with LlvmUnreachableOp (lines 1192-1201, 1834-1843). Dead inner merge block detection at line 1604-1608. |
| 5 | All 66 E2E tests pass (REG-01 gate, updated count) | VERIFIED | fslit tests/compiler/ reports 66/66 passed. All phase 17-19 regression tests confirmed passing including 17-05-unary-match.flt, 19-06-try-fallthrough.flt. |

**Score:** 4/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/LangBackend.Compiler/Elaboration.fs` | Arity-aware Constructor(name, None, _) case | VERIFIED | Lines 1361-1369: `if info.Arity >= 1 then elaborateExpr env (Lambda(...))` — confirmed present and substantive |
| `src/LangBackend.Compiler/Elaboration.fs` | resolveAccessorTyped parent Ptr in Field branches | VERIFIED | Lines 978, 1009, 1021, 1657, 1678, 1690 all call `resolveAccessorTyped parent Ptr` |
| `src/LangBackend.Compiler/Elaboration.fs` | Conditional merge branch in emitDecisionTree/2 Leaf+Guard | VERIFIED | Lines 1192-1201 (Match Leaf), 1245-1253 (Match Guard), 1834-1843 (TryWith Leaf), 1880-1888 (TryWith Guard) |
| `src/LangBackend.Compiler/Elaboration.fs` | Dead inner merge block detection | VERIFIED | Lines 1604-1608: hasPredecessors check emits LlvmUnreachableOp for dead blocks |
| `src/LangBackend.Compiler/MlirIR.fs` | LlvmIntToPtrOp and LlvmPtrToIntOp DU cases | VERIFIED | Lines 73-76: both DU cases present with Phase 20 comment |
| `src/LangBackend.Compiler/Printer.fs` | llvm.inttoptr and llvm.ptrtoint emission | VERIFIED | Lines 135-138: both match arms emit correct MLIR text |
| `tests/compiler/20-01-firstclass-ctor.flt` | E2E test for first-class constructor | VERIFIED | File exists, 8 lines, uses `let s = Some in match s 42 with`, expects exit 42. Passes. |
| `tests/compiler/20-02-nested-adt-pat.flt` | E2E test for nested ADT pattern | VERIFIED | File exists, 8 lines, nested Node pattern, expects exit 15. Passes. |
| `tests/compiler/20-03-raise-in-handler.flt` | E2E test for raise inside handler arm | VERIFIED | File exists, 11 lines, nested try-with with raise in handler, expects exit 42. Passes. |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `Constructor(name, None, _) arity>=1` | Lambda elaboration | Re-elaborates as `Lambda(__ctor_N_Name, Constructor(...))` | WIRED | Line 1369: `elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))` |
| `env.TypeEnv` | arity check | `Map.find name env.TypeEnv` then `info.Arity` | WIRED | Line 1362-1363: lookup then branch on Arity |
| `resolveAccessorTyped Field branches` | GEP base as Ptr | `resolveAccessorTyped parent Ptr` in all Field sub-branches | WIRED | 6 call sites confirmed at lines 978, 1009, 1021, 1657, 1678, 1690 |
| `emitDecisionTree Leaf case` | conditional CfBrOp | `List.tryLast bodyOps` check before appending merge branch | WIRED | Lines 1193-1196 and 1835-1838 |
| `emitDecisionTree Fail case` | exn_fail block | `CfBrOp(exnFailLabel, [])` | WIRED | Line 1822 |
| `exn_fail block` | lang_throw | `LlvmCallVoidOp("@lang_throw", [exnPtrVal])` | WIRED | Line 1903 |

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| ADT-11 (first-class constructors) | PARTIAL | Constructor-as-let-bound-value works (`let s = Some in s 42`). Passing constructor as argument to higher-order function does not work (`apply Some 42` fails). |
| ADT-12 (nested ADT patterns) | SATISFIED | resolveAccessorTyped parent Ptr fix enables depth-2+ GEP chains. Test 20-02 confirms. |
| EXN-07 (handler miss re-raise) | SATISFIED | exn_fail block calls lang_throw. Test 19-06 passes. Was already implemented in Phase 19. |
| EXN-08 (raise inside handler body) | SATISFIED | emitDecisionTree/2 conditional merge branch + dead block detection. Test 20-03 confirms. |

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| 20-01-SUMMARY.md (documented limitation) | "Higher-order constructor passing still blocked" | Warning (known, documented) | ADT-11 only partially satisfied — constructor-as-argument fails |

No stub patterns, TODO/FIXME markers, or empty implementations found in the modified source files.

### Gaps Summary

**One gap blocks full goal achievement:**

Success criterion 1 from the ROADMAP (`List.map Some [1; 2; 3]` compiles) is not satisfied. The implementation narrowed the scope to `let s = Some in s 42` — constructor closures work only when bound via a direct `let` binding, not when passed as arguments to higher-order functions.

**Root cause:** The uniform closure ABI uses `(ptr, i64) -> i64` — all function parameters have type I64. When a constructor closure (Ptr) is passed as an argument, the callee sees an I64-typed value. The App elaboration dispatches indirect calls only when the function expression has Type=Ptr. An I64 parameter cannot be recognized as a callable closure.

**Impact on other criteria:** Criteria 2-5 are fully achieved. The gap is isolated to the higher-order constructor-passing scenario described in criterion 1.

**The SUMMARY documents this limitation explicitly** — it was a known architectural constraint, not an oversight. However, the ROADMAP criterion 1 uses `List.map Some` as the success benchmark, and this specific pattern does not compile.

---

_Verified: 2026-03-27T07:48:37Z_
_Verifier: Claude (gsd-verifier)_
