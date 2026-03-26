---
phase: 16-environment-infrastructure
plan: "02"
subsystem: compiler
tags: [matchcompiler, pattern-matching, adt, records, decision-tree, fsharp]

# Dependency graph
requires:
  - phase: 11-pattern-matching
    provides: MatchCompiler with ConsCtor/NilCtor/TupleCtor and decision tree compilation
  - phase: 13-pattern-matching-extensions
    provides: OrPat expansion in Elaboration.fs before MatchCompiler
provides:
  - CtorTag DU with AdtCtor(name, tag, arity) and RecordCtor(fields) variants
  - ctorArity returning correct values for new variants
  - desugarPattern dispatching ConstructorPat to CtorTest(AdtCtor(...), subPats)
  - desugarPattern dispatching RecordPat to CtorTest(RecordCtor sortedFieldNames, subPats)
affects:
  - 16-03 (if exists)
  - 17-adt-codegen
  - 18-records-codegen

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "AdtCtor tag=0 placeholder for Phase 16; Phase 17 will fill real tag values"
    - "RecordCtor uses sorted field names as canonical identity for structural equality"
    - "Elaboration.fs failwith placeholders keep exhaustive-match compilation clean across phases"

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/MatchCompiler.fs
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "AdtCtor carries name (for structural equality in splitClauses), tag placeholder 0 (Phase 17 fills real tag), and arity"
  - "RecordCtor fields list is sorted by name for canonical ordering — identity is the sorted field name list"
  - "Elaboration.fs gets failwith Phase 17/18 placeholder arms rather than leaving non-exhaustive match holes"

patterns-established:
  - "Phase-layered implementation: Phase 16 builds dispatch structure, Phase 17/18 wire real IR emission"
  - "Placeholder failwith messages include phase number for discoverability"

# Metrics
duration: 3min
completed: 2026-03-26
---

# Phase 16 Plan 02: MatchCompiler ADT and Record Pattern Dispatch Summary

**AdtCtor and RecordCtor variants added to CtorTag DU with correct arity; desugarPattern now dispatches ConstructorPat and RecordPat to decision-tree CtorTest nodes instead of failwith stubs**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-03-26T23:18:04Z
- **Completed:** 2026-03-26T23:21:00Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- Extended CtorTag DU with `AdtCtor(name: string * tag: int * arity: int)` and `RecordCtor(fields: string list)` variants
- Updated `ctorArity` to return correct arity for both new variants (arity field for AdtCtor, `List.length fields` for RecordCtor)
- Replaced `ConstructorPat` failwith stub with CtorTest(AdtCtor(name, 0, arity), subPats) dispatch
- Replaced `RecordPat` failwith stub with CtorTest(RecordCtor sortedFieldNames, subPats) dispatch with canonical field ordering
- Added Phase 17/18 placeholder arms in Elaboration.fs to keep exhaustive-match compilation clean
- All 45 E2E tests pass (REG-01 gate maintained)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add AdtCtor/RecordCtor to CtorTag and update ctorArity** - `1a2ada0` (feat)
2. **Task 2: Implement desugarPattern for ConstructorPat and RecordPat** - `921757c` (feat)

## Files Created/Modified

- `src/LangBackend.Compiler/MatchCompiler.fs` - Added AdtCtor/RecordCtor variants, updated ctorArity, implemented desugarPattern arms
- `src/LangBackend.Compiler/Elaboration.fs` - Added failwith placeholder arms in scrutineeTypeForTag and emitCtorTest for new CtorTag variants

## Decisions Made

- `AdtCtor` uses `tag = 0` as placeholder in Phase 16; Phase 17 will supply real integer tags from the ADT layout table
- `RecordCtor` identity is the sorted field name list — sorting ensures canonical representation regardless of source order
- Elaboration.fs gets explicit `failwith "Phase 17: ..."` / `failwith "Phase 18: ..."` rather than relying on non-exhaustive match warnings, making the boundary visible

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- MatchCompiler dispatch structure is complete for ADT and Record patterns
- Phase 17 (ADT codegen) can now implement IR emission by replacing the `failwith "Phase 17: AdtCtor not yet implemented"` arms in Elaboration.fs with real tag-load and branch logic
- Phase 18 (Records codegen) similarly targets the `failwith "Phase 18: RecordCtor not yet implemented"` arms
- The `tag = 0` placeholder in AdtCtor will need replacement with real tag lookup in Phase 17 — tracked as pending concern

---
*Phase: 16-environment-infrastructure*
*Completed: 2026-03-26*
