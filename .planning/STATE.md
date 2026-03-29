# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-29)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v9.0 — Phase 35: Prelude Modules (next — final phase)

## Current Position

Phase: 34 of 35 (Language Constructs) — VERIFIED ✓
Plan: 3 of 3 in current phase (all done)
Status: Phase 34 verified, ready for Phase 35
Last activity: 2026-03-30 — Phase 34 verified (4/4 must-haves, 173 E2E tests)

Progress: [████████████████████] 97% (34/35 phases complete, 0 plans in Phase 35 started)

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

- v9.0 Phase 32-03: array_sort uses LlvmCallVoidOp (void C return); array_of_seq delegates to lang_array_of_list at C level
- v9.0 Phase 32-03: externalFuncs appears twice in Elaboration.fs (elaborateModule + elaborateProgram) — both must be kept in sync
- v9.0 Phase 32-02: list_sort_by uses insertion sort with parallel int64_t arrays + LangClosureFn key extractor
- v9.0 Phase 32-02: list_of_seq is identity void* cast — no C logic, just type coercion at elaboration time
- v9.0 Phase 33-02: queue_dequeue is two-arg curried App(App(Var, q), unit) — unit elaborated and discarded; C function takes only queue pointer
- v9.0 Phase 33-02: mutablelist_set (three-arg) BEFORE mutablelist_get (two-arg) in Elaboration.fs — same rule as hashtable_set before hashtable_get
- v9.0 Phase 34-03: for-in mutable capture in closures (`sum <- sum + x`) segfaults (pre-existing bug); E2E tests use single-element iteration for non-deterministic collections
- v9.0 Phase 34-03: I64->Ptr coercion for TuplePat ForInExpr placed in LetPat(TuplePat) arm (generalizes to any tuple bind from I64 source)
- v9.0 Phase 34-03: lang_for_in_* prototypes placed after Phase 33 typedefs in lang_runtime.h (types defined later in file)
- v9.0 Phase 34-02: ListCompExpr var is a string (not Pattern); lang_list_comp uses LangCons* for both input and result; E2E tests use per-element for-in (not to_string on list)
- v9.0 Phase 34-01: lang_string_slice uses stop<0 sentinel for open-ended slices (not a separate function); StringSliceExpr arm placed before failwithf catch-all
- v9.0 Phase 33-02: LangMutableList grow uses GC_malloc+memcpy (not realloc); LangQueue uses GC_malloc nodes with head/tail/count
- v9.0 Phase 33-01: Struct typedefs defined only in lang_runtime.h — never redefined in lang_runtime.c (prevents clang redefinition errors)
- v9.0 Phase 33-01: hashset_add uses LlvmCallOp returning I64 (not LlvmCallVoidOp) — matches LangThree bool return
- v9.0 Phase 33-01: StringBuilder buffer growth uses GC_malloc + memcpy (not realloc); HashSet reuses lang_ht_hash murmurhash3
- v9.0 Phase 32-01: hashtable_count uses inline GEP at LangHashtable field index 2 (size), no C function needed
- v9.0 Phase 32-01: E2E tests use `println (to_string ...)` — `printfn "%d"` does not exist in elaborator
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

- LANG-03 and LANG-04 COMPLETE (Phase 34-03)
- PRE-05 list extension module is large (12 functions); may need own plan
- Two-sequential-if MLIR limitation: two `if` expressions in sequence produce invalid MLIR (empty entry block). E2E tests must avoid this pattern. Not blocking for current phases.
- Pre-existing bug: for-in closures capturing `let mut` variables via `sum <- sum + x` segfaults (not fixed in v9.0)

## Session Continuity

Last session: 2026-03-29
Stopped at: Completed 34-03-PLAN.md (ForIn collection dispatch + TuplePat — 173 tests pass)
Resume file: None
