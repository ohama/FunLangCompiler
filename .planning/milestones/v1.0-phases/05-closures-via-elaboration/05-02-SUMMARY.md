---
phase: 05-closures-via-elaboration
plan: "02"
subsystem: compiler-elaboration
tags: [mlir, llvm-dialect, closures, free-variables, elaboration, fsharp, fslit, e2e-tests]

# Dependency graph
requires:
  - phase: 05-closures-via-elaboration plan 01
    provides: Ptr type, 7 LLVM MlirOps, IsLlvmFunc, freeVars, ClosureInfo, Lambda compilation in Let handler, App 3-way dispatch skeleton

provides:
  - Corrected freeVars bound set: captures = freeVars {innerParam} innerBody (not {outerParam, innerParam})
  - Verified 3-way App dispatch: direct call, closure-making call, indirect closure call all work E2E
  - 05-01-closure-basic.flt: add_n closure test (let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3 exits 8)
  - 05-02-closure-no-capture.flt: zero-capture closure test (fun y -> y + 1 exits 5)
  - 13/13 FsLit tests passing (full regression suite)

affects:
  - Phase 6 (any future closure extensions): freeVars pattern established
  - Any phase adding nested lambdas or multi-capture closures

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "captures = freeVars (Set.singleton innerParam) innerBody — only innerParam is bound; outerParam appears free and is captured"
    - "outerParam is both a param of the closure-maker AND a captured variable stored in the env struct at env[1+i]"
    - "Zero-capture closure struct has only fn_ptr (struct size = 1); closure still works correctly via indirect call"

key-files:
  created:
    - tests/compiler/05-01-closure-basic.flt
    - tests/compiler/05-02-closure-no-capture.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "freeVars must use Set.singleton innerParam as bound set (not both params) so outerParam is recognized as a capture"
  - "For add_n: captures = ['n'] because n is free in (x + n) when only x is bound; n is stored at env[1] by the closure-maker"
  - "Zero-capture closure (fun y -> y + 1): captures = [] so struct is {fn_ptr only}; numCaptures=0 produces !llvm.struct<(ptr)>"

patterns-established:
  - "Phase 5 success criteria met: add_n 5 applied to 3 exits 8 (1-capture closure, E2E pipeline verified)"
  - "FsLit test naming convention: 05-01-closure-basic.flt, 05-02-closure-no-capture.flt"

# Metrics
duration: 3min
completed: 2026-03-26
---

# Phase 5 Plan 02: App Dispatch Verification and Closure E2E Tests Summary

**Fixed freeVars bound set bug (captures excluded outerParam), verified 3-way App dispatch E2E, 13/13 FsLit tests green including add_n closure exiting 8**

## Performance

- **Duration:** 3 min
- **Started:** 2026-03-26T03:44:18Z
- **Completed:** 2026-03-26T03:48:16Z
- **Tasks:** 2
- **Files modified:** 3 (Elaboration.fs, 2 new .flt files)

## Accomplishments
- Found and fixed a critical bug in freeVars: the bound set used {outerParam, innerParam} instead of {innerParam}, causing outerParam to be excluded from captures (runtime "unbound variable" error at elaboration time)
- Verified the 3-way App dispatch (direct call, closure-making call, indirect closure call) works E2E after the fix
- Created two FsLit E2E test files covering 1-capture and 0-capture closure cases
- Full 13/13 regression suite green

## Task Commits

Each task was committed atomically:

1. **Task 1: Fix freeVars bound set in Lambda/Let handler** - `0054bca` (fix)
2. **Task 2: FsLit E2E tests for closures** - `b1f6645` (feat)

**Plan metadata:** (docs commit below)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - Fixed freeVars call: `Set.singleton innerParam` instead of `Set.ofList [outerParam; innerParam]`
- `tests/compiler/05-01-closure-basic.flt` - E2E test for add_n closure (exits 8)
- `tests/compiler/05-02-closure-no-capture.flt` - E2E test for zero-capture lambda (exits 5)

## Decisions Made
- The correct captures computation is `freeVars (Set.singleton innerParam) innerBody` — not both params. `outerParam` is itself a "captured" variable (stored in the env struct by the closure-maker at env[1+i]). Using `{outerParam, innerParam}` treats `outerParam` as already-in-scope and excludes it from captures, which is wrong.
- For zero-capture closure (`fun y -> y + 1`): `captures = []`, `numCaptures = 0`, struct is `!llvm.struct<(ptr)>` (fn_ptr only). This is correct and the indirect call still works.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed freeVars bound set causing "unbound variable 'n'" at runtime**

- **Found during:** Task 1 (App dispatch verification — running the add_n closure test)
- **Issue:** `freeVars (Set.ofList [outerParam; innerParam]) innerBody` with `outerParam = "n"` and `innerParam = "x"` computed `freeVars {"n","x"} (x+n) = {}` (empty captures). So `n` was never stored in the closure struct. When the inner llvm.func body tried to elaborate `x + n`, there was no binding for `n` in the inner env → "unbound variable 'n'"
- **Fix:** Changed to `freeVars (Set.singleton innerParam) innerBody` → `freeVars {"x"} (x+n) = {"n"}` → captures = ["n"]. The closure-maker now stores `n` at env[1], and the inner body loads it via GEP+Load.
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3` compiles and exits 8; 11/11 prior tests still pass
- **Committed in:** 0054bca (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - bug in freeVars bound set from Plan 01 implementation)
**Impact on plan:** Critical correctness fix. Without this, no closure that uses outerParam in the inner body would work. No scope creep.

## Issues Encountered
- The 05-01 SUMMARY claimed App dispatch was fully implemented, but the freeVars bug meant any closure using outerParam in innerBody would fail. Task 1 was essentially a debugging+fix task rather than a pure verification task.

## Next Phase Readiness
- Phase 5 is complete: all closure requirements satisfied (ELAB-03, ELAB-04, TEST-02)
- Phase 5 success criteria all met: add_n exits 8, ClosureAllocOp in MlirIR, IndirectCallOp for closure application, 13/13 tests pass
- Phase 6 can build on this: the closure machinery is correct and tested E2E

---
*Phase: 05-closures-via-elaboration*
*Completed: 2026-03-26*
