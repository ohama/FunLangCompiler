---
phase: 28-syntax-desugaring
plan: 01
subsystem: testing
tags: [fsharp, mlir, e2e-tests, desugaring, sequencing, if-then]

# Dependency graph
requires:
  - phase: 07-io
    provides: print/println builtins used in side-effect tests
  - phase: 09-tuples
    provides: Tuple AST node used as unit in if-then-without-else desugaring
provides:
  - E2E tests verifying SEQ (e1; e2) desugaring via LetPat(WildcardPat)
  - E2E tests verifying ITE (if-then without else) desugaring via If(cond, then, Tuple([]))
  - Bug fix: Tuple([]) returns I64 0 instead of GC_malloc ptr (unit type correctness)
  - Bug fix: LetPat(WildcardPat) handles block terminator ops from nested If expressions
affects: [29-loops, any future phase using if-then-without-else or unit values]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Unit value as I64 0: Tuple([]) elaborates to arith.constant 0 : i64, not GC_malloc(0)"
    - "LetPat WildcardPat terminator handling: mirrors Let node smart merge-block injection"

key-files:
  created:
    - tests/compiler/28-01-seq-basic.flt
    - tests/compiler/28-02-seq-chain.flt
    - tests/compiler/28-03-seq-side-effect.flt
    - tests/compiler/28-04-ite-basic.flt
    - tests/compiler/28-05-ite-unit.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "Tuple([]) = unit as I64 0: avoids MLIR type mismatch between then-branch (I64 from print) and implicit else (was Ptr from GC_malloc)"
  - "ITE tests use let _ = if cond then sideEffect in result syntax (not bare if-then on separate line), required by module-level parser"

patterns-established:
  - "Unit-returning builtins (print, println, array_set, etc.) all return I64 0 — Tuple([]) now consistent"
  - "LetPat(WildcardPat, bindExpr, bodyExpr): same smart terminator handling as Let node for if/match in bind position"

# Metrics
duration: 29min
completed: 2026-03-28
---

# Phase 28 Plan 01: SEQ and ITE Verification Tests Summary

**5 E2E tests proving expression sequencing (;) and if-then-without-else work via LangThree parser desugaring, with two Elaboration.fs bug fixes enabling ITE to compile correctly**

## Performance

- **Duration:** 29 min
- **Started:** 2026-03-28T00:45:58Z
- **Completed:** 2026-03-28T01:15:24Z
- **Tasks:** 2
- **Files modified:** 6 (5 new tests + Elaboration.fs)

## Accomplishments
- 3 SEQ tests verify `e1; e2` sequencing via `LetPat(WildcardPat, e1, e2)` desugaring
- 2 ITE tests verify `if cond then expr` via `If(cond, expr, Tuple([]))` desugaring
- Fixed `Tuple([])` to return `I64 0` (unit) instead of `GC_malloc(0)` (Ptr) — eliminates MLIR type mismatch in if-then-without-else
- Fixed `LetPat(WildcardPat, ...)` to handle block terminator ops from nested If expressions, enabling `e1; e2` where e1 is an if-then expression
- All 128 tests pass

## Task Commits

Each task was committed atomically:

1. **Task 1: Add SEQ E2E tests** - `8ec2131` (test)
2. **Task 2: Add ITE E2E tests** - `21812b7` (test)

Note: Elaboration.fs fixes were committed as part of the concurrent 28-02 plan execution (`90835ef`).

**Plan metadata:** committed with docs commit (see below)

## Files Created/Modified
- `tests/compiler/28-01-seq-basic.flt` - SEQ-01: basic e1; e2 returns e2's value
- `tests/compiler/28-02-seq-chain.flt` - SEQ-02: e1; e2; e3 chains correctly, returns last
- `tests/compiler/28-03-seq-side-effect.flt` - SEQ side effects: print calls execute in order
- `tests/compiler/28-04-ite-basic.flt` - ITE-01: if true then sideEffect executes then-branch
- `tests/compiler/28-05-ite-unit.flt` - ITE-02: if false then expr skips branch, returns unit (42)
- `src/LangBackend.Compiler/Elaboration.fs` - Two bug fixes (Tuple[] unit + LetPat WildcardPat terminator)

## Decisions Made
- ITE tests use `let _ = if cond then sideEffect in result` form (not bare `if true then ...;\n0`) because multi-line bare expressions without `let` don't parse through the module parser
- `Tuple([])` as unit = `I64 0`: consistent with all existing unit-returning builtins (print, array_set, etc.) — no GC allocation needed for empty tuple

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed Tuple([]) type returning Ptr instead of I64**
- **Found during:** Task 2 (ITE tests)
- **Issue:** `Tuple([])` called `GC_malloc(0)` returning `Ptr`, but `print` returns `I64`. When used as implicit else in if-then-without-else, MLIR merge block got type mismatch (`!llvm.ptr` vs `i64`)
- **Fix:** Added `if n = 0` guard in Tuple elaboration returning `ArithConstantOp(0L)` (I64) instead of allocating
- **Files modified:** `src/LangBackend.Compiler/Elaboration.fs`
- **Verification:** `let _ = if true then print "yes" in 0` compiles and runs correctly
- **Committed in:** `90835ef` (part of concurrent 28-02 execution)

**2. [Rule 3 - Blocking] Fixed LetPat(WildcardPat) missing block terminator handling**
- **Found during:** Task 2 (ITE tests)
- **Issue:** `e1; e2` desugars to `LetPat(WildcardPat, e1, e2)`. When `e1` is an if-then expression, its elaboration emits `CfCondBrOp` (a block terminator). The plain `bops @ rops` concat placed `rops` after a terminator, violating MLIR block structure
- **Fix:** Added same `blocksBeforeBind`/`isTerminator`/merge-block-injection logic that `Let` already uses
- **Files modified:** `src/LangBackend.Compiler/Elaboration.fs`
- **Verification:** All 128 tests pass including new ITE tests
- **Committed in:** `90835ef` (part of concurrent 28-02 execution)

---

**Total deviations:** 2 auto-fixed (2 blocking)
**Impact on plan:** Both fixes essential for correctness. Plan claimed "no code changes needed" — the fixes were necessary. No scope creep — both fixes narrowly target the ITE compilation path.

## Issues Encountered
- Plan stated "no source code changes needed" but ITE actually required two Elaboration.fs fixes to compile correctly
- Multi-line bare expressions (without top-level `let`) don't work through the module parser — ITE tests restructured to use `let _ = if ... in result` form

## Next Phase Readiness
- SEQ and ITE verified working via parser desugaring + backend elaboration
- `Tuple([])` unit fix benefits all future code paths using unit as a return value
- `LetPat(WildcardPat)` terminator fix means any future `e1; e2` where e1 is an if/match expression will work correctly
- Ready for Phase 28 plan 02 (IDX desugaring) and Phase 29 (LOOP)

---
*Phase: 28-syntax-desugaring*
*Completed: 2026-03-28*
