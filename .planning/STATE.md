# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-02)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v21.0 Partial Env Pattern — Issue #5 완전 해결

## Current Position

Phase: Not started (defining requirements)
Plan: —
Status: Defining requirements
Last activity: 2026-04-02 — Milestone v21.0 started

Progress: v1.0-v20.0 complete [████████████████████] 64/64 phases

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

### Pending Todos

(None — captured in v21.0 requirements)

### Blockers/Concerns

Issue #5 LetRec body에서 captures가 있는 3+ arg KnownFuncs 호출 시:
- Caller-side store: env.Vars에 capture 없음 (LetRec Vars 리셋)
- Indirect fallback: env.Vars에도 없음 (KnownFuncs에만 등록)
- 필요: "partial env" — definition site에서 env+captures 미리 생성

## Session Continuity

Last session: 2026-04-02
Stopped at: v21.0 milestone initialized
Resume file: None
