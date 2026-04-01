# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-01)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v17.0 Project File (funproj.toml) — COMPLETE

## Current Position

Phase: 61 of 61 — Complete (2/2 plans complete)
Plan: 61-02 complete
Status: Phase 61 complete — v17.0 milestone complete + E2E tests added
Last activity: 2026-04-01 — Completed 61-02-PLAN.md (fnc build/test E2E tests)

Progress: v1.0-v17.0 complete [██████████████████████] 61/61 phases (102 total plans)

## Performance Metrics

**Velocity:**
- Total plans completed: 102 (v1.0-v10.0: 81 + v11.0: 4 + v12.0: 4 + v13.0: 3 + v14.0: 3 + v15.0: 2 + v16.0: 2 + v17.0: 3)
- Average duration: ~10 min/plan

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| (prior milestones) | 97 | ~970 min | ~10 min |
| 58 | 1 | ~6 min | ~6 min |
| 59 | 1 | ~8 min | ~8 min |
| 60 | 1 | ~2 min | ~2 min |
| 61 | 2 | ~10 min | ~5 min |

**Recent Trend:**
- Last 5 plans: v17.0 (2 plans) + v16.0 (2 plans) + v15.0 (1 plan)
- Trend: Stable, accelerating

## Accumulated Context

### Decisions

| Phase | Decision | Rationale |
|-------|----------|-----------|
| 60-01 | No external TOML library — hand-rolled parser | Only small subset needed; avoids NuGet dependency |
| 60-01 | Standalone test project (tests/projfile/) vs dotnet fsi | fsproj more reliable than FSI DLL loading |
| 60-01 | ProjectFile.fs last in Compile list | No compiler modules depend on it yet; Phase 61 wires it |
| 61-01 | Single-file routing scoped to .fun extension check | Keeps routing unambiguous; avoids false-positive on binary names |
| 61-01 | handleTest does not abort on first failure | Standard test runner behavior: run all, report summary |
| 61-01 | build/ shared by both build and test subcommands | Consistent output location for all compiled targets |
| 61-02 | CONTAINS: for error messages in .flt tests | Avoids brittleness on error message suffix changes |
| 61-02 | Binary stdout included in fnc test expected output | Test runner captures all stdout — binary output precedes PASS line |

### Pending Todos

None.

### Blockers/Concerns

None.

## Session Continuity

Last session: 2026-04-01T12:13:00Z
Stopped at: Completed 61-02-PLAN.md (fnc build/test E2E tests) — v17.0 fully complete with E2E coverage
Resume file: None
