---
phase: 50-unboxing-comparison-bug
plan: 01
subsystem: compiler
tags: [fsharp, mlir, elaboration, coercion, ptr, i64, arith.cmpi, list, higher-order]

# Dependency graph
requires:
  - phase: 35-list-prelude
    provides: List.choose, List.filter, List.tryFind implementations in Prelude
provides:
  - Ptr-to-I64 coercion in all four ordinal comparison operators (LessThan, GreaterThan, LessEqual, GreaterEqual)
  - E2E tests verifying List.filter and List.choose with comparison predicates on integer lists
affects:
  - any phase using higher-order comparison predicates on boxed-ptr lambda params

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "coerceToI64 applied before ArithCmpIOp when operand may be Ptr (same pattern as If-branch coercion)"

key-files:
  created:
    - tests/compiler/35-08-list-tryfind-choose.fun
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs
    - tests/compiler/35-08-list-tryfind-choose.flt

key-decisions:
  - "Coerce both operands (lv and rv) even when only lv is Ptr — symmetric coercion is safe and simpler"
  - "Op ordering is lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp] — coercion after both operands elaborated"
  - "Equal/NotEqual cases untouched — they handle Ptr via strcmp for string comparison"

patterns-established:
  - "Ordinal comparison fix pattern: call coerceToI64 on both sides before ArithCmpIOp"

# Metrics
duration: 19min
completed: 2026-04-01
---

# Phase 50 Plan 01: Unboxing Comparison Bug Summary

**Ptr-to-I64 coercion added to all four ordinal comparison operators (>, <, >=, <=), fixing arith.cmpi type mismatch when lambda params are boxed as Ptr by isPtrParamBody**

## Performance

- **Duration:** 19 min
- **Started:** 2026-04-01T00:52:55Z
- **Completed:** 2026-04-01T01:11:55Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- Fixed unboxing comparison bug: LessThan, GreaterThan, LessEqual, GreaterEqual now call `coerceToI64 env lv` and `coerceToI64 env rv` before emitting `ArithCmpIOp`
- List.filter with `(fun x -> x > 2)` on integer list now produces `[3;4]` correctly
- List.choose with `(fun x -> if x > 2 then Some x else None)` on integer list now produces `[3;4]` correctly
- All 217 E2E tests pass (zero regressions)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add Ptr-to-I64 coercion in four ordinal comparison operators** - `1093c32` (feat)
2. **Task 2: Add E2E tests for comparison predicates on boxed-ptr params** - `cf1041e` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/Elaboration.fs` - LessThan/GreaterThan/LessEqual/GreaterEqual cases updated with coerceToI64 before ArithCmpIOp
- `tests/compiler/35-08-list-tryfind-choose.fun` - Appended List.filter and List.choose with x > 2 predicate
- `tests/compiler/35-08-list-tryfind-choose.flt` - Updated expected output: existing 5 lines + 3,4,3,4 + exit 0

## Decisions Made
- Coerce both operands symmetrically even when only lv is Ptr — simpler and safe since `coerceToI64` is a no-op for non-Ptr values
- Op ordering: `lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp]` ensures both operands are elaborated before coercion ops
- Equal/NotEqual left untouched — their Ptr path is intentional (strcmp for string equality)

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The fix was surgical and the coerceToI64 helper already existed in the codebase. The 2 pre-existing E2E failures (17-04, 32-02) are LangThree-related and unrelated to this phase; they were already failing before this change.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Phase 50 complete. v12.0 all 4 phases done.
- List.choose/filter with comparison predicates now work correctly on integer lists
- No known blockers for future phases

---
*Phase: 50-unboxing-comparison-bug*
*Completed: 2026-04-01*
