# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v3.0 — Language Completeness (연산자, 빌트인, 패턴 매칭 확장)

## Current Position

Phase: 13-pattern-matching-extensions (1/1 plans complete)
Plan: 13-01 of 01
Status: Phase complete
Last activity: 2026-03-26 — Completed 13-01-PLAN.md (when-guard, OrPat, CharConst)

Progress: [███░░░░░░░] 30% (3 plans done, v3.0 in progress)

## Performance Metrics

**Velocity:**
- v1.0: 11 plans, 6 phases — ~0.38 hours
- v2.0: 9 plans, 5 phases — 34 FsLit tests, 1,861 LOC
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

### Pending Todos

None.

### Blockers/Concerns

None.

## Session Continuity

Last session: 2026-03-26
Stopped at: Completed 13-01-PLAN.md — phase 13-pattern-matching-extensions complete
Resume file: None
