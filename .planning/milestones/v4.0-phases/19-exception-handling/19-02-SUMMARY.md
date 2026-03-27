---
phase: 19-exception-handling
plan: 02
subsystem: compiler
tags: [fsharp, mlir, llvm, exception-handling, raise, ssa, elaboration]

# Dependency graph
requires:
  - phase: 19-01
    provides: C runtime lang_throw/lang_try_enter/lang_try_exit, ExceptionDecl prePassDecls, freeVars Raise/TryWith

provides:
  - Raise case in elaborateExpr: constructs exception ADT, calls @lang_throw, terminates with llvm.unreachable
  - appendReturnIfNeeded helper: prevents dead ReturnOp after llvm.unreachable in entry block
  - E2E tests 19-01 and 19-02 verifying raise compiles and unhandled exceptions abort correctly

affects:
  - 19-03 (TryWith codegen, if planned)
  - any future phase using Raise expression elaboration

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Raise elaboration: exnOps @ [ArithConstantOp(deadVal); LlvmCallVoidOp(@lang_throw); LlvmUnreachableOp]"
    - "appendReturnIfNeeded: suppress ReturnOp when last op is LlvmUnreachableOp (both elaborateModule and elaborateProgram)"
    - "deadVal defined by ArithConstantOp(0) before call+unreachable to satisfy MLIR SSA validity"

key-files:
  created:
    - tests/compiler/19-01-raise-unhandled.flt
    - tests/compiler/19-02-raise-payload.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "Raise deadVal defined via ArithConstantOp(0L) before llvm.unreachable — MLIR SSA requires all referenced names be defined even if unreachable"
  - "appendReturnIfNeeded helper gates ReturnOp on last-op-not-LlvmUnreachableOp in both elaborateModule and elaborateProgram"
  - "Raise uses existing Constructor elaboration for exnExpr — no special constructor path needed since prePassDecls registers exception ctors in TypeEnv"

patterns-established:
  - "Noreturn expressions: always define a deadVal via ArithConstantOp before the noreturn call + unreachable"
  - "Block termination: use appendReturnIfNeeded instead of unconditional @ [ReturnOp] to handle noreturn paths"

# Metrics
duration: 7min
completed: 2026-03-27
---

# Phase 19 Plan 02: Raise Elaboration Summary

**`raise (Failure "boom")` compiles to ADT construction + `@lang_throw(ptr)` + `llvm.unreachable`, with unhandled exceptions aborting via "Fatal: unhandled exception" exit 1 — 59/59 E2E tests pass**

## Performance

- **Duration:** 7 min
- **Started:** 2026-03-27T05:17:00Z
- **Completed:** 2026-03-27T05:24:43Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- Raise case added to elaborateExpr: elaborates exception constructor via existing Constructor case, emits @lang_throw call + llvm.unreachable
- Fixed SSA validity: deadVal defined by ArithConstantOp(0L) before noreturn sequence
- Fixed block termination: appendReturnIfNeeded prevents dead ReturnOp after llvm.unreachable in both elaborateModule and elaborateProgram
- Two new E2E tests (19-01-raise-unhandled, 19-02-raise-payload) both pass
- All 59 tests pass (57 existing + 2 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: Raise elaboration case** - `fac4df7` (feat)
2. **Task 2: E2E tests + unreachable-before-return fix** - `add2f72` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - Raise case in elaborateExpr, appendReturnIfNeeded helper, both elaborateModule and elaborateProgram updated
- `tests/compiler/19-01-raise-unhandled.flt` - E2E: Failure "boom" aborts with "Fatal: unhandled exception", exit 1
- `tests/compiler/19-02-raise-payload.flt` - E2E: user-defined ParseError exception also aborts correctly

## Decisions Made
- deadVal must be defined by ArithConstantOp(0L) before the noreturn call + unreachable sequence, because MLIR SSA validation checks all referenced names are defined even if unreachable makes them dead.
- appendReturnIfNeeded: check `List.tryLast ops = Some LlvmUnreachableOp` to suppress ReturnOp. Applied in both elaborateModule and elaborateProgram to keep them consistent.
- Raise reuses existing Constructor elaboration for the exception expression — no special path needed because prePassDecls (from 19-01) registers exception constructors in TypeEnv with correct tag and arity.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added ArithConstantOp(deadVal, 0L) before noreturn sequence**
- **Found during:** Task 1 (Raise elaboration case) — discovered during Task 2 testing
- **Issue:** Plan specified `let deadVal = freshName; (deadVal, ops @ [call; unreachable])` without defining deadVal via an instruction. MLIR SSA validation rejects undefined SSA names even after llvm.unreachable.
- **Fix:** Added `ArithConstantOp(deadVal, 0L)` before `LlvmCallVoidOp` in the op list.
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** mlir-opt accepted the generated IR.
- **Committed in:** add2f72

**2. [Rule 3 - Blocking] Added appendReturnIfNeeded to suppress ReturnOp after llvm.unreachable**
- **Found during:** Task 2 (E2E test execution)
- **Issue:** elaborateModule and elaborateProgram unconditionally appended `ReturnOp [resultVal]` after entryOps. When Raise is the last expression, this generates `llvm.unreachable` followed by `return`, which mlir-opt rejects with "must be the last operation in the parent block".
- **Fix:** Added `appendReturnIfNeeded` private helper that checks if the last op is LlvmUnreachableOp and skips ReturnOp if so. Applied to both elaborateModule and elaborateProgram entry-block and last-side-block paths.
- **Files modified:** src/LangBackend.Compiler/Elaboration.fs
- **Verification:** 59/59 E2E tests pass.
- **Committed in:** add2f72

---

**Total deviations:** 2 auto-fixed (1 missing critical SSA def, 1 blocking MLIR terminator order)
**Impact on plan:** Both auto-fixes required for correctness. No scope creep.

## Issues Encountered
- Initial Raise implementation produced undefined SSA name `%tN` — fixed by adding ArithConstantOp.
- MLIR block terminator constraint: `llvm.unreachable` must be last op — fixed by appendReturnIfNeeded guard.

## Next Phase Readiness
- Raise (throw half) is complete and tested.
- TryWith (catch half, EXN-03/EXN-04) can now be implemented using lang_try_enter/lang_try_exit runtime from 19-01 and the Raise infrastructure from 19-02.
- Phase 19 plan 02 of 2 complete — phase 19 is now complete for the throw path.

---
*Phase: 19-exception-handling*
*Completed: 2026-03-27*
