# Requirements: FunLangCompiler v21.0

**Defined:** 2026-04-02
**Core Value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v21.0 Requirements

Requirements for Issue #5 완전 해결 — Partial Env Pattern.

### Env Construction

- [x] **ENV-01**: Definition site에서 env를 GC_malloc하고 captures를 즉시 store
- [x] **ENV-02**: Call site에서는 outerParam만 env에 추가 store
- [x] **ENV-03**: Maker func.func body가 captures SSA를 참조하지 않음 (fn_ptr + outerParam만)

### LetRec Correctness

- [x] **REC-01**: LetRec body에서 3+ arg curried function 호출 시 captures가 env에 이미 존재
- [x] **REC-02**: LetRec body에서 indirect fallback 없이 direct call 가능

### Regression Prevention

- [x] **REG-01**: 2-arg curried function (기존 KnownFuncs 최적화) 정상 동작
- [x] **REG-02**: Capture 없는 curried function 정상 동작
- [x] **REG-03**: 기존 246 E2E 테스트 전체 통과

### E2E Verification

- [x] **TEST-01**: LetRec body에서 3+ arg curried function + outer capture 호출 E2E 테스트
- [x] **TEST-02**: Nested LetRec에서 outer capture가 있는 curried function 호출 테스트

## Future Requirements

None — this is a focused bug fix milestone.

## Out of Scope

| Feature | Reason |
|---------|--------|
| N-ary function direct call optimization | Correctness first, optimization later |
| Closure ABI 변경 | v18.0에서 이미 통일 완료 |
| General env sharing/deduplication | Unnecessary complexity for this fix |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| ENV-01 | Phase 65 | Complete |
| ENV-02 | Phase 65 | Complete |
| ENV-03 | Phase 65 | Complete |
| REC-01 | Phase 65 | Complete |
| REC-02 | Phase 65 | Complete |
| REG-01 | Phase 65 | Complete |
| REG-02 | Phase 65 | Complete |
| REG-03 | Phase 65 | Complete |
| TEST-01 | Phase 65 | Complete |
| TEST-02 | Phase 65 | Complete |

**Coverage:**
- v21.0 requirements: 10 total
- Mapped to phases: 10
- Unmapped: 0

---
*Requirements defined: 2026-04-02*
*Last updated: 2026-04-02 — traceability mapped to Phase 65*
