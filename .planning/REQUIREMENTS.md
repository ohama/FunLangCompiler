# Requirements: v20.0 Caller-Side Closure Env Population

**Defined:** 2026-04-02
**Core Value:** 3+ arg curried function + outer capture 조합의 scope loss를 근본 해결
**Issue:** ohama/FunLangCompiler#5

## Milestone Requirements

### 구조 수정

- [ ] **FIX-01**: 2-lambda 패턴에서 3+ lambda guard 제거 — 모든 multi-lambda를 2-lambda로 처리
- [ ] **FIX-02**: 2-lambda maker가 outer SSA를 직접 참조하지 않도록 구조 변경 (caller-side env population 또는 다른 방식)
- [ ] **FIX-03**: 3+ arg function + outer variable capture가 LetRec body에서 호출 가능

### 테스트

- [ ] **TEST-01**: 3-arg curried function + outer variable capture E2E 테스트
- [ ] **TEST-02**: 3-arg curried function을 LetRec body에서 호출하는 E2E 테스트
- [ ] **TEST-03**: 기존 244 E2E 테스트 전부 통과
- [ ] **TEST-04**: Issue #5 close

## Future Requirements

None.

## Out of Scope

- Closure 성능 최적화 — correctness 우선
- N-ary function calling convention 변경 — 현재 currying 유지

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| FIX-01 | Phase 64 | Pending |
| FIX-02 | Phase 64 | Pending |
| FIX-03 | Phase 64 | Pending |
| TEST-01 | Phase 64 | Pending |
| TEST-02 | Phase 64 | Pending |
| TEST-03 | Phase 64 | Pending |
| TEST-04 | Phase 64 | Pending |

**Coverage:**
- v20.0 requirements: 7 total
- Mapped to phases: 7
- Unmapped: 0

---
*Created: 2026-04-02*
