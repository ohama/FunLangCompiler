---
phase: 07-gc-runtime-integration
plan: 02
subsystem: compiler-ir
tags: [mlir, boehm-gc, bdw-gc, closures, elaboration, fsharp, gc-malloc]

# Dependency graph
requires:
  - phase: 07-gc-runtime-integration/07-01
    provides: LlvmCallOp, LlvmCallVoidOp, ExternalFuncDecl, MlirGlobal in MlirIR + Printer
  - phase: 05-closures-via-elaboration
    provides: LlvmAllocaOp closure env allocation (migrated away from in this plan)
provides:
  - GC_init emitted as first op in @main (GC requirement C-8)
  - Closure environments allocated via GC_malloc (heap-safe, no stack UAF)
  - MlirModule.ExternalFuncs populated with @GC_init and @GC_malloc declarations
  - E2E test confirming escaped closures work with GC_malloc env
affects:
  - 07-03-print-println (adds @printf to ExternalFuncs; same pattern)
  - 08+ (all future heap allocation already uses GC_malloc foundation)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Closure env byte count = (numCaptures + 1) * 8 (fn ptr slot + one slot per capture)
    - GC_init prepended to @main entry block body before any user ops
    - ExternalFuncs always populated unconditionally in elaborateModule

key-files:
  created:
    - tests/compiler/07-01-gc-closure-escape.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Closure env byte count uses (numCaptures + 1) * 8 — slot 0 is fn ptr, slots 1..N are captures"
  - "GC_init prepended unconditionally to @main regardless of whether program uses closures"
  - "ExternalFuncs always includes both @GC_init and @GC_malloc (dead declarations are harmless)"

patterns-established:
  - "Pattern: elaborateModule always appends GC declarations to ExternalFuncs — future plans add @printf etc."
  - "Pattern: GC_malloc replaces alloca; caller computes byte count, result is Ptr"

# Metrics
duration: 4min
completed: 2026-03-26
---

# Phase 7 Plan 02: GC Closure Migration Summary

**Closure environments migrated from llvm.alloca to GC_malloc; GC_init emitted as first op in @main; all 16 FsLit tests pass including new escaped-closure E2E test**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-03-26T05:00:00Z
- **Completed:** 2026-03-26T05:04:00Z
- **Tasks:** 2
- **Files modified:** 2 (Elaboration.fs + new test file)

## Accomplishments

- Replaced `LlvmAllocaOp` with `LlvmCallOp(@GC_malloc)` in App dispatch closure-making path — closure envs now live on the GC heap and cannot dangle after the maker function returns
- Prepended `LlvmCallVoidOp(@GC_init)` as first op in @main entry block — GC collector initialized before any user code runs (satisfies requirement C-8)
- Populated `MlirModule.ExternalFuncs` with `@GC_init` and `@GC_malloc` forward declarations — MLIR text contains proper `llvm.func` declarations before use
- Added `07-01-gc-closure-escape.flt` E2E test: `add_n 5` applied to 10 = 15, confirming escaped closure with GC heap env works end-to-end

## Task Commits

Each task was committed atomically:

1. **Task 1: Migrate closure alloca to GC_malloc and emit GC_init in @main** - `bbf2cdf` (feat)
2. **Task 2: Add escaped-closure FsLit E2E test** - `e433a44` (test)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` - Change A: LlvmAllocaOp -> LlvmCallOp(@GC_malloc) with byte count; Change B: GC_init prepend to @main; Change C: ExternalFuncs populated
- `tests/compiler/07-01-gc-closure-escape.flt` - Escaped closure test: add5 10 = 15

## Decisions Made

- Byte count for env alloc = `(numCaptures + 1) * 8` — slot 0 stores the fn ptr (8 bytes), slots 1..N store captures (I64, 8 bytes each); matches the GEP index scheme already used by the closure body and maker
- GC_init emitted unconditionally for all programs (not only closure programs) — overhead is negligible and avoids conditional logic; matches the v2.0 design decision recorded in STATE.md
- Both @GC_init and @GC_malloc always declared in ExternalFuncs — dead forward declarations are harmless in MLIR/LLVM and avoids conditional logic

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None additional — Boehm GC installation documented in 07-01 USER-SETUP notes (brew install bdw-gc / apt install libgc-dev).

## Next Phase Readiness

- Plan 07-03 (print/println builtins): ExternalFuncs pattern established; add @printf declaration and MlirGlobal string constants
- All 16 tests passing (15 v1 + 1 gc-closure-escape)
- Requirements GC-01 (GC_init in @main) and GC-02 (all heap via GC_malloc) both satisfied
- No blockers

---
*Phase: 07-gc-runtime-integration*
*Completed: 2026-03-26*
