---
phase: 40-multi-file-import
plan: 01
subsystem: compiler
tags: [fsharp, ast, import, multi-file, cycle-detection, hashset, path-resolution]

# Dependency graph
requires:
  - phase: 35-prelude-modules
    provides: parseProgram function and prelude auto-loading in Program.fs
provides:
  - expandImports function with HashSet-based cycle detection and relative path resolution
  - resolveImportPath helper for absolute/relative path normalization
  - 5 E2E tests covering COMP-01 through COMP-04 plus diamond import
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "AST-level import expansion in Program.fs before elaboration (not in Elaboration.fs)"
    - "HashSet push/pop for cycle detection — diamond imports are NOT cycles"
    - "printf \\x22 escaping for double quotes in single-quoted bash -c flt commands"

key-files:
  created:
    - tests/compiler/40-01-basic-import.flt
    - tests/compiler/40-02-recursive-import.flt
    - tests/compiler/40-03-circular-import.flt
    - tests/compiler/40-04-relative-path.flt
    - tests/compiler/40-05-diamond-import.flt
  modified:
    - src/FunLangCompiler.Cli/Program.fs

key-decisions:
  - "expandImports in Program.fs (not Elaboration.fs) — keeps I/O at CLI boundary, elaboration stays pure"
  - "HashSet push/pop per traversal path — diamond imports correctly handled without false cycle detection"
  - "Imported files parsed standalone (no prelude prefix) — prelude already in outer combinedSrc"
  - "printf \\x22 hex escape for double quotes in flt test commands to avoid single-quote escaping issues"

patterns-established:
  - "Multi-file flt tests: use printf with \\x22 for double quotes in file content within bash -c single-quoted strings"

# Metrics
duration: 15min
completed: 2026-03-30
---

# Phase 40 Plan 01: Multi-file Import Summary

**AST-level FileImportDecl expansion via recursive HashSet-cycle-detected expandImports in Program.fs — enables open "file.fun" multi-file programs with transitive imports, circular error detection, and subdirectory paths (197/197 tests pass)**

## Performance

- **Duration:** 15 min
- **Started:** 2026-03-29T22:50:57Z
- **Completed:** 2026-03-29T23:05:57Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- Added `resolveImportPath` and `expandImports` to Program.fs with HashSet push/pop cycle detection
- Integrated expansion into main pipeline: after `parseProgram`, before `elaborateProgram`
- NamespaceDecl inner decls also recursed (per plan spec)
- 5 E2E tests: COMP-01 (basic), COMP-02 (transitive A->B->C), COMP-03 (circular error), COMP-04 (relative path), diamond import
- All 197 tests pass (192 existing + 5 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add expandImports to Program.fs** - `74d5f44` (feat)
2. **Task 2: Add E2E tests for all COMP requirements** - `e155bd2` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/FunLangCompiler.Cli/Program.fs` - Added resolveImportPath, expandImports, and expandedAst integration
- `tests/compiler/40-01-basic-import.flt` - COMP-01: open "utils.fun", use add function
- `tests/compiler/40-02-recursive-import.flt` - COMP-02: transitive A->B->C, all bindings visible
- `tests/compiler/40-03-circular-import.flt` - COMP-03: circular import error (grep-based stable output)
- `tests/compiler/40-04-relative-path.flt` - COMP-04: relative path from subdirectory
- `tests/compiler/40-05-diamond-import.flt` - Diamond: A->B, A->C, both->shared — not a false cycle

## Decisions Made
- `expandImports` lives in Program.fs, not Elaboration.fs — keeps I/O at the CLI boundary; elaboration remains pure
- HashSet push/pop (not global visited set) — diamond imports require pop after each subtree so sibling imports don't appear as cycles
- Imported files parsed with `parseProgram src resolvedPath` without prelude prefix — prelude is already injected into outer `combinedSrc`
- `printf \x22` hex escape for double quotes in flt test commands — cleaner than `'"'"'` heredoc escaping inside single-quoted bash -c

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- `\"` inside single-quoted bash -c does not produce `"` — it produces literal backslash-quote. Resolved by using `\x22` hex escape in printf format strings, which printf expands to `"` regardless of shell quoting.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Phase 40 (Multi-file Import) is COMPLETE — this is the final plan of v10.0
- All COMP requirements verified: COMP-01, COMP-02, COMP-03, COMP-04, diamond
- v10.0 milestone (FunLexYacc native compilation support) is COMPLETE
- 197/197 tests pass, zero regressions

---
*Phase: 40-multi-file-import*
*Completed: 2026-03-30*
