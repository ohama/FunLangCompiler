# Roadmap: LangBackend

## Milestones

- [x] **v1.0 Core Compiler** — Phases 1–6 (shipped 2026-03-26)
- [x] **v2.0 Data Types & Pattern Matching** — Phases 7–11 (shipped 2026-03-26)
- [x] **v3.0 Language Completeness** — Phases 12–15 (shipped 2026-03-26)
- [x] **v4.0 Type System & Error Handling** — Phases 16–20 (shipped 2026-03-27)
- [ ] **v5.0 Mutable & Collections** — Phases 21–24 (in progress)

## Phases

<details>
<summary>v1.0–v4.0 (Phases 1–20) — SHIPPED 2026-03-27</summary>

See `.planning/milestones/` for archived phase details.

Phases 1–6: Core compiler (int/bool/arith/closures/CLI)
Phases 7–11: Boehm GC, strings, tuples, lists, pattern matching
Phases 12–15: Operators, builtins, range syntax
Phases 16–20: ADTs, records, exceptions, first-class constructors

</details>

### v5.0 Mutable & Collections (In Progress)

**Milestone Goal:** Mutable variable bindings (let mut / assign), Array, and Hashtable compile to native code, enabling LangThree imperative programming patterns.

#### Phase 21: Mutable Variables

**Goal**: Programs using mutable bindings compile and execute with correct mutation semantics
**Depends on**: Phase 20 (v4.0 complete — GC, closures, elaboration infrastructure)
**Requirements**: MUT-01, MUT-02, MUT-03, MUT-04, MUT-05, MUT-06
**Success Criteria** (what must be TRUE):
  1. `let mut x = 5 in x <- 10; x` compiles and evaluates to 10
  2. A closure capturing a mutable variable sees mutations made after closure creation
  3. Module-level `let mut x = e` declarations compile via extractMainExpr desugaring
  4. `Var(name)` for a mutable name emits a load through the ref cell pointer (not a direct SSA value)
  5. freeVars correctly identifies mutable variables so closures capture the ref cell, not the value
**Plans**: 2 plans

Plans:
- [x] 21-01-PLAN.md — Core mutable variable support (freeVars, ElabEnv, LetMut/Assign/Var/LetMutDecl)
- [x] 21-02-PLAN.md — Closure capture of mutable ref cells

#### Phase 22: Array Core

**Goal**: Array creation, element access, mutation, length query, and list conversion all work correctly
**Depends on**: Phase 21 (mutable variables — arrays are mutable by nature)
**Requirements**: ARR-01, ARR-02, ARR-03, ARR-04, ARR-05, ARR-06, ARR-07
**Success Criteria** (what must be TRUE):
  1. `array_create 5 0` allocates an array of length 5 with all slots set to 0
  2. `array_get arr 2` returns the element at index 2; raises an exception on out-of-bounds index
  3. `array_set arr 2 99` stores 99 at index 2 and subsequent `array_get arr 2` returns 99
  4. `array_length arr` returns the number of elements (not counting the internal length slot)
  5. `array_of_list lst` and `array_to_list arr` round-trip: converting a list to an array and back yields an equal list
**Plans**: 2 plans

Plans:
- [ ] 22-01-PLAN.md — IR extension (LlvmGEPDynamicOp) and C runtime array functions
- [ ] 22-02-PLAN.md — Array builtin elaboration cases and E2E tests

#### Phase 23: Hashtable

**Goal**: Hashtable creation, key-value insertion/lookup/removal, and key enumeration all work correctly
**Depends on**: Phase 22 (array core — hashtable C runtime may share GEP patterns; also needs stable value hashing)
**Requirements**: HT-01, HT-02, HT-03, HT-04, HT-05, HT-06, HT-07, HT-08
**Success Criteria** (what must be TRUE):
  1. `hashtable_create ()` returns a usable empty hashtable; subsequent set/get round-trips correctly
  2. `hashtable_set ht "key" 42` followed by `hashtable_get ht "key"` returns 42
  3. `hashtable_get ht "missing"` raises an exception (does not return garbage)
  4. `hashtable_containsKey ht "key"` returns true after insertion and false after removal
  5. `hashtable_keys ht` returns a cons-cell list containing exactly the currently inserted keys
**Plans**: TBD

Plans:
- [ ] 23-01: TBD

#### Phase 24: Array HOF Builtins

**Goal**: Higher-order array builtins (iter, map, fold, init) work correctly with arbitrary function arguments
**Depends on**: Phase 22 (array core — HOFs operate on arrays produced by core ops)
**Requirements**: ARR-08, ARR-09, ARR-10, ARR-11
**Success Criteria** (what must be TRUE):
  1. `array_iter print arr` calls print on each element in order
  2. `array_map (fun x -> x * 2) arr` returns a new array with each element doubled
  3. `array_fold (fun acc x -> acc + x) 0 arr` returns the sum of all elements
  4. `array_init 5 (fun i -> i * i)` returns an array [0; 1; 4; 9; 16]
**Plans**: TBD

Plans:
- [ ] 24-01: TBD

## Progress

**Execution Order:** 21 → 22 → 23 → 24

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1–20. Foundation–Exceptions | v1.0–v4.0 | 37/37 | Complete | 2026-03-27 |
| 21. Mutable Variables | v5.0 | 2/2 | Complete | 2026-03-27 |
| 22. Array Core | v5.0 | 0/2 | Not started | - |
| 23. Hashtable | v5.0 | 0/? | Not started | - |
| 24. Array HOF Builtins | v5.0 | 0/? | Not started | - |
