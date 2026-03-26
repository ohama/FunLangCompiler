---
phase: 16-environment-infrastructure
plan: 01
subsystem: compiler
tags: [fsharp, elaboration, adt, records, exceptions, elabenv, prepass]

# Dependency graph
requires:
  - phase: 15-range
    provides: all 45 E2E tests passing with elaborateModule entry point
provides:
  - ElabEnv with TypeEnv, RecordEnv, ExnTags fields
  - prePassDecls: scans Decl list and builds type/record/exception maps
  - extractMainExpr: converts LetDecl/LetRecDecl list into nested Expr chain
  - elaborateProgram: new entry point accepting Ast.Module
  - Program.fs using parseModule/elaborateProgram with bare-expression fallback
affects:
  - 16-02 (MatchCompiler.fs pattern compilation extensions)
  - 17-adt-codegen (will consume TypeEnv for constructor tag lookup)
  - 18-records-codegen (will consume RecordEnv for field index lookup)
  - 19-exception-handling (will consume ExnTags for exception tag lookup)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "prePassDecls: declaration pre-pass pattern — scan Decl list, mutate Maps, return tuple"
    - "elaborateProgram as module-level entry point wrapping elaborateModule logic"
    - "parseModule with bare-expression fallback via try/catch in parseProgram"

key-files:
  created: []
  modified:
    - src/LangBackend.Compiler/Elaboration.fs
    - src/LangBackend.Cli/Program.fs

key-decisions:
  - "parseModule fallback: try parseModule, if parse error fall back to parseExpr + synthetic Module wrapper — ensures all 45 bare-expression tests pass without reverting to Parser.start"
  - "ExnTags uses ref int counter (not Map.count) for sequential unique exception tags"
  - "LetDecl wrapping in fallback: bare expr parsed as parseExpr, wrapped in Ast.Module([LetDecl('_', expr)])"
  - "extractMainExpr chains multiple LetDecls into nested Let/LetRec expressions for multi-decl module support"

patterns-established:
  - "Pattern: ElabEnv field propagation — when adding ElabEnv fields, update all 3 explicit construction sites (LetRec inner env, KnownFunc inner env, Lambda inner env)"

# Metrics
duration: 8min
completed: 2026-03-26
---

# Phase 16 Plan 01: Environment Infrastructure Summary

**ElabEnv extended with TypeEnv/RecordEnv/ExnTags; elaborateProgram entry point added; CLI switches to parseModule with bare-expression fallback; all 45 E2E tests pass**

## Performance

- **Duration:** 8 min
- **Started:** 2026-03-26T23:17:35Z
- **Completed:** 2026-03-26T23:26:18Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Added `TypeInfo` record and three new `ElabEnv` fields (`TypeEnv`, `RecordEnv`, `ExnTags`) with `Map.empty` defaults in `emptyEnv`
- Implemented `prePassDecls` scanning `Decl list` for `TypeDecl`, `RecordTypeDecl`, and `ExceptionDecl`, building sequential-tag maps
- Implemented `extractMainExpr` to chain `LetDecl`/`LetRecDecl` into nested `Let`/`LetRec` expressions
- Implemented `elaborateProgram` as the new `Ast.Module`-accepting entry point (same IR logic as `elaborateModule`)
- Switched `Program.fs` to `parseProgram` → `elaborateProgram`, with fallback for bare-expression inputs
- All 45 E2E tests pass unchanged

## Task Commits

Each task was committed atomically:

1. **Task 1: Extend ElabEnv and implement prePassDecls + elaborateProgram** - `3239953` (feat)
2. **Task 2: Switch Program.fs to parseModule/elaborateProgram and verify E2E** - `745fa89` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/LangBackend.Compiler/Elaboration.fs` - TypeInfo type; TypeEnv/RecordEnv/ExnTags in ElabEnv; prePassDecls; extractMainExpr; elaborateProgram
- `src/LangBackend.Cli/Program.fs` - parseProgram with parseModule + bare-expression fallback; main uses elaborateProgram

## Decisions Made

1. **parseModule fallback strategy:** `Parser.parseModule` rejects bare expressions (e.g. `42`, `true`, `if ... then ... else ...`) with a parse error — 40 of 45 existing tests use bare inputs. Rather than reverting to `Parser.start`, `parseProgram` catches parse errors and falls back to `parseExpr` + wraps result in `Ast.Module([LetDecl("_", expr)])`. This keeps `elaborateProgram` as the single elaboration entry point while preserving all 45 tests.

2. **ExnTags counter:** Used a `ref int` counter (not `Map.count`) for exception tag indices, as recommended by the research doc. This prevents tag corruption if the map is pre-seeded or processed multiple times.

3. **ElabEnv propagation:** Three explicit `ElabEnv` construction sites (inside LetRec function, KnownFunc LetRec, and Lambda closure) required manual update to propagate the three new fields. All updated.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] parseModule rejects 40/45 test inputs**
- **Found during:** Task 2 (Program.fs switch and E2E verification)
- **Issue:** `Parser.parseModule` throws a parse error on bare expressions like `42`, `true`, `if...then...else`, `match...`, `let rec...in...`. Empirically verified 40 of 45 tests fail. Research doc said "parseModule produces Module([LetDecl...]) for bare expression inputs" — this is incorrect for the vast majority of inputs.
- **Fix:** `parseProgram` tries `parseModule` first; on any exception, falls back to `parseExpr` and wraps the result in a synthetic `Ast.Module([LetDecl("_", expr, unknownSpan)], unknownSpan)`. The `elaborateProgram` function then processes this uniformly via `extractMainExpr`.
- **Files modified:** `src/LangBackend.Cli/Program.fs`
- **Verification:** All 45 E2E tests pass (Results: 45/45 passed)
- **Committed in:** `745fa89` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Auto-fix was necessary for correctness — the plan's stated expectation about parseModule behavior was empirically incorrect. The fix is architecturally sound: elaborateProgram remains the single entry point, and bare-expression backward compatibility is maintained cleanly.

## Issues Encountered
- `Parser.parseModule` grammar only accepts top-level `Decl` productions — bare expressions (`Expr`) are not valid at module level. The only 5 tests that work with `parseModule` use `let x = ... in ...` (which matches `LET IDENT EQUALS Expr IN Expr → LetDecl("_", Let(...))`). The research document incorrectly assumed `parseModule` would accept bare expressions.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Phase 16-02: `MatchCompiler.fs` may need `CtorTag` extensions — but current inspection shows `AdtCtor` and `RecordCtor` are already implemented there
- Phase 17 (ADT codegen): `TypeEnv` is populated and available via `ElabEnv`; `elaborateProgram` is the entry point
- Phase 18 (Records codegen): `RecordEnv` is populated and available via `ElabEnv`
- Phase 19 (Exception handling): `ExnTags` is populated and available via `ElabEnv`

---
*Phase: 16-environment-infrastructure*
*Completed: 2026-03-26*
