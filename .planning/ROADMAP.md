# Roadmap: v17.0 Project File (funproj.toml)

## Phase 60: funproj.toml 파서

**Goal:** FunLang 호환 funproj.toml 파싱 — [project], [[executable]], [[test]] 섹션을 구조체로 변환

**Requirements:** TOML-01 ~ TOML-05, TEST-01

**Plans:** 1 plans

Plans:
- [x] 60-01-PLAN.md — TOML subset parser (ProjectFile.fs) + E2E test

**Success Criteria:**
1. funproj.toml을 읽어 FunProjConfig 구조체로 파싱
2. [project].name, [project].prelude 추출
3. 복수 [[executable]]/[[test]] 타겟 추출
4. 경로가 funproj.toml 기준 상대 경로로 해석
5. 파싱 유닛 테스트 또는 E2E 테스트 통과

**Approach:**
- 새 파일 `ProjectFile.fs` — 최소 TOML 서브셋 파서 (외부 의존성 없음)
- FunProjConfig = { Name; PreludePath; Executables; Tests }
- findFunProj: CWD에서 funproj.toml 탐색

---

## Phase 61: fnc build/test 서브커맨드

**Goal:** `fnc build`와 `fnc test` CLI 서브커맨드로 프로젝트 단위 컴파일/실행

**Requirements:** CLI-01 ~ CLI-06, OUT-01 ~ OUT-03, ERR-01 ~ ERR-03, TEST-02, TEST-03

**Success Criteria:**
1. `fnc build` — 모든 [[executable]] 타겟을 build/에 네이티브 바이너리로 컴파일
2. `fnc build calc` — 특정 타겟만 컴파일
3. `fnc test` — 모든 [[test]] 타겟을 컴파일 + 실행, 결과 보고
4. `fnc test unit` — 특정 테스트만
5. `fnc hello.fun` — 기존 단일 파일 모드 유지 (regression 없음)
6. `-O` 플래그가 build/test에도 적용
7. 에러 메시지: funproj.toml 없음, 타겟 파일 없음, 존재하지 않는 타겟 이름

**Approach:**
- Program.fs CLI 라우팅: 첫 인자가 "build"/"test" → 프로젝트 모드, ".fun" → 단일 파일 모드
- buildTarget: parseProgram → expandImports → elaborateTypeclasses → elaborateProgram → Pipeline.compile
- testTarget: buildTarget + 바이너리 실행 + exit code 검사
- prelude 경로: funproj.toml prelude > 기존 walkUp 탐색

---
*Created: 2026-04-01*
*Milestone: v17.0 Project File (funproj.toml)*
*Phase numbering continues from v16.0 (Phase 59)*
