# Roadmap: LangBackend v12.0 — Error Message Accuracy

## Overview

에러 메시지의 줄 번호 정확성, 파서 에러 위치 추가, 테스트 안정성, 비교 연산 unboxing 버그 수정. Prelude 별도 파싱으로 줄 번호를 유저 소스 기준으로 정확하게 만들고, 파서 에러에 토큰 위치를 포함하며, fslit의 CHECK-RE로 테스트를 Prelude 변경에 독립적으로 만들고, List.choose 등에서 발생하는 비교 람다 unboxing 버그를 수정한다.

## Milestones

- v1.0-v10.0: Shipped (Phases 1-42, archived)
- Phase 43: Uncommitted work (stripAnnot, BoolVars, mutual recursion, sanitizeMlirName)
- v11.0 Compiler Error Messages: Phases 44-46 (archived)
- v12.0 Error Message Accuracy: Phases 47-50 (current)

## Phases

- [x] **Phase 47: Prelude Separate Parsing** - Prelude와 유저 코드를 별도 파싱 후 AST merge로 줄 번호 정확성 확보
- [x] **Phase 48: Parse Error Position** - 파서 에러 메시지에 마지막 토큰 위치(file:line:col) 포함
- [x] **Phase 49: Error Tests CHECK-RE** - 에러 테스트를 CHECK-RE 정규식 매칭으로 전환하여 Prelude 변경에 독립적
- [ ] **Phase 50: Unboxing Comparison Bug** - boxed ptr에 arith.cmpi 적용되는 비교 람다 unboxing 버그 수정

## Phase Details

### Phase 47: Prelude Separate Parsing
**Goal**: 에러 메시지의 줄 번호가 유저 소스 파일 기준으로 정확하게 표시됨
**Depends on**: Nothing (first phase of v12.0)
**Requirements**: LINE-01 (유저 코드 1행 → 에러 1행), LINE-02 (Prelude 내부 에러 구분)
**Plans:** 1 plan
Plans:
- [ ] 47-01-PLAN.md — Separate Prelude/user parsing + update 7 error test line numbers
**Success Criteria** (what must be TRUE):
  1. Prelude와 유저 코드가 별도로 파싱되어 AST가 merge됨
  2. 유저 코드 1행의 에러가 `file:1:col`로 표시됨 (현재 `file:174:col`)
  3. Prelude 내부 에러 발생 시 `<prelude>` 경로로 구분됨
  4. 기존 217개 E2E 테스트 모두 통과

### Phase 48: Parse Error Position
**Goal**: 파서 에러 메시지에 file:line:col 위치 정보가 포함됨
**Depends on**: Phase 47 (줄 번호 정확성 확보 후)
**Requirements**: PARSE-POS-01 (파서 에러에 위치 포함)
**Plans:** 1 plan
Plans:
- [ ] 48-01-PLAN.md — Add lastParsedPos tracking + update 3 parse error test expectations
**Success Criteria** (what must be TRUE):
  1. 문법 오류 시 `[Parse] file:line:col: parse error` 형태로 출력
  2. 마지막으로 처리된 토큰의 위치가 에러에 포함됨
  3. 기존 parse error 테스트 업데이트

### Phase 49: Error Tests CHECK-RE
**Goal**: 에러 테스트가 Prelude 줄 수 변경에 독립적
**Depends on**: Phase 47, Phase 48 (최종 에러 포맷 확정 후)
**Requirements**: TEST-01 (CHECK-RE 전환), TEST-02 (안정성)
**Plans:** 1 plan
Plans:
- [ ] 49-01-PLAN.md — Convert 7 error tests from exact-match to CHECK-RE regex patterns
**Success Criteria** (what must be TRUE):
  1. 44-*, 45-*, 46-* 에러 테스트가 CHECK-RE 정규식 매칭 사용
  2. Prelude에 줄을 추가/삭제해도 에러 테스트 통과
  3. 에러 메시지의 핵심 내용(카테고리, 메시지)은 정확히 검증

### Phase 50: Unboxing Comparison Bug
**Goal**: boxed 리스트 원소에 대한 비교 연산이 올바르게 동작
**Depends on**: Phase 47 (테스트를 위해 정확한 줄 번호 필요)
**Requirements**: UNBOX-01 (비교 unboxing), UNBOX-02 (테스트)
**Success Criteria** (what must be TRUE):
  1. `List.choose (fun x -> if x > 2 then Some x else None) [1;2;3;4]`가 `[3;4]` 반환
  2. `List.filter (fun x -> x > 2) [1;2;3;4]`가 `[3;4]` 반환
  3. 비교 연산자(>, <, >=, <=, =, <>)가 boxed ptr 피연산자를 자동 unboxing
  4. 기존 E2E 테스트 모두 통과

## Progress

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 47. Prelude Separate Parsing | 1/1 | ✓ Complete | 2026-04-01 |
| 48. Parse Error Position | 1/1 | ✓ Complete | 2026-04-01 |
| 49. Error Tests CHECK-RE | 1/1 | ✓ Complete | 2026-04-01 |
| 50. Unboxing Comparison Bug | 0/? | Not started | - |
