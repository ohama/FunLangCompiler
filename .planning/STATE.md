# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-02)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v21.0 Partial Env Pattern — Phase 65

## Current Position

Phase: 65 of 65 (Partial Env Pattern Implementation)
Plan: 1 of 2 in current phase
Status: In progress
Last activity: 2026-04-02 — Completed 65-01-PLAN.md (partial env implementation)

Progress: v1.0-v21.0 in progress [████████████████████░] 65/66 plans — Phase 65 plan 1/2 done

## Performance Metrics

**Velocity:**
- Total plans completed: 106
- Average duration: ~10 min/plan

## Accumulated Context

### Decisions

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-04-02 | Caller-side env population (v20.0) | Maker가 outer SSA 참조 안 하도록 — 대부분 동작 |
| 2026-04-02 | Fallback to indirect call when captures not in scope | LetRec body에서 crash 방지 |
| 2026-04-02 | "Partial env" 패턴 필요 | Definition site에서 env+captures 미리 생성해야 LetRec body 동작 |
| 2026-04-02 | Template stored in env.Vars[name] (Ptr) | Runtime SSA value, 같은 map LetRec bodies가 참조 |
| 2026-04-02 | Template-copy (no mutation) — fresh env per call | 공유 template 변경하면 다음 call에서 오염됨 |

### Pending Todos

None.

### Blockers/Concerns

Issue #5 RESOLVED in Phase 65 Plan 01:
- Partial env pattern implemented: definition site에서 template env pre-allocated
- Template-copy call path: LetRec body에서 template clone + outerParam fill + indirect call
- 246/246 E2E tests pass

## Session Continuity

Last session: 2026-04-02
Stopped at: Completed 65-01-PLAN.md — partial env pattern implemented, 246/246 tests pass
Resume file: None
