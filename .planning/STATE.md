# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-02)

**Core value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** Issue #5 추가 수정 필요 — "partial env" 패턴 구현

## Current Position

Phase: v20.0 shipped but Issue #5 partially unresolved
Plan: N/A — 새 milestone 필요
Status: Issue #5 reopen — LetRec body에서 captures 미접근 문제 남음
Last activity: 2026-04-02 — caller-side fallback 커밋 (f8f6c47)

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

### Pending Todos

- Issue #5 완전 해결: "partial env" 패턴 필요
  - Definition site에서 env 생성 + captures 미리 채움
  - Call site에서는 outerParam만 추가
  - LetRec body에서 captures 접근 불필요하게 됨

### Blockers/Concerns

Issue #5 LetRec body에서 captures가 있는 3+ arg KnownFuncs 호출 시:
- Caller-side store: env.Vars에 capture 없음 (LetRec Vars 리셋)
- Indirect fallback: env.Vars에도 없음 (KnownFuncs에만 등록)
- 필요: "partial env" — definition site에서 env+captures 미리 생성

## Session Continuity

Last session: 2026-04-02
Stopped at: Issue #5 partial fix committed (f8f6c47), "partial env" 패턴 구현 필요
Resume file: None

### 다음 세션 재개 방법

```
/gsd:new-milestone "Issue #5 partial env 패턴으로 완전 해결"
```

또는 직접 코드 수정 후:
```
/gsd:progress
```
