---
phase: 57-unknownspan-removal
plan: 01
subsystem: compiler
tags: [elaboration, ast, span, error-reporting, fsharp]

# Dependency graph
requires:
  - phase: 56-prelude-sync
    provides: stable compiler foundation before span cleanup
provides:
  - Zero unknownSpan references in Elaboration.fs and Program.fs
  - Real AST spans on printfn/eprintfn/show/eq desugar sites
  - Real AST spans on closure capture errors
  - Real AST spans on first-class constructor wrapping
  - extractMainExpr accepts moduleSpan parameter
affects: [error-message-quality, diagnostic-reporting, future-span-work]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Bind outer pattern match field (e.g., App last arg) to named span var instead of using unknownSpan"
    - "Pass real module span into extractMainExpr via parameter rather than using global fallback"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs
    - src/FunLangCompiler.Cli/Program.fs

key-decisions:
  - "Bind outer App span (e.g., last _ field) to named var s/appSpan for desugar nodes"
  - "extractMainExpr gains moduleSpan parameter; call site uses Ast.moduleSpanOf ast"
  - "Program.fs parseExpr fallback uses Ast.spanOf expr for both LetDecl and Module spans"

patterns-established:
  - "Pattern: bind outer constructor span field, remove let s = unknownSpan"

# Metrics
duration: 8min
completed: 2026-04-01
---

# Phase 57 Plan 01: unknownSpan Removal (Elaboration + Program) Summary

**11 unknownSpan usages replaced with real AST spans across Elaboration.fs and Program.fs — error messages will now show correct file:line:col instead of `<unknown>:0:0:0`**

## Performance

- **Duration:** 8 min
- **Started:** 2026-04-01T09:40:27Z
- **Completed:** 2026-04-01T09:48:21Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- Replaced all 10 unknownSpan occurrences in Elaboration.fs with real spans (SPAN-01 through SPAN-07)
- Replaced 1 unknownSpan occurrence in Program.fs with Ast.spanOf expr (SPAN-08)
- Extended extractMainExpr signature with moduleSpan parameter and updated call site
- All 230 E2E tests pass after changes

## Task Commits

Each task was committed atomically:

1. **Task 1: Replace 10 unknownSpan in Elaboration.fs** - `804ac1f` (feat)
2. **Task 2: Replace unknownSpan in Program.fs** - `1c5b860` (feat)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` - 10 unknownSpan replaced: printfn/eprintfn desugar (SPAN-01,02), show/eq string literal builtins (SPAN-03,04), closure capture failure (SPAN-05), first-class constructor wrapping (SPAN-06), extractMainExpr signature + call site (SPAN-07)
- `src/FunLangCompiler.Cli/Program.fs` - 1 unknownSpan replaced: parseExpr fallback now uses Ast.spanOf expr for both LetDecl and Module spans (SPAN-08)

## Decisions Made

- Bound outer App/Constructor/Let span field to named variable (s, appSpan, ctorSpan, letSpan) and removed the `let s = Ast.unknownSpan` line — minimal, clean change per pattern match arm
- extractMainExpr receives moduleSpan as explicit parameter instead of reading from a global; call site `elaborateProgram` already has the Ast.Module value, so `Ast.moduleSpanOf ast` is the natural span
- Program.fs: used `Ast.spanOf expr` (already exposed in Ast module) rather than inventing a new API

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

The full-suite test run showed 2 transient failures on first run due to a concurrent `apphost` copy race condition in the .NET build system (MSB3030). Running those tests individually confirmed they pass. Second full-suite run: 230/230 passed.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 57 Plan 01 complete; Elaboration.fs and Program.fs have zero unknownSpan references
- Plan 57-02 can proceed to address any remaining span quality improvements in other files
- No blockers

---
*Phase: 57-unknownspan-removal*
*Completed: 2026-04-01*
