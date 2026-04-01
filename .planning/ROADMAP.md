# Roadmap: v19.0 3-Lambda SSA Scope Fix

## Phase 63: 3-Lambda SSA Scope Fix

**Goal:** 2-lambda 패턴에서 3+ arg curried function의 maker body가 inner body SSA를 leak하지 않도록 수정

**Requirements:** SSA-01 ~ SSA-03, TEST-01, TEST-02

**Success Criteria:**
1. 3-arg curried function (mutable record + list + bool) 컴파일 성공
2. 생성된 MLIR에서 maker func.func가 inner body SSA를 참조하지 않음
3. 기존 243 E2E 테스트 전부 통과
4. Issue #4 재현 케이스 통과

**Approach:**
- Research 필요: 2-lambda 패턴의 capture store가 env.Vars에서 가져오는 SSA가 왜 inner body의 것인지 분석
- maker body와 inner body elaboration의 순서/스코프 관계 확인
- capture store를 maker 스코프 내에서만 유효한 값으로 제한하는 방법 결정

---
*Created: 2026-04-02*
*Milestone: v19.0 3-Lambda SSA Scope Fix*
*Phase numbering continues from v18.0 (Phase 62)*
