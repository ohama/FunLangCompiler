# Roadmap: v20.0 Caller-Side Closure Env Population

## Phase 64: 2-Lambda Caller-Side Env Fix

**Goal:** 2-lambda 패턴의 maker body가 outer SSA를 참조하지 않도록 구조 변경, Issue #5 근본 해결

**Requirements:** FIX-01 ~ FIX-03, TEST-01 ~ TEST-04

**Success Criteria:**
1. 3-arg curried function + outer variable capture가 정상 컴파일
2. LetRec body에서 위 함수 호출 가능
3. 기존 244 E2E 테스트 전부 통과
4. Issue #5 재현 케이스 통과
5. 2-arg curried function KnownFuncs 최적화 유지

**Approach:**
- Research 필수: 2-lambda maker body의 capture store를 caller 측으로 이동하는 방법 분석
- 현재: maker func.func 내부에서 capture GEP+store → outer SSA 참조 (SSA leak)
- 목표: caller가 env 할당 + capture store → maker는 fn_ptr만 store + return env

---
*Created: 2026-04-02*
*Milestone: v20.0 Caller-Side Closure Env Population*
*Phase numbering continues from v19.0 (Phase 63)*
