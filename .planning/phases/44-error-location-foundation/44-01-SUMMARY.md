---
phase: 44
plan: 01
subsystem: compiler-errors
tags: [error-messages, source-location, elaboration]
dependency_graph:
  requires: []
  provides: [failWithSpan-helper, source-location-errors]
  affects: [44-02]
tech_stack:
  added: []
  patterns: [Printf.ksprintf-based-error-formatting]
key_files:
  created: []
  modified:
    - src/LangBackend.Compiler/Elaboration.fs
decisions:
  - id: d44-01-01
    decision: "Use inline + ksprintf for failWithSpan to allow polymorphic return type"
    rationale: "Printf.StringFormat<'a, string> constrains return to string; inline allows F# type inference to flow through failwith's polymorphic return"
  - id: d44-01-02
    decision: "Use Ast.unknownSpan for closure capture error (1 of 18 sites)"
    rationale: "Closure capture error is deep inside Let/Lambda pattern; extracting span would require restructuring the pattern match. Can be refined in a future pass."
metrics:
  duration: ~5 min
  completed: 2026-03-31
---

# Phase 44 Plan 01: failWithSpan Helper and Error Site Conversion Summary

**One-liner:** Added failWithSpan helper using Printf.ksprintf and converted all 18 user-facing failwithf sites to include file:line:col source locations in error messages.

## What Was Done

### Task 1: Add failWithSpan helper function
- Added `inline private failWithSpan` between `emptyEnv` and `isArrayExpr`
- Uses `Printf.ksprintf` to support format strings like failwithf
- Outputs errors in `file:line:col: message` format
- Key insight: must be `inline` so F# infers correct polymorphic return type per call site

### Task 2: Convert all 18 user-facing error sites
Converted every user-facing `failwithf` to `failWithSpan` with appropriate span:

| # | Error Message | Span Source |
|---|---------------|-------------|
| 1 | unbound variable | `Var(name, span)` |
| 2 | unbound mutable variable | `Assign(name, valExpr, span)` |
| 3 | closure capture not found | `Ast.unknownSpan` |
| 4 | ConsPat head must be VarPat | `Ast.patternSpanOf hPat` |
| 5 | ConsPat tail must be VarPat | `Ast.patternSpanOf tPat` |
| 6 | TuplePat sub-pattern not supported | `Ast.patternSpanOf subPat` |
| 7 | pattern not supported in v2 | `Ast.patternSpanOf pat` |
| 8 | unsupported sub-pattern in TuplePat | `Ast.patternSpanOf pat` |
| 9 | sprintf unsupported 2-arg specifier | outer App span |
| 10 | unsupported App (not known function) | `appSpan` from App pattern |
| 11 | unsupported App (unsupported type) | `appSpan` from App pattern |
| 12 | ensureRecordFieldTypes | `matchSpan` from Match pattern |
| 13 | RecordExpr cannot resolve | `recSpan` from RecordExpr pattern |
| 14 | FieldAccess unknown field | `faSpan` from FieldAccess pattern |
| 15 | RecordUpdate cannot resolve | `ruSpan` from RecordUpdate pattern |
| 16 | SetField unknown field | `sfSpan` from SetField pattern |
| 17 | ensureRecordFieldTypes2 | `trySpan` from TryWith pattern |
| 18 | unsupported expression | `Ast.spanOf expr` |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] failWithSpan type signature needed `inline`**
- **Found during:** Task 1 verification
- **Issue:** `Printf.StringFormat<'a, string>` constrains return type to `string`, causing type errors at call sites that need non-string returns
- **Fix:** Changed to `inline private failWithSpan` with inferred format type
- **Files modified:** Elaboration.fs
- **Commit:** 0218711

## Verification

- Build: succeeded (0 errors, 0 warnings)
- Remaining `failwithf` calls: 0 (only comment reference in helper docstring)
- `failWithSpan` count: 19 (1 definition + 18 call sites)
- E2E tests: 207/207 passed

## Next Phase Readiness

Plan 44-02 can proceed. The failWithSpan helper is available and all error sites are converted. E2E test suite validates no regressions.
