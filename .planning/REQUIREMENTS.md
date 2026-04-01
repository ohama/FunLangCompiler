# Requirements: v14.0 FunLang Standard Library Sync

## Milestone Requirements

### String Module Extension

- [ ] **STR-01**: `String.split s sep` — 문자열을 구분자로 분리하여 리스트 반환 (C 런타임 `string_split` 필요)
- [ ] **STR-02**: `String.indexOf s sub` — 부분 문자열 위치 반환 (C 런타임 `string_indexof` 필요)
- [ ] **STR-03**: `String.replace s old new` — 부분 문자열 치환 (C 런타임 `string_replace` 필요)
- [ ] **STR-04**: `String.toUpper s` — 대문자 변환 (C 런타임 `string_toupper` 필요)
- [ ] **STR-05**: `String.toLower s` — 소문자 변환 (C 런타임 `string_tolower` 필요)
- [ ] **STR-06**: `String.join sep lst` — `string_concat_list` 별칭 (런타임 변경 없음)
- [ ] **STR-07**: `String.substring s start len` — `string_sub` 별칭 (런타임 변경 없음)

### List Module Extension

- [ ] **LIST-01**: 17개 새 List 함수 Prelude 동기화 — init, find, findIndex, partition, groupBy, scan, replicate, collect, pairwise, sumBy, sum, minBy, maxBy, contains, unzip, forall, iter (순수 FunLang 구현, 기존 primitive 기반)

### Runtime & Elaboration

- [ ] **RT-01**: C 런타임 `lang_string_split` 구현 — 구분자로 문자열 분리, cons list 반환
- [ ] **RT-02**: C 런타임 `lang_string_indexof` 구현 — strstr 기반 부분 문자열 검색
- [ ] **RT-03**: C 런타임 `lang_string_replace` 구현 — 모든 occurrence 치환
- [ ] **RT-04**: C 런타임 `lang_string_toupper` 구현 — toupper 루프
- [ ] **RT-05**: C 런타임 `lang_string_tolower` 구현 — tolower 루프
- [ ] **RT-06**: Elaboration.fs에 5개 새 string builtin dispatch 추가 (string_split, string_indexof, string_replace, string_toupper, string_tolower)

### Testing

- [ ] **TEST-01**: String 새 함수 E2E 테스트 (split, indexOf, replace, toUpper, toLower, join, substring)
- [ ] **TEST-02**: List 새 함수 E2E 테스트 (17개 함수 각각)

## Future Requirements

None for this milestone scope.

## Out of Scope

- REPL 개선 (v14.0 FunLang REPL 변경) — 배치 컴파일러와 무관
- Multi-param lambda — FunLang 파서가 자동 desugar하므로 컴파일러 변경 불필요
- LangThree → FunLang 리네이밍 — 이미 완료

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| STR-01 | Phase 54 | Pending |
| STR-02 | Phase 54 | Pending |
| STR-03 | Phase 54 | Pending |
| STR-04 | Phase 54 | Pending |
| STR-05 | Phase 54 | Pending |
| STR-06 | Phase 54 | Pending |
| STR-07 | Phase 54 | Pending |
| RT-01 | Phase 54 | Pending |
| RT-02 | Phase 54 | Pending |
| RT-03 | Phase 54 | Pending |
| RT-04 | Phase 54 | Pending |
| RT-05 | Phase 54 | Pending |
| RT-06 | Phase 54 | Pending |
| LIST-01 | Phase 55 | Pending |
| TEST-01 | Phase 56 | Pending |
| TEST-02 | Phase 56 | Pending |

---
*Created: 2026-04-01*
