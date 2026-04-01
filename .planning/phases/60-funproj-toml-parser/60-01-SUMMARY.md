---
phase: 60-funproj-toml-parser
plan: 01
subsystem: compiler
tags: [fsharp, toml, project-file, parser, funproj]

# Dependency graph
requires:
  - phase: 59-pipeline
    provides: Pipeline.fs — compiler library ProjectFile.fs is appended to
provides:
  - ProjectFile.fs TOML subset parser with Target, FunProjConfig types
  - parseFunProj: [project]/[[executable]]/[[test]] line-by-line parser
  - findFunProj: walk-up directory search for funproj.toml
  - resolveTarget: absolute path resolution from projectDir + target.Main
  - E2E test project at tests/projfile/ exercising all parser behaviors
affects: [61-fnc-cli, any phase wiring fnc build/test commands]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "RequireQualifiedAccess DU for section tracking in TOML parser"
    - "Mutable accumulator pattern for list building in line-by-line parser"
    - "Standalone test console project (tests/projfile/) for library unit testing"

key-files:
  created:
    - src/FunLangCompiler.Compiler/ProjectFile.fs
    - tests/projfile/Program.fs
    - tests/projfile/projfile.fsproj
  modified:
    - src/FunLangCompiler.Compiler/FunLangCompiler.Compiler.fsproj

key-decisions:
  - "No external TOML library — hand-rolled line-by-line parser for the small subset needed"
  - "Standalone test console project (tests/projfile/) chosen over dotnet fsi DLL ref for reliability"
  - "ProjectFile.fs placed last in Compile list since no compiler modules depend on it yet (Phase 61 will wire it)"

patterns-established:
  - "TOML section tracking: RequireQualifiedAccess DU (None/Project/Executable/Test)"
  - "finishTarget flush pattern: called before every section header and at end of file"

# Metrics
duration: 2min
completed: 2026-04-01
---

# Phase 60 Plan 01: ProjectFile TOML Parser Summary

**Minimal TOML subset parser for funproj.toml — produces FunProjConfig with Name, PreludePath, Executables, and Tests from [project]/[[executable]]/[[test]] sections**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-04-01T11:48:47Z
- **Completed:** 2026-04-01T11:50:48Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- ProjectFile.fs module with Target/FunProjConfig types and parseFunProj/findFunProj/resolveTarget functions
- Clean build — compiler library with ProjectFile.fs, 0 warnings, 0 errors
- E2E test project with 5 test cases: full parse, resolveTarget, missing optionals, comments, whitespace variants — all pass

## Task Commits

1. **Task 1: Create ProjectFile.fs with TOML subset parser** - `5652ce1` (feat)
2. **Task 2: Add to fsproj and create E2E test** - `019151d` (test)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/ProjectFile.fs` - TOML parser module with all five exported symbols
- `src/FunLangCompiler.Compiler/FunLangCompiler.Compiler.fsproj` - Added ProjectFile.fs to Compile list
- `tests/projfile/Program.fs` - 5-test E2E harness for parseFunProj and resolveTarget
- `tests/projfile/projfile.fsproj` - Standalone console project referencing FunLangCompiler.Compiler

## Decisions Made
- No external TOML library — a hand-rolled line-by-line parser is sufficient for the narrow subset needed and avoids NuGet dependencies.
- Used `[<RequireQualifiedAccess>]` on the `Section` DU to avoid name conflicts with the standard `None` option case.
- Standalone test console project (`tests/projfile/`) chosen over `dotnet fsi` with DLL reference, as FSI DLL loading is fragile; a proper fsproj is more reliable.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Replaced System.Option/System.Some with standard F# option**
- **Found during:** Task 1 (first build attempt)
- **Issue:** Used `System.Option.None` / `System.Some` which are not valid F# identifiers; `None` and `Some` are global.
- **Fix:** Rewrote file using plain `None` / `Some` / `option`, added `open System` / `open System.IO`.
- **Files modified:** src/FunLangCompiler.Compiler/ProjectFile.fs
- **Verification:** Build succeeded with 0 warnings after rewrite.
- **Committed in:** 5652ce1 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug in initial draft)
**Impact on plan:** Necessary correctness fix in first draft; no scope change.

## Issues Encountered
- Initial draft used `System.Option.None` / `System.Some` (not valid F# syntax). Rewrote cleanly using idiomatic `None`/`Some` and qualified the internal Section DU with `[<RequireQualifiedAccess>]`.

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- Phase 61 (fnc build/test CLI) can import `ProjectFile` module from FunLangCompiler.Compiler and call `findFunProj()` + `parseFunProj` + `resolveTarget` directly.
- All three functions are tested and confirmed working.
- No blockers.

---
*Phase: 60-funproj-toml-parser*
*Completed: 2026-04-01*
