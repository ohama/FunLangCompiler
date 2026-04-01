---
phase: 47-prelude-separate-parsing
plan: 01
subsystem: compiler
tags: [fsharp, parsing, ast, prelude, line-numbers, error-messages]

# Dependency graph
requires:
  - phase: 46-context-hints-unified-format
    provides: failWithSpan infrastructure, [Elaboration]/[Parse]/[Compile] error categories
provides:
  - Separate Prelude/user parseProgram calls with AST merge
  - User code line numbers starting at 1 (not 174)
  - Prelude errors distinguishable via "<prelude>" filename
affects: [48, 49, 50, future-error-message-phases]

# Tech tracking
tech-stack:
  added: []
  patterns: [two-phase-parsing, ast-level-merge, source-scoped-filenames]

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Cli/Program.fs
    - tests/compiler/44-01-error-location-unbound.flt
    - tests/compiler/44-02-error-location-pattern.flt
    - tests/compiler/44-03-error-location-field.flt
    - tests/compiler/46-01-record-type-hint.flt
    - tests/compiler/46-02-field-hint.flt
    - tests/compiler/46-03-function-hint.flt
    - tests/compiler/46-04-error-category-elab.flt

key-decisions:
  - "Merge at AST Decl list level (preludeDecls @ userDecls) using userSpan for module span"
  - "Prelude parsed under literal '<prelude>' filename — distinguishable in error messages"
  - "userSpan (not unknownSpan) preserves user module position in merged AST"

patterns-established:
  - "Two-phase parsing: separate parseProgram calls, AST merge before elaboration"
  - "Source-scoped filenames: each source gets its own filename so line counters are independent"

# Metrics
duration: 7min
completed: 2026-04-01
---

# Phase 47 Plan 01: Prelude Separate Parsing Summary

**Replaced Prelude+user string concatenation with two separate parseProgram calls and AST-level merge, fixing user code line numbers from 174+ down to actual 1-based positions**

## Performance

- **Duration:** 7 min
- **Started:** 2026-03-31T22:47:11Z
- **Completed:** 2026-03-31T22:55:10Z
- **Tasks:** 2
- **Files modified:** 8

## Accomplishments
- Prelude is now parsed under `"<prelude>"` filename, user code under actual inputPath
- User code line numbers start at 1 (was off by 173 due to Prelude prepended as text)
- 7 error test .flt files updated with correct line numbers
- 216/217 E2E tests pass (43-02 has pre-existing FunLang build issue, unrelated)

## Task Commits

Each task was committed atomically:

1. **Task 1: Separate Prelude and user code parsing in Program.fs** - `431d9a5` (feat)
2. **Task 2: Update 7 error test files with corrected line numbers** - `68dd547` (fix)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/FunLangCompiler.Cli/Program.fs` - Replaced combinedSrc with two-phase parseProgram calls and AST merge
- `tests/compiler/44-01-error-location-unbound.flt` - 175:17 -> 2:17
- `tests/compiler/44-02-error-location-pattern.flt` - 174:4 -> 1:4 (including StartLine/EndLine)
- `tests/compiler/44-03-error-location-field.flt` - 176:17 -> 3:17
- `tests/compiler/46-01-record-type-hint.flt` - 175:6 -> 2:6
- `tests/compiler/46-02-field-hint.flt` - 176:17 -> 3:17
- `tests/compiler/46-03-function-hint.flt` - 175:27 -> 2:27
- `tests/compiler/46-04-error-category-elab.flt` - 174:17 -> 1:17

## Decisions Made
- Used `userSpan` (not `Ast.unknownSpan`) for the merged Module span to preserve user code position
- `preludeDecls @ userDecls` ordering ensures Prelude definitions available for user code elaboration
- Pattern match on all three Module variants (Module/NamedModule/NamespacedModule) for robust AST extraction

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

Test 43-02-return-type-annotation.flt fails due to a pre-existing FunLang apphost build issue (unrelated to our changes). This was already failing before plan execution.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Separate parsing foundation is in place for Phase 48+
- Future error test additions should use 1-based line numbers for user code
- Prelude-internal errors will show `<prelude>:line:col` path (ready for Phase 48 prelude error handling if needed)

---
*Phase: 47-prelude-separate-parsing*
*Completed: 2026-04-01*
