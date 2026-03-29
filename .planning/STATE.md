# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-29)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v9.0 — MILESTONE COMPLETE

## Current Position

Phase: 35 of 35 (Prelude Modules) — COMPLETE ✓
Plan: 3 of 3 in current phase (all done)
Status: All phases complete, 183 E2E tests pass
Last activity: 2026-03-30 — Phase 35 complete (all 3 plans done)

Progress: [████████████████████] 100% (35/35 phases complete)

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
- v9.0 Phase 35-02: Module name collision (Option.map vs List.map) — both produce flat @map in MLIR after flattenDecls; tests use bare `type Option 'a = ...` instead of inlining full Option module alongside List module
- v9.0 Phase 35-02: coerceToPtrArg must be applied to ALL array/list builtin call sites — collection args captured in closure wrappers arrive as I64 (ptrtoint)
- v9.0 Phase 35-02: Accessor cache snapshot/restore in emitDecisionTree Switch — prevents MLIR operand dominance violations across ifMatch/ifNoMatch branches
- v9.0 Phase 35-02: LetRec preReturnType = match body with Lambda _ -> Ptr | _ -> I64 (predict before seeing actual return)
- v9.0 Phase 35-02: LetRec KnownFuncs = Map.add name sig_ env.KnownFuncs (not Map.ofList — must include outer env)
- v9.0 Phase 35-01: Bool values from module-wrapped builtins return I64 (1/0) not I1; to_string calls @lang_to_string_int → "1"/"0"
- v9.0 Phase 35-01: E2E module tests use top-level `let _ = expr` (no `in` chaining) — parseModule doesn't accept `in` at top level
- v9.0 Phase 35-01: Hashtable tests use integer keys (not strings); C hashtable uses int64_t key ABI
- v9.0 Phase 35-01: closure captures filter fix — two-param Let-Lambda-Lambda arm now filters by env.Vars; coerceToPtrArg/coerceToI64 helpers for closure ABI
- v9.0 Phase 31-03: eprintfn desugars to @lang_eprintln (two-arg %s case); two-arg App(App(...)) arm must appear before one-arg App(...) arm
- v9.0 Phase 31-02: Char transformer E2E tests use exit-code comparison (`result = char_to_int 'X'`) since compiler has no %c format printing
- v9.0 Phase 31-01: E2E tests for bool-returning builtins use `to_string(bool)` pattern (not `if/then/else`) to avoid the two-sequential-if MLIR empty-block limitation
- v9.0 Phase 31-01: LangCons-using C functions must be placed after LangCons typedef in lang_runtime.c
- v8.0: ForInExpr uses compile-time ArrayVars dispatch (not runtime GC_size check)
- v5.0: LangClosureFn callback ABI — C runtime calls MLIR closures via fn_ptr
- v5.0: C runtime hashtable with chained buckets + murmurhash3

### Pending Todos

None.

### Phase 35 Progress Notes

- 35-01 complete: String/Hashtable/StringBuilder/Char prelude .fun files + 4 E2E tests
- 35-02 complete: Option/Result/List/Array prelude .fun files + 5 E2E tests (35-05 through 35-09); 8 compiler fixes; 182 passing
- 35-03 complete: CLI prelude auto-loading (input-file-dir walk-up); module-qualified naming in Elaboration; 183 passing

### Blockers/Concerns

- LANG-03 and LANG-04 COMPLETE (Phase 34-03)
- Two-sequential-if MLIR limitation: two `if` expressions in sequence produce invalid MLIR (empty entry block). E2E tests must avoid this pattern. Not blocking for current phases.
- Pre-existing bug: for-in closures capturing `let mut` variables via `sum <- sum + x` segfaults (not fixed in v9.0)
- Phase 35: Bool values from module-wrapped builtins return as I64 (1/0) not I1 (true/false); to_string prints "1"/"0" (workaround: use `<> 0`)
- Phase 35: Hashtable string keys crash — C hashtable uses int64_t key ABI; tests must use integer keys
- Phase 35-03: RESOLVED — Module-qualified naming (Option_map, String_endsWith) eliminates MLIR collision; 183/183 tests pass.
- Phase 35: take/drop/zip absent from List.fun (pre-existing if-else-match MLIR bug); can be added if compiler is fixed.

## Session Continuity

Last session: 2026-03-29
Stopped at: Completed 35-03-PLAN.md (CLI prelude auto-loading — 183 tests pass)
Resume file: None
