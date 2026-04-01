# Requirements: v19.0 3-Lambda SSA Scope Fix

**Defined:** 2026-04-02
**Core Value:** 3+ arg curried function의 SSA scope leak 코드 생성 버그 수정
**Issue:** ohama/FunLangCompiler#4

## Milestone Requirements

### SSA Scope 수정

- [ ] **SSA-01**: 2-lambda maker의 capture store가 maker func.func 스코프 내 유효한 SSA만 참조
- [ ] **SSA-02**: inner body elaboration에서 생성된 SSA 값이 maker 함수 경계를 넘지 않음
- [ ] **SSA-03**: 3-arg curried function (buildCharClass 패턴) 정상 컴파일

### 테스트

- [ ] **TEST-01**: 3-arg curried function E2E 테스트 (mutable record + list + bool)
- [ ] **TEST-02**: 기존 243 E2E 테스트 전부 통과

## Future Requirements

None.

## Out of Scope

- 4+ arg curried function 최적화 — 3-arg가 올바르게 동작하면 4+도 자동 해결 (recursive Lambda 처리)
- closure ABI 추가 변경 — v18.0에서 이미 통일 완료

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| SSA-01 | Phase 63 | Pending |
| SSA-02 | Phase 63 | Pending |
| SSA-03 | Phase 63 | Pending |
| TEST-01 | Phase 63 | Pending |
| TEST-02 | Phase 63 | Pending |

**Coverage:**
- v19.0 requirements: 5 total
- Mapped to phases: 5
- Unmapped: 0

---
*Created: 2026-04-02*
