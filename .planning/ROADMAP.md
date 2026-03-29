# Roadmap: LangBackend

## Milestones

- ✅ **v1.0 Core Compiler** — Phases 1–6 (shipped 2026-03-26)
- ✅ **v2.0 Data Types & Pattern Matching** — Phases 7–11 (shipped 2026-03-26)
- ✅ **v3.0 Language Completeness** — Phases 12–15 (shipped 2026-03-26)
- ✅ **v4.0 Type System & Error Handling** — Phases 16–20 (shipped 2026-03-27)
- ✅ **v5.0 Mutable & Collections** — Phases 21–24 (shipped 2026-03-28)
- ✅ **v6.0 Modules & I/O** — Phases 25–27 (shipped 2026-03-28)
- ✅ **v7.0 Imperative Syntax** — Phases 28–29 (shipped 2026-03-28)
- ✅ **v8.0 Final Parity** — Phase 30 (shipped 2026-03-28)
- 🚧 **v9.0 Collections & Builtins Parity** — Phases 31–36 (in progress)

## Phases

<details>
<summary>✅ v1.0 – v8.0 (Phases 1–30) — SHIPPED</summary>

See .planning/MILESTONES.md for full history.

- Phases 1–6: Core compiler (MlirIR, Elaboration, CLI) — 15 E2E tests
- Phases 7–11: Data types + pattern matching — 34 E2E tests
- Phases 12–15: Language completeness — 45 E2E tests
- Phases 16–20: ADT/Records/Exceptions — 67 E2E tests
- Phases 21–24: Mutable variables + Array + Hashtable — 92 E2E tests
- Phases 25–27: Module system + File I/O — 118 E2E tests
- Phases 28–29: Imperative syntax (while/for/indexing) — 138 E2E tests
- Phase 30: Type annotations + for-in — 144 E2E tests

</details>

---

### 🚧 v9.0 Collections & Builtins Parity (In Progress)

**Milestone Goal:** LangThree v7.0/v7.1 기능을 컴파일러에 구현하여 Phase 62까지 패리티 달성.
새 컬렉션 타입, 빌트인 함수, 언어 구문, Prelude 모듈을 추가한다.

---

#### Phase 31: String, Char & IO Builtins

**Goal:** Users can compile programs that call the new string manipulation, character inspection, and stderr formatting builtins.
**Depends on:** Phase 30 (v8.0 shipped)
**Requirements:** STR-01, STR-02, STR-03, STR-04, CHR-01, CHR-02, CHR-03, CHR-04, CHR-05, CHR-06, IO-01
**Success Criteria** (what must be TRUE):
  1. A program calling `string_endswith`, `string_startswith`, `string_trim` compiles and returns correct results.
  2. A program calling `string_concat_list` with a separator and a list of strings compiles and returns the joined string.
  3. A program calling all six char builtins (`char_is_digit`, `char_to_upper`, `char_is_letter`, `char_is_upper`, `char_is_lower`, `char_to_lower`) compiles and returns correct values.
  4. A program calling `eprintfn` outputs its message to stderr (not stdout) at runtime.
**Plans:** 3 plans

Plans:
- [x] 31-01-PLAN.md — String builtins: C runtime + Elaboration + 4 E2E tests
- [x] 31-02-PLAN.md — Char builtins: C runtime (ctype.h) + Elaboration + 6 E2E tests
- [x] 31-03-PLAN.md — eprintfn: Elaboration desugaring to eprintln + 1 E2E test

---

#### Phase 32: Hashtable & List/Array Builtins

**Goal:** Users can compile programs that use `hashtable_trygetvalue`, `hashtable_count`, `list_sort_by`, `list_of_seq`, `array_sort`, and `array_of_seq`.
**Depends on:** Phase 31
**Requirements:** HT-01, HT-02, LA-01, LA-02, LA-03, LA-04
**Success Criteria** (what must be TRUE):
  1. A program calling `hashtable_trygetvalue` receives a `(bool, value)` tuple and can pattern-match on the bool.
  2. A program calling `hashtable_count` returns the correct integer element count.
  3. A program calling `list_sort_by` with a comparison closure produces a correctly ordered list.
  4. A program calling `list_of_seq` and `array_of_seq` on a collection returns an equivalent list/array.
  5. A program calling `array_sort` produces a sorted array in place.
**Plans:** 3 plans

