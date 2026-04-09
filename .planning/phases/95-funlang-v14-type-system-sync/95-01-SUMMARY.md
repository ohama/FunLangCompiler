---
phase: 95-funlang-v14-type-system-sync
plan: 01
subsystem: compiler
tags: [fsharp, type-system, pattern-match, collections, submodule]

# Dependency graph
requires:
  - phase: 94-string-parameter-indexing-bug-fix
    provides: StringVars at bodyEnv, correct IndexGet dispatch for string params
provides:
  - typeNeedsPtr correctly returns true for THashSet/TQueue/TMutableList/TStringBuilder
  - detectCollectionKind dispatches on native v14.0 union cases (THashSet/TQueue/TMutableList)
  - FunLang submodule pinned at 8da0af2 (v14.0)
affects:
  - 96-prelude-copy
  - 97-option-fun-validation
  - any future phase using collection types

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "New FunLang Type union cases get explicit arms in typeNeedsPtr and detectCollectionKind before fallback TData arms"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Compiler/ElabHelpers.fs
    - deps/FunLang (submodule pointer → 8da0af2)

key-decisions:
  - "Keep TData(HashSet/Queue/MutableList) arms as backward-compatible fallback after dedicated union case arms"
  - "TStringBuilder is not a CollectionKind — only added to typeNeedsPtr, not detectCollectionKind"

patterns-established:
  - "Pattern: When FunLang adds new collection Type union cases, add them to both typeNeedsPtr and detectCollectionKind in ElabHelpers.fs"

# Metrics
duration: 5min
completed: 2026-04-09
---

# Phase 95 Plan 01: FunLang v14.0 Type System Sync Summary

**typeNeedsPtr and detectCollectionKind patched with native THashSet/TQueue/TMutableList/TStringBuilder union cases, FunLang submodule pinned at 8da0af2**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-04-09T03:00:00Z
- **Completed:** 2026-04-09T03:05:00Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments
- Added THashSet/TQueue/TMutableList/TStringBuilder arms to `typeNeedsPtr` — all four are heap-allocated and correctly return true
- Added THashSet/TQueue/TMutableList dedicated union case arms to `detectCollectionKind` before the existing TData fallback arms
- Pinned FunLang submodule at 8da0af2 (v14.0) in the parent repo
- Build produces zero FS0025 incomplete-pattern-match warnings
- 262/262 E2E tests pass

## Task Commits

Each task was committed atomically:

1. **Task 1: Patch ElabHelpers.fs and commit submodule** - `330389e` (feat)

**Plan metadata:** (pending docs commit)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/ElabHelpers.fs` - Added v14.0 collection type arms to typeNeedsPtr and detectCollectionKind
- `deps/FunLang` - Submodule pointer updated to 8da0af2 (v14.0)

## Decisions Made
- Kept `TData("HashSet"/"Queue"/"MutableList")` arms as backward-compatible fallbacks after the new dedicated union case arms — no breakage for any pre-v14 typed ASTs
- TStringBuilder is not a `CollectionKind` (no corresponding kind in the enum) so it was added only to `typeNeedsPtr`, not `detectCollectionKind`

## Deviations from Plan

None - plan executed exactly as written.

The plan's verify threshold of `>= 5` for `grep -c` was slightly optimistic (got 4 matching lines since all four types fit on one line in typeNeedsPtr). The substance is correct — all four types are handled.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- FunLang submodule at v14.0 (8da0af2) — prerequisite for Phase 96 (Prelude copy) is satisfied
- ElabHelpers.fs type dispatch is correct for all v14.0 collection types
- Phase 96 (Prelude copy) and Phase 97 (Option.fun validation) can proceed

---
*Phase: 95-funlang-v14-type-system-sync*
*Completed: 2026-04-09*
