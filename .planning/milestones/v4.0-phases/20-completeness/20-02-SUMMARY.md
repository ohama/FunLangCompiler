---
phase: 20-completeness
plan: 02
subsystem: compiler
tags: [fsharp, mlir, pattern-matching, exception-handling, adt, codegen]

# Dependency graph
requires:
  - phase: 17-adt
    provides: ADT layout (slot 0 = tag, slot 1 = payload), resolveAccessorTyped
  - phase: 19-exceptions
    provides: TryWith/Raise elaboration, emitDecisionTree2, exn_fail/exn_caught blocks
  - phase: 20-completeness plan 01
    provides: LlvmIntToPtrOp/LlvmPtrToIntOp, resolveAccessorTyped Root inttoptr case
provides:
  - Nested ADT constructor pattern matching at depth 2+ (ADT-12 fix)
  - Raise inside handler arm propagates to outer handler correctly (EXN-08 fix)
  - resolveAccessorTyped always ensures GEP base is Ptr-typed
  - Dead inner merge block detection for noreturn handler arms
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "resolveAccessorTyped Field branches use resolveAccessorTyped parent Ptr (not resolveAccessor) to guarantee GEP base type correctness"
    - "emitDecisionTree Leaf/Guard: conditional merge branch — only append CfBrOp when body is not already terminated"
    - "TryWith try body CfBrOp patch: check inner merge block predecessors; emit LlvmUnreachableOp for dead blocks"

key-files:
  created:
    - tests/compiler/20-02-nested-adt-pat.flt
    - tests/compiler/20-03-raise-in-handler.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "ADT-12 root cause: resolveAccessorTyped false,_ and true,v Field branches called resolveAccessor parent (may return cached I64) then used it as GEP base (Ptr required). Fix: use resolveAccessorTyped parent Ptr in all Field branches of both resolveAccessorTyped and resolveAccessorTyped2"
  - "EXN-08 root cause: emitDecisionTree/2 Leaf and Guard cases unconditionally append CfBrOp after body ops, violating MLIR block structure when body ends with LlvmUnreachableOp. Fix: check List.tryLast bodyOps before appending"
  - "Dead inner merge block: when all TryWith handler arms are noreturn, inner try's merge block has no predecessors. Patching it with cf.br causes mlir-translate dialect error. Fix: check predecessor existence in env.Blocks before patching; emit LlvmUnreachableOp for dead blocks"
  - "ensureAdtFieldTypes left as I64 preload (not changed to Ptr). The retype mechanism in resolveAccessorTyped correctly handles I64->Ptr conversion when parent is needed as GEP base"

patterns-established:
  - "Pattern: resolveAccessorTyped Field branches always resolve parent as Ptr for GEP correctness"
  - "Pattern: emitDecisionTree terminators are conditional — check List.tryLast before appending merge branch"
  - "Pattern: dead block detection via predecessor scan before patching inner merge blocks"

# Metrics
duration: 30min
completed: 2026-03-27
---

# Phase 20 Plan 02: Nested ADT Pattern + Raise-in-Handler Summary

**resolveAccessorTyped parent Ptr fix for nested ADT patterns (ADT-12) and conditional merge branch for raise-inside-handler (EXN-08), with dead inner merge block detection for all-noreturn handler arms**

## Performance

- **Duration:** ~30 min
- **Started:** 2026-03-27T07:10:00Z
- **Completed:** 2026-03-27T07:40:00Z
- **Tasks:** 2
- **Files modified:** 3 (Elaboration.fs + 2 new test files)

## Accomplishments

- Fixed ADT-12: nested constructor patterns like `Node(Node(_, v, _), root, _)` now correctly extract values at depth 2+
- Fixed EXN-08: `raise` inside a TryWith handler arm no longer appends CfBrOp after LlvmUnreachableOp
- Fixed EXN-08 followup: dead inner merge blocks (all handler arms noreturn) now emit LlvmUnreachableOp instead of cf.br, preventing mlir-translate dialect errors
- All 66 E2E tests pass (64 pre-existing + 2 new)
- EXN-07 regression gate (19-06-try-fallthrough) confirmed passing

## Task Commits

