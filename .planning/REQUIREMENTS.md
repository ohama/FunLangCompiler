# Requirements: v17.0 Project File (funproj.toml)

**Defined:** 2026-04-01
**Core Value:** FunLang과 동일한 funproj.toml로 멀티파일 프로젝트를 네이티브 바이너리로 컴파일

## Milestone Requirements

### TOML 파서

- [ ] **TOML-01**: funproj.toml 파싱 — [project], [[executable]], [[test]] 섹션 지원
- [ ] **TOML-02**: [project].name, [project].prelude 필드 파싱
- [ ] **TOML-03**: [[executable]].name, [[executable]].main 필드 파싱 (복수 타겟)
- [ ] **TOML-04**: [[test]].name, [[test]].main 필드 파싱 (복수 타겟)
- [ ] **TOML-05**: 경로 해석 — funproj.toml 위치 기준 상대 경로

### CLI 서브커맨드

- [ ] **CLI-01**: `fnc build` — 모든 [[executable]] 타겟을 네이티브 바이너리로 컴파일
- [ ] **CLI-02**: `fnc build <name>` — 특정 [[executable]] 타겟만 컴파일
- [ ] **CLI-03**: `fnc test` — 모든 [[test]] 타겟을 컴파일 + 실행
- [ ] **CLI-04**: `fnc test <name>` — 특정 [[test]] 타겟만 컴파일 + 실행
- [ ] **CLI-05**: `fnc <file.fun>` — 기존 단일 파일 모드 유지 (regression 없음)
- [ ] **CLI-06**: `-O0/-O1/-O2/-O3` 플래그가 build/test에도 적용

### 빌드 출력

- [ ] **OUT-01**: build/ 디렉토리에 바이너리 출력 (자동 생성)
- [ ] **OUT-02**: 빌드 결과 출력 — `OK: <name> → build/<name> (Xs)` 형식
- [ ] **OUT-03**: funproj.toml의 prelude 경로가 Prelude 로딩에 우선 적용

### 에러 처리

- [ ] **ERR-01**: funproj.toml 없으면 에러 메시지
- [ ] **ERR-02**: 타겟 파일 없으면 에러 메시지
- [ ] **ERR-03**: 존재하지 않는 타겟 이름이면 에러 메시지

### 테스트

- [ ] **TEST-01**: funproj.toml 파싱 E2E 테스트
- [ ] **TEST-02**: fnc build E2E 테스트 (프로젝트 컴파일 + 바이너리 실행)
- [ ] **TEST-03**: fnc test E2E 테스트

## Future Requirements

- funproj.toml 의존성 선언 (`[dependencies]` 섹션) — 패키지 매니저와 함께
- `fnc run` 서브커맨드 — 컴파일 + 즉시 실행

## Out of Scope

- NuGet/패키지 의존성 관리 — 현재 open "file.fun" 수동 임포트로 충분
- TOML 라이브러리 외부 의존성 — 최소 서브셋만 수동 파싱
- `fnc init` 프로젝트 초기화 커맨드 — 수동 funproj.toml 작성

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| TOML-01 | Phase 60 | Pending |
| TOML-02 | Phase 60 | Pending |
| TOML-03 | Phase 60 | Pending |
| TOML-04 | Phase 60 | Pending |
| TOML-05 | Phase 60 | Pending |
| CLI-01 | Phase 61 | Pending |
| CLI-02 | Phase 61 | Pending |
| CLI-03 | Phase 61 | Pending |
| CLI-04 | Phase 61 | Pending |
| CLI-05 | Phase 61 | Pending |
| CLI-06 | Phase 61 | Pending |
| OUT-01 | Phase 61 | Pending |
| OUT-02 | Phase 61 | Pending |
| OUT-03 | Phase 61 | Pending |
| ERR-01 | Phase 61 | Pending |
| ERR-02 | Phase 61 | Pending |
| ERR-03 | Phase 61 | Pending |
| TEST-01 | Phase 60 | Pending |
| TEST-02 | Phase 61 | Pending |
| TEST-03 | Phase 61 | Pending |

**Coverage:**
- v17.0 requirements: 20 total
- Mapped to phases: 20
- Unmapped: 0

---
*Created: 2026-04-01*