Plans:
- [x] 32-01-PLAN.md — Hashtable builtins: hashtable_trygetvalue (tuple return) + hashtable_count (inline GEP) + 2 E2E tests
- [x] 32-02-PLAN.md — List builtins: list_sort_by (closure key extractor) + list_of_seq (identity) + 2 E2E tests
- [x] 32-03-PLAN.md — Array builtins: array_sort (qsort in-place) + array_of_seq (delegates to array_of_list) + 2 E2E tests

---

#### Phase 33: Collection Types

**Goal:** Users can compile programs that create and manipulate the four new collection types: StringBuilder, HashSet, Queue, and MutableList.
**Depends on:** Phase 32
**Requirements:** COL-01, COL-02, COL-03, COL-04
**Success Criteria** (what must be TRUE):
  1. A program creating a `StringBuilder`, calling `add` multiple times, and calling `toString` compiles and returns the concatenated string.
  2. A program creating a `HashSet`, calling `add` and `contains`, compiles and returns correct membership results.
  3. A program creating a `Queue`, calling `enqueue` and `dequeue`, compiles and returns elements in FIFO order.
  4. A program creating a `MutableList`, calling `add`, indexed get/set, and `count`, compiles and returns correct values.
**Plans:** 2 plans

Plans:
- [x] 33-01-PLAN.md — StringBuilder + HashSet (C runtime, Elaboration, E2E tests)
- [x] 33-02-PLAN.md — Queue + MutableList (C runtime, Elaboration, E2E tests)

---

#### Phase 34: Language Constructs

**Goal:** Users can compile programs that use string slicing, list comprehensions, for-in with tuple destructuring, and for-in over the new collection types.
**Depends on:** Phase 33
**Requirements:** LANG-01, LANG-02, LANG-03, LANG-04
**Success Criteria** (what must be TRUE):
  1. A program using `s.[start..end]` or `s.[start..]` string slice syntax compiles and returns the correct substring.
  2. A program using `[for x in coll -> expr]` or `[for i in 0..n -> expr]` list comprehension compiles and returns the correct list.
  3. A program using `for (k, v) in ht do ...` compiles and destructures tuple elements inside the loop body.
  4. A program using for-in over HashSet, Queue, MutableList, and Hashtable compiles and iterates all elements.
**Plans:** 3 plans

Plans:
- [x] 34-01-PLAN.md — String slicing (C runtime helper + E2E tests)
- [x] 34-02-PLAN.md — List comprehension (E2E tests)
- [x] 34-03-PLAN.md — ForInExpr TuplePat + collection for-in (Elaboration + E2E tests)

---

#### Phase 35: Prelude Modules

**Goal:** Users can compile programs that use the String, Hashtable, StringBuilder, Char, List, Array, Option, and Result prelude modules.
**Depends on:** Phase 34
**Requirements:** PRE-01, PRE-02, PRE-03, PRE-04, PRE-05, PRE-06, PRE-07, PRE-08
**Success Criteria** (what must be TRUE):
  1. A program calling `String.endsWith`, `String.startsWith`, `String.trim`, `String.length`, `String.contains` via the String module compiles correctly.
  2. A program calling `Hashtable.tryGetValue` and `Hashtable.count` via the Hashtable module compiles correctly.
  3. A program calling `Char.IsDigit`, `Char.ToUpper`, and other Char module functions compiles correctly.
  4. A program using List and Array extension functions (`sort`, `sortBy`, `ofSeq`, `tryFind`, `choose`, etc.) via their modules compiles and returns correct results.
  5. A program using `Option.map`, `Option.defaultValue`, `Result.map`, `Result.bind` and related utilities compiles and returns correct results.
**Plans:** 3 plans

Plans:
- [ ] 35-01-PLAN.md — String + Hashtable + StringBuilder + Char prelude .fun files + E2E tests
- [ ] 35-02-PLAN.md — Option + Result + List + Array prelude .fun files + E2E tests
- [ ] 35-03-PLAN.md — CLI prelude auto-loading in Program.fs + integration test

---

## Progress

**Execution order:** 31 → 32 → 33 → 34 → 35

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1–30. v1.0–v8.0 | v1.0–v8.0 | 58/58 | Complete | 2026-03-28 |
| 31. String/Char/IO Builtins | v9.0 | 3/3 | Complete | 2026-03-29 |
| 32. Hashtable & List/Array Builtins | v9.0 | 3/3 | Complete | 2026-03-29 |
| 33. Collection Types | v9.0 | 2/2 | Complete | 2026-03-30 |
| 34. Language Constructs | v9.0 | 3/3 | Complete | 2026-03-30 |
| 35. Prelude Modules | v9.0 | 0/3 | Not started | - |
