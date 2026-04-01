---
phase: 36-bug-fixes
plan: 01
subsystem: compiler
tags: [mlir, elaboration, codegen, sequential-if, and-or, while, fsharp]

# Dependency graph
requires:
  - phase: 35-prelude-modules
    provides: freeVars fix that resolved FIX-01 segfault (commit 01c173f)
  - phase: 31-string-io
    provides: Phase 35 I64 coercion pattern in If case
provides:
  - FIX-02: sequential if expressions produce valid MLIR (no empty block error)
  - FIX-03: And/Or/While/If expressions accept I64-typed condition values (module Bool functions)
  - FIX-01: E2E test confirming for-in mutable capture works correctly
affects:
  - 37-native-compile
  - Any phase using And/Or/While with module Bool functions
  - Any phase with sequential if expressions in same block

# Tech tracking
tech-stack:
  added: []
  patterns:
    - blocksAfterBind index-based patching (capture block count BEFORE bodyExpr elaboration)
    - I64→I1 coercion for And/Or/While condition operands
    - If case detects And/Or terminator-producing condExpr and patches into condition's merge block

key-files:
  created:
    - tests/compiler/36-01-forin-mutable-capture.flt
    - tests/compiler/36-02-sequential-if.flt
    - tests/compiler/36-03-bool-and-or-while.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "FIX-02: use blocksAfterBind - 1 index instead of List.last to target outer merge block"
  - "FIX-03: If case also needs terminator detection when condExpr is And/Or (not just Let/LetPat)"
  - "I64→I1 coercion is inline (3-line pattern from If case), no helper function needed"

patterns-established:
  - "blocksAfterBind pattern: always capture env.Blocks.Value.Length BEFORE elaborating continuation to correctly target outer merge block"
  - "Terminator detection in condExpr: If case now checks if condOps ends in terminator and patches CfCondBrOp into merge block"

# Metrics
duration: 13min
completed: 2026-03-30
---

# Phase 36 Plan 01: Bug Fixes Summary

**Three compiler bugs fixed: sequential-if empty-block MLIR error, I64 coercion in And/Or/While/If conditions, and E2E test confirming for-in mutable capture correctness**

## Performance

- **Duration:** 13 min
- **Started:** 2026-03-29T20:40:29Z
- **Completed:** 2026-03-29T20:53:11Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- FIX-02: Two consecutive `if` expressions in same block now compile to valid MLIR without empty block errors
- FIX-03: And/Or/While expressions accept I64-typed conditions (module Bool functions); If case also handles And/Or as condition without double-terminator
- FIX-01: E2E test confirms for-in mutable capture accumulates correctly (prints 6)
- All 9 Phase 35 module tests pass without regression

## Task Commits

Each task was committed atomically:

1. **Task 1: Fix FIX-02 and FIX-03 in Elaboration.fs** - `df729f8` (fix)
2. **Task 2: Write E2E tests for all three fixes** - `fab820d` (test)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - FIX-02 in Let/LetPat cases; FIX-03 in And/Or/WhileExpr/If cases
- `tests/compiler/36-01-forin-mutable-capture.flt` - FIX-01 E2E test (for-in mutable capture)
- `tests/compiler/36-02-sequential-if.flt` - FIX-02 E2E test (sequential if expressions)
- `tests/compiler/36-03-bool-and-or-while.flt` - FIX-03 E2E test (And/Or/While with I1-typed helper functions)

## Decisions Made
- **blocksAfterBind index**: FIX-02 uses `blocksAfterBind - 1` (index of outer merge block) instead of `List.last innerBlocks`. This is the correct approach when bodyExpr adds MORE side blocks (inner if/while/for).
- **If case also needs FIX-03 treatment**: When `And` or `Or` is used as condition to `If`, the returned `condOps` include a `CfCondBrOp` terminator. The `If` case must detect this and patch its own `CfCondBrOp` into the And/Or's merge block, not append inline.
- **No helper function for coercion**: The 3-line I64→I1 coercion pattern is inline (matching existing codebase style).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] If case needs terminator detection for And/Or condExpr**
- **Found during:** Task 1 (FIX-03 implementation)
- **Issue:** Plan specified coercing And/Or operands to I1, but when And/Or is used as condition to `If`, the condOps include a `CfCondBrOp` terminator. The If case then appended another `CfCondBrOp` inline, creating two terminators in the same block (mlir-opt error: "operation with block successors must terminate its parent block").
- **Fix:** Added terminator detection to the `If` case (mirroring FIX-02 Let/LetPat logic): if `condOps` ends with a terminator and blocks were added during condition elaboration, patch the If's `CfCondBrOp` into the condition's merge block (at `blocksAfterCond - 1`).
- **Files modified:** src/FunLangCompiler.Compiler/Elaboration.fs
- **Verification:** `if always_true () && always_true () then ... else ...` compiles and runs correctly
- **Committed in:** df729f8 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 missing critical)
**Impact on plan:** Auto-fix necessary for correctness. The If+And/Or interaction was not in the plan but is essential for FIX-03 to work.

## Issues Encountered
- The initial FIX-03 implementation caused mlir-opt error "operation with block successors must terminate its parent block" because And/Or emit a `CfCondBrOp` inline, and the outer `If` case tried to append another `CfCondBrOp` after it. Resolved by applying the same FIX-02 terminator-detection pattern to the `If` case's condition elaboration.

## Next Phase Readiness
- FIX-01, FIX-02, FIX-03 are all resolved and tested
- Sequential if expressions work correctly — downstream code using this pattern can remove workarounds
- And/Or/While conditions accept I64 and I1 types — `Bool` module functions work in conditions
- Ready for Phase 37 (native compile / FunLexYacc integration)

---
*Phase: 36-bug-fixes*
*Completed: 2026-03-30*
