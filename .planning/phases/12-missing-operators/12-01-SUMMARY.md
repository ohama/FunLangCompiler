---
phase: 12-missing-operators
plan: 01
subsystem: compiler
tags: [mlir, arith, modulo, char, closure, elaboration]

# Dependency graph
requires:
  - phase: 11-match-failure
    provides: existing elaboration infrastructure, MlirOp DU pattern
provides:
  - ArithRemSIOp MLIR op in MlirIR.fs (arith.remsi emission)
  - Modulo operator elaboration (10 % 3 = 1)
  - Char literal elaboration ('A' = 65)
  - App(Lambda) inline call fix (prerequisite for PipeRight)
  - freeVars fix for Modulo/PipeRight/ComposeRight/ComposeLeft
affects: [12-02-missing-operators]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "New binary op: add DU case to MlirIR, print case to Printer, elaboration case matching Divide pattern"
    - "Char as int64: int64 (int c) gives Unicode code point directly"
    - "App(Lambda) inline: bind param in env, elaborate body (no closure allocation)"

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/MlirIR.fs
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "App(Lambda) inlines as env binding, not closure — no allocation for immediately-applied lambdas"
  - "Char uses int64 (int c): F# int on char gives Unicode BMP code point directly"
  - "freeVars conservative catch-all extended with explicit Modulo/pipe/compose cases"

patterns-established:
  - "Pattern: Adding a binary arith op = 1 DU case in MlirIR + 1 print case in Printer + 1 elaboration case"

# Metrics
duration: 5min
completed: 2026-03-26
---

# Phase 12 Plan 01: ArithRemSIOp + Modulo + Char + App(Lambda) inline fix

**ArithRemSIOp added to MLIR IR, Modulo/Char/App(Lambda) elaboration implemented — 10 % 3 exits 1, 'A' exits 65, inline lambdas work**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-03-26T00:00:00Z
- **Completed:** 2026-03-26T00:05:00Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- ArithRemSIOp DU case added to MlirIR.fs; Printer.fs emits `arith.remsi`
- Elaboration.fs handles Modulo (%), Char ('A'), and App(Lambda) inline call
- freeVars extended to recurse into Modulo/PipeRight/ComposeRight/ComposeLeft subexpressions
- Verified: `10 % 3` exits 1, `'A'` exits 65, `(fun x -> x + 1) 5` exits 6

## Task Commits

1. **Task 1: Add ArithRemSIOp to MlirIR and Printer** - `c593380` (feat)
2. **Task 2: Add Modulo, Char, App(Lambda), and freeVars cases to Elaboration.fs** - `b9315d2` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/MlirIR.fs` - Added ArithRemSIOp DU case after ArithDivSIOp
- `src/LangBackend.Compiler/Printer.fs` - Added arith.remsi print case
- `src/LangBackend.Compiler/Elaboration.fs` - Char, Modulo, App(Lambda), freeVars fixes

## Decisions Made
- App(Lambda) inline path binds the argument in env and elaborates the body directly, avoiding closure allocation for immediately-applied lambdas
- Char lowering uses F# `int64 (int c)` — no UTF-8 codec needed, BMP code point is what LangThree expects
- freeVars previously had a conservative `| _ -> Set.empty` catch-all; Modulo has two subexpressions so it MUST recurse both to correctly track free variables in closure bodies

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Plan test syntax used `let main = X` but parser expects bare expression**
- **Found during:** Task 2 verification
- **Issue:** The plan's smoke tests used `let main = 10 % 3` but the LangThree parser `start` rule expects a bare `Expr EOF`, not a top-level `let main = ...` binding
- **Fix:** Used bare expressions: `10 % 3`, `'A'`, `(fun x -> x + 1) 5`
- **Files modified:** None (test syntax only)
- **Verification:** All three expressions compiled and exited with correct codes

---

**Total deviations:** 1 auto-fixed (test syntax correction)
**Impact on plan:** No code changes needed — the compiler and tests are correct. Plan docs had wrong test syntax.

## Issues Encountered
- Plan tests used `let main = ...` wrapper which doesn't parse. The LangThree CLI takes a bare expression. Fixed by using bare expressions in verification.

## Next Phase Readiness
- App(Lambda) inline call works: prerequisite for Plan 02 PipeRight is complete
- freeVars handles all new operators: closures capturing modulo/pipe/compose expressions will work correctly
- Ready for Plan 02: PipeRight, ComposeRight, ComposeLeft desugaring

---
*Phase: 12-missing-operators*
*Completed: 2026-03-26*
