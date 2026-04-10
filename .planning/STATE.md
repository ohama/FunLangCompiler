# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-09)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** Phase 96 — Prelude Copy

## Current Position

Phase: 95 of 97 (FunLang v14.0 Type System Sync) — COMPLETE
Plan: 1 of 1 in current phase
Status: Phase complete
Last activity: 2026-04-09 — Completed 95-01-PLAN.md (v14.0 type system sync)

Progress: v1.0–v23.0 in progress [████████████████████████] 95 phases done / 2 phases remaining in v23.0

## Performance Metrics

**Velocity:**
- Total plans completed: 122
- v13.1: 3 phases, 6 plans in 1 day
- v13.0: 3 phases, 7 plans in 1 day

## Accumulated Context

### Decisions

- v23.0: Phase 94 first (BUG-01 blocks FunLexYacc runtime — user explicit priority)
- v23.0: Type system sync (Phase 95) before Prelude copy (Phase 96) — submodule must be at v14.0 first
- v23.0: Option.fun validated first as canary for multi-param style (Phase 97)
- Phase 94: TEString (not TEName "string") is FunLang's AST TypeExpr for the string primitive — use TEString in all pattern matches on type annotations
- Phase 94: Build warnings from `dotnet run` go to stdout — test scripts must use `>/dev/null 2>/dev/null`
- Phase 94: StringVars must be populated at bodyEnv construction for function parameters; heuristic inference alone is insufficient for IndexGet dispatch
- Phase 95: TStringBuilder is not a CollectionKind — add to typeNeedsPtr only, not detectCollectionKind
- Phase 95: Keep TData("HashSet"/"Queue"/"MutableList") fallback arms after new v14.0 union case arms for backward compatibility

### Pending Todos

- None

### Roadmap Evolution

- Phase 98 added: --trace compiler flag for function entry tracing (DEBUG-01)
- Phase 99 added: match failure diagnostics — 소스 위치, 값, 콜 스택 backtrace (DEBUG-02)
- Phase 100 added: Hashtable.tryGetValue option 태그 불일치 수정 (BUG-02, Issue #23)
- Phase 101 added: failwith/unhandled exception backtrace (DEBUG-03)

### Blockers/Concerns

- None

## Session Continuity

Last session: 2026-04-09T03:05:00Z
Stopped at: Completed 95-01-PLAN.md — FunLang v14.0 type system sync, submodule at 8da0af2
Resume file: None
Next action: /gsd:plan-phase 96 (Prelude Copy)
