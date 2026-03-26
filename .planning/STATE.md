# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v4.0 Type System & Error Handling — Phase 16: Environment Infrastructure

## Current Position

Phase: 16 of 20 (Environment Infrastructure)
Plan: — (not started)
Status: Ready to plan
Last activity: 2026-03-26 — v4.0 roadmap created; 27 requirements mapped across 5 phases (16-20)

Progress: [░░░░░░░░░░] 0% (v4.0)

## Performance Metrics

**Velocity:**
- v1.0: 11 plans, 6 phases — ~0.38 hours
- v2.0: 9 plans, 5 phases — 34 FsLit tests, 1,861 LOC
- v3.0: 5 plans, 4 phases — 45 FsLit tests
- Average duration: ~2.3 min/plan

## Accumulated Context

### Key Decisions

- MLIR text format direct generation (no P/Invoke)
- MlirIR as typed internal IR (F# DU)
- Flat closure struct {fn_ptr, env_fields}, caller-allocates
- Boehm GC (libgc) — conservative collector, `-lgc` link
- Uniform boxed representation (all heap types as ptr)
- Sequential cf.cond_br match compilation (Jacobs decision tree)
- @lang_match_failure fallback for non-exhaustive match
- PipeRight/ComposeRight/ComposeLeft are elaboration-time desugar only, no new MLIR ops (12-02)
- OrPat expanded in Elaboration.fs before MatchCompiler; char is already i64 (13-01, 14-01)
- Range -> lang_range(start, stop, step): C runtime returns Phase-10-compatible cons list (15)
- FsLit test inputs must not have trailing newlines — indent-sensitive lexer (15)
- [v4.0 pending] ADT layout: {i64 tag @field0, ptr payload @field1} — separate from tuple/cons-cell paths (C-11)
- [v4.0 pending] Nullary ctors always allocate real 16-byte block with tag — null encoding reserved for NilCtor (C-12)
- [v4.0 pending] lang_try_enter must call setjmp via static inline/macro — not out-of-line C function (C-15)
- [v4.0 pending] Pop handler stack before handler body executes, not after (C-16)

### Pending Todos

None.

### Blockers/Concerns

- Phase 19 (Exception Handling): validate `static inline setjmp` + clang `-O2` interaction with MLIR-emitted LLVM IR before full TryWith codegen. Write standalone proof-of-concept first.
- Phase 18 (Records): verify that `RecordExpr` with `typeName = None` is handled (recommend requiring `Some _` for v4.0).

## Session Continuity

Last session: 2026-03-26
Stopped at: v4.0 roadmap created — ready to plan Phase 16
Resume file: None
