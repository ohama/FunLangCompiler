# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-04-01)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v12.0 Error Message Accuracy

## Current Position

Phase: 50 (4 of 4 in v12.0) — Unboxing Comparison Bug
Plan: 0/? (not yet planned)
Status: Phase not started
Last activity: 2026-04-01 — Phase 49 complete, verified ✓

Progress: v1.0-v11.0 complete (46 phases). v12.0: [████████░░] 75%

## Performance Metrics

**Velocity:**
- Total plans completed: 88 (v1.0-v10.0: 81 + v11.0: 4 + v12.0: 3)
- Average duration: ~10 min/plan

## Accumulated Context

### Decisions

- v11.0: failWithSpan 인프라 완성, [Elaboration]/[Parse]/[Compile] 카테고리 분류
- v11.0: LangThree PositionedToken/filterPositioned 적용 → 실제 file:line:col 표시
- v12.0: Prelude 별도 파싱 방식 채택 (문자열 concat → 별도 파싱 후 AST merge)
- v12.0: fslit CHECK-RE 사용으로 에러 테스트 안정화
- v12.0 Phase 47: two-phase parsing 구현 — preludeDecls @ userDecls, userSpan 사용, "<prelude>" 파일명으로 Prelude 오류 식별 가능
- v12.0 Phase 48: lastParsedPos mutable (try 블록 전 선언) → parse 오류에 file:line:col 위치 포함, CHECK-RE로 경로 무관 테스트
- v12.0 Phase 49: fslit CHECK-RE는 라인별 적용 — CHECK-RE: 접두사 라인만 정규식, 나머지는 exact match. 44-02 멀티라인: 첫 줄만 CHECK-RE, 나머지 exact

### Pending Todos

None.

### Blockers/Concerns

- List.choose 비교 람다에서 arith.cmpi + !llvm.ptr 타입 불일치 (Phase 50에서 해결 예정)

## Session Continuity

Last session: 2026-04-01
Stopped at: Phase 49 complete, Phase 50 계획 대기
Resume file: None
