---
phase: 51-ast-structure-sync
plan: 01
subsystem: compiler
tags: [fsharp, ast, pattern-match, elaboration, langthree]

# Dependency graph
requires:
  - phase: 50-error-format
    provides: working Elaboration.fs baseline
provides:
  - Elaboration.fs compiles against updated LangThree AST with 5-field TypeDecl
  - Explicit skip arms for TypeClassDecl, InstanceDecl, DerivingDecl in prePassDecls
affects:
  - 52-typeclass-elaboration
  - 53-end-to-end-typeclass

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Explicit skip arms pattern: new Decl cases get explicit | _ -> () arms before the wildcard, with a comment pointing to the phase that will implement them"

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "TypeDecl 5th field is Span (not deriving: the order is name, typeParams, ctors, deriving, span)"
  - "TypeClassDecl/InstanceDecl/DerivingDecl explicitly skipped with comment referencing Phase 52"

patterns-established:
  - "New Decl variants: add explicit skip arm with Phase comment rather than relying on wildcard"

# Metrics
duration: 5min
completed: 2026-04-01
---

# Phase 51 Plan 01: AST Structure Sync Summary

**TypeDecl pattern updated to 5-field match and TypeClassDecl/InstanceDecl/DerivingDecl explicit skip arms added — build unblocked against LangThree v12.0 AST**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-04-01T00:00:00Z
- **Completed:** 2026-04-01T00:05:00Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments

- Updated `TypeDecl(_, _, ctors, _)` to `TypeDecl(_, _, ctors, _, _)` in `prePassDecls`
- Added explicit `TypeClassDecl _`, `InstanceDecl _`, `DerivingDecl _` skip arms with Phase 52 comments
- `dotnet build` succeeds with 0 errors and 0 warnings
- E2E tests verified: 35-08-list-tryfind-choose (output matches), 46-01-record-type-hint (error message matches)

## Task Commits

Each task was committed atomically:

1. **Task 1: Update TypeDecl pattern and add explicit Decl arms** - `414e407` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/LangBackend.Compiler/Elaboration.fs` - Updated TypeDecl pattern match (line 4073) and added 3 explicit skip arms (lines 4101-4103)

## Decisions Made

- Explicit skip arms reference "Phase 52: typeclasses handled in elaborateTypeclasses" as the future implementation point — keeps intent clear for the next phase

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Build is unblocked; Elaboration.fs compiles cleanly against LangThree v12.0 AST
- Phase 52 (typeclass elaboration) can now proceed — `elaborateTypeclasses` function needs to be added
- No blockers

---
*Phase: 51-ast-structure-sync*
*Completed: 2026-04-01*
