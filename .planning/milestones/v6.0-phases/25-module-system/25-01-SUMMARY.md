---
phase: 25-module-system
plan: 01
subsystem: compiler
tags: [fsharp, elaboration, module-system, flattenDecls, prePassDecls, LetPatDecl, OpenDecl, NamespaceDecl]

# Dependency graph
requires:
  - phase: 24-array-higher-order
    provides: Elaboration.fs foundation with elaborateProgram entry point

provides:
  - prePassDecls recursive with shared exnCounter for cross-module exception tag safety
  - flattenDecls helper that collapses ModuleDecl/NamespaceDecl into flat Decl list
  - LetPatDecl supported in extractMainExpr (tuple destructuring at module level)
  - OpenDecl and NamespaceDecl are no-ops (filtered by wildcard in build)
  - 5 E2E tests covering all module scenarios (97 total)

affects: [25-02-qualified-names, future module-related phases]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Module flattening at Elaboration boundary: all module nesting collapsed before elaboration; no IR changes needed"
    - "Shared exnCounter ref threaded recursively through prePassDecls to prevent exception tag collisions across module boundaries"

key-files:
  created:
    - tests/compiler/25-01-module-basic.flt
    - tests/compiler/25-02-module-letpat.flt
    - tests/compiler/25-03-module-open.flt
    - tests/compiler/25-04-module-namespace.flt
    - tests/compiler/25-05-module-exn.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "flattenDecls defined before extractMainExpr so it can be called at module entry point"
  - "prePassDecls made recursive (let rec) with exnCounter: int ref parameter; elaborateProgram passes ref 0"
  - "LetPatDecl in extractMainExpr maps to LetPat expression node which already has full support in elaborateExpr"
  - "OpenDecl filtered by wildcard in build (no-op); backend never needs to resolve module paths"

patterns-established:
  - "Module system at Elaboration layer: flatten first, then elaborate as if flat"
  - "Shared exnCounter: thread as ref through recursive prePassDecls rather than returning/merging counter state"

# Metrics
duration: 11min
completed: 2026-03-27
---

# Phase 25 Plan 01: Module System Foundation Summary

**AST flattening of ModuleDecl/NamespaceDecl in Elaboration.fs with shared exnCounter for exception tag safety and LetPatDecl support, enabling module syntax to compile without IR changes**

## Performance

- **Duration:** 11 min
- **Started:** 2026-03-27T20:39:25Z
- **Completed:** 2026-03-27T20:50:25Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- Made `prePassDecls` recursive with shared `exnCounter: int ref` parameter so exception tags assigned inside modules do not collide with top-level exception tags
- Added `flattenDecls` helper that recursively expands `ModuleDecl`/`NamespaceDecl` into a single flat `Decl list`, called at the top of `extractMainExpr`
- Extended `extractMainExpr` to filter and handle `LetPatDecl` (maps to existing `LetPat` expression node)
- `OpenDecl` remains a no-op (falls through wildcard in `build`)
- Created 5 E2E tests covering basic module, LetPatDecl, open, namespace, and exception collision scenarios

## Task Commits

Each task was committed atomically:

1. **Task 1: prePassDecls recursion + flattenDecls + extractMainExpr changes** - `0a44b12` (feat)
2. **Task 2: E2E tests for module system** - `6d68027` (feat)

**Plan metadata:** (docs commit to follow)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - prePassDecls made recursive with shared exnCounter; flattenDecls added; extractMainExpr updated for LetPatDecl
- `tests/compiler/25-01-module-basic.flt` - basic module with let binding
- `tests/compiler/25-02-module-letpat.flt` - LetPatDecl inside module (tuple destructuring)
- `tests/compiler/25-03-module-open.flt` - open directive (no-op, compiles cleanly)
- `tests/compiler/25-04-module-namespace.flt` - namespace declaration (no-op, compiles cleanly)
- `tests/compiler/25-05-module-exn.flt` - exception in module + top-level (shared exnCounter)

## Decisions Made
- Thread `exnCounter` as `int ref` parameter through recursive `prePassDecls` rather than merging counter state via return values — cleaner and avoids off-by-one issues when merging
- `flattenDecls` placed before `extractMainExpr` in source so it can be called from it; both are `private`
- `LetPatDecl` handled via existing `LetPat` expression node which already has full elaboration support (TuplePat, VarPat, WildcardPat arms all work)
- `OpenDecl` is a backend no-op — qualified name resolution is a future plan (25-02)

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Module flattening foundation in place; elaborateProgram now processes all bindings from nested modules
- Ready for Plan 25-02: qualified name access (M.x desugaring in Elaboration.fs)
- All 97 tests passing (92 pre-existing + 5 new module tests)

---
*Phase: 25-module-system*
*Completed: 2026-03-27*
