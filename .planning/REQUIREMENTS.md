# Requirements: LangBackend v9.0

**Defined:** 2026-03-29
**Core Value:** LangThree v7.0/v7.1 기능을 컴파일러에 구현하여 Phase 62까지 패리티 달성

## v9.0 Requirements

### String Builtins

- [ ] **STR-01**: `string_endswith` — 문자열 끝 검사 (C runtime lang_string_endswith)
- [ ] **STR-02**: `string_startswith` — 문자열 시작 검사 (C runtime lang_string_startswith)
- [ ] **STR-03**: `string_trim` — 문자열 공백 제거 (C runtime lang_string_trim)
- [ ] **STR-04**: `string_concat_list` — 구분자 + 리스트 → 결합 문자열 (C runtime lang_string_concat_list)

### Char Builtins

- [ ] **CHR-01**: `char_is_digit` — 숫자 판별 (elaboration to inline compare)
- [ ] **CHR-02**: `char_to_upper` — 대문자 변환
- [ ] **CHR-03**: `char_is_letter` — 문자 판별
- [ ] **CHR-04**: `char_is_upper` — 대문자 판별
- [ ] **CHR-05**: `char_is_lower` — 소문자 판별
- [ ] **CHR-06**: `char_to_lower` — 소문자 변환

### Hashtable Builtins

- [ ] **HT-01**: `hashtable_trygetvalue` — (bool, value) 튜플 반환 (C runtime)
- [ ] **HT-02**: `hashtable_count` — 해시테이블 요소 수 반환 (C runtime)

### List/Array Builtins

- [ ] **LA-01**: `list_sort_by` — 비교 함수로 리스트 정렬 (C runtime, closure callback)
- [ ] **LA-02**: `list_of_seq` — 컬렉션을 리스트로 변환 (C runtime)
- [ ] **LA-03**: `array_sort` — 배열 정렬 (C runtime)
- [ ] **LA-04**: `array_of_seq` — 컬렉션을 배열로 변환 (C runtime)

### IO Builtins

- [ ] **IO-01**: `eprintfn` — stderr 포맷 출력 (printf/printfn과 동일 패턴, stderr 대상)

### Collection Types

- [ ] **COL-01**: StringBuilder — 생성, add, toString (C runtime struct + 3 함수)
- [ ] **COL-02**: HashSet — 생성, add, contains, count (C runtime struct + 4 함수)
- [ ] **COL-03**: Queue — 생성, enqueue, dequeue, count (C runtime struct + 4 함수)
- [ ] **COL-04**: MutableList — 생성, add, index get/set, count (C runtime struct + 5 함수)

### Language Constructs

- [ ] **LANG-01**: String slicing — `s.[start..end]` (inclusive), `s.[start..]` (끝까지) 구문 컴파일
- [ ] **LANG-02**: List comprehension — `[for x in coll -> expr]`, `[for i in 0..n -> expr]` 컴파일
- [ ] **LANG-03**: ForInExpr 패턴 분해 — `for (k, v) in ht do ...` 튜플 디스트럭처링 컴파일
- [ ] **LANG-04**: Collection for-in — HashSet, Queue, MutableList, Hashtable 순회 컴파일

### Prelude Modules

- [ ] **PRE-01**: String 모듈 — endsWith, startsWith, trim, length, contains 함수
- [ ] **PRE-02**: Hashtable 모듈 — tryGetValue, count 함수
- [ ] **PRE-03**: StringBuilder 모듈 — add, toString 함수
- [ ] **PRE-04**: Char 모듈 — IsDigit, ToUpper, IsLetter, IsUpper, IsLower, ToLower 함수
- [ ] **PRE-05**: List 확장 — sort, sortBy, tryFind, choose, distinctBy, exists, mapi, item, isEmpty, head, tail, ofSeq
- [ ] **PRE-06**: Array 확장 — sort, ofSeq
- [ ] **PRE-07**: Option 유틸리티 — map, bind, defaultValue, iter, filter, isSome, isNone
- [ ] **PRE-08**: Result 유틸리티 — map, bind, defaultValue, mapError, toOption

## Out of Scope

| Feature | Reason |
|---------|--------|
| Dot dispatch (.Length, .Count, obj.Method()) | LangThree v7.1에서 제거됨 — module function API로 대체 |
| KeyValuePair record | LangThree v7.1에서 tuple로 대체됨 |
| printf/sprintf 포맷 문자열 | 이전 milestone에서 제외, 복잡도 높음 |
| Generic type parameters | 동적 타이핑으로 충분 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| STR-01 | Phase 31 | Pending |
| STR-02 | Phase 31 | Pending |
| STR-03 | Phase 31 | Pending |
| STR-04 | Phase 31 | Pending |
| CHR-01 | Phase 31 | Pending |
| CHR-02 | Phase 31 | Pending |
| CHR-03 | Phase 31 | Pending |
| CHR-04 | Phase 31 | Pending |
| CHR-05 | Phase 31 | Pending |
| CHR-06 | Phase 31 | Pending |
| IO-01 | Phase 31 | Pending |
| HT-01 | Phase 32 | Pending |
| HT-02 | Phase 32 | Pending |
| LA-01 | Phase 32 | Pending |
| LA-02 | Phase 32 | Pending |
| LA-03 | Phase 32 | Pending |
| LA-04 | Phase 32 | Pending |
| COL-01 | Phase 33 | Pending |
| COL-02 | Phase 33 | Pending |
| COL-03 | Phase 33 | Pending |
| COL-04 | Phase 33 | Pending |
| LANG-01 | Phase 34 | Pending |
| LANG-02 | Phase 34 | Pending |
| LANG-03 | Phase 34 | Pending |
| LANG-04 | Phase 34 | Pending |
| PRE-01 | Phase 35 | Pending |
| PRE-02 | Phase 35 | Pending |
| PRE-03 | Phase 35 | Pending |
| PRE-04 | Phase 35 | Pending |
| PRE-05 | Phase 35 | Pending |
| PRE-06 | Phase 35 | Pending |
| PRE-07 | Phase 35 | Pending |
| PRE-08 | Phase 35 | Pending |

**Coverage:**
- v9.0 requirements: 33 total (4 STR + 6 CHR + 1 IO + 2 HT + 4 LA + 4 COL + 4 LANG + 8 PRE)
- Mapped to phases: 33
- Unmapped: 0

**Note:** The original requirement count of "30" in the header was incorrect. Actual count is 33 (confirmed by enumerating all requirement IDs above).

---
*Requirements defined: 2026-03-29*
*Traceability added: 2026-03-29*
