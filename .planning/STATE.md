# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-02)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v20.0 complete — Issue #5 resolved

## Current Position

Phase: 64 of 64 — Complete
Plan: 01 of 01 complete
Status: Phase complete
Last activity: 2026-04-01 — Completed 64-01-PLAN.md (caller-side closure env population)

Progress: v1.0-v20.0 complete [████████████████████] 64/64 phases

## Performance Metrics

**Velocity:**
- Total plans completed: 106
- Average duration: ~10 min/plan

## Accumulated Context

### Decisions

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-04-01 | Caller-side env population for 2-lambda closures | Moves non-outerParam capture stores to call site where SSA values are in scope; eliminates SSA scope violation in maker func.func |
| 2026-04-01 | ClosureInfo carries CaptureNames + OuterParamName | Call site needs to know which captures to store and which to skip (maker handles outerParam) |
| 2026-04-01 | LetRec bodies don't inherit outer env.Vars | Pre-existing limitation: module-level let-constants not accessible inside LetRec func.func bodies |

### Pending Todos

None.

### Blockers/Concerns

Pre-existing limitation: Module-level `let` constants not accessible from LetRec body envs (different SSA scope). Not a regression from this phase.

## Session Continuity

Last session: 2026-04-01
Stopped at: Completed 64-01-PLAN.md
Resume file: None
