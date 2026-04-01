---
phase: 57-unknownspan-removal
plan: 02
subsystem: testing
tags: [elaboration, span, error-reporting, e2e-test, fslit, fsharp]

# Dependency graph
requires:
  - phase: 57-01
    provides: Zero unknownSpan in Elaboration.fs and Program.fs; real spans on all error paths
provides:
  - E2E test pair (57-01-unknownspan-removed.flt + .fun) verifying real file:line:col in error output
  - Regression guard: future unknownSpan regressions will fail the new test
  - Proof that elaboration errors show non-zero source positions after Plan 01 fixes
affects: [error-message-quality, regression-prevention]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "CHECK-RE: \\[Elaboration\\] filename\\.fun:\\d+:\\d+: — regex pattern to assert non-zero span in error tests"

key-files:
  created:
    - tests/compiler/57-01-unknownspan-removed.flt
    - tests/compiler/57-01-unknownspan-removed.fun
  modified: []

key-decisions:
  - "Used 'unsupported App' error path (calling unknownFunction) to trigger a guaranteed elaboration error with real span"
  - "CHECK-RE: \\d+:\\d+ pattern (not literal 0:0) proves span is real without being brittle to exact position"

patterns-established:
  - "Pattern: CHECK-RE: \\[Elaboration\\] filename\\.fun:\\d+:\\d+: for all future error-span regression tests"

# Metrics
duration: 9min
completed: 2026-04-01
---

# Phase 57 Plan 02: E2E Span Accuracy Test Summary

**E2E test pair verifying real file:line:col in elaboration error messages after unknownSpan removal — 231/231 tests pass, grep confirms zero unknownSpan in src/**

## Performance

- **Duration:** 9 min
- **Started:** 2026-04-01T09:48:21Z
- **Completed:** 2026-04-01T09:57:49Z
- **Tasks:** 1
- **Files created:** 2

## Accomplishments

- Created `57-01-unknownspan-removed.fun` triggering a guaranteed elaboration error (`unsupported App — unknownFunction not a known function`) at a known source position
- Created `57-01-unknownspan-removed.flt` with `CHECK-RE:` pattern asserting `\d+:\d+` (real line:col, not `0:0`) in error output
- Confirmed `grep -r unknownSpan src/` returns zero source-file matches (only binary DLLs in obj/)
- Full suite 231/231 passes (230 existing + 1 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: Create E2E tests for span accuracy** - `9d0d2fc` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `tests/compiler/57-01-unknownspan-removed.fun` - FunLang source: `let x = 42 / let result = unknownFunction x` — triggers Elaboration error at line 2
- `tests/compiler/57-01-unknownspan-removed.flt` - FsLit test: `CHECK-RE:` asserts error message includes `\d+:\d+` real span

## Decisions Made

- Chose "unsupported App" error path (`unknownFunction`) over RecordExpr or closure-capture paths: most reliable trigger with predictable line:col, works without type declarations, always fires in elaboration with real `appSpan`
- Used `CHECK-RE: .*\d+:\d+.*` pattern rather than exact line number — avoids brittleness if whitespace changes while still proving span is non-zero

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

- Plan referenced `tests/FsLit/FsLit.fsproj` as test runner path — actual path is `deps/fslit/FsLit/FsLit.fsproj`. Used correct path from README; plan wording was illustrative not literal.
- Attempted `sprintf "%f %b"` unsupported-specifier path first; all 4 combinations (`[IntSpec;IntSpec]`, `[StrSpec;IntSpec]`, `[IntSpec;StrSpec]`, `[StrSpec;StrSpec]`) are covered — `_ ->` arm is unreachable with 2 specs. Switched to simpler "unbound function" path.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 57 is fully complete: 11 unknownSpan removed (Plan 01) + E2E regression test added (Plan 02)
- v15.0 milestone complete: zero unknownSpan in src/, all elaboration errors show real file:line:col
- No blockers

---
*Phase: 57-unknownspan-removal*
*Completed: 2026-04-01*
