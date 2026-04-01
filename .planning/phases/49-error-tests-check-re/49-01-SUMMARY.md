---
phase: 49-error-tests-check-re
plan: 01
subsystem: testing
tags: [fslit, CHECK-RE, error-tests, flt, regex]

# Dependency graph
requires:
  - phase: 48-parse-error-location
    provides: file:line:col in parse errors, enabling line:col flexibility need
  - phase: 46-context-hints
    provides: 7 error test files that needed CHECK-RE conversion
provides:
  - 7 error .flt tests converted from exact-match to CHECK-RE with \d+:\d+ for line:col
  - Tests now robust to Prelude line count changes
affects: [future phases adding Prelude functions, any phase changing error message format]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "CHECK-RE: \\[Category\\] filename\\.fun:\\d+:\\d+: message.* — standard pattern for error location tests"
    - "fslit matches lines 1:1 — CHECK-RE only on lines with CHECK-RE: prefix, rest are exact"

key-files:
  created: []
  modified:
    - tests/compiler/44-01-error-location-unbound.flt
    - tests/compiler/44-02-error-location-pattern.flt
    - tests/compiler/44-03-error-location-field.flt
    - tests/compiler/46-01-record-type-hint.flt
    - tests/compiler/46-02-field-hint.flt
    - tests/compiler/46-03-function-hint.flt
    - tests/compiler/46-04-error-category-elab.flt

key-decisions:
  - "fslit CHECK-RE applies only to lines prefixed with CHECK-RE: — other lines are exact match"
  - "44-02 multi-line output: CHECK-RE first line + exact match continuation lines (not single .* pattern)"
  - "46-03 function hint: .* swallows growing In scope list (Prelude adds more functions over time)"
  - "44-03, 46-01, 46-02: .* swallows Known records / field list suffixes that may evolve"

patterns-established:
  - "Error test pattern: CHECK-RE: \\[Category\\] filename\\.fun:\\d+:\\d+: ErrorType: core message.*"
  - "Multi-line error output: use CHECK-RE for first line, literal exact for continuation lines"

# Metrics
duration: 8min
completed: 2026-04-01
---

# Phase 49 Plan 01: Error Tests CHECK-RE Summary

**7 error test .flt files converted from exact line:col to CHECK-RE `\d+:\d+`, making them robust to Prelude line count changes**

## Performance

- **Duration:** 8 min
- **Started:** 2026-04-01T00:17:00Z
- **Completed:** 2026-04-01T00:25:21Z
- **Tasks:** 2
- **Files modified:** 7

## Accomplishments

- Converted 5 single-line error tests (44-01, 44-03, 46-01, 46-02, 46-04) to use `CHECK-RE:` with `\d+:\d+` for line:col flexibility
- Converted 2 special-case tests (44-02 multi-line, 46-03 long In scope list) to CHECK-RE
- All 217 compiler tests pass with no regressions
- Discovered fslit behavior: CHECK-RE applies per-line only to lines starting with `CHECK-RE:`, making `.*` for multi-line output impossible

## Task Commits

Each task was committed atomically:

1. **Task 1: Convert 5 single-line error tests to CHECK-RE** - `c7f7692` (test)
2. **Task 2: Convert 2 special-case tests to CHECK-RE** - `cc5dcc4` (test)

**Plan metadata:** (pending)

## Files Created/Modified

- `tests/compiler/44-01-error-location-unbound.flt` - CHECK-RE with `\d+:\d+` for unbound variable error
- `tests/compiler/44-02-error-location-pattern.flt` - CHECK-RE first line + exact continuation lines for multi-line AST dump
- `tests/compiler/44-03-error-location-field.flt` - CHECK-RE with `\d+:\d+` + `.*` for FieldAccess error
- `tests/compiler/46-01-record-type-hint.flt` - CHECK-RE with `\d+:\d+` + `.*` for RecordExpr error
- `tests/compiler/46-02-field-hint.flt` - CHECK-RE with `\d+:\d+` + `.*` for FieldAccess error
- `tests/compiler/46-03-function-hint.flt` - CHECK-RE with `\d+:\d+` + `.*` for unsupported App error
- `tests/compiler/46-04-error-category-elab.flt` - CHECK-RE with `\d+:\d+` for Elaboration error

## Decisions Made

- `CHECK-RE:` in fslit is a per-line directive — only lines starting with `CHECK-RE:` are treated as regex, all other lines are exact matches. This means a single `CHECK-RE: ...*` cannot absorb extra actual output lines.
- For 44-02 (6-line output): Used CHECK-RE for first line only; kept continuation lines as exact matches. The AST dump content (FileName, StartLine, etc.) is stable enough for exact match, while line:col on the first line needed flexibility.
- For 46-03 (growing In scope list): `.*` on the first (and only) CHECK-RE line absorbs the entire suffix — this works because fslit output is one line.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed 44-02 fallback pattern: `\}\)` → `})`**
- **Found during:** Task 2 (44-02-error-location-pattern conversion)
- **Issue:** Plan's fallback multi-line pattern had `\}\)` on continuation line, but that line is exact-matched (not CHECK-RE), so `\}` matched literal backslash-brace, not `}`
- **Fix:** Changed closing `\}\)` to literal `})` on the continuation line
- **Files modified:** tests/compiler/44-02-error-location-pattern.flt
- **Verification:** Test passed after fix
- **Committed in:** cc5dcc4 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug in plan's fallback pattern)
**Impact on plan:** Minor correction to plan's regex escaping. No scope creep.

## Issues Encountered

- fslit does not support glob expansion — running `fslit tests/compiler/*.flt` only runs the first file. Full suite validation done by iterating files in a shell loop.

## Next Phase Readiness

- All 7 error tests now robust to Prelude line count changes
- Phase 50 (List.choose fix) can proceed without error test fragility concerns
- 45-02 intentionally kept as exact match (no position by design) — confirmed unchanged

---
*Phase: 49-error-tests-check-re*
*Completed: 2026-04-01*
