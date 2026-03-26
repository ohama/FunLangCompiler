---
phase: 01-mlirir-foundation
plan: "02"
subsystem: compiler
tags: [fsharp, mlir, llvm, printer, pipeline, clang, mlir-opt, mlir-translate]

# Dependency graph
requires:
  - phase: 01-01
    provides: MlirIR discriminated union (MlirModule, FuncOp, MlirRegion, MlirBlock, MlirOp, MlirValue, MlirType) and return42Module
provides:
  - Printer.printModule serializes MlirModule to valid MLIR 20 text (pure function, no I/O)
  - Pipeline.compile orchestrates mlir-opt → mlir-translate → clang via System.Diagnostics.Process with absolute tool paths
  - CompileError DU for structured error reporting (MlirOptFailed, TranslateFailed, ClangFailed)
affects:
  - 01-03 (smoke tests will call Printer.printModule and Pipeline.compile end-to-end)
  - future phases that add new MlirOp cases (Printer must handle them)

# Tech tracking
tech-stack:
  added: [System.Diagnostics.Process, System.IO.Path, System.IO.File]
  patterns:
    - Pure serializer pattern (Printer is a pure function, no side effects)
    - Structured error with DU (CompileError wraps exit code + stderr per tool)
    - Stderr-before-WaitForExit pattern (prevents deadlock on large tool output)
    - Finally-block cleanup (temp files deleted regardless of success/failure)

key-files:
  created:
    - src/LangBackend.Compiler/Printer.fs
    - src/LangBackend.Compiler/Pipeline.fs
  modified:
    - src/LangBackend.Compiler/LangBackend.Compiler.fsproj

key-decisions:
  - "Printer is pure (no I/O) — caller writes .mlir text to disk; enables unit testing without file system"
  - "stderr read before WaitForExit to prevent pipe deadlock on large mlir-opt output"
  - "LLVM 20 pass order: --convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts (arith must precede func per PR #120548)"
  - "Absolute tool paths hard-coded as [<Literal>] constants for LLVM 20.1.4 at /usr/local/bin/"
  - "Temp files use Path.GetTempFileName() + extension suffix; cleaned in finally block"

patterns-established:
  - "Printer pattern: pure module → string function, one printX helper per IR node type"
  - "Pipeline pattern: Result<unit, CompileError> railway — early return on first tool failure"

# Metrics
duration: 1min
completed: 2026-03-26
---

# Phase 1 Plan 02: Printer and Pipeline Summary

**Pure MLIR text serializer (Printer.printModule) and 4-step shell pipeline (mlir-opt → mlir-translate → clang) wiring MlirIR to native ELF compilation**

## Performance

- **Duration:** ~1 min
- **Started:** 2026-03-26T01:23:45Z
- **Completed:** 2026-03-26T01:24:44Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- Printer.fs: pure function serializing any MlirModule to valid MLIR 20 text — handles func.func, basic blocks, arith.constant, and func.return
- Pipeline.fs: orchestrates mlir-opt → mlir-translate → clang via System.Diagnostics.Process with structured CompileError reporting
- Both files compile cleanly with 0 warnings under net10.0

## Task Commits

Each task was committed atomically:

1. **Task 1: Implement MlirIR Printer (pure string serializer)** - `b7ed8a6` (feat)
2. **Task 2: Implement shell Pipeline (mlir-opt → mlir-translate → clang)** - `6fc88c9` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/Printer.fs` - Pure serializer: MlirModule → MLIR 20 text string
- `src/LangBackend.Compiler/Pipeline.fs` - Shell pipeline wrapping mlir-opt, mlir-translate, clang via Process
- `src/LangBackend.Compiler/LangBackend.Compiler.fsproj` - Added Printer.fs and Pipeline.fs as ordered Compile entries

## Decisions Made

- Printer is a pure function (no I/O side effects) — the pipeline writes the returned string to a temp file. This allows unit testing Printer without a file system.
- stderr is read to end before WaitForExit to prevent pipe buffer deadlock when mlir-opt produces large diagnostic output.
- LLVM 20 pass order: arith lowering must precede func lowering (PR #120548). reconcile-unrealized-casts must be last.
- Tool paths are [<Literal>] string constants — no PATH lookup, no shell expansion, deterministic on the verified machine.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Printer and Pipeline are implemented and build-verified
- Plan 01-03 (smoke tests) can now call Printer.printModule and Pipeline.compile on return42Module
- The end-to-end truth "return42Module compiles to an ELF that exits with code 42" is ready to be verified by the smoke test suite

---
*Phase: 01-mlirir-foundation*
*Completed: 2026-03-26*
