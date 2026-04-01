# Requirements: v15.0 unknownSpan 제거

**Defined:** 2026-04-01
**Core Value:** 에러 메시지에서 정확한 소스 위치를 표시하여 디버깅 효율 향상

## Milestone Requirements

### Elaboration Span Propagation

- [x] **SPAN-01**: printfn 3가지 desugar 패턴(0/1/2-arg)에서 원본 App Span 사용 (Elaboration.fs:2190,2196,2202)
- [x] **SPAN-02**: eprintfn desugar에서 원본 App Span 사용 (Elaboration.fs:2299)
- [x] **SPAN-03**: show 빌트인 문자열 리터럴 경로에서 원본 App Span 사용 (Elaboration.fs:1500)
- [x] **SPAN-04**: eq 빌트인 문자열 리터럴 경로에서 원본 App Span 사용 (Elaboration.fs:1528-1529)
- [x] **SPAN-05**: 클로저 캡처 실패 에러에서 Lambda/LetRec Span 전달 (Elaboration.fs:798)
- [x] **SPAN-06**: first-class constructor 래핑에서 Var/Constructor Span 전달 (Elaboration.fs:3153)
- [x] **SPAN-07**: extractMainExpr에서 모듈 Span 사용 (Elaboration.fs:4314)

### Program Span Propagation

- [x] **SPAN-08**: parseExpr fallback에서 expression wrapping 시 적절한 Span 사용 (Program.fs:51)

### Testing

- [x] **TEST-01**: unknownSpan 에러 경로에 대한 E2E 테스트 — 에러 메시지에 0:0 위치가 나타나지 않음을 검증

## Future Requirements

None for this milestone scope.

## Out of Scope

- 새로운 에러 메시지 종류 추가 — 기존 unknownSpan 제거만 수행
- failWithSpan 인프라 변경 — 이미 v11.0에서 완성
- Prelude 내부 에러 위치 — Prelude는 "<prelude>" 파일명으로 이미 구분됨

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| SPAN-01 | Phase 57 | Complete |
| SPAN-02 | Phase 57 | Complete |
| SPAN-03 | Phase 57 | Complete |
| SPAN-04 | Phase 57 | Complete |
| SPAN-05 | Phase 57 | Complete |
| SPAN-06 | Phase 57 | Complete |
| SPAN-07 | Phase 57 | Complete |
| SPAN-08 | Phase 57 | Complete |
| TEST-01 | Phase 57 | Complete |

**Coverage:**
- v15.0 requirements: 9 total
- Mapped to phases: 9
- Unmapped: 0

---
*Created: 2026-04-01*
