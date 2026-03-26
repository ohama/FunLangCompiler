---
phase: 02-scalar-codegen-via-mlirir
plan: "01"
subsystem: compiler
tags: [fsharp, mlir, elaboration, arith, codegen, fslit]

# Dependency graph
requires:
  - phase: 01-mlirir-foundation
    provides: MlirIR DU, Printer, Pipeline, FsLit smoke test infrastructure
provides:
  - Elaboration pass (ElabEnv, freshName, elaborateExpr, elaborateModule)
  - MlirOp DU extended with ArithAddIOp, ArithSubIOp, ArithMulIOp, ArithDivSIOp
  - Printer handles all 6 MlirOp cases
  - CLI parses real .lt files and elaborates to MlirIR
  - FsLit test 02-01-literal.flt verifies integer literal 7 compiles end-to-end
affects:
  - 02-02-arithmetic-and-let-bindings
  - future phases adding more Elaboration.elaborateExpr cases

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Elaboration pass: LangThree Ast.Expr -> MlirValue * MlirOp list (accumulator pattern)"
    - "ElabEnv with int ref Counter for SSA fresh-name generation (%t0, %t1, ...)"
    - "elaborateModule wraps result in @main func returning i64 with ReturnOp"
    - "CLI arg parsing: parseArgs extracts -o separately, input is remaining[0]"

key-files:
  created:
    - src/LangBackend.Compiler/Elaboration.fs
    - tests/compiler/02-01-literal.flt
  modified:
    - src/LangBackend.Compiler/MlirIR.fs
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/LangBackend.Compiler.fsproj
    - src/LangBackend.Cli/Program.fs

key-decisions:
  - "Elaboration.fs placed after Printer.fs in .fsproj (F# compilation order: depends on MlirIR and Ast)"
  - "freshName generates %t0, %t1, ... SSA names via int ref counter in ElabEnv"
  - "Negate lowered as: zero = arith.constant 0; result = arith.subi zero, inner"
  - "CLI uses parseArgs to extract -o flag independently, supports any argument order"
  - "parseExpr in Program.fs replicates LangThree.Program.parse 3-line pattern (avoids Eval/Prelude init)"

patterns-established:
  - "elaborateExpr: Expr -> (MlirValue * MlirOp list) — return value plus accumulated ops"
  - "Binary ops: elaborate lhs, elaborate rhs, emit result op at end of combined list"
  - "Let binding: elaborate bind, extend env with var->value, elaborate body in extended env"

# Metrics
duration: 2min
completed: 2026-03-26
---

# Phase 2 Plan 01: Elaboration Pass Skeleton and Integer Literal Codegen Summary

**Elaboration pass (Ast.Expr -> MlirIR) introduced with SSA name generation, four binary arith ops added to MlirOp DU and Printer, CLI wired to parse real .lt files — integer literal 7 compiles and exits with code 7**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-03-26T02:10:28Z
- **Completed:** 2026-03-26T02:12:53Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- Elaboration pass skeleton with ElabEnv, freshName, elaborateExpr (handles Number, Add, Subtract, Multiply, Divide, Negate, Var, Let, catch-all), and elaborateModule wrapping in @main
- MlirOp DU extended from 2 cases to 6 (added ArithAddIOp, ArithSubIOp, ArithMulIOp, ArithDivSIOp)
- Printer updated with exhaustive match covering all 6 op cases, emitting valid MLIR text
- CLI now parses real .lt source files via Lexer/Parser from LangThree, then calls Elaboration instead of hardcoded return42Module
- FsLit test 02-01-literal.flt proves integer literal 7 compiles end-to-end and binary exits with code 7
- Regression: 01-return42.flt still passes (42 parsed and elaborated correctly)

## Task Commits

Each task was committed atomically:

1. **Task 1: MlirIR arith op types, Printer cases, and Elaboration pass skeleton** - `f78769b` (feat)
2. **Task 2: Wire CLI to parse .lt files and call Elaboration, add FsLit literal test** - `1c45a05` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - New elaboration pass: ElabEnv, freshName, elaborateExpr, elaborateModule
- `src/LangBackend.Compiler/MlirIR.fs` - Added ArithAddIOp, ArithSubIOp, ArithMulIOp, ArithDivSIOp to MlirOp DU
- `src/LangBackend.Compiler/Printer.fs` - Added printOp cases for addi, subi, muli, divsi
- `src/LangBackend.Compiler/LangBackend.Compiler.fsproj` - Added Elaboration.fs compile entry after Printer.fs
- `src/LangBackend.Cli/Program.fs` - Replaced hardcoded return42Module with parseExpr + Elaboration.elaborateModule
- `tests/compiler/02-01-literal.flt` - FsLit test for integer literal 7 end-to-end

## Decisions Made
- Elaboration.fs placed after Printer.fs in .fsproj since F# compiles top-to-bottom and Elaboration depends on MlirIR
- freshName uses `%t0`, `%t1`, ... pattern with int ref counter in ElabEnv for SSA value naming
- Negate lowered as two ops: `arith.constant 0` then `arith.subi zero, inner`
- CLI parseArgs extracts `-o` and its value independently, remainder[0] is the input file (any arg order)
- parseExpr in Program.fs replicates LangThree.Program.parse's 3-line pattern directly, avoiding Eval/Prelude module initialization

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Elaboration pass ready for Plan 02-02 to add arithmetic expression and let binding support
- All four binary arith op types (add, sub, mul, div) already wired in MlirIR and Printer — Plan 02-02 focuses purely on elaboration logic
- ElabEnv.Vars map is in place for let binding variable tracking in 02-02

---
*Phase: 02-scalar-codegen-via-mlirir*
*Completed: 2026-03-26*
