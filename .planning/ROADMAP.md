# Roadmap: v19.0 3-Lambda SSA Scope Fix

## Phase 63: 3-Lambda SSA Scope Fix

**Goal:** 2-lambda 패턴에서 3+ arg curried function의 maker body가 inner body SSA를 leak하지 않도록 수정

**Requirements:** SSA-01 ~ SSA-03, TEST-01, TEST-02

**Plans:** 1 plan

Plans:
- [ ] 63-01-PLAN.md — Add guard to 2-lambda pattern + 3-arg curried function E2E test

**Success Criteria:**
1. 3-arg curried function (mutable record + list + bool) 컴파일 성공
2. 생성된 MLIR에서 maker func.func가 inner body SSA를 참조하지 않음
3. 기존 243 E2E 테스트 전부 통과
4. Issue #4 재현 케이스 통과

**Approach:**
- 2-lambda 패턴에 `when` guard 추가: innerBody가 Lambda일 때 매칭 거부
- 3+ lambda는 general Let path로 fallthrough → 각 lambda layer가 별도 closure로 올바르게 컴파일
- 기존 2-arg curried function은 영향 없음 (guard 통과)

---
*Created: 2026-04-02*
*Milestone: v19.0 3-Lambda SSA Scope Fix*
*Phase numbering continues from v18.0 (Phase 62)*
