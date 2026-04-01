# Roadmap: v15.0 unknownSpan 제거

## Phase 57: unknownSpan 전면 제거

**Goal:** Elaboration.fs와 Program.fs의 unknownSpan 11곳을 모두 실제 AST Span으로 교체하여 에러 메시지에 정확한 소스 위치 표시

**Requirements:** SPAN-01 ~ SPAN-08, TEST-01

**Plans:** 2 plans

Plans:
- [x] 57-01-PLAN.md -- Replace all 11 unknownSpan with real AST spans
- [x] 57-02-PLAN.md -- E2E tests for span accuracy verification

**Success Criteria:**
1. `grep -r "unknownSpan" src/` 결과가 0건
2. 기존 230 E2E 테스트 전부 통과
3. 에러 테스트에서 `0:0` 위치가 나타나지 않음
4. 클로저 캡처 실패, printfn/sprintf desugar, show/eq 빌트인 경로에서 정확한 줄/열 표시

**Approach:**
- 각 unknownSpan 사용처에서 이미 패턴매칭으로 바인딩된 AST 노드의 Span을 추출하여 전달
- printfn/eprintfn desugar: 원본 `App` 노드의 span 변수 활용
- show/eq 빌트인: 외부 `App` 패턴의 마지막 span 인자 활용
- 클로저 캡처: Lambda/LetRec elaboration 시 span을 인자로 전달
- first-class ctor: `Var(name, s)` 또는 `Constructor(name, _, s)`의 s 활용
- extractMainExpr: decls 리스트의 첫/마지막 decl에서 span 추출
- Program.fs parseExpr: expression의 span 또는 dummy span with filename

---
*Created: 2026-04-01*
*Milestone: v15.0 unknownSpan 제거*
*Phase numbering continues from v14.0 (Phase 56)*
