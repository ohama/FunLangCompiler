# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-28)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v7.0 Imperative Syntax — Phase 28: Syntax Desugaring

## Current Position

Phase: 28 of 29 (Syntax Desugaring)
Plan: — of TBD in current phase
Status: Ready to plan
Last activity: 2026-03-27 — v7.0 roadmap created (Phases 28–29)

Progress: [██████████████████████░░] v6.0 shipped (27 phases complete), 2 phases remaining

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
- Phase 28: IDX desugar to existing array_get/set/hashtable_get/set builtins in elaborateExpr
- Phase 29: LOOP requires new codegen — WhileExpr/ForExpr are new AST nodes (scf.while/scf.for or tail-call)

### Pending Todos

None.

### Blockers/Concerns

- Pre-existing MLIR domination bug with two named-ctor match arms (workaround documented)
- Phase 29 loop codegen approach TBD: scf.while/scf.for vs recursive tail-call vs C runtime helper

## Session Continuity

Last session: 2026-03-27
Stopped at: v7.0 roadmap created — Phase 28 ready to plan
Resume file: None
