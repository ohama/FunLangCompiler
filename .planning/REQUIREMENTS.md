# Requirements: LangBackend v7.0

**Defined:** 2026-03-28
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements

Requirements for v7.0 milestone. Each maps to roadmap phases.

### Expression Sequencing (Phase 45)

- [ ] **SEQ-01**: Semicolon sequencing — `e1; e2` desugars to `LetPat(WildcardPat, e1, e2)` at elaboration time
- [ ] **SEQ-02**: Multi-statement sequencing — `e1; e2; e3` chains correctly (right-associative nesting)

### Loop Constructs (Phase 46)

- [ ] **LOOP-01**: WhileExpr elaboration — `while cond do body` compiles to MLIR loop or recursive structure, returns unit
- [ ] **LOOP-02**: ForExpr ascending — `for i = start to stop do body` iterates inclusive range, returns unit
- [ ] **LOOP-03**: ForExpr descending — `for i = start downto stop do body` iterates descending range
- [ ] **LOOP-04**: For-loop variable is immutable — `i` is a fresh binding per iteration, not a mutable ref cell
- [ ] **LOOP-05**: freeVars extension — WhileExpr/ForExpr cases for correct closure variable capture

### Array/Hashtable Indexing (Phase 47)

- [ ] **IDX-01**: IndexGet for arrays — `arr.[i]` desugars to `array_get arr i` elaboration
- [ ] **IDX-02**: IndexSet for arrays — `arr.[i] <- v` desugars to `array_set arr i v` elaboration
- [ ] **IDX-03**: IndexGet for hashtables — `ht.[key]` desugars to `hashtable_get ht key` elaboration
- [ ] **IDX-04**: IndexSet for hashtables — `ht.[key] <- v` desugars to `hashtable_set ht key v` elaboration

### If-Then Without Else (Phase 48)

- [ ] **ITE-01**: If-then without else — `if cond then expr` desugars to `If(cond, expr, Tuple([]))` at elaboration time
- [ ] **ITE-02**: Then-branch must be unit-typed — non-unit then-branch behavior matches interpreter

### Regression

- [ ] **REG-01**: 기존 118개 E2E 테스트 전체 통과 유지

## v2 Requirements

Deferred to future release.

- **ADV-01**: Loop break/continue — not in LangThree AST
- **ADV-02**: For-in loop (iterate over collections) — not in LangThree AST

## Out of Scope

| Feature | Reason |
|---------|--------|
| Loop break/continue | LangThree AST에 없음 |
| For-in collection loop | LangThree AST에 없음 |
| Do-while loop | LangThree AST에 없음 |
| Chained indexing optimization | arr.[i].[j] works via nested desugar, no special handling |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| SEQ-01 | TBD | Pending |
| SEQ-02 | TBD | Pending |
| LOOP-01 | TBD | Pending |
| LOOP-02 | TBD | Pending |
| LOOP-03 | TBD | Pending |
| LOOP-04 | TBD | Pending |
| LOOP-05 | TBD | Pending |
| IDX-01 | TBD | Pending |
| IDX-02 | TBD | Pending |
| IDX-03 | TBD | Pending |
| IDX-04 | TBD | Pending |
| ITE-01 | TBD | Pending |
| ITE-02 | TBD | Pending |
| REG-01 | All | Pending |

**Coverage:**
- v1 requirements: 14 total
- Mapped to phases: 0 (TBD)
- Unmapped: 14

---
*Requirements defined: 2026-03-28*
*Last updated: 2026-03-28 after initial definition*
