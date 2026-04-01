# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-02)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v21.0 Partial Env Pattern — Phase 65 COMPLETE

## Current Position

Phase: 65 of 65 (Partial Env Pattern Implementation)
Plan: 2 of 2 in current phase
Status: Phase complete — v21.0 milestone complete
Last activity: 2026-04-02 — Completed 65-02-PLAN.md (E2E tests + global template fix)

Progress: v1.0-v21.0 complete [█████████████████████] 66/66 plans — Phase 65 done

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
| 2026-04-02 | Template stored in LLVM global @__tenv_<name> | LetRec bodies are separate func.funcs — cannot reference @main SSA values; globals solve cross-function access |
| 2026-04-02 | Template-copy fallback returns Ptr closure (not IndirectCallOp) | Fallback replaces maker — returns env Ptr just like maker does |
| 2026-04-02 | Template-copy (no mutation) — fresh env per call | 공유 template 변경하면 다음 call에서 오염됨 |

### Pending Todos

None.

### Blockers/Concerns

Issue #5 FULLY RESOLVED in Phase 65:
- Plan 01: partial env pattern, template-copy call path
- Plan 02: LLVM global for cross-func-func access, fallback returns Ptr closure
- 248/248 E2E tests pass (246 original + 2 new phase 65 tests)

## Session Continuity

Last session: 2026-04-02
Stopped at: Completed 65-02-PLAN.md — Phase 65 complete, v21.0 milestone complete
Resume file: None
