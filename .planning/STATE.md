# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-28)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v7.0 Imperative Syntax — Phase 28 complete, Phase 29 next

## Current Position

Phase: 28 of 29 (Syntax Desugaring) — COMPLETE
Plan: 02 of 02 complete
Status: Phase 28 done, Phase 29 (Loops) ready to plan
Last activity: 2026-03-28 — Completed 28-02-PLAN.md (IDX desugaring)

Progress: [███████████████████████░] Phase 28 complete (28/29 phases), 1 phase remaining

## Performance Metrics

**Velocity:**
- v1.0: 11 plans, 6 phases — 15 FsLit tests
- v2.0: 9 plans, 5 phases — 34 FsLit tests
- v3.0: 5 plans, 4 phases — 45 FsLit tests
- v4.0: 12 plans, 5 phases — 67 FsLit tests
- v5.0: 8 plans, 4 phases — 92 FsLit tests
- v6.0: 5 plans, 3 phases — 118 FsLit tests

**Recent Trend:** Stable (phases getting smaller as language matures)

## Accumulated Context

### Key Decisions

See: .planning/PROJECT.md Key Decisions table (full history)

Recent decisions relevant to v7.0:
- Phase 28: SEQ/ITE desugar at elaboration time (LetPat(WildcardPat)/If with Tuple([]) else) — no new MLIR ops
- Phase 28: IDX uses runtime dispatch via lang_index_get/set; LangHashtable gets tag=-1 as first field to distinguish from arrays (length >= 0 at offset 0)
- Phase 29: LOOP requires new codegen — WhileExpr/ForExpr are new AST nodes (scf.while/scf.for or tail-call)

### Pending Todos

None.

### Blockers/Concerns

- Pre-existing MLIR domination bug with two named-ctor match arms (workaround documented)
- Phase 29 loop codegen approach TBD: scf.while/scf.for vs recursive tail-call vs C runtime helper

## Session Continuity

Last session: 2026-03-28
Stopped at: Completed 28-02-PLAN.md — Phase 28 done, Phase 29 (Loops) up next
Resume file: None
