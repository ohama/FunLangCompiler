# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-29)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v9.0 — Phase 32: Array Builtins (next)

## Current Position

Phase: 31 of 35 (String/Char/IO Builtins) — VERIFIED ✓
Plan: 3 of 3 in current phase (all done)
Status: Phase 31 verified, ready for Phase 32
Last activity: 2026-03-29 — Phase 31 verified (11/11 must-haves, 155 E2E tests)

Progress: [██████████████████░░] 89% (31/35 phases complete, 0 plans in Phase 32 started)

## Performance Metrics

**Velocity:**
- Total plans completed: 58 (v1.0–v8.0)
- Average duration: ~10 min/plan (estimated)
- Total execution time: ~9.7 hours

**By Phase (v9.0):**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 31 | TBD | - | - |
| 32 | TBD | - | - |
| 33 | TBD | - | - |
| 34 | TBD | - | - |
| 35 | TBD | - | - |

**Recent Trend:** Stable (v8.0: 2 plans, quick execution)

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table. Recent decisions:

- v9.0 Phase 31-03: eprintfn desugars to @lang_eprintln (two-arg %s case); two-arg App(App(...)) arm must appear before one-arg App(...) arm
- v9.0 Phase 31-02: Char transformer E2E tests use exit-code comparison (`result = char_to_int 'X'`) since compiler has no %c format printing
- v9.0 Phase 31-01: E2E tests for bool-returning builtins use `to_string(bool)` pattern (not `if/then/else`) to avoid the two-sequential-if MLIR empty-block limitation
- v9.0 Phase 31-01: LangCons-using C functions must be placed after LangCons typedef in lang_runtime.c
- v8.0: ForInExpr uses compile-time ArrayVars dispatch (not runtime GC_size check)
- v5.0: LangClosureFn callback ABI — C runtime calls MLIR closures via fn_ptr
- v5.0: C runtime hashtable with chained buckets + murmurhash3

### Pending Todos

None.

### Blockers/Concerns

- LANG-03 (for-in pattern destructuring) depends on ForInExpr already working (done in v8.0 Phase 30)
- LANG-04 (new collection for-in) needs Phase 33 collection types to exist first
- PRE-05 list extension module is large (12 functions); may need own plan
- Two-sequential-if MLIR limitation: two `if` expressions in sequence produce invalid MLIR (empty entry block). E2E tests must avoid this pattern. Not blocking for current phases.
- ForInExpr `var: Pattern` mismatch was pre-existing bug (LangThree AST updated but LangBackend was not). Fixed in 31-01.

## Session Continuity

Last session: 2026-03-29T13:26:33Z
Stopped at: Completed 31-03-PLAN.md (eprintfn builtin, Phase 31 complete — 155 tests pass)
Resume file: None
