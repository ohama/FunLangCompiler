---
phase: 17-adt-construction-pattern-matching
plan: "01"
subsystem: compiler
tags: [fsharp, elaboration, adt, constructor, pattern-matching, mlir, heap-allocation]

# Dependency graph
requires:
  - phase: 16-environment-infrastructure
    provides: ElabEnv.TypeEnv populated by prePassDecls; AdtCtor placeholder in MatchCompiler; scrutineeTypeForTag/emitCtorTest stubs
provides:
  - Constructor(name, None, _) elaboration: 16-byte GC_malloc block with tag at slot 0, null at slot 1
  - Constructor(name, Some argExpr, _) elaboration: 16-byte block with tag at slot 0, argVal at slot 1
  - freeVars Constructor cases: prevents closure capture bugs
  - scrutineeTypeForTag AdtCtor -> Ptr (replaces failwith stub)
  - emitCtorTest AdtCtor: tag load + ArithCmpIOp comparison (replaces failwith stub)
  - ensureAdtFieldTypes: pre-populates payload field accessor cache for unary ADT constructors
  - 3 new E2E tests: 17-01-nullary-ctor, 17-02-unary-ctor, 17-03-multi-arg-ctor
affects:
  - 17-02-pattern-matching (will use same 16-byte layout for ConstructorPat tests, now working)
  - 18-records-codegen (RecordCtor stubs still remain)
  - 19-exception-handling

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ADT heap block layout: 16-byte GC_malloc, slot 0=i64 tag (LlvmGEPLinearOp), slot 1=payload stored directly"
    - "Store arg directly at slot 1 (not behind extra indirection) — I64 payload loads cleanly; Ptr payload also works"
    - "ensureAdtFieldTypes mirrors ensureConsFieldTypes pattern for payload preloading"

key-files:
  created:
    - tests/compiler/17-01-nullary-ctor.flt
    - tests/compiler/17-02-unary-ctor.flt
    - tests/compiler/17-03-multi-arg-ctor.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

key-decisions:
  - "Store argVal directly at ADT slot 1 (no extra indirection heap block): I64 and Ptr payloads both work; resolveAccessor default I64 load is correct for integer payloads"
  - "LlvmGEPLinearOp for ADT blocks (not LlvmGEPStructOp) — consistent with cons-cell and closure env patterns"
  - "ensureAdtFieldTypes pre-loads payload as I64 by default; resolveAccessorTyped re-loads as Ptr when needed for tuple/string payloads"
  - "Multi-arg constructor Pair(3,4): parser produces Constructor('Pair', Some(Tuple([3;4])), _); elaborating Tuple yields Ptr stored at slot 1 — no special case needed"

patterns-established:
  - "Phase 17 ADT construction pattern: GC_malloc(16) -> GEPLinear(0) store tag -> GEPLinear(1) store payload"
  - "Stub replacement pattern: find failwith 'Phase N:...' in scrutineeTypeForTag and emitCtorTest, replace with real IR emission"

# Metrics
duration: 6min
completed: 2026-03-26
---

# Phase 17 Plan 01: ADT Constructor Elaboration Summary

**GC_malloc 16-byte {i64 tag, payload} ADT blocks emitted for nullary/unary/multi-arg constructors; AdtCtor pattern-match stubs replaced with tag-comparison IR; 48/48 E2E tests pass**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-03-26T23:59:33Z
- **Completed:** 2026-03-26T23:59:33Z (end ~2026-03-27T00:05:23Z)
- **Tasks:** 2
- **Files modified:** 1 modified, 3 created

## Accomplishments

- Added `Constructor(name, None, _)` case to `elaborateExpr`: allocates 16-byte GC block with `i64 tag` at slot 0 (via `LlvmGEPLinearOp`) and `null ptr` at slot 1
- Added `Constructor(name, Some argExpr, _)` case: same 16-byte layout but argVal stored directly at slot 1 (works for both I64 and Ptr values — unary and multi-arg constructors handled uniformly)
- Added `Constructor` cases to `freeVars` to prevent closure capture bugs
- Replaced `scrutineeTypeForTag AdtCtor` failwith stub with `-> Ptr` return
- Replaced `emitCtorTest AdtCtor` failwith stub with full tag load + ArithCmpIOp emission (loads slot 0, compares with TypeEnv tag constant)
- Added `ensureAdtFieldTypes` helper (mirrors `ensureConsFieldTypes`) to pre-load payload accessor cache for arity > 0 AdtCtor
- Updated `Switch` preloadOps to call `ensureAdtFieldTypes` for `AdtCtor(_, _, arity) when arity > 0`
- Created 3 E2E test files verifying nullary (Red), unary (Some 42), and multi-arg (Pair(3,4)) constructors compile and run
- All 48 tests pass (45 existing + 3 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add Constructor cases to elaborateExpr and freeVars** - `49af55f` (feat)
2. **Task 2: Add E2E tests for constructor elaboration** - `ac60941` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/FunLangCompiler.Compiler/Elaboration.fs` - Constructor elaboration cases, freeVars cases, AdtCtor IR emission stubs replaced, ensureAdtFieldTypes added
- `tests/compiler/17-01-nullary-ctor.flt` - E2E test: nullary constructor (Red in Color = Red|Green|Blue)
- `tests/compiler/17-02-unary-ctor.flt` - E2E test: unary constructor (Some 42 in Option = None|Some of int)
- `tests/compiler/17-03-multi-arg-ctor.flt` - E2E test: multi-arg constructor (Pair(3,4) in Pair = Pair of int * int)

## Decisions Made

- **Store arg directly at slot 1 (no extra heap block):** The research proposed two options; the simpler approach — storing the value directly at slot 1 — avoids double indirection. `resolveAccessor` default I64 load correctly retrieves integer payloads; Ptr payloads (tuples, strings) work via `resolveAccessorTyped` re-load. No additional runtime overhead.
- **Multi-arg constructor requires no special case:** `Pair(3, 4)` is parsed as `Constructor("Pair", Some(Tuple([3;4])), _)`. Elaborating the Tuple arg produces a Ptr value, stored directly at slot 1. The unary/multi-arg case collapses naturally.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 17-02 (ConstructorPat pattern matching codegen): `emitCtorTest` now emits real AdtCtor tag comparison IR; `ensureAdtFieldTypes` pre-loads payload fields. The `scrutineeTypeForTag` and `emitCtorTest` stubs that were blocking 17-02 are now live. Phase 17-02 can proceed.
- Phase 18 (Records): `RecordCtor` stubs (`failwith "Phase 18: RecordCtor not yet implemented"`) remain in place — not touched.
- Phase 19 (Exception Handling): unaffected by this plan.

---
*Phase: 17-adt-construction-pattern-matching*
*Completed: 2026-03-26*
