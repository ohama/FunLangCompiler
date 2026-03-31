# Requirements: LangBackend v11.0

**Defined:** 2026-03-31
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v11 Requirements

컴파일러 에러 메시지를 개선하여 사용자가 문제의 원인과 위치를 빠르게 파악할 수 있게 한다.

### 소스 위치 추가

- [ ] **LOC-01**: Elaboration 에러 메시지에 파일명:행:열 위치가 포함된다 (unbound variable 등 ~15곳)
- [ ] **LOC-02**: `failWithSpan` 헬퍼 함수가 `Span → string → 'a` 형태로 모든 에러에서 사용된다
- [ ] **LOC-03**: 패턴 매칭 에러 (unsupported pattern, ConsPat 등)에 소스 위치가 포함된다

### 파서 에러 보존

- [ ] **PARSE-01**: 파서 fallback 시 첫 번째 파싱 에러가 보존되어 출력된다 (현재는 삼킴)
- [ ] **PARSE-02**: 파서 에러 메시지에 행:열 위치가 포함된다

### MLIR 디버그

- [ ] **MLIR-01**: mlir-opt/mlir-translate 실패 시 `.mlir` 임시 파일이 보존된다
- [ ] **MLIR-02**: 에러 메시지에 디버그용 `.mlir` 파일 경로가 포함된다

### 컨텍스트 보강

- [ ] **CTX-01**: Record 타입 해석 실패 시 사용 가능한 레코드 타입 목록이 표시된다
- [ ] **CTX-02**: 필드 접근 에러 시 해당 레코드의 유효한 필드 목록이 표시된다
- [ ] **CTX-03**: 함수 호출 에러 시 스코프 내 사용 가능한 함수 목록이 힌트로 표시된다

### 에러 분류

- [ ] **CAT-01**: CLI 에러 출력이 에러 단계를 구분한다 (Parse Error / Elaboration Error / Compile Error)
- [ ] **CAT-02**: 에러 메시지 포맷이 `[phase] file:line:col: message` 형태로 통일된다

## Future Requirements

- **DIAG-01**: LSP/에디터 연동을 위한 구조화된 에러 출력 (JSON)
- **DIAG-02**: 에러 복구 (첫 에러 후 계속 파싱하여 다중 에러 보고)
- **DIAG-03**: "did you mean?" 제안 (유사 이름 변수/함수)

## Out of Scope

| Feature | Reason |
|---------|--------|
| 타입 에러 보고 | LangBackend는 타입 검사를 하지 않음 (균일 표현) |
| 경고 시스템 | v11.0은 에러만 대상, 경고는 추후 |
| LSP 서버 | 구조화된 에러 출력은 Future에서 다룸 |
| 에러 복구 파싱 | 파서는 LangThree 것을 재사용, 수정 불가 |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| LOC-01 | Phase 44 | Complete |
| LOC-02 | Phase 44 | Complete |
| LOC-03 | Phase 44 | Complete |
| PARSE-01 | Phase 45 | Complete |
| PARSE-02 | Phase 45 | Complete |
| MLIR-01 | Phase 45 | Complete |
| MLIR-02 | Phase 45 | Complete |
| CTX-01 | Phase 46 | Complete |
| CTX-02 | Phase 46 | Complete |
| CTX-03 | Phase 46 | Complete |
| CAT-01 | Phase 46 | Complete |
| CAT-02 | Phase 46 | Complete |

**Coverage:**
- v11 requirements: 12 total
- Mapped to phases: 12
- Unmapped: 0

---
*Requirements defined: 2026-03-31*
