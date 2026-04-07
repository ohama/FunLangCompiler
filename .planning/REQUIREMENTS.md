# Requirements: v13.0 Uniform Tagged Representation

## Tagged Value Representation (TAG)

- [ ] **TAG-01**: 정수를 `2n+1` (LSB=1)로 인코딩, 포인터는 그대로 (LSB=0, 힙 정렬 보장)
- [ ] **TAG-02**: Boolean/char/unit을 tagged integer로 인코딩 (false=1, true=3, char='A'→131)
- [ ] **TAG-03**: 런타임에서 `val & 1`로 int/pointer 즉시 구분 가능

## Arithmetic (ARITH)

- [ ] **ARITH-01**: 덧셈/뺄셈에 tag 보정 적용 (`a+b-1`, `a-b+1`)
- [ ] **ARITH-02**: 곱셈/나눗셈/나머지에 untag→연산→retag 적용
- [ ] **ARITH-03**: 비교 연산은 tagged 상태에서 직접 비교 (변경 없음 확인)
- [ ] **ARITH-04**: 단항 부정 `-a` → `2-a` 적용

## C Runtime Unification (RT)

- [ ] **RT-01**: Hashtable 7개 `*_str` 함수를 LSB dispatch로 통합 (create, get, set, containsKey, keys, remove, trygetvalue)
- [ ] **RT-02**: int 입출력 함수에 untag 추가 (int_to_string, print_int)
- [ ] **RT-03**: char 변환 함수에 tag/untag 추가 (char_to_int, int_to_char)
- [ ] **RT-04**: Array 인덱스 함수에 untag 추가 (array_get, array_set)

## Interpreter Compatibility (COMPAT)

- [ ] **COMPAT-01**: 인터프리터에서 tagged/untagged 양쪽 builtin 인식 (hashtable_create + hashtable_create_str 모두 동작)
- [ ] **COMPAT-02**: 기존 723 flt 테스트 전체 통과 유지

## Future (deferred to later milestones)

- Generic equality (`(=)` 하나로 int/string 비교)
- Generic hash (모든 타입 key 가능)
- Generic to_string (런타임 타입 판별 출력)
- 힙 블록 header tag byte (tuple/record/string 세부 구분)

## Out of Scope

- 64-bit 정수 전체 범위 보존 — 63-bit로 충분 (±2.3×10^18)
- Float 타입 지원 — 현재 미지원, 별도 milestone
- OCaml 수준의 완전한 structural equality — LSB int/ptr 구분만으로 충분

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| TAG-01 | Phase 88 | Complete |
| TAG-02 | Phase 88 | Complete |
| TAG-03 | Phase 88 | Complete |
| ARITH-01 | Phase 88 | Complete |
| ARITH-02 | Phase 88 | Complete |
| ARITH-03 | Phase 88 | Complete |
| ARITH-04 | Phase 88 | Complete |
| RT-01 | Phase 90 | Complete |
| RT-02 | Phase 89 | Complete |
| RT-03 | Phase 89 | Complete |
| RT-04 | Phase 89 | Complete |
| COMPAT-01 | Phase 90 | Complete |
| COMPAT-02 | Phase 90 | Complete |

---
*Created: 2026-04-07 -- 13 requirements across 4 categories*
