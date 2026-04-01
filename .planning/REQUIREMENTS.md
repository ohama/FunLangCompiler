# Requirements: v18.0 Closure ABI 통일

**Defined:** 2026-04-01
**Core Value:** 클로저 파라미터 타입을 !llvm.ptr로 통일하여 isPtrParamBody 휴리스틱 버그 근본 해결
**Issue:** ohama/FunLangCompiler#1

## Milestone Requirements

### Closure ABI 변경

- [x] **ABI-01**: 클로저 함수 시그니처를 `(%arg0: !llvm.ptr, %arg1: !llvm.ptr) -> i64`로 변경 (기존: %arg1: i64)
- [x] **ABI-02**: 클로저 body에서 i64 파라미터 필요 시 `ptrtoint` 코어션 삽입 (기존: inttoptr)
- [x] **ABI-03**: 2-lambda (maker+inner) 패턴의 inner 클로저도 동일하게 %arg1: !llvm.ptr로 변경
- [x] **ABI-04**: 단독 Lambda 클로저 (line ~3087)도 동일하게 변경

### isPtrParamBody 정리

- [x] **CLEAN-01**: 클로저 생성 코드에서 isPtrParamBody 호출 제거 (불필요해짐)
- [x] **CLEAN-02**: isPtrParamBody 자체는 유지 (KnownFuncs 등록 시 사용)

### 테스트

- [x] **TEST-01**: Issue #1 재현 케이스 E2E 테스트 (mutable record + string 클로저)
- [x] **TEST-02**: 기존 239+ E2E 테스트 전부 통과 (regression 없음)
- [x] **TEST-03**: Issue #1 자동 종료 (gh issue close)

## Future Requirements

None.

## Out of Scope

- isPtrParamBody 함수 삭제 — KnownFuncs 파라미터 타입 결정에 여전히 사용
- Caller 측 ptrtoint/inttoptr 브릿징 변경 — 기존 동작 유지
- FuncOp 직접 호출 시그니처 변경 — 클로저만 해당

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| ABI-01 | Phase 62 | Complete |
| ABI-02 | Phase 62 | Complete |
| ABI-03 | Phase 62 | Complete |
| ABI-04 | Phase 62 | Complete |
| CLEAN-01 | Phase 62 | Complete |
| CLEAN-02 | Phase 62 | Complete |
| TEST-01 | Phase 62 | Complete |
| TEST-02 | Phase 62 | Complete |
| TEST-03 | Phase 62 | Complete |

**Coverage:**
- v18.0 requirements: 9 total
- Mapped to phases: 9
- Unmapped: 0

---
*Created: 2026-04-01*
