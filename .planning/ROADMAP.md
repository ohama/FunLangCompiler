# Roadmap: v13.0 Uniform Tagged Representation

## Overview

OCaml-style LSB 1-bit tagging을 도입하여 모든 값을 i64 하나로 uniform 표현하고, 런타임에서 int/pointer를 즉시 구분할 수 있게 한다. 이를 통해 hashtable `*_str` 함수 7개 중복을 제거하고, Prelude wrapper 함수의 타입 정보 소실 문제를 근본적으로 해결한다.

**Phases:** 3 (88-90)
**Requirements:** 13
**Start:** Phase 88

---

## Phase 88: Tagged Literals and Arithmetic

**Goal:** 컴파일러가 모든 정수/bool/char 리터럴을 2n+1로 인코딩하고, 산술 연산이 tagged 값에서 올바르게 동작한다.

**Dependencies:** None (foundation phase)

**Requirements:** TAG-01, TAG-02, TAG-03, ARITH-01, ARITH-02, ARITH-03, ARITH-04

**Plans:** 3 plans

Plans:
- [ ] 88-01-PLAN.md — IR infrastructure (ArithShRSIOp, ArithShLIOp, ArithOrIOp + helpers)
- [ ] 88-02-PLAN.md — Core tagging (literals, arithmetic ops, truthiness, @main return)
- [ ] 88-03-PLAN.md — C boundary untag/retag + full test verification

**Success Criteria:**

1. `fnc` 로 컴파일한 프로그램에서 정수 리터럴 42가 LLVM IR에서 85 (2*42+1)로 출력된다
2. `true`는 3, `false`는 1, `char 'A'`는 131로 인코딩된다
3. `10 + 20`이 30을 반환하고, `10 - 3`이 7을 반환한다 (tag 보정 적용)
4. `6 * 7`이 42를, `10 / 3`이 3을, `10 % 3`이 1을 반환한다 (untag-op-retag)
5. `3 < 5`가 true, `-a`가 올바른 부정값을 반환한다 (tagged 직접 비교 + 단항 부정)

---

## Phase 89: C Runtime Adaptation

**Goal:** C 런타임 함수들이 tagged 정수를 올바르게 untag/retag 하여, int 입출력/char 변환/array 인덱싱이 정상 동작한다.

**Dependencies:** Phase 88 (tagged literals must be in place)

**Requirements:** RT-02, RT-03, RT-04

**Success Criteria:**

1. `print_int 42`가 "42"를 출력하고, `int_to_string 42`가 "42" 문자열을 반환한다 (tagged 85를 untag 후 처리)
2. `char_to_int 'A'`가 65를, `int_to_char 65`가 'A'를 반환한다 (tag/untag 적용)
3. `arr.[2]`로 배열 세 번째 원소에 접근하고, `arr.[2] <- v`로 설정할 수 있다 (인덱스 untag)

---

## Phase 90: Hashtable Unification and Compatibility

**Goal:** Hashtable 7개 `*_str` 변종이 LSB dispatch 기반 통합 함수로 대체되고, 인터프리터 호환성이 유지되며, 전체 테스트가 통과한다.

**Dependencies:** Phase 89 (runtime tagging infrastructure must work)

**Requirements:** RT-01, COMPAT-01, COMPAT-02

**Success Criteria:**

1. `hashtable_create()` 하나로 int-key와 string-key 해시테이블 모두 생성/조회/삽입 가능하다 (LSB dispatch)
2. 인터프리터에서 `hashtable_create`와 `hashtable_create_str` 양쪽 이름 모두 동작한다 (하위 호환)
3. 기존 723+ flt 테스트 전체 통과한다
4. Prelude/Hashtable.fun에서 `*_str` 변종 호출이 제거되고 통합 함수만 사용한다

---

## Progress

| Phase | Name | Requirements | Status |
|-------|------|-------------|--------|
| 88 | Tagged Literals and Arithmetic | TAG-01, TAG-02, TAG-03, ARITH-01, ARITH-02, ARITH-03, ARITH-04 | Complete |
| 89 | C Runtime Adaptation | RT-02, RT-03, RT-04 | Complete |
| 90 | Hashtable Unification and Compatibility | RT-01, COMPAT-01, COMPAT-02 | Pending |

---

## Coverage

| Requirement | Phase | Category |
|-------------|-------|----------|
| TAG-01 | 88 | Tagged Value |
| TAG-02 | 88 | Tagged Value |
| TAG-03 | 88 | Tagged Value |
| ARITH-01 | 88 | Arithmetic |
| ARITH-02 | 88 | Arithmetic |
| ARITH-03 | 88 | Arithmetic |
| ARITH-04 | 88 | Arithmetic |
| RT-02 | 89 | C Runtime |
| RT-03 | 89 | C Runtime |
| RT-04 | 89 | C Runtime |
| RT-01 | 90 | C Runtime |
| COMPAT-01 | 90 | Compatibility |
| COMPAT-02 | 90 | Compatibility |

**Mapped: 13/13** -- no orphans, no duplicates.

---
*Created: 2026-04-07 -- v13.0 Uniform Tagged Representation*
