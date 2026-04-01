---
phase: 18-records
plan: 02
subsystem: compiler
tags: [fsharp, mlir, elaboration, records, pattern-matching, decision-tree, slot-ordering]

# Dependency graph
requires:
  - phase: 18-records-01
    provides: RecordEnv (Map<typeName, Map<fieldName, slotIdx>>) + flat record layout (GC_malloc, LlvmGEPLinearOp)
  - phase: 17-adt-construction-pattern-matching
    provides: emitDecisionTree Switch dispatch, ensureConsFieldTypes/ensureAdtFieldTypes pattern, accessorCache
provides:
  - scrutineeTypeForTag RecordCtor -> Ptr
  - emitCtorTest RecordCtor -> unconditional i1=1 (structural match, no tag discriminant)
  - ensureRecordFieldTypes: declaration-order slot remapping for alphabetical argAccs
  - RecordPat wired into preloadOps dispatch in emitDecisionTree Switch
  - E2E tests 18-05 (basic RecordPat) and 18-06 (ordering-sensitive RecordPat)
affects:
  - 19-exceptions (no direct dependency, but completes Phase 18 REC-06 requirement)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ensureRecordFieldTypes: resolve fieldMap via Set superset match in RecordEnv, then remap alphabetical argAccs to declaration-order slot indices using Map.find fieldName fieldMap"
    - "RecordCtor structural match: unconditional i1=1 (no tag prefix, unlike ADT)"
    - "preloadOps dispatch on RecordCtor fields: ensureRecordFieldTypes takes 3 args (fields, scrutAcc, argAccs)"

key-files:
  created:
    - tests/compiler/18-05-record-pat.flt
    - tests/compiler/18-06-record-pat-ordering.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "ensureRecordFieldTypes resolves the record type from RecordEnv by field-set superset match (same strategy as RecordExpr type resolution)"
  - "argAccs are in alphabetical order (MatchCompiler.splitClauses sorts field names); RecordEnv slots use declaration order — ensureRecordFieldTypes bridges this with per-field slot lookup"
  - "Test syntax uses explicit `{ x = xv; y = yv }` form since grammar requires IDENT EQUALS Pattern; shorthand `{ x; y }` not in grammar"

patterns-established:
  - "Record pattern preload: loop over sorted fields list with List.iteri, use Map.find fieldName fieldMap for declaration-order slot index, emit GEP+load directly into accessorCache"

# Metrics
duration: 5min
completed: 2026-03-27
---

# Phase 18 Plan 02: RecordPat Pattern Matching Summary

**RecordCtor decision-tree emission complete: structural match with declaration-order slot remapping via ensureRecordFieldTypes, 57/57 tests passing**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-03-27T04:17:55Z
- **Completed:** 2026-03-27T04:22:22Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- Filled both RecordCtor stubs in Elaboration.fs (`scrutineeTypeForTag` and `emitCtorTest`)
- Implemented `ensureRecordFieldTypes` to correctly remap alphabetical sub-pattern accessors to declaration-order heap slots via RecordEnv
- Wired RecordCtor into preloadOps dispatch in emitDecisionTree Switch
- Added two E2E tests: basic extraction (18-05) and ordering-sensitive test (18-06) — the critical correctness test
- All 57 tests pass, no regressions

## Task Commits

Each task was committed atomically:

1. **Task 1: Fill RecordCtor stubs + add ensureRecordFieldTypes** - `bf2f9be` (feat)
2. **Task 2: Add RecordPat E2E tests including ordering-sensitive test** - `c971e7a` (feat)

**Plan metadata:** (upcoming docs commit)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` - scrutineeTypeForTag RecordCtor, emitCtorTest RecordCtor, ensureRecordFieldTypes, preloadOps dispatch
- `tests/compiler/18-05-record-pat.flt` - Basic RecordPat field extraction (exits 4)
- `tests/compiler/18-06-record-pat-ordering.flt` - Declaration order != alphabetical (exits 20 for x from Pair{y=10,x=20})

## Decisions Made

- `ensureRecordFieldTypes` resolves the record type from RecordEnv by checking whether the pattern's field set is a subset of each entry's field set — same field-set equality strategy as RecordExpr type resolution in 18-01.
- The alphabetical vs. declaration-order mismatch is the critical correctness concern (documented in STATE.md blockers). `ensureRecordFieldTypes` bridges this by using `Map.find fieldName fieldMap` (declaration-order slot) rather than the `i` loop index (alphabetical order).
- Test syntax uses explicit `{ field = var }` form; grammar does not support shorthand `{ field }` variable binding.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Test syntax corrected from shorthand to explicit form**

- **Found during:** Task 2 (E2E test creation)
- **Issue:** Plan specified `{ x = 3; y }` and `{ x; y }` shorthand syntax but the FunLang grammar only supports `IDENT EQUALS Pattern` — shorthand form produces parse error
- **Fix:** Updated both test files to use explicit `{ x = 3; y = yv }` and `{ x = xv; y = yv }` syntax
- **Files modified:** tests/compiler/18-05-record-pat.flt, tests/compiler/18-06-record-pat-ordering.flt
- **Verification:** Both tests pass with corrected syntax; 18-06 exits with 20 confirming correct slot mapping
- **Committed in:** c971e7a (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (grammar syntax correction)
**Impact on plan:** Tests verify identical semantics as specified. No scope creep.

## Issues Encountered

- Grammar only supports `field = pattern` record pattern syntax (no shorthand). Discovered during test execution (parse error on shorthand). Fixed immediately.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 18 (Records) complete: RecordExpr, FieldAccess, RecordUpdate, SetField, RecordPat all implemented (18-01 + 18-02)
- 57/57 tests pass
- Ready for Phase 19 (Exception Handling)
- Outstanding concern: validate `static inline setjmp` + clang `-O2` interaction with MLIR-emitted LLVM IR before full TryWith codegen (per STATE.md blockers)

---
*Phase: 18-records*
*Completed: 2026-03-27*
