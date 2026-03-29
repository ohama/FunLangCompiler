---
phase: 31-string-char-io-builtins
plan: 03
subsystem: compiler
tags: [mlir, eprintfn, stderr, elaboration, builtins, e2e-tests]

# Dependency graph
requires:
  - phase: 31-02
    provides: char builtins + pattern for elaboration arms + externalFuncs
  - phase: 26
    provides: existing lang_eprintln C runtime function + @lang_eprintln externalFuncs entries
provides:
  - eprintfn builtin desugaring (two-arg "%s" case + one-arg literal case) in Elaboration.fs
  - E2E test 31-11-eprintfn.flt with stderr suppression via 2>/dev/null
affects: [FunLexYacc error diagnostics, any phase using eprintfn for stderr output]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "eprintfn desugaring: two-arg %s case calls @lang_eprintln directly; one-arg literal desugars to eprintln"
    - "More-specific-first pattern matching: two-arg App(App(...)) case before one-arg App(...) case"
    - "E2E test stderr suppression: use 2>/dev/null in command to verify output goes to stderr not stdout"

key-files:
  created:
    - tests/compiler/31-11-eprintfn.flt
  modified:
    - src/LangBackend.Compiler/Elaboration.fs

key-decisions:
  - "eprintfn desugars to existing @lang_eprintln (no new C runtime code needed)"
  - "Two-arg case placed before one-arg case in match; avoids one-arg arm consuming the format string of two-arg calls"

patterns-established:
  - "Format-string desugaring: eprintfn two-arg is a special case of eprintln (reuse existing runtime)"
  - "E2E stderr test pattern: 2>/dev/null in command, verify stdout only contains expected non-stderr output"

# Metrics
duration: 9min
completed: 2026-03-29
---

# Phase 31 Plan 03: eprintfn Builtin Summary

**eprintfn builtin desugared to @lang_eprintln via two Elaboration.fs pattern arms; 155/155 tests pass**

## Performance

- **Duration:** 9 min
- **Started:** 2026-03-29T13:17:41Z
- **Completed:** 2026-03-29T13:26:33Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments

- Added two-arg eprintfn arm: `eprintfn "%s" str` desugars to `LlvmCallVoidOp("@lang_eprintln", [strVal])` — no new C runtime functions needed
- Added one-arg eprintfn arm: `eprintfn "literal"` desugars by recursive `elaborateExpr` call to the existing eprintln arm
- Two-arg case placed before one-arg case (more specific first) to ensure correct pattern resolution
- Created E2E test 31-11-eprintfn.flt: verifies `eprintfn "%s" "error msg"` writes to stderr (not stdout) using `2>/dev/null` redirect; stdout only contains "ok" from `println "ok"`
- 155/155 tests pass (154 existing + 1 new)

## Task Commits

Each task was committed atomically:

1. **Task 1: eprintfn elaboration arm + E2E test** - `11a9840` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `src/LangBackend.Compiler/Elaboration.fs` - Added two-arg and one-arg eprintfn pattern match arms after eprintln arm
- `tests/compiler/31-11-eprintfn.flt` - E2E test: eprintfn writes to stderr; stdout clean; exit code 0

## Decisions Made

- **eprintfn reuses @lang_eprintln** — no new C runtime function needed. The `%s` format specifier is the identity case (pass argument directly to lang_eprintln). The one-arg literal case desugars to eprintln of the same string.
- **Two-arg before one-arg** — F# pattern matching is top-down; `App(App(...))` must appear before `App(...)` or the one-arg arm would match the outer App of a two-arg call.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 31 complete: all 3 plans done (string builtins, char builtins, eprintfn)
- 155 tests pass; no regressions
- Phase 32 (array builtins) ready to execute
- FunLexYacc lexer can now use `eprintfn "%s"` for stderr diagnostics when compiled through LangBackend

---
*Phase: 31-string-char-io-builtins*
*Completed: 2026-03-29*
