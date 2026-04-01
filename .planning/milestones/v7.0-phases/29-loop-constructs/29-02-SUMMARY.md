---
phase: 29-loop-constructs
plan: 02
subsystem: compiler
tags: [mlir, cfg, for-loop, elaboration, freeVars, block-argument, cf.br, cf.cond_br, arith.cmpi]

# Dependency graph
requires:
  - phase: 29-loop-constructs
    provides: WhileExpr 3-block header CFG pattern and nested-loop back-edge patching
  - phase: 21-mutable-variables
    provides: LetMut/Assign/MutableVars elaboration for mutable vars in loop bodies
provides:
  - ForExpr elaboration via 3-block CFG with block arg for loop counter (%i : i64)
  - ForExpr freeVars case binding loop var in body scope (immutable)
  - 6 E2E compiler tests for for loops (to/downto/empty-range/immutability/nested)
affects: [any phase using for loops in test programs]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ForExpr uses ^for_header(%i : i64) block arg — SSA-correct loop counter without phi nodes"
    - "startVal/stopVal defined in entry fragment — both dominate for_header (SSA-valid)"
    - "oneConst/nextVal defined per body block — each iteration gets fresh SSA names"
    - "unitConst defined in entry fragment (not side blocks) for MLIR domination"
    - "Nested for loop back-edge: same len-4 patching as WhileExpr"

key-files:
  created:
    - tests/compiler/29-05-for-to-basic.flt
    - tests/compiler/29-06-for-to-sum.flt
    - tests/compiler/29-07-for-downto.flt
    - tests/compiler/29-08-for-empty-range.flt
    - tests/compiler/29-09-for-immutable-var.flt
    - tests/compiler/29-10-for-nested.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Block-argument pattern for loop counter: ^for_header(%i : i64) carries counter SSA-correctly without duplication"
  - "stopVal from entry fragment: elaborated once in entry (dominates header), reused in header's cmpi"
  - "oneConst/nextVal per body block: fresh SSA names each iteration, no dominance issues"
  - "for-variable bound in env.Vars only (NOT MutableVars): enforces LOOP-04 immutability at elaboration level"
  - "Test range adjusted for exit-code constraint: sum 1..100=5050 truncated to byte 186; changed to 1..10=55"

patterns-established:
  - "For loop block-arg pattern: entry branches to ^for_header(%start), body branches to ^for_header(%next)"
  - "Ascending predicate sle, descending predicate sge — empty range (10 to 5) never executes body"

# Metrics
duration: 8min
completed: 2026-03-28
---

# Phase 29 Plan 02: ForExpr Loop Constructs Summary

**ForExpr compiled via block-argument CFG (^for_header(%i : i64)/body/exit) with sle/sge comparison for ascending/descending ranges; 138/138 tests pass**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-03-28T02:08:03Z
- **Completed:** 2026-03-28T02:16:00Z
- **Tasks:** 2
- **Files modified:** 7

## Accomplishments
- ForExpr freeVars case added (var bound inside body, not free)
- ForExpr elaborateExpr case using block-argument CFG: entry(%start) → for_header(%i) → for_body → for_header(%next) | for_exit
- Ascending (`to`): cmpi sle, addi %next; Descending (`downto`): cmpi sge, subi %next
- Empty range (start > stop for `to`) correctly skips body — LOOP-03 satisfied
- Nested for loop support via same len-4 back-edge patching as WhileExpr
- 6 E2E tests: basic to (15), sum to (55), downto last (1), empty range (42), immutable var (55), nested 3x4 (12)
- Zero regressions: all 138 tests pass (132 + 4 while + 2 additional for)

## Task Commits

1. **Task 1: Add ForExpr elaboration case and freeVars case** - `b7b2459` (feat)
2. **Task 2: Add 6 ForExpr E2E test fixtures + full regression pass** - `fcd72fc` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - ForExpr freeVars case + ForExpr elaborateExpr case (3-block CFG)
- `tests/compiler/29-05-for-to-basic.flt` - Ascending for 1..5 sum = 15
- `tests/compiler/29-06-for-to-sum.flt` - Ascending for 1..10 sum = 55
- `tests/compiler/29-07-for-downto.flt` - Descending for 10..1, last = 1
- `tests/compiler/29-08-for-empty-range.flt` - Empty range (10 to 5), x stays 42
- `tests/compiler/29-09-for-immutable-var.flt` - Loop var used in i*i expression, sum = 55
- `tests/compiler/29-10-for-nested.flt` - Nested for loops 3x4 = 12

## Decisions Made
- **Block-argument pattern for loop counter**: `^for_header(%i : i64)` carries the counter SSA-correctly. `startVal` enters via the initial `CfBrOp(headerLabel, [startVal])` from entry; `nextVal` re-enters via `CfBrOp(headerLabel, [nextVal])` from body. No phi nodes needed.
- **stopVal in entry fragment**: elaborated once in the entry fragment, which dominates `^for_header` via the initial branch. No re-elaboration needed (unlike WhileExpr condition which needs re-elaboration for mutable vars).
- **oneConst/nextVal per body block**: These values are defined inside `^for_body`, so each logical iteration uses the same SSA names (but they're block-scoped). No SSA violations.
- **for-variable in env.Vars only (NOT MutableVars)**: Enforces immutability at the elaboration level — an `Assign` to the loop var would produce an error during elaboration (LOOP-04).
- **Test range adjustment**: Plan specified sum 1..100 = 5050, but exit codes are 0-255 (5050 % 256 = 186). Changed test to sum 1..10 = 55 which fits in a byte.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Test expected output adjusted for exit-code byte constraint**
- **Found during:** Task 2 (running 29-06-for-to-sum.flt)
- **Issue:** Plan specified sum 1..100 = 5050 as expected output, but `echo $?` captures exit code (0-255). 5050 % 256 = 186, so the test got `186` instead of `5050`.
- **Fix:** Changed the test range from `1 to 100` to `1 to 10` (sum = 55, fits in byte). This still exercises the for-loop accumulation pattern, just with a smaller range.
- **Files modified:** tests/compiler/29-06-for-to-sum.flt
- **Verification:** Test passes with output 55
- **Committed in:** fcd72fc (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - test expected output correction)
**Impact on plan:** Minor test value adjustment. Functionality unchanged. No scope creep.

## Issues Encountered
- Plan test syntax used `ref`/`!`/`:=` style (OCaml ref cells), but the compiler uses `let mut`/`<-` mutable variables. Used the correct syntax based on 29-01 SUMMARY guidance.

## Next Phase Readiness
- ForExpr fully functional for ascending and descending ranges with 138/138 tests passing
- Phase 29 (Loop Constructs) is now complete — both WhileExpr and ForExpr implemented
- No known blockers for future phases

---
*Phase: 29-loop-constructs*
*Completed: 2026-03-28*
