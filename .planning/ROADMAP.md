# Roadmap: LangBackend

## Milestones

- ✅ **v1.0 Foundations** - Phases 1-5 (shipped)
- ✅ **v2.0 Data Structures** - Phases 6-11 (shipped)
- ✅ **v3.0 Operator & Builtin Parity** - Phases 12-16 (shipped)
- ✅ **v4.0 ADT/Records/Exceptions** - Phases 17-22 (shipped)
- ✅ **v5.0 Mutable/Array/Hashtable** - Phases 23-25 (shipped)
- ✅ **v6.0 Modules & File I/O** - Phases 26-27 (shipped)
- ✅ **v7.0 Control Flow** - Phases 28-29 (shipped)
- 🚧 **v8.0 Final Parity** - Phase 30 (in progress)

## Phases

<details>
<summary>✅ v1.0–v7.0 (Phases 1–29) - SHIPPED</summary>

Phases 1–29 complete. 138 E2E tests passing. See PROJECT.md for full history.

</details>

### 🚧 v8.0 Final Parity (In Progress)

**Milestone Goal:** Annot/LambdaAnnot type annotation pass-through + ForInExpr collection iteration loop — closing the remaining gap between LangBackend and the LangThree interpreter.

#### Phase 30: Annotations and For-In Loop

**Goal**: The compiler handles type-annotated expressions and for-in collection loops, producing correct binaries for all existing and new language constructs.
**Depends on**: Phase 29
**Requirements**: ANN-01, ANN-02, ANN-03, FIN-01, FIN-02, FIN-03, FIN-04, REG-01
**Success Criteria** (what must be TRUE):
  1. `(e : T)` type annotation expressions compile and produce the same output as `e` alone
  2. `fun (x: T) -> body` annotated lambdas compile identically to unannotated `fun x -> body`
  3. `for x in list do body` iterates every element of a cons-cell list in order
  4. `for x in arr do body` iterates every element of an array in order
  5. All 138 existing E2E tests continue to pass after the new features land
**Plans**: TBD

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1–29 | v1.0–v7.0 | 56/56 | Complete | 2026-03-28 |
| 30. Annotations and For-In Loop | v8.0 | 0/TBD | Not started | - |
