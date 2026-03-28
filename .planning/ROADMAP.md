# Roadmap: LangBackend

## Milestones

- ✅ **v1.0 Core Compiler** — Phases 1–6 (shipped 2026-03-26)
- ✅ **v2.0 Data Types & Pattern Matching** — Phases 7–11 (shipped 2026-03-26)
- ✅ **v3.0 Language Completeness** — Phases 12–15 (shipped 2026-03-26)
- ✅ **v4.0 Type System & Error Handling** — Phases 16–20 (shipped 2026-03-27)
- ✅ **v5.0 Mutable & Collections** — Phases 21–24 (shipped 2026-03-28)
- ✅ **v6.0 Modules & I/O** — Phases 25–27 (shipped 2026-03-28)
- 🚧 **v7.0 Imperative Syntax** — Phases 28–29 (in progress)

## Phases

<details>
<summary>✅ v1.0–v6.0 (Phases 1–27) — SHIPPED</summary>

See .planning/MILESTONES.md for full history.

- [x] Phase 1–6: Core Compiler (v1.0)
- [x] Phase 7–11: Data Types & Pattern Matching (v2.0)
- [x] Phase 12–15: Language Completeness (v3.0)
- [x] Phase 16–20: Type System & Error Handling (v4.0)
- [x] Phase 21–24: Mutable & Collections (v5.0)
- [x] Phase 25–27: Modules & I/O (v6.0)

</details>

### 🚧 v7.0 Imperative Syntax (In Progress)

**Milestone Goal:** Expression sequencing (;), array/hashtable indexing (.[]), if-then without else, and loop constructs (while/for) compile to native code, completing LangThree's imperative programming syntax.

- [ ] **Phase 28: Syntax Desugaring** — SEQ, ITE, and IDX all desugar at elaboration time; no new MLIR ops
- [ ] **Phase 29: Loop Constructs** — WhileExpr/ForExpr new codegen + full regression gate

## Phase Details

### Phase 28: Syntax Desugaring

**Goal**: Expression sequencing, if-then-without-else, and array/hashtable indexing are all transparently supported — programs using these constructs compile to correct native binaries.
**Depends on**: Phase 27 (v6.0 complete)
**Requirements**: SEQ-01, SEQ-02, ITE-01, ITE-02, IDX-01, IDX-02, IDX-03, IDX-04
**Success Criteria** (what must be TRUE):
  1. `e1; e2` compiles and evaluates e1 for side effects then returns e2's value
  2. `e1; e2; e3` chains correctly — right-associative, all side effects execute in order
  3. `if cond then expr` compiles without else branch — evaluates to unit when cond is false
  4. `arr.[i]` and `arr.[i] <- v` compile to correct array reads and writes
  5. `ht.[key]` and `ht.[key] <- v` compile to correct hashtable reads and writes
**Plans:** 2 plans

Plans:
- [ ] 28-01-PLAN.md — Verify SEQ + ITE already work (E2E tests only, no code changes)
- [ ] 28-02-PLAN.md — Implement IndexGet/IndexSet elaboration (C runtime dispatch + Elaboration.fs)

### Phase 29: Loop Constructs

**Goal**: While and for loops compile to correct native code — programs using loops execute with proper iteration, correct loop variable scoping, and unit return semantics.
**Depends on**: Phase 28
**Requirements**: LOOP-01, LOOP-02, LOOP-03, LOOP-04, LOOP-05, REG-01
**Success Criteria** (what must be TRUE):
  1. `while cond do body` executes body repeatedly until cond is false, returns unit
  2. `for i = start to stop do body` iterates i from start to stop inclusive, returns unit
  3. `for i = start downto stop do body` iterates i from start down to stop inclusive, returns unit
  4. Loop variable `i` is immutable within the loop body — cannot be assigned
  5. All 118 existing E2E tests continue to pass after loop codegen additions
**Plans**: TBD

Plans:
- [ ] 29-01: TBD

## Progress

**Execution Order:** 28 → 29

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1–6. Core Compiler | v1.0 | 11/11 | Complete | 2026-03-26 |
| 7–11. Data Types | v2.0 | 9/9 | Complete | 2026-03-26 |
| 12–15. Language Completeness | v3.0 | 5/5 | Complete | 2026-03-26 |
| 16–20. Type System | v4.0 | 12/12 | Complete | 2026-03-27 |
| 21–24. Mutable & Collections | v5.0 | 8/8 | Complete | 2026-03-28 |
| 25–27. Modules & I/O | v6.0 | 5/5 | Complete | 2026-03-28 |
| 28. Syntax Desugaring | v7.0 | 0/2 | Planning complete | - |
| 29. Loop Constructs | v7.0 | 0/TBD | Not started | - |
