# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v4.0 Type System & Error Handling

## Current Position

Phase: Not started (defining requirements)
Plan: —
Status: Defining requirements
Last activity: 2026-03-26 — Milestone v4.0 started

Progress: [░░░░░░░░░░] 0%

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
- App(Lambda) inlines as env binding, no closure allocation (12-01)
- Bare Lambda as expression creates inline closure via GC_malloc + llvm.func (12-02)
- PipeRight/ComposeRight/ComposeLeft are elaboration-time desugar only, no new MLIR ops (12-02)
- OrPat expanded in Elaboration.fs before MatchCompiler; PAT-08 is IntLit(int c) remap; PAT-06 Guard node in DecisionTree (13-01)
- char_to_int and int_to_char are identity elaborations — char is already i64 (14-01)
- string_contains: C returns I64 0/1; ArithCmpIOp(ne, 0) converts to I1 for boolean use (14-01)
- failwith: LlvmCallVoidOp + dead ArithConstantOp(0L) for unit return type (14-01)
- Range -> lang_range(start, stop, step): C runtime returns ptr to Phase-10-compatible cons list; default step=1 is ArithConstantOp(v, 1L) (15)
- FsLit test inputs must not have trailing newlines — indent-sensitive lexer emits NEWLINE tokens that break the parser (15)

### Pending Todos

None.

### Blockers/Concerns

None.

## Session Continuity

Last session: 2026-03-26
Stopped at: v4.0 milestone questioning complete, entering requirements
Resume file: None
