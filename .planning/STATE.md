# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-27)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v5.0 Mutable & Collections — Phase 21: Mutable Variables

## Current Position

Phase: 21 of 24 (Mutable Variables)
Plan: 0 of ? in current phase
Status: Ready to plan
Last activity: 2026-03-27 — v5.0 roadmap created (phases 21–24)

Progress: [##########░░░░░░░░░░░░░░░░░░░░] v4.0 complete, v5.0 starting

## Performance Metrics

**Velocity:**
- v1.0: 11 plans, 6 phases
- v2.0: 9 plans, 5 phases — 34 FsLit tests, 1,861 LOC
- v3.0: 5 plans, 4 phases — 45 FsLit tests
- v4.0: 12 plans, 5 phases — 67 FsLit tests, 2,861 F# LOC + 184 C LOC
- v5.0: 0 plans, 0/4 phases — in progress

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

### Pending Todos

None.

### Blockers/Concerns

- Phase 22 (Array Core) requires LlvmGEPDynamicOp (dynamic SSA-value index GEP) — new IR op needed
- Phase 23 (Hashtable) requires new C runtime file with LangHashtable struct and lang_ht_* functions + value hash function

## Session Continuity

Last session: 2026-03-27
Stopped at: v5.0 roadmap created — ready to plan Phase 21
Resume file: None
