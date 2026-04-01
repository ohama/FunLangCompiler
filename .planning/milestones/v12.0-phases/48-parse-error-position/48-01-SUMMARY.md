---
phase: 48-parse-error-position
plan: 01
subsystem: compiler
tags: [fsharp, parse-error, position, error-messages, fslit, check-re]

# Dependency graph
requires:
  - phase: 46-context-hints
    provides: unified [Parse]/[Elaboration]/[Compile] error categories established
  - phase: 47-prelude-separate-parsing
    provides: two-phase parseProgram with parseModule + parseExpr fallback
provides:
  - Position-aware parse error messages: file:line:col format in parse errors
  - lastParsedPos mutable tracking last token consumed by parseModule tokenizer
  - CHECK-RE based test expectations for machine-independent path matching
affects: [49, 50, future error message phases]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "lastParsedPos mutable declared before try block for with-handler access (F# scoping rule)"
    - "CHECK-RE directive for path-dependent test expectations"

key-files:
  created: []
  modified:
    - src/FunLangCompiler.Cli/Program.fs
    - tests/compiler/45-01-parse-error-preserved.flt
    - tests/compiler/46-05-error-category-parse.flt

key-decisions:
  - "lastParsedPos must be declared BEFORE try block - F# with-handlers cannot access variables defined inside try"
  - "Use CHECK-RE for path-dependent parse error tests (absolute path varies by machine)"
  - "45-02 test unchanged: parseExpr fallback succeeds on def foo(x)=x input, producing same message as before"

patterns-established:
  - "Position tracking: mutable before try, update in tokenizer closure, use in with handler"
  - "CHECK-RE: \\[Parse\\] .*filename\\.fun:\\d+:\\d+: parse error for path-agnostic matching"

# Metrics
duration: 15min
completed: 2026-04-01
---

# Phase 48 Plan 01: Parse Error Position Summary

**Parse errors now include file:line:col position via lastParsedPos mutable tracking in parseProgram tokenizer closure**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-04-01T00:00:00Z
- **Completed:** 2026-04-01T00:15:00Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- Parse error messages now include file:line:col: `[Parse] file.fun:2:0: parse error`
- lastParsedPos mutable correctly declared before try block (F# scoping requirement)
- CHECK-RE directives used for path-agnostic test matching
- All 217 E2E tests pass

## Task Commits

Each task was committed atomically:

1. **Task 1: Add lastParsedPos tracking to parseProgram** - `4059c3b` (feat)
2. **Task 2: Update parse error test expectations empirically** - `67352d8` (test)

## Files Created/Modified

- `src/FunLangCompiler.Cli/Program.fs` - Added lastParsedPos mutable, tracking in tokenizer, positioned posMsg in with handler
- `tests/compiler/45-01-parse-error-preserved.flt` - Updated to CHECK-RE for file:line:col match
- `tests/compiler/46-05-error-category-parse.flt` - Updated to CHECK-RE for file:line:col match

## Decisions Made

- **lastParsedPos placement:** Must be declared BEFORE `try` block. F# scoping: `with` handler cannot reference variables defined inside `try`. Placed after `let mutable idx = 0`.
- **CHECK-RE for path tests:** 45-01 uses absolute path (machine-specific), so CHECK-RE with filename pattern is used instead of exact match.
- **45-02 unchanged:** Input `def foo(x) = x\ndef 123bar() = 1` has parseExpr succeed on the first declaration, so no positioned error path is taken. The test expectation `[Parse] parse error` remains correct.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The implementation matched the plan specification precisely. The only notable finding was that 45-02's parseExpr fallback succeeds (it parses `def foo(x) = x` as a single expression), so the positioned error path is not triggered for that input - this was already the existing behavior.

## Next Phase Readiness

- Parse error position implemented and all tests green
- Phase 49 (next in v12.0) can build on positioned error infrastructure
- No blockers

---
*Phase: 48-parse-error-position*
*Completed: 2026-04-01*
