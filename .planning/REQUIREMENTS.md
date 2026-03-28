# Requirements: LangBackend v8.0

**Defined:** 2026-03-28
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements

### Type Annotations

- [ ] **ANN-01**: Annot pass-through — `(e : T)` elaborates to just `e` (type annotation ignored at codegen)
- [ ] **ANN-02**: LambdaAnnot pass-through — `fun (x: T) -> e` elaborates as `Lambda(x, e)` (annotation ignored)
- [ ] **ANN-03**: freeVars extension — Annot/LambdaAnnot cases for correct closure capture

### For-In Loop

- [ ] **FIN-01**: ForInExpr list iteration — `for x in list do body` iterates cons-cell list elements
- [ ] **FIN-02**: ForInExpr array iteration — `for x in array do body` iterates array elements
- [ ] **FIN-03**: ForInExpr loop variable immutable — `x` is fresh binding per iteration
- [ ] **FIN-04**: freeVars extension — ForInExpr case

### Regression

- [ ] **REG-01**: 기존 138개 E2E 테스트 전체 통과 유지

## Out of Scope

| Feature | Reason |
|---------|--------|
| ForInExpr hashtable iteration | LangThree evaluator doesn't support it |
| sprintf/printf/printfn | Variadic format strings — complex, separate milestone |
| get_args | @main signature change required |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| ANN-01 | Phase 30 | Pending |
| ANN-02 | Phase 30 | Pending |
| ANN-03 | Phase 30 | Pending |
| FIN-01 | Phase 30 | Pending |
| FIN-02 | Phase 30 | Pending |
| FIN-03 | Phase 30 | Pending |
| FIN-04 | Phase 30 | Pending |
| REG-01 | Phase 30 | Pending |

**Coverage:**
- v1 requirements: 8 total
- Mapped to phases: 8
- Unmapped: 0

---
*Requirements defined: 2026-03-28*
