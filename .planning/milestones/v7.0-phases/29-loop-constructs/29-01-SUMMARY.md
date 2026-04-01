---
phase: 29-loop-constructs
plan: 01
subsystem: compiler
tags: [mlir, cfg, while-loop, elaboration, freeVars, cf.br, cf.cond_br]

# Dependency graph
requires:
  - phase: 28-syntax-desugaring
    provides: SEQ/ITE desugaring patterns and LetPat(WildcardPat) terminator handling
  - phase: 21-mutable-variables
    provides: LetMut/Assign/MutableVars elaboration (mutable var ref cells)
provides:
  - WhileExpr elaboration via 3-block header CFG (while_header/while_body/while_exit)
  - WhileExpr freeVars case for correct closure capture
  - 4 E2E compiler tests for while loops (basic, accumulation, nested, no-exec)
affects: [30-for-loops (if implemented), any phase using loops in test programs]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "WhileExpr uses header-block CFG: entry→header→body (back-edge to header) / exit"
    - "unitConst defined in entry fragment (not side blocks) for MLIR domination"
    - "Nested body terminator detection: back-edge patched into inner merge block"

key-files:
  created:
    - tests/compiler/29-01-while-basic.flt
    - tests/compiler/29-02-while-mutable.flt
    - tests/compiler/29-03-while-nested.flt
    - tests/compiler/29-04-while-no-exec.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Header-block CFG pattern (3 side blocks) chosen over dual-elaboration pattern for clarity"
  - "unitConst defined in entry fragment (before CfBrOp to header) to satisfy MLIR domination"
  - "condExpr re-elaborated in body block (duplicate elaboration) for mutable-safe back-edge"
  - "Nested while back-edge: patched into inner merge block at index (len-4) when bodyOps has terminator"
  - "Test format uses module-level let mut declarations (not let...in single-line expressions)"

patterns-established:
  - "While loop body terminator check: if bodyOps ends with CfBrOp/CfCondBrOp, back-edge goes into inner last block"
  - "While loop tests return mutable variable value as exit code (no print needed)"

# Metrics
duration: 10min
completed: 2026-03-28
---

# Phase 29 Plan 01: WhileExpr Loop Constructs Summary

**WhileExpr compiled via 3-block header CFG (while_header/body/exit) with mutable-safe condition re-elaboration and nested-loop back-edge patching; 132/132 tests pass**

## Performance

- **Duration:** 10 min
- **Started:** 2026-03-28T01:54:15Z
- **Completed:** 2026-03-28T02:04:18Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- WhileExpr freeVars case added (prevents closure capture bugs for loops)
- WhileExpr elaborateExpr case using header-block CFG: entry→header→body→header (back-edge)→exit
- Nested while loop support: back-edge ops patched into inner merge block when body has side blocks
- 4 E2E tests: basic counter (5), sum accumulation (55), nested 3×4 (12), no-exec false condition (42)
- Zero regressions: all 132 tests pass

## Task Commits

1. **Task 1: Add WhileExpr elaboration case and freeVars case** - `34b757a` (feat)
2. **Task 2: Add WhileExpr E2E test fixtures + nested while fix** - `0eb2b3d` (feat)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - WhileExpr freeVars case + WhileExpr elaborateExpr case
- `tests/compiler/29-01-while-basic.flt` - Basic while counter 0→5
- `tests/compiler/29-02-while-mutable.flt` - Sum accumulation 1+...+10=55
- `tests/compiler/29-03-while-nested.flt` - Nested loops 3×4=12
- `tests/compiler/29-04-while-no-exec.flt` - False condition, body never executes

## Decisions Made
- **Header-block pattern over dual-elaboration**: The 3-block header CFG (entry → while_header → while_body → back-edge to header → while_exit) was chosen. This avoids double-elaborating the condition in the entry fragment, but requires double-elaboration in the body block for mutable-safe back-edge evaluation.
- **unitConst in entry fragment**: The unit constant (0L) passed as the `while_exit` block arg must be defined in the entry fragment (before `CfBrOp(headerLabel)`) so it dominates all loop blocks per MLIR SSA rules. Defining it in the body block would cause domination violations.
- **Nested while back-edge patching**: When the body elaboration itself produces side blocks (e.g., nested while), `bodyOps` ends with a terminator. The back-edge (`condOps2 + CfCondBrOp`) must be appended to the last inner side block (at index `len-4` after pushing the 3 while blocks), not inline in bodyOps.
- **Module-level declaration test format**: Tests use top-level `let mut` / `let _ = while ... do` / `let _ = x` declarations rather than single-line let...in expressions, matching Phase 21/28 test patterns.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] SSA violation: unitConst redefined in body block**
- **Found during:** Task 2 (running first test)
- **Issue:** Initial implementation placed `ArithConstantOp(unitConst, 0L)` in both the entry fragment AND the body block body. MLIR SSA requires each value name to be defined exactly once.
- **Fix:** Removed the redundant `ArithConstantOp(unitConst, 0L)` from the body block body. The entry fragment's definition dominates the body block, so it's accessible there without redefinition.
- **Files modified:** src/FunLangCompiler.Compiler/Elaboration.fs
- **Verification:** Test 29-01 passes (exit=5 correct)
- **Committed in:** `0eb2b3d`

**2. [Rule 1 - Bug] Nested while: back-edge ops appended after terminator in body block**
- **Found during:** Task 2 (running nested while test 29-03)
- **Issue:** When the while body itself contains a nested while, `bodyOps` ends with `CfBrOp(innerHeader)` (a terminator). Appending `condOps2 @ [CfCondBrOp(...)]` after this terminator produces invalid MLIR ("block with no terminator" or "ops after terminator").
- **Fix:** Added terminator detection in WhileExpr case. When `bodyOps` ends with a terminator and new side blocks were added, the back-edge ops are patched into the inner last block (at `env.Blocks.Value.Length - 4` after pushing the 3 while blocks: header/body/exit).
- **Files modified:** src/FunLangCompiler.Compiler/Elaboration.fs
- **Verification:** Test 29-03 passes (exit=12 correct for 3×4 nested loops)
- **Committed in:** `0eb2b3d`

---

**Total deviations:** 2 auto-fixed (both Rule 1 - bugs found during testing)
**Impact on plan:** Both fixes essential for correctness. No scope creep.

## Issues Encountered
- Test file format: plan used `ref`/`!`/`:=` syntax but compiler uses `let mut`/`x <- val`/direct-read. Updated test programs accordingly.
- FsLit file format: plan's "expected output: 5 then 0" was ambiguous. Actual format: output is just the exit code from `echo $?` (one line).

## Next Phase Readiness
- WhileExpr fully functional with 132/132 tests passing
- ForExpr (for i = start to/downto stop do body) still unimplemented — Phase 29 may have a Plan 02
- The nested-loop back-edge patching pattern (index `len-4`) is specific to the 3-block WhileExpr CFG shape; ForExpr will need the same approach

---
*Phase: 29-loop-constructs*
*Completed: 2026-03-28*
