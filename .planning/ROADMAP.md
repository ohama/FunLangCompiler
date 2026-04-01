# Roadmap: FunLangCompiler v21.0 — Partial Env Pattern

## Overview

v21.0 completes Issue #5 by moving env allocation and capture storage to the definition site, so that LetRec body calls to 3+ arg curried functions find their captures already in the env. This is a single focused change to Elaboration.fs with regression verification against all 246 existing tests.

## Milestones

- ✅ v1.0–v20.0 — Phases 1–64 (shipped)
- 🚧 **v21.0 Partial Env Pattern** — Phase 65 (in progress)

## Phases

<details>
<summary>✅ v1.0–v20.0 (Phases 1–64) — SHIPPED 2026-04-02</summary>

See MILESTONES.md for full history.

</details>

### ✅ v21.0 — Partial Env Pattern (Completed 2026-04-02)

**Milestone Goal:** Issue #5 완전 해결 — definition site에서 env+captures 미리 생성하여 LetRec body에서도 3+ arg curried function 정상 동작

#### Phase 65: Partial Env Pattern Implementation

**Goal**: LetRec body에서 3+ arg curried function + outer capture 호출이 crash 없이 올바른 값을 반환한다
**Depends on**: Phase 64 (v20.0 caller-side env)
**Requirements**: ENV-01, ENV-02, ENV-03, REC-01, REC-02, REG-01, REG-02, REG-03, TEST-01, TEST-02
**Success Criteria** (what must be TRUE):
  1. Definition site에서 GC_malloc'd env에 captures가 즉시 저장되어 있어, LetRec body의 call site가 captures를 재-store할 필요 없음
  2. LetRec body에서 3+ arg curried function을 호출하면 올바른 결과값이 반환됨 (indirect fallback 없이)
  3. 기존 2-arg curried function과 capture 없는 curried function이 변경 전과 동일하게 동작함
  4. `dotnet run -- tests/compiler/` 실행 시 246개 기존 E2E 테스트가 전부 통과함
  5. LetRec body + outer capture + 3-arg curried function 시나리오를 커버하는 신규 E2E 테스트 2개가 통과함
**Plans**: 2/2 complete

Plans:
- [x] 65-01: Definition site env allocation + immediate capture store (ENV-01~03, REC-01~02)
- [x] 65-02: Regression verification + E2E tests (REG-01~03, TEST-01~02)

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 65. Partial Env Pattern | v21.0 | 2/2 | ✓ Complete | 2026-04-02 |
