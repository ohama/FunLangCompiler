---
phase: 18-records
plan: 01
subsystem: compiler
tags: [fsharp, mlir, elaboration, records, gc_malloc, llvm_gep]

# Dependency graph
requires:
  - phase: 16-environment-infrastructure
    provides: RecordEnv (Map<typeName, Map<fieldName, slotIdx>>) populated by prePassDecls
  - phase: 17-adt-construction-pattern-matching
    provides: elaborateExpr patterns for GC_malloc + LlvmGEPLinearOp + LlvmStoreOp
provides:
  - RecordExpr elaboration (type resolution from RecordEnv, n-slot GC_malloc, field stores)
  - FieldAccess elaboration (slot lookup + GEP + load)
  - RecordUpdate elaboration (new block allocation, field copy + override)
  - SetField elaboration (in-place store, unit return)
  - freeVars cases for all four record expression types
  - Four E2E tests (18-01 through 18-04)
affects:
  - 18-02 (RecordPat decision tree emission — builds on same RecordEnv and GEP patterns)
  - 19-exceptions (no direct dependency, but uses same elaborateExpr extension pattern)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Flat n-slot record layout: GC_malloc(n*8), field at slot RecordEnv[typeName][fieldName]"
    - "RecordExpr type resolution: field-set equality match in RecordEnv (typeName=None from parser)"
    - "RecordUpdate: srcOps first, overrideOps second, allocOps, then all copyOps (topological order)"
    - "SetField returns unit (i64=0) via fresh ArithConstantOp — not the stored value"
    - "FieldAccess/SetField: search all RecordEnv entries for fieldName slot (field-name uniqueness assumption)"

key-files:
  created:
    - tests/compiler/18-01-record-create.flt
    - tests/compiler/18-02-field-access.flt
    - tests/compiler/18-03-record-update.flt
    - tests/compiler/18-04-setfield.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "Field-name search across all RecordEnv types (no explicit type annotation at FieldAccess/SetField sites) — field names must be unique across types in v4.0"
  - "RecordExpr uses List.item + List.findIndex instead of deprecated List.nth for field value lookup"
  - "RecordUpdate overrideVals built as Map via List.map (fn, v) |> Map.ofList — each field elaborated once"

patterns-established:
  - "Task 1 (freeVars) committed separately from Task 2 (elaborateExpr + tests) for atomic history"

# Metrics
duration: 4min
completed: 2026-03-27
---

# Phase 18 Plan 01: Record Elaboration (Construction, Access, Update, Mutation) Summary

**RecordExpr/FieldAccess/RecordUpdate/SetField elaboration in Elaboration.fs with flat GC_malloc n-slot layout and four E2E tests; 55/55 tests pass**

## Performance

- **Duration:** 4 min
- **Started:** 2026-03-27T04:10:46Z
- **Completed:** 2026-03-27T04:15:14Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Added freeVars cases for all four record expression types (RecordExpr, FieldAccess, RecordUpdate, SetField) ensuring correct closure capture
- Implemented four elaborateExpr cases: RecordExpr resolves type from RecordEnv by field-set match, allocates n*8 GC_malloc block, stores fields in declaration-order slots; FieldAccess searches RecordEnv for slot and emits GEP+load; RecordUpdate allocates new block and copies/overrides fields; SetField mutates in-place and returns unit (i64=0)
- Four E2E tests pass: 18-01 (p.x=3), 18-02 (p.y=4), 18-03 (p2.x=3 after update), 18-04 (r.v=42 after SetField)
- Full regression: 55/55 tests pass (51 existing + 4 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add freeVars cases for RecordExpr, FieldAccess, RecordUpdate, SetField** - `b5790a6` (feat)
2. **Task 2: Add elaborateExpr cases + E2E tests** - `aa18d0e` (feat)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - Added freeVars cases (Task 1) and four elaborateExpr cases (Task 2)
- `tests/compiler/18-01-record-create.flt` - E2E: record construction + p.x access, expects 3
- `tests/compiler/18-02-field-access.flt` - E2E: field access p.y, expects 4
- `tests/compiler/18-03-record-update.flt` - E2E: functional update p2.x copied from p, expects 3
- `tests/compiler/18-04-setfield.flt` - E2E: mutable SetField r.v=42, expects 42

## Decisions Made
- Field-name uniqueness assumed at FieldAccess/SetField sites: search all RecordEnv entries for first matching fieldName. Field names must be unique across record types in v4.0 (no type annotation present at call sites, matching research recommendation).
- List.nth replaced with List.item (List.nth is deprecated in F#) when indexing fieldVals in RecordExpr storeOps.
- RecordUpdate overrideVals: built as `List.map (fn, v) |> Map.ofList` rather than `Map.ofList |> Map.map` (plan's exact version); same semantics, avoids double traversal.

## Deviations from Plan

None - plan executed exactly as written (minor: used List.item instead of deprecated List.nth in RecordExpr storeOps — same behavior, cleaner API).

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- RecordExpr/FieldAccess/RecordUpdate/SetField are complete and tested
- Phase 18 Plan 02 (RecordPat pattern matching) is unblocked: `scrutineeTypeForTag` and `emitCtorTest` stubs for `RecordCtor` remain in Elaboration.fs; `ensureRecordFieldTypes` helper needs implementation per RESEARCH.md Approach A
- Blocker from RESEARCH: `argAccessors.[i]` uses alphabetically-sorted index `i` but heap slot is declaration-order from RecordEnv — `ensureRecordFieldTypes` must remap these via declaration-order lookup

---
*Phase: 18-records*
*Completed: 2026-03-27*
