# Requirements: v13.1 Tagged Representation Extensions

## HashSet Unification (HS)

- [ ] **HS-01**: HashSet의 int/string 값을 LSB dispatch로 통합 (hashset_add, hashset_contains에서 타입별 분기 제거)
- [ ] **HS-02**: HashSet C struct 통합 — tagged 값 as-is 저장, unified hash/equality
- [ ] **HS-03**: for_in_hashset이 tagged 값을 직접 전달 (retag 불필요)

## C Boundary Simplification (CB)

- [ ] **CB-01**: C 런타임 함수가 tagged 정수를 직접 받아 내부에서 untag (int_to_string, print_int 등)
- [ ] **CB-02**: C 런타임 함수가 tagged 정수를 직접 반환하여 retag (string_to_int, string_length, array_length 등)
- [ ] **CB-03**: Elaboration.fs에서 C 호출 전후 emitUntag/emitRetag 제거 (~50곳 단순화)
- [ ] **CB-04**: lang_range, lang_array_create, lang_array_bounds_check 등이 tagged 인자를 직접 처리

## Generic Equality and Hash (GE)

- [ ] **GE-01**: 힙 블록 header에 tag byte 추가 (string, tuple, record, list, ADT 구분)
- [ ] **GE-02**: Generic hash 함수 — LSB로 int/ptr 구분 후, header tag로 세부 타입별 hash
- [ ] **GE-03**: Generic equality 함수 — 구조적 비교 (int: 직접, string: memcmp, tuple/record: 필드별 재귀)
- [ ] **GE-04**: Hashtable/HashSet이 generic hash/equality 사용하여 tuple/record 키 지원
- [ ] **GE-05**: 257+ E2E 테스트 전체 통과 유지

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| HS-01 | Phase 91 | Complete |
| HS-02 | Phase 91 | Complete |
| HS-03 | Phase 91 | Complete |
| CB-01 | Phase 92 | Complete |
| CB-02 | Phase 92 | Complete |
| CB-03 | Phase 92 | Complete |
| CB-04 | Phase 92 | Complete |
| GE-01 | Phase 93 | Complete |
| GE-02 | Phase 93 | Complete |
| GE-03 | Phase 93 | Complete |
| GE-04 | Phase 93 | Complete |
| GE-05 | Phase 93 | Complete |

---
*Created: 2026-04-07 — 12 requirements across 3 categories*
