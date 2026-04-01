---
phase: 61-fnc-build-test
plan: 01
subsystem: cli
tags: [fsharp, cli, project-file, build-system, fnc]

# Dependency graph
requires:
  - phase: 60-funproj-toml-parser
    provides: ProjectFile.findFunProj / parseFunProj / resolveTarget / FunProjConfig / Target types
provides:
  - compileFile helper extracting full compilation pipeline (preludeDir, inputPath, outputPath, optLevel)
  - fnc build subcommand compiling all or named [[executable]] targets to build/
  - fnc test subcommand compiling and running all or named [[test]] targets with PASS/FAIL reporting
  - CLI routing: build / test / single-file (.fun) / usage / unknown-command
affects: [future phases using fnc build/test, any CLI extension work]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "compileFile helper: preludeDir=None triggers walkUp search, Some dir uses explicit path"
    - "handleBuild/handleTest: funproj.toml discovery -> parse -> target resolution -> compile -> output to build/"
    - "Test runner: compile to build/<name> -> Process.Start -> WaitForExit -> PASS/FAIL summary"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Cli/Program.fs

key-decisions:
  - "Existing single-file routing scoped to .fun extension check to keep routing unambiguous"
  - "handleTest does not abort on first failure — compiles and runs all test targets, reports summary"
  - "build/ directory always created (both build and test share it) — keeps output paths consistent"
  - "preludeDir from funproj.toml passed as Some to compileFile, skipping the walkUp search"

patterns-established:
  - "CLI routing pattern: match remaining with | 'build' :: rest | 'test' :: rest | .fun path | [] | unknown"
  - "compileFile(preludeDir, inputPath, outputPath, optLevel): reused by all three modes"

# Metrics
duration: 2min
completed: 2026-04-01
---

# Phase 61 Plan 01: FnC Build/Test CLI Routing Summary

**fnc build/test subcommands wired to ProjectFile (Phase 60), with compileFile helper reused across single-file, build, and test modes — completing v17.0 project file support**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-04-01T12:00:59Z
- **Completed:** 2026-04-01T12:03:09Z
- **Tasks:** 2
- **Files modified:** 1

## Accomplishments
- Extracted full compilation pipeline into `compileFile(preludeDir, inputPath, outputPath, optLevel)` reused by all three CLI modes
- Wired `fnc build [<target>]` → `handleBuild`: discovers funproj.toml, compiles all/named [[executable]] targets to build/, prints `OK: name -> build/name (Xs)`
- Wired `fnc test [<target>]` → `handleTest`: discovers funproj.toml, compiles + runs all/named [[test]] targets, prints `PASS/FAIL: name (Xs)` + `N/N tests passed`
- All three error codes implemented: ERR-01 (no funproj.toml), ERR-02 (missing source file), ERR-03 (unknown target name)
- Single-file mode and -O flags preserved exactly; 0 regressions

## Task Commits

Each task was committed atomically:

1. **Task 1 + Task 2: Extract compileFile and wire build/test routing** - `e104a30` (feat)

**Plan metadata:** (pending docs commit)

## Files Created/Modified
- `src/FunLangCompiler.Cli/Program.fs` - compileFile helper + handleBuild + handleTest + updated main routing + updated usage message

## Decisions Made
- Scoped single-file routing to `.fun` extension check so `fnc somebin` (no extension) routes to unknown-command, not single-file fallback
- `handleTest` does not abort on first compile failure — runs all tests and reports summary (matches standard test runner behavior)
- Both `build` and `test` output to `build/<name>` in project directory — consistent output location
- `preludeDir = None` in compileFile preserves existing walkUp behavior for single-file mode

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- v17.0 Project File support is complete: ProjectFile.fs (Phase 60) + CLI routing (Phase 61)
- `fnc build` and `fnc test` are fully functional
- No blockers for future phases

---
*Phase: 61-fnc-build-test*
*Completed: 2026-04-01*
