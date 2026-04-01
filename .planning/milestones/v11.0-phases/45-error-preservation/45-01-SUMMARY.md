# Phase 45 Plan 01: Error Preservation Summary

Parser firstEx preservation + MLIR debug file retention on compile failure

## Tasks Completed

| Task | Name | Commit | Key Files |
|------|------|--------|-----------|
| 1 | Parser error preservation + MLIR debug file preservation | 0072005 | Program.fs, Pipeline.fs |
| 2 | E2E tests for error preservation | 1c12643 | 45-01-*.flt/fun, 45-02-*.flt/fun |

## Changes Made

### Parser Error Preservation (Program.fs)
- `parseProgram` now saves the first exception (`firstEx`) from `parseModule`
- Falls back to `parseExpr` as before
- If both fail, re-raises the original `firstEx` instead of losing it
- Removed verbose DEBUG token dump output (was noise in production)

### MLIR Debug File Preservation (Pipeline.fs)
- `CompileError` DU extended: `MlirOptFailed` and `TranslateFailed` now include `mlirFile: string`
- On MlirOpt or MlirTranslate failure, the `.mlir` temp file is preserved (not deleted)
- Error message includes `MLIR file preserved: <path>` for easy debugging
- Removed old `finally` block with unconditional cleanup; replaced with explicit `cleanup` function
- Old `/tmp/debug_last.mlir` copy hack removed (no longer needed)

### Tests
- 45-01: Incomplete `let` expression triggers double parse failure, verifies original error preserved
- 45-02: Invalid second declaration triggers parseModule error, verifies preservation on fallback
- All 212 tests pass (210 existing + 2 new)

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| Keep catch-all as "Error:" not "Parse error:" | Catch-all handles elaboration/codegen errors too, not just parse errors |
| Preserve mlirFile only for MlirOpt/Translate, not Clang | By Clang stage the LLVM IR is already generated; .mlir less useful |
| Remove DEBUG token dump | Was temporary debug aid; firstEx preservation makes it unnecessary |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Catch-all error prefix**
- **Found during:** Task 1
- **Issue:** Plan suggested changing "Error:" to "Parse error:" in catch-all handler, but this broke elaboration error tests (44-01, 44-03)
- **Fix:** Kept original "Error:" prefix since the handler catches all exception types
- **Commit:** 0072005

## Verification

- `dotnet build` succeeds with 0 errors, 0 warnings
- `fslit tests/compiler/` — 212/212 passed, 0 failed

## Metrics

- Duration: ~8 min
- Completed: 2026-03-31
