---
phase: 04-known-functions-via-elaboration
plan: "01"
subsystem: compiler
tags: [fsharp, mlir, elaboration, func-call, let-rec, recursive-functions, directcallop]

# Dependency graph
requires:
  - phase: 03-booleans-comparisons-control-flow
    provides: multi-block elaboration, If/And/Or, CfCondBrOp/CfBrOp, entry+side-block assembly
provides:
  - DirectCallOp in MlirOp DU for direct function calls
  - func.call serialization in Printer
  - FuncSignature type and KnownFuncs/Funcs fields in ElabEnv
  - LetRec elaboration: builds FuncOp with fresh body env, shares Funcs ref
  - App elaboration: emits DirectCallOp for known-function calls
  - elaborateModule collects accumulated FuncOps before @main
  - E2E tests: factorial (exit 120) and fibonacci (exit 55)
affects:
  - 05-closures (builds on FuncOp pattern; Phase 5 adds ClosureAllocOp/IndirectCallOp)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Fresh body env for LetRec: Blocks = ref [], only Funcs ref is shared with parent"
    - "Forward-declare recursive function in body env KnownFuncs before elaborating body"
    - "Accumulate FuncOps via env.Funcs ref; elaborateModule prepends them before @main"

key-files:
  created:
    - tests/compiler/04-01-fact.flt
    - tests/compiler/04-02-fib.flt
  modified:
    - src/FunLangCompiler.Compiler/MlirIR.fs
    - src/FunLangCompiler.Compiler/Printer.fs
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Fresh body env for LetRec has Blocks = ref [] (not shared) — function body blocks are isolated from caller"
  - "Funcs ref IS shared so nested LetRec can accumulate FuncOps into the same module-level list"
  - "App(Var(name)) checks KnownFuncs; unknown functions fail fast with clear message"
  - "elaborateModule: env.Funcs.Value @ [mainFunc] places helpers before @main (conventional order)"

patterns-established:
  - "KnownFuncs: forward-declare before body elaboration to enable recursion"
  - "FuncSignature stores MlirName (with @ sigil), ParamTypes, ReturnType"

# Metrics
duration: 3min
completed: 2026-03-26
---

# Phase 4 Plan 1: Known Functions via Elaboration Summary

**DirectCallOp + LetRec/App elaboration enabling recursive functions: factorial (exit 120) and fibonacci (exit 55) compile end-to-end with func.func/func.call MLIR, no closures**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-03-26T03:06:38Z
- **Completed:** 2026-03-26T03:09:14Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- DirectCallOp added to MlirOp DU; Printer serializes to `func.call @callee(%args) : (argTypes) -> retType`
- ElabEnv extended with FuncSignature, KnownFuncs, and Funcs; LetRec and App cases handle recursive functions
- elaborateModule collects accumulated FuncOps from env.Funcs before @main
- All 11 FsLit E2E tests pass (9 existing + 2 new: fact and fib), zero regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: DirectCallOp in MlirIR and Printer, ElabEnv extension, LetRec and App elaboration** - `23ac7f4` (feat)
2. **Task 2: FsLit E2E tests for factorial and fibonacci** - `282f9fc` (test)

**Plan metadata:** (docs commit to follow)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/MlirIR.fs` - Added DirectCallOp case to MlirOp DU
- `src/FunLangCompiler.Compiler/Printer.fs` - Added func.call serialization for DirectCallOp
- `src/FunLangCompiler.Compiler/Elaboration.fs` - FuncSignature type, KnownFuncs/Funcs in ElabEnv, LetRec/App cases, elaborateModule update
- `tests/compiler/04-01-fact.flt` - E2E test: `let rec fact n = ... in fact 5` exits 120
- `tests/compiler/04-02-fib.flt` - E2E test: `let rec fib n = ... in fib 10` exits 55

## Decisions Made
- Fresh body env for LetRec: `Blocks = ref []` (isolated), `Funcs = env.Funcs` (shared). Only the Funcs accumulator is shared so nested functions emit into the same module-level list.
- App case checks KnownFuncs map; unsupported calls fail fast with a clear message ("only direct calls to known functions supported in Phase 4").
- elaborateModule emits `env.Funcs.Value @ [mainFunc]`: helper functions appear before @main, which is conventional MLIR style (forward calls are fine regardless).

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Phase 4 Plan 1 complete: recursive single-argument functions compile to native binaries
- ELAB-02 requirement satisfied
- Phase 5 (closures) can build on this: FuncOp emission pattern established, IndirectCallOp and ClosureAllocOp will extend MlirOp similarly
- Constraint for Phase 5: Phase 4 enforces "no free variables" in LetRec body (only param in Vars) — Phase 5 must capture free variables into closure structs

---
*Phase: 04-known-functions-via-elaboration*
*Completed: 2026-03-26*
