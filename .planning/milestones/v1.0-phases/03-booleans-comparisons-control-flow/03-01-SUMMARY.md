---
phase: 03-booleans-comparisons-control-flow
plan: "01"
subsystem: compiler
tags: [mlir, fsharp, arith.cmpi, cf.cond_br, cf.br, i1, bool, comparison, elaboration]

# Dependency graph
requires:
  - phase: 02-scalar-codegen-via-mlirir
    provides: Elaboration pass, SSA naming, ArithOp printing, FsLit E2E test harness
provides:
  - ArithCmpIOp, CfCondBrOp, CfBrOp cases in MlirOp DU
  - Printer support for arith.cmpi, cf.cond_br, cf.br MLIR text
  - Bool literal elaboration (I1 type, arith.constant 0/1)
  - Six comparison operators elaborated to ArithCmpIOp with correct predicates
  - Dynamic ReturnType in elaborateModule (uses resultVal.Type)
  - Two FsLit E2E tests: 03-01-bool-literal.flt, 03-02-comparison.flt
affects:
  - 03-02-booleans-if-else (multi-block if-else using CfCondBrOp/CfBrOp)
  - 04-functions (needs I1 return type for boolean-returning functions)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "arith.cmpi predicate strings: eq, ne, slt, sgt, sle, sge (signed integer predicates)"
    - "cmpi type suffix is OPERAND type (i64), not result type (i1)"
    - "Bool literals map to arith.constant : i1 (0 for false, 1 for true)"
    - "Dynamic ReturnType: elaborateModule uses resultVal.Type, not hardcoded I64"

key-files:
  created:
    - tests/compiler/03-01-bool-literal.flt
    - tests/compiler/03-02-comparison.flt
  modified:
    - src/LangBackend.Compiler/MlirIR.fs
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "arith.cmpi type annotation uses the operand type (i64), not the result type (i1) — matches MLIR spec"
  - "CfCondBrOp/CfBrOp args use (value : type) format; empty arg lists omit parentheses entirely"
  - "elaborateModule ReturnType changed from hardcoded I64 to resultVal.Type to support bool-returning programs"

patterns-established:
  - "Comparison elaboration: inline lhs/rhs elaborateExpr calls, fresh I1 result, append ArithCmpIOp"
  - "Bool literal elaboration: ArithConstantOp with I1 type and 0/1 integer value"

# Metrics
duration: 2min
completed: 2026-03-26
---

# Phase 3 Plan 01: Booleans and Comparison Operators Summary

**ArithCmpIOp/CfCondBrOp/CfBrOp IR ops, bool/comparison elaboration with I1 type, and dynamic ReturnType — enabling bool-valued programs to compile and run**

## Performance

- **Duration:** 2 min
- **Started:** 2026-03-26T02:40:00Z
- **Completed:** 2026-03-26T02:42:13Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Added three MlirOp DU cases (ArithCmpIOp, CfCondBrOp, CfBrOp) with full Printer support
- Extended Elaboration to handle Bool literals and all six comparison operators with correct MLIR predicates
- Fixed hardcoded I64 ReturnType so bool-valued programs produce valid i1-returning functions
- Two new FsLit E2E tests pass: `true` exits 1, `3 < 5` exits 1

## Task Commits

Each task was committed atomically:

1. **Task 1: MlirOp new cases, Printer cases, and Elaboration for bool/comparison** - `6451305` (feat)
2. **Task 2: FsLit E2E tests for bool literal and comparison operator** - `aaf8a0b` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/MlirIR.fs` - Added ArithCmpIOp, CfCondBrOp, CfBrOp to MlirOp DU
- `src/LangBackend.Compiler/Printer.fs` - Added printOp cases for cmpi, cond_br, br
- `src/LangBackend.Compiler/Elaboration.fs` - Bool/comparison elaboration, dynamic ReturnType
- `tests/compiler/03-01-bool-literal.flt` - E2E test: `true` exits 1
- `tests/compiler/03-02-comparison.flt` - E2E test: `3 < 5` exits 1

## Decisions Made
- `arith.cmpi` type annotation uses the operand type (i64), not the result type (i1) — this matches the MLIR specification where cmpi annotates the input comparison type
- CfCondBrOp/CfBrOp args format: `(%v : type, ...)` with parentheses; empty arg lists omit parentheses
- ReturnType changed from `Some I64` to `Some resultVal.Type` in elaborateModule — required for all non-integer-returning programs going forward

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] F# type ambiguity on MlirValue.Name vs FuncOp.Name in Printer lambda**
- **Found during:** Task 1 (build verification)
- **Issue:** `fmtArgs` local function's lambda parameter `v` was resolved by F# compiler as `FuncOp` (both `FuncOp` and `MlirValue` have a `Name` field); caused 5 build errors
- **Fix:** Added explicit `(args: MlirValue list)` and `(v: MlirValue)` type annotations to `fmtArgs` lambdas in CfCondBrOp and CfBrOp printer cases
- **Files modified:** src/LangBackend.Compiler/Printer.fs
- **Verification:** `dotnet build` succeeded with 0 warnings, 0 errors
- **Committed in:** 6451305 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - build error from F# type inference ambiguity)
**Impact on plan:** Required fix for correctness. No scope creep.

## Issues Encountered
- F# type ambiguity between `FuncOp.Name` and `MlirValue.Name` in the Printer's new `fmtArgs` helper required explicit type annotations. Fixed immediately.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- ArithCmpIOp, CfCondBrOp, CfBrOp are ready for Plan 03-02 (if-else multi-block control flow)
- Dynamic ReturnType ensures future plans with non-I64 results work out of the box
- All 6 FsLit tests pass

---
*Phase: 03-booleans-comparisons-control-flow*
*Completed: 2026-03-26*
