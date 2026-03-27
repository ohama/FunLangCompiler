# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-27)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v5.0 Mutable & Collections — Phase 23: Hashtable (COMPLETE), Phase 24: Array HOFs (next)

## Current Position

Phase: 23 of 24 (Hashtable) — COMPLETE
Plan: 2 of 2 in phase 23
Status: Phase complete
Last activity: 2026-03-27 — Completed 23-02-PLAN.md (hashtable elaboration + 8 E2E tests, 88 tests passing)

Progress: [################░░░░░░░░░░░░░░] v4.0 complete, v5.0 phases 21+22+23 done (88 tests passing)

## Performance Metrics

**Velocity:**
- v1.0: 11 plans, 6 phases
- v2.0: 9 plans, 5 phases — 34 FsLit tests, 1,861 LOC
- v3.0: 5 plans, 4 phases — 45 FsLit tests
- v4.0: 12 plans, 5 phases — 67 FsLit tests, 2,861 F# LOC + 184 C LOC
- v5.0: 6 plans, 3/4 phases complete — Phases 21+22+23 done (88 E2E tests)

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| v1.0–v4.0 | 37 | — | — |

*Updated after each plan completion*

## Accumulated Context

### Key Decisions

See: .planning/PROJECT.md Key Decisions table (full history)

Recent decisions affecting v5.0:
- [v4.0] setjmp/longjmp with inline _setjmp for ARM64 PAC compatibility — exceptions infra available for array bounds/hashtable missing-key raises
- [v5.0 research] Arrays: one-block layout GC_malloc((n+1)*8), slot 0 = length, slots 1..n = elements
- [v5.0 research] Hashtable: must be C runtime (LangHashtable struct), all ops via lang_ht_* functions
- [v5.0 research] Build order: MutableVars first (independent), Array core second (dynamic GEP needed), Hashtable third, Array HOFs last
- [21-01] GC ref cell approach: 8-byte GC_malloc'd cell; Var with name in MutableVars emits LlvmLoadOp transparently
- [21-01] MutableVars not propagated into closure inner envs — closure capture of mutable vars deferred to Plan 02
- [21-01] Assign returns unit 0L via ArithConstantOp after store
- [21-02] Closure captures mutable var as Ptr (ref cell pointer); capType conditional on MutableVars membership
- [21-02] freeVars must handle LetPat(WildcardPat) and LetPat(VarPat) — previously fell to Set.empty causing invisible captures
- [22-01] lang_array_bounds_check uses lang_throw (not lang_failwith) — OOB is catchable by try/with
- [22-01] LlvmGEPDynamicOp element type is i64 (not !llvm.ptr) — MLIR verifier requires element type, i64 gives 8-byte stride
- [22-01] lang_array_to_list iterates backwards (arr[n] to arr[1]) to build cons list in correct forward order
- [22-02] Three-arg array_set must appear before two-arg array_get/array_create in elaborateExpr match
- [22-02] Shell $? truncates exit codes to 8 bits; try/with E2E tests must use return values 0-255
- [22-02] Both externalFuncs lists in Elaboration.fs (elaborateModule + elaborateProgram) must be kept in sync
- [23-01] murmurhash3 fmix64 finalizer used for i64 key hashing — fast, no dependencies, good avalanche
- [23-01] lang_throw used for missing-key error (not lang_failwith) — hashtable KeyNotFound is catchable by try/with
- [23-01] Rehash threshold is size*4 > capacity*3 (load > 0.75), capacity doubles on each rehash
- [23-01] lang_hashtable_keys returns LangCons* in bucket-iteration order (unspecified, not insertion order)
- [23-02] hashtable_create discards unit arg by elaborating and ignoring result — keeps parser semantics correct
- [23-02] coerceToI64 inlined per-arm (not extracted as helper) — consistent with array_create inline pattern
- [23-02] containsKey returns I1 via ArithCmpIOp("ne", rawI64, 0) — consistent with if/then/else consumption
- [23-02] summing three let-bound containsKey I64 values (a+b+c) triggered MLIR empty-block error — use keys+len instead

### Pending Todos

None.

### Blockers/Concerns

None — Phase 23 complete. Phase 24 (Array HOFs) ready to proceed.

## Session Continuity

Last session: 2026-03-27T12:24:02Z
Stopped at: Completed 23-02-PLAN.md — hashtable elaboration + 8 E2E tests (88 tests passing)
Resume file: None
