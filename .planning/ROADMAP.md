# Roadmap: v13.1 Tagged Representation Extensions

## Overview

v13.0의 tagged representation 기반 위에 HashSet 통합, C boundary 단순화, generic equality/hash를 추가한다. Elaboration.fs의 ~50곳 untag/retag 제거로 코드 단순화, tuple/record 키를 hashtable에서 사용 가능하게 한다.

**Phases:** 3 (91-93)
**Requirements:** 12
**Start:** Phase 91

---

## Phase 91: HashSet LSB Unification

**Goal:** HashSet이 Hashtable과 동일한 LSB dispatch 패턴을 사용하여 int/string 값을 통합 처리한다.

**Dependencies:** Phase 90 (Hashtable unification pattern)

**Requirements:** HS-01, HS-02, HS-03

**Success Criteria:**

1. `hashset_add hs 42` 와 `hashset_add hs "hello"` 가 같은 C 함수로 동작
2. for_in_hashset이 tagged 값을 직접 전달 (LANG_TAG_INT 불필요)
3. 257+ E2E 테스트 전체 통과

---

## Phase 92: C Boundary Simplification

**Goal:** C 런타임 함수가 tagged 정수를 직접 받고 반환하여, Elaboration.fs의 C 호출 전후 emitUntag/emitRetag를 제거한다.

**Dependencies:** Phase 91 (HashSet도 tagged 값 사용)

**Requirements:** CB-01, CB-02, CB-03, CB-04

**Plans:** 2 plans

Plans:
- [ ] 92-01-PLAN.md — Simple C boundary sites (char/to_string/sprintf/string-int/counts/mutablelist)
- [ ] 92-02-PLAN.md — Structural changes (new C wrappers, array access, index dispatch)

**Success Criteria:**

1. `lang_to_string_int(tagged_val)`가 내부에서 untag 후 변환
2. `lang_string_length(str)`가 tagged length를 직접 반환
3. Elaboration.fs에서 C 호출 전후 emitUntag/emitRetag 코드 제거 (~50곳)
4. 257+ E2E 테스트 전체 통과

---

## Phase 93: Generic Equality and Hash

**Goal:** 힙 블록 header tag로 string/tuple/record/list/ADT를 구분하고, generic hash/equality로 모든 값을 hashtable 키로 사용 가능하게 한다.

**Dependencies:** Phase 92 (C runtime이 tagged 값을 직접 처리)

**Requirements:** GE-01, GE-02, GE-03, GE-04, GE-05

**Success Criteria:**

1. 힙 블록에 tag byte가 있어 string(252), tuple(0), record(1), list(2), ADT(3) 구분 가능
2. `hashtable_set ht (1, "a") 42` — tuple key로 hashtable 사용 가능
3. `(1, "a") = (1, "a")` — generic structural equality 동작
4. 257+ E2E 테스트 전체 통과

---

## Progress

| Phase | Name | Requirements | Status |
|-------|------|-------------|--------|
| 91 | HashSet LSB Unification | HS-01, HS-02, HS-03 | Complete |
| 92 | C Boundary Simplification | CB-01, CB-02, CB-03, CB-04 | Pending |
| 93 | Generic Equality and Hash | GE-01, GE-02, GE-03, GE-04, GE-05 | Pending |

---

## Coverage

| Requirement | Phase | Category |
|-------------|-------|----------|
| HS-01 | 91 | HashSet |
| HS-02 | 91 | HashSet |
| HS-03 | 91 | HashSet |
| CB-01 | 92 | C Boundary |
| CB-02 | 92 | C Boundary |
| CB-03 | 92 | C Boundary |
| CB-04 | 92 | C Boundary |
| GE-01 | 93 | Generic Eq/Hash |
| GE-02 | 93 | Generic Eq/Hash |
| GE-03 | 93 | Generic Eq/Hash |
| GE-04 | 93 | Generic Eq/Hash |
| GE-05 | 93 | Generic Eq/Hash |

**Mapped: 12/12** — no orphans, no duplicates.

---
*Created: 2026-04-07 — v13.1 Tagged Representation Extensions*
