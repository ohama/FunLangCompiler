# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-29)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v9.0 — Phase 31: String/Char/IO Builtins

## Current Position

Phase: 31 of 35 (String/Char/IO Builtins)
Plan: 2 of 3 in current phase
Status: In progress
Last activity: 2026-03-29 — Completed 31-02-PLAN.md (char builtins: is_digit, is_letter, is_upper, is_lower, to_upper, to_lower)

Progress: [██████████████████░░] 87% (30/35 phases + 2 plans in Phase 31)

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

Last session: 2026-03-29T13:16:03Z
Stopped at: Completed 31-02-PLAN.md (char builtins: is_digit, is_letter, is_upper, is_lower, to_upper, to_lower)
Resume file: None
