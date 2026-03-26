---
phase: 03-booleans-comparisons-control-flow
plan: "02"
subsystem: compiler
tags: [mlir, elaboration, control-flow, if-else, short-circuit, cf.cond_br, fsharp]

# Dependency graph
requires:
  - phase: 03-01
    provides: CfCondBrOp/CfBrOp in MlirIR, Printer support for multi-block MLIR, bool/comparison elaboration

provides:
  - Multi-block elaboration with ElabEnv.Blocks accumulator and freshLabel helper
  - If elaboration emitting then/else/merge blocks via cf.cond_br
  - Short-circuit && elaboration (false left skips right-side evaluation)
  - Short-circuit || elaboration (true left skips right-side evaluation)
  - elaborateModule assembles single or multi-block regions correctly
  - Three FsLit E2E tests covering if-else, short-circuit AND, short-circuit OR

affects:
  - 04-functions
  - 05-closures-lambdas
  - Any phase that adds new Expr constructors to Elaboration.fs

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Multi-block elaboration: entry block ends with CfCondBrOp terminator; side blocks appended to env.Blocks ref; merge block holds result as block argument"
    - "freshLabel generates unique block labels (e.g., then0, else1, merge2) via int ref counter in ElabEnv"
    - "elaborateModule patches last side block with ReturnOp so result flows out of merge block"

key-files:
  created:
    - tests/compiler/03-03-if-else.flt
    - tests/compiler/03-04-short-circuit-and.flt
    - tests/compiler/03-05-short-circuit-or.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "env.Blocks ref accumulates side blocks in emission order; elaborateModule appends them after the entry block and patches ReturnOp onto the final merge block"
  - "mergeArg block argument carries the result value — no phi node needed, MLIR block args serve this role"
  - "Short-circuit And: CfCondBrOp(leftVal, evalRightLabel, [], mergeLabel, [leftVal]) — false takes merge branch directly with leftVal"
  - "Short-circuit Or: CfCondBrOp(leftVal, mergeLabel, [leftVal], evalRightLabel, []) — true takes merge branch directly with leftVal"

patterns-established:
  - "Multi-block elaboration pattern: elaborate sub-expressions into env, append blocks to env.Blocks, return (mergeArg, entryOps)"

# Metrics
duration: 2min
completed: 2026-03-26
---

# Phase 3 Plan 02: Multi-block Elaboration for If/And/Or Summary

**Multi-block elaboration with ElabEnv.Blocks accumulator enabling if-else and short-circuit &&/|| lowering to cf.cond_br + block arguments in MLIR**

## Performance

- **Duration:** 2 min
- **Started:** 2026-03-26T02:44:38Z
- **Completed:** 2026-03-26T02:46:47Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments

- Extended ElabEnv with LabelCounter and Blocks fields; added freshLabel helper
- If elaboration emits then/else/merge blocks; entry block ends with cf.cond_br
- Short-circuit && and || elaboration with correct branch semantics
- elaborateModule patched to support both single-block and multi-block regions
- Three new FsLit E2E tests pass; all 9 compiler tests pass with zero regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: Multi-block elaboration for If, And, Or** - `e77a4e5` (feat)
2. **Task 2: FsLit E2E tests for if-else, short-circuit && and ||** - `951c19b` (test)

## Files Created/Modified

- `src/LangBackend.Compiler/Elaboration.fs` - Added LabelCounter/Blocks to ElabEnv, freshLabel, If/And/Or cases, multi-block elaborateModule
- `tests/compiler/03-03-if-else.flt` - E2E: if 5 <= 0 then 0 else 1 exits 1
- `tests/compiler/03-04-short-circuit-and.flt` - E2E: true && false exits 0
- `tests/compiler/03-05-short-circuit-or.flt` - E2E: false || true exits 1

## Decisions Made

- env.Blocks ref accumulates side blocks in emission order; elaborateModule appends them after the entry block and patches ReturnOp onto the final merge block. This keeps elaborateExpr pure with respect to MlirIR structure while allowing multi-block emission.
- mergeArg block argument carries the result value — MLIR block args serve as phi nodes.
- Short-circuit semantics encoded directly in CfCondBrOp branch target selection: And passes leftVal to mergeLabel on false; Or passes leftVal to mergeLabel on true.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## Next Phase Readiness

- Phase 3 complete: bool literals, comparisons, if-else, short-circuit && and || all compile to native binaries via MLIR
- All 9 FsLit tests pass
- Phase 4 (functions) can extend Elaboration.fs with FuncOp/DirectCallOp cases; the multi-block pattern is established and reusable

---
*Phase: 03-booleans-comparisons-control-flow*
*Completed: 2026-03-26*