1. **Task 1: Fix ensureAdtFieldTypes pre-load type + emitDecisionTree terminator check** - `d03cbae` (fix)
2. **Task 2: E2E tests for nested ADT patterns and raise-in-handler** - `ae87fa5` (test)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` - Four fixes: resolveAccessorTyped/2 parent Ptr, emitDecisionTree/2 Leaf+Guard conditional terminator, tryBodyBlock dead merge detection
- `tests/compiler/20-02-nested-adt-pat.flt` - E2E test for nested ADT pattern (exit 15)
- `tests/compiler/20-03-raise-in-handler.flt` - E2E test for raise inside handler arm (exit 42)

## Decisions Made

- ADT-12 root cause is in `resolveAccessorTyped` (not `ensureAdtFieldTypes`): the `false, _` and `true, v ->` Field branches called `resolveAccessor parent` which returns cached I64, then used it directly as GEP base. GEP requires Ptr. Fix: `resolveAccessorTyped parent Ptr` in all four Field sub-branches (both resolveAccessorTyped and resolveAccessorTyped2).

- `ensureAdtFieldTypes` was NOT changed to Ptr (plan's original approach). The retype mechanism in resolveAccessorTyped already handles I64->Ptr conversion correctly when the parent is needed as a GEP base. Changing ensureAdtFieldTypes to Ptr would break I64 payload extraction in simple cases like `Some n -> n`.

- EXN-08 required two separate fixes: (1) conditional merge branch in emitDecisionTree/2 Leaf+Guard cases, and (2) dead block detection in the TryWith try body CfBrOp patch. The second issue manifested as mlir-translate "Dialect cf not found" error when ALL handler arms raised.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Different root cause for ADT-12 than plan described**
- **Found during:** Task 1 (implementation)
- **Issue:** Plan stated fix was in `ensureAdtFieldTypes` (I64→Ptr preload). Analysis showed the actual bug is in `resolveAccessorTyped false,_` and `true,v` Field branches using `resolveAccessor parent` (returns cached I64) as GEP base.
- **Fix:** Changed both Field branches of `resolveAccessorTyped` and `resolveAccessorTyped2` to use `resolveAccessorTyped parent Ptr` instead of `resolveAccessor parent`.
- **Files modified:** src/FunLangCompiler.Compiler/Elaboration.fs
- **Verification:** 17-05-unary-match.flt (previously broken by plan's approach) passes; nested ADT test returns 15.
- **Committed in:** d03cbae (Task 1 commit)

**2. [Rule 1 - Bug] Additional EXN-08 fix needed: dead inner merge block**
- **Found during:** Task 1 (testing EXN-08)
- **Issue:** After fixing the Leaf/Guard conditional terminator, mlir-translate still failed with "Dialect cf not found for cf.br" in the lowered MLIR. The TryWith tryBodyBlock CfBrOp patching code appended `cf.br ^inner_merge(bodyVal)` to the inner TryWith's merge block, which was dead (no predecessors) when all handler arms raised.
- **Fix:** Added predecessor check before patching inner merge block. If no predecessor found, emit `LlvmUnreachableOp` instead of `CfBrOp`.
- **Files modified:** src/FunLangCompiler.Compiler/Elaboration.fs
- **Verification:** 20-03-raise-in-handler test compiles and returns 42.
- **Committed in:** d03cbae (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (2 × Rule 1 - Bug)
**Impact on plan:** Both fixes were necessary for correctness. Different implementation than planned but same outcome. No scope creep.

## Issues Encountered

- Plan's proposed fix (ensureAdtFieldTypes I64→Ptr) broke existing 17-05-unary-match.flt test. Root cause analysis via MLIR debug output revealed the actual bug location.
- EXN-08 manifested as two separate issues: the unconditional merge branch (fixed by Leaf/Guard conditional) AND the dead block cf.br (fixed by predecessor check). Required iterative debugging.

## Next Phase Readiness

- Phase 20 completeness work is complete (2/2 plans done)
- All 66 E2E tests pass; 0 regressions
- v4.0 compiler is feature-complete per phase plan scope

---
*Phase: 20-completeness*
*Completed: 2026-03-27*
