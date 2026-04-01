---
phase: 01-mlirir-foundation
plan: "03"
subsystem: compiler
tags: [fsharp, cli, fslit, e2e, mlir, llvm, cross-platform]

# Dependency graph
requires:
  - phase: 01-02
    provides: Printer.printModule (pure serializer) and Pipeline.compile (mlir-opt → mlir-translate → clang orchestration)
provides:
  - FunLangCompiler.Cli console app accepting `-o <output> <input>` and calling Pipeline.compile with return42Module
  - FsLit E2E smoke test verifying full MlirIR → Printer → mlir-opt → mlir-translate → clang → binary chain
  - Cross-platform LLVM tool path resolution (LLVM_BIN_DIR env var, Homebrew, Linux fallback)
affects:
  - Phase 2+ (CLI will switch from hardcoded return42Module to real AST elaboration)
  - All future phases that add FsLit test files (same test pattern)

# Tech tracking
tech-stack:
  added: [FunLangCompiler.Cli (console app), FsLit (E2E test runner)]
  patterns:
    - CLI arg parsing with recursive list match (-o flag extraction)
    - FsLit test pattern using %S relative paths and mktemp for output binary
    - Cross-platform tool resolution (env var → Homebrew → Linux fallback)

key-files:
  created:
    - src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj
    - src/FunLangCompiler.Cli/Program.fs
    - tests/compiler/01-return42.flt
  modified:
    - src/FunLangCompiler.Compiler/Pipeline.fs

key-decisions:
  - "Cross-platform LLVM tool paths via resolveTool helper checking LLVM_BIN_DIR, then Homebrew, then Linux paths"
  - "FsLit test uses mktemp for output binary to avoid FsLit %output overwrite issue"

patterns-established:
  - "FsLit E2E test pattern: bash -c with mktemp, dotnet run --project %S/../../src/..., echo $?"

# Metrics
duration: 2min
completed: 2026-03-26
---

# Plan 01-03: CLI + FsLit E2E Summary

**FunLangCompiler.Cli console app and FsLit smoke test verifying full MlirIR → binary pipeline exits 42**

## Performance

- **Duration:** ~2 min
- **Tasks:** 2 auto + 1 checkpoint (approved)
- **Files modified:** 4

## Accomplishments
- CLI console app with `-o <output> <input>` interface calling Pipeline.compile
- FsLit smoke test `01-return42.flt` passes end-to-end
- Cross-platform LLVM tool path resolution (auto-fix deviation)

## Task Commits

1. **Task 1: Create FunLangCompiler.Cli console project** - `34e4735` (feat)
2. **Task 2: Write and verify FsLit smoke test** - `2e6d10e` (test)
3. **Auto-fix: Cross-platform LLVM tool paths** - `c5c4823` (fix)

## Files Created/Modified
- `src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj` - Console project referencing FunLangCompiler.Compiler
- `src/FunLangCompiler.Cli/Program.fs` - Phase 1 CLI entry point (hardcoded return42Module)
- `tests/compiler/01-return42.flt` - FsLit E2E smoke test
- `src/FunLangCompiler.Compiler/Pipeline.fs` - Cross-platform tool path resolution

## Decisions Made
- Cross-platform LLVM tool paths: resolveTool helper checks LLVM_BIN_DIR env var, then Homebrew path, then Linux path

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Cross-platform LLVM tool paths**
- **Found during:** Task 2 (FsLit test verification)
- **Issue:** Pipeline.fs had hardcoded `/usr/local/bin/` paths (Linux); macOS Homebrew has tools at `/opt/homebrew/opt/llvm/bin/`
- **Fix:** Added `resolveTool` helper checking LLVM_BIN_DIR, Homebrew, Linux paths
- **Files modified:** src/FunLangCompiler.Compiler/Pipeline.fs
- **Verification:** FsLit test passes on macOS
- **Committed in:** c5c4823

---

**Total deviations:** 1 auto-fixed (blocking)
**Impact on plan:** Essential for cross-platform correctness. No scope creep.

## Issues Encountered
None beyond the auto-fixed tool path issue.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Full Phase 1 pipeline verified: MlirIR → Printer → mlir-opt → mlir-translate → clang → binary
- Phase 2 can replace hardcoded return42Module with real AST elaboration
- FsLit test pattern established for future test files

---
*Phase: 01-mlirir-foundation*
*Completed: 2026-03-26*
