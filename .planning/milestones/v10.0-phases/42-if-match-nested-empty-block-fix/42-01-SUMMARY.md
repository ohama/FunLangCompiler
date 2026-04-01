---
phase: 42-if-match-nested-empty-block-fix
plan: 01
subsystem: compiler
tags: [mlir, elaboration, if-expression, match-expression, block-patching, cfg]

# Dependency graph
requires:
  - phase: 36-bug-fixes
    provides: FIX-02 blocksAfterBind pattern for patching CfBrOp into side blocks
  - phase: 41-prelude-sync-compiler-changes
    provides: OpenDecl + 200 passing E2E tests as regression baseline
provides:
  - Fixed If handler in Elaboration.fs that patches CfBrOp into match merge blocks
  - E2E tests covering if-then-else-match, if-then-match-else, and let rec take patterns
affects:
  - 42-02 (if any further nested block fix needed)
  - Prelude List module (take, drop, filter use if/match nesting)
  - Any future code using if...then...else match or if...then match...else

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "blocksBeforeThen/blocksAfterThen tracking around branch elaboration to detect side blocks"
    - "isBranchTerminator helper detects CfBrOp/CfCondBrOp/LlvmUnreachableOp terminators"
    - "Patch CfBrOp into env.Blocks[blocksAfterX - 1] (match merge block) when side blocks exist"

key-files:
  created:
    - tests/compiler/42-01-if-match-nested.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Phase 42: same FIX-02 pattern (blocksAfterX - 1 index) applied to both then AND else branches of If handler"
  - "Phase 42: isBranchTerminator defined locally before block construction (separate from existing isTerminator for condOps)"
  - "Phase 42: patchedTarget prepends coerce ops + CfBrOp BEFORE existing block body (match merge block starts empty)"

patterns-established:
  - "If handler: for any branch expr that ends with terminator + creates side blocks, patch the continuation into the last side block"

# Metrics
duration: 14min
completed: 2026-03-30
---

# Phase 42 Plan 01: If-Match Nested Empty Block Fix Summary

**Terminator-aware CfBrOp patching in If handler's then/else branches — fixes `if...then...else match...` and `if...then match...else...` by routing continuation into match merge block instead of appending unreachably inline**

## Performance

- **Duration:** 14 min
- **Started:** 2026-03-30T05:13:17Z
- **Completed:** 2026-03-30T05:27:30Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Fixed root cause of empty match_merge block bug: CfBrOp to if_merge now patched into match's merge block (not appended after terminator inline)
- All three success criteria patterns compile and run correctly (SC-1: if-else-match, SC-2: if-then-match-else, SC-3: let rec take)
- 186/201 runnable E2E tests pass (185 pre-existing + 1 new); 9 tests need stdin/args/stderr (pre-existing), 6 skip (special commands)

## Task Commits

Each task was committed atomically:

1. **Task 1: Fix If handler to detect terminator in then/else branch ops** - `7e3378a` (feat)
2. **Task 2: Add E2E tests for if-match nested patterns** - `d2e201b` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - Added blocksBeforeThen/AfterThen/BeforeElse/AfterElse tracking and isBranchTerminator helper; replaced unconditional block construction with terminator-aware patching for both then and else branches
- `tests/compiler/42-01-if-match-nested.flt` - E2E tests for SC-1 (if-else-match), SC-2 (if-then-match-else), SC-3 (let rec take with if/match nesting)

## Decisions Made
- Followed FIX-02 pattern exactly: `blocksAfterX - 1` index targets the last side block created by the branch expression (the match's merge block)
- Defined `isBranchTerminator` as a separate local helper (not reusing the existing `isTerminator` for condOps) to keep scoping clear
- Patched target block body: `coerceOps @ [CfBrOp(mergeLabel, [finalVal])] @ targetBlock.Body` — prepend before existing body since match merge block starts empty
- Used `println (to_string x)` pattern for test output (consistent with existing tests using `to_string`)

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The fix applied cleanly following the FIX-02 blueprint from Phase 36.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- List.take, List.drop, List.filter patterns in Prelude can now compile
- Phase 41-02 (operator sanitization, Prelude LangThree sync) unblocked for if/match patterns

---
*Phase: 42-if-match-nested-empty-block-fix*
*Completed: 2026-03-30*
