# Requirements: LangBackend v5.0

**Defined:** 2026-03-27
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements

Requirements for v5.0 milestone. Each maps to roadmap phases.

### Mutable Variables

- [ ] **MUT-01**: LetMut elaboration — `let mut x = e in body` allocates GC_malloc'd ref cell, binds name to ref cell pointer
- [ ] **MUT-02**: Assign elaboration — `x <- e` stores new value into ref cell, returns unit
- [ ] **MUT-03**: Var transparent deref — `Var(name)` where name is mutable emits LlvmLoadOp through ref cell
- [ ] **MUT-04**: LetMutDecl module-level — `let mut x = e` at top-level via extractMainExpr extension
- [ ] **MUT-05**: freeVars extension — LetMut/Assign cases for correct closure capture of mutable variables
- [ ] **MUT-06**: Closure capture of mutable ref cell — closure captures the ref cell pointer (not the value), mutations visible through closures

### Array

- [ ] **ARR-01**: array_create builtin — `array_create n default` allocates (n+1)*8 block, slot 0 = length, slots 1..n = default
- [ ] **ARR-02**: array_get builtin — bounds check + GEP(i+1) + load; raises exception on out-of-bounds
- [ ] **ARR-03**: array_set builtin — bounds check + GEP(i+1) + store; returns unit; raises on out-of-bounds
- [ ] **ARR-04**: array_length builtin — GEP slot 0 + load length
- [ ] **ARR-05**: array_of_list builtin — convert cons-cell list to array
- [ ] **ARR-06**: array_to_list builtin — convert array to cons-cell list
- [ ] **ARR-07**: Dynamic index GEP — LlvmGEPDynamicOp or equivalent for SSA-value array indexing
- [ ] **ARR-08**: array_iter builtin — `(a -> unit) -> a array -> unit` iterate and call function
- [ ] **ARR-09**: array_map builtin — `(a -> b) -> a array -> b array` map function over array
- [ ] **ARR-10**: array_fold builtin — `(acc -> a -> acc) -> acc -> a array -> acc` left fold
- [ ] **ARR-11**: array_init builtin — `int -> (int -> a) -> a array` construct from index function

### Hashtable

- [ ] **HT-01**: C runtime hashtable — LangHashtable struct with open-addressing or chaining, GC_malloc'd
- [ ] **HT-02**: hashtable_create builtin — `unit -> hashtable` allocates empty hashtable
- [ ] **HT-03**: hashtable_get builtin — lookup key, raise exception on missing key
- [ ] **HT-04**: hashtable_set builtin — insert/update key-value pair, return unit
- [ ] **HT-05**: hashtable_containsKey builtin — return bool (i64 0/1)
- [ ] **HT-06**: hashtable_keys builtin — return cons-cell list of all keys
- [ ] **HT-07**: hashtable_remove builtin — remove key entry, return unit
- [ ] **HT-08**: Value hashing — C runtime hash function for boxed int, string, tuple, ADT values

### Regression

- [ ] **REG-01**: 기존 67개 E2E 테스트 전체 통과 유지

## v2 Requirements

Deferred to future release.

### Advanced Features

- **ADV-01**: Array literal syntax `[| e1; e2 |]` — requires LangThree parser changes
- **ADV-02**: Unboxed integer arrays — requires monomorphization
- **ADV-03**: hashtable_values, hashtable_size builtins — not in current LangThree builtin set

## Out of Scope

| Feature | Reason |
|---------|--------|
| Array literal syntax `[| ... |]` | LangThree parser에 토큰 없음 |
| Hashtable literal syntax | LangThree parser에 문법 없음 |
| Mutable variable polymorphism | LangThree type checker가 이미 제한 (value restriction) |
| RefValue first-class type | 평가기 내부 구현 세부사항, 타입 시스템에 노출되지 않음 |
| array_fill / array_copy / Array.blit | LangThree builtin set에 없음 |
| hashtable_values builtin | LangThree builtin set에 없음 |
| Hashtable iteration order guarantees | .NET Dictionary 동작과 일치 (미지정 순서) |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| MUT-01 | Phase 21 | Pending |
| MUT-02 | Phase 21 | Pending |
| MUT-03 | Phase 21 | Pending |
| MUT-04 | Phase 21 | Pending |
| MUT-05 | Phase 21 | Pending |
| MUT-06 | Phase 21 | Pending |
| ARR-01 | Phase 22 | Pending |
| ARR-02 | Phase 22 | Pending |
| ARR-03 | Phase 22 | Pending |
| ARR-04 | Phase 22 | Pending |
| ARR-05 | Phase 22 | Pending |
| ARR-06 | Phase 22 | Pending |
| ARR-07 | Phase 22 | Pending |
| ARR-08 | Phase 24 | Pending |
| ARR-09 | Phase 24 | Pending |
| ARR-10 | Phase 24 | Pending |
| ARR-11 | Phase 24 | Pending |
| HT-01 | Phase 23 | Pending |
| HT-02 | Phase 23 | Pending |
| HT-03 | Phase 23 | Pending |
| HT-04 | Phase 23 | Pending |
| HT-05 | Phase 23 | Pending |
| HT-06 | Phase 23 | Pending |
| HT-07 | Phase 23 | Pending |
| HT-08 | Phase 23 | Pending |
| REG-01 | All phases | Pending |

**Coverage:**
- v1 requirements: 26 total
- Mapped to phases: 26/26
- Unmapped: 0

---
*Requirements defined: 2026-03-27*
*Last updated: 2026-03-27 — traceability filled after roadmap creation*
