# Roadmap: v18.0 Closure ABI 통일

## Phase 62: Closure %arg1 Ptr 통일

**Goal:** 클로저 함수의 %arg1을 항상 !llvm.ptr로 선언하고, body에서 i64 필요 시 ptrtoint 삽입. isPtrParamBody 클로저 의존성 제거.

**Requirements:** ABI-01 ~ ABI-04, CLEAN-01 ~ CLEAN-02, TEST-01 ~ TEST-03

**Plans:** 2 plans

Plans:
- [ ] 62-01-PLAN.md — Closure ABI change: %arg1 always Ptr + reversed coercion
- [ ] 62-02-PLAN.md — Issue #1 E2E test + gh issue close

**Success Criteria:**
1. Issue #1 재현 케이스 (mutable record + string curried function) 컴파일 성공
2. 생성되는 모든 클로저 함수가 `(%arg0: !llvm.ptr, %arg1: !llvm.ptr) -> i64` 시그니처
3. 기존 239+ E2E 테스트 전부 통과
4. isPtrParamBody가 클로저 생성 코드에서 호출되지 않음

**Approach:**
- Elaboration.fs의 2곳 클로저 생성 코드 (line ~711, ~3087) 수정:
  - `%arg1`을 `!llvm.ptr`로 선언
  - `isPtrParamBody` 결과와 무관하게 항상 Ptr
  - body에서 i64 필요 시: `ptrtoint %arg1 → i64` (기존과 반대 방향)
  - body에서 Ptr 필요 시: `%arg1` 그대로 사용 (코어션 불필요)
- 기존 `inttoptr` 코어션을 `ptrtoint`로 반전하거나, Ptr 직접 사용

---
*Created: 2026-04-01*
*Milestone: v18.0 Closure ABI 통일*
*Phase numbering continues from v17.0 (Phase 61)*
