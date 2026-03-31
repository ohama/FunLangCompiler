# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-31)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v11.0 Compiler Error Messages — Phase 45 plan 01 complete

## Current Position

Phase: 45 (2 of 3 in v11.0) — Error Preservation
Plan: 1/1 complete
Status: Phase complete
Last activity: 2026-03-31 — Completed 45-01-PLAN.md

Progress: v1.0-v10.0 complete (42 phases). v11.0: [██████░░░░] 67%

## Performance Metrics

**Velocity:**
- Total plans completed: 84 (v1.0-v10.0: 81 + Phase 44: 2 + Phase 45: 1)
- Average duration: ~10 min/plan

## Accumulated Context

### Decisions

- Phase 44: failWithSpan 인프라 완성으로 수락. Span 값이 0인 것은 LangThree 파서 upstream 이슈 (survey/langthree-span-zeroing-fix.md에 기록).
- Phase 44: `Ast.unknownSpan` fallback — 1곳(closure capture) 사용.
- Phase 45: parseProgram에서 firstEx 보존 — 양쪽 파서 실패 시 원래 parseModule 에러 표시.
- Phase 45: MLIR 파일 실패 시 보존 — MlirOpt/Translate 실패 시 .mlir 임시 파일 삭제하지 않고 경로 표시.
- Phase 45: catch-all 핸들러 "Error:" 접두사 유지 — 파싱 외 에러도 잡으므로 "Parse error:" 불가.

### Pending Todos

None.

### Blockers/Concerns

- LangThree 파서가 Span에 위치를 채우지 않음 → 에러 메시지가 `:0:0:` 표시. survey/에 수정 가이드 작성 완료.

## Session Continuity

Last session: 2026-03-31
Stopped at: Phase 45 complete, ready for Phase 46
Resume file: None
