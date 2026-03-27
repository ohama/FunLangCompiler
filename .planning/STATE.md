# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v4.0 Type System & Error Handling — Phase 17: ADT Construction & Pattern Matching

## Current Position

Phase: 17 of 20 (ADT Construction & Pattern Matching)
Plan: 1 of 2 (17-01 complete)
Status: In progress
Last activity: 2026-03-26 — Completed 17-01-PLAN.md (ADT constructor elaboration, 48/48 tests pass)

Progress: [███░░░░░░░] 30% (v4.0, 1/5 phases complete, 17-01 done)

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
- ADT layout: 16-byte GC_malloc block, slot 0 = i64 tag (LlvmGEPLinearOp), slot 1 = payload stored directly (I64 or Ptr) — live in Elaboration.fs (17-01)
- Nullary ctors allocate real 16-byte block with tag and null at slot 1 — null encoding reserved for NilCtor (17-01)
- ADT payload stored directly at slot 1 (no extra heap indirection) — I64 and Ptr both work; resolveAccessor default I64 load correct for int payloads (17-01)
- [v4.0 pending] lang_try_enter must call setjmp via static inline/macro — not out-of-line C function (C-15)
- [v4.0 pending] Pop handler stack before handler body executes, not after (C-16)
- AdtCtor real tag now supplied from TypeEnv in emitCtorTest (17-01); Phase 16 placeholder replaced
- RecordCtor identity = sorted field names list; canonical ordering enforced at desugarPattern site (16-02)
- parseModule fallback: parseProgram tries parseModule, falls back to parseExpr + synthetic Module for bare-expression inputs (16-01)
- ElabEnv gains TypeEnv/RecordEnv/ExnTags; elaborateProgram is new entry point; prePassDecls scans Decl list (16-01)

### Pending Todos

None.

### Blockers/Concerns

- Phase 19 (Exception Handling): validate `static inline setjmp` + clang `-O2` interaction with MLIR-emitted LLVM IR before full TryWith codegen. Write standalone proof-of-concept first.
- Phase 18 (Records): verify that `RecordExpr` with `typeName = None` is handled (recommend requiring `Some _` for v4.0).

## Session Continuity

Last session: 2026-03-26
Stopped at: Completed 17-01-PLAN.md — ADT constructor elaboration complete, 48/48 tests pass
Resume file: None
