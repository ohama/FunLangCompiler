# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-28)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v7.0 Imperative Syntax — Phase 29 in progress (WhileExpr done)

## Current Position

Phase: 29 of 29 (Loop Constructs) — In progress
Plan: 01 of ?? complete
Status: Phase 29 Plan 01 done (WhileExpr), ForExpr TBD
Last activity: 2026-03-28 — Completed 29-01-PLAN.md (WhileExpr CFG elaboration + 4 E2E tests)

Progress: [████████████████████████] 132 E2E tests passing, WhileExpr complete

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
- Phase 28-01: SEQ/ITE desugar at elaboration time (LetPat(WildcardPat)/If with Tuple([]) else) — verified via E2E tests
- Phase 28-01: Tuple([]) = I64 0 (not GC_malloc ptr) — unit type uniform across all builtins
- Phase 28-01: LetPat(WildcardPat) needs same terminator handling as Let for if/match in bind position
- Phase 28-01: ITE tests use "let _ = if cond then expr in result" (not bare multi-line if-then) due to module parser requirement
- Phase 28-02: IDX uses runtime dispatch via lang_index_get/set; LangHashtable gets tag=-1 as first field to distinguish from arrays (length >= 0 at offset 0)
- Phase 29-01: WhileExpr uses header-block CFG (3 side blocks: while_header/body/exit) — NOT scf.while
- Phase 29-01: unitConst defined in entry fragment (dominates all loop blocks) for MLIR SSA correctness
- Phase 29-01: Nested loop back-edge patched into inner merge block at index (len-4) after pushing 3 while blocks
- Phase 29-01: condExpr re-elaborated in body block (fresh SSA names) for mutable-variable safe back-edge

### Pending Todos

None.

### Blockers/Concerns

- Pre-existing MLIR domination bug with two named-ctor match arms (workaround documented)
- ForExpr codegen not yet implemented (Phase 29 Plan 01 only covers WhileExpr)

## Session Continuity

Last session: 2026-03-28T02:04:18Z
Stopped at: Completed 29-01-PLAN.md — WhileExpr CFG elaboration + 4 E2E tests (132/132 passing)
Resume file: None
