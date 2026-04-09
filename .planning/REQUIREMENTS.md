# Requirements: v23.0 FunLang v14.0 Sync + Prelude Unification

**Defined:** 2026-04-09
**Core Value:** FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v23.0 Requirements

### Bug Fix

- [ ] **BUG-01**: 함수 파라미터로 받은 string의 `s.[i]` 인덱싱이 올바른 값 반환 (Issue #22)

### Type System Sync

- [ ] **TYPE-01**: `typeNeedsPtr`에 THashSet/TQueue/TMutableList/TStringBuilder 추가 (빌드 경고 해소)
- [ ] **TYPE-02**: `detectCollectionKind`에 새 타입 union case 추가 (silent bug 수정)

### Prelude Unification

- [ ] **PRE-01**: 7개 trivial 파일을 FunLang v14.0 Prelude로 교체 (Array, Char, Hashtable, Int, Queue, String, StringBuilder)
- [ ] **PRE-02**: HashSet.fun — FunLang 복사 + `keys`, `toList` 추가
- [ ] **PRE-03**: MutableList.fun — FunLang 복사 + `toList` 추가
- [ ] **PRE-04**: Core.fun — FunLang 복사 + `char_to_int`, `int_to_char` 보존
- [ ] **PRE-05**: List.fun — FunLang v14.0 multi-param 스타일로 통합
- [ ] **PRE-06**: Option.fun — FunLang v14.0 multi-param 스타일로 통합
- [ ] **PRE-07**: Result.fun — FunLang v14.0 multi-param 스타일로 통합
- [ ] **PRE-08**: Typeclass.fun — FunLang v14.0 multi-param 스타일로 통합

### Submodule

- [ ] **SUB-01**: FunLang 서브모듈을 v14.0 (8da0af2)으로 업데이트 커밋

## Out of Scope

| Feature | Reason |
|---------|--------|
| Prelude 심링크 (FunLang 직접 참조) | 컴파일러 전용 함수 존재, 별도 관리 필요 |
| FunLang Bidir.fs THashSet 수정 | FunLang 쪽 변경 — issue 등록 필요 |
| Issue #21 (type error silent ignore) | 별도 마일스톤에서 처리 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| BUG-01 | Phase 94 | Complete |
| TYPE-01 | Phase 95 | Complete |
| TYPE-02 | Phase 95 | Complete |
| SUB-01 | Phase 95 | Complete |
| PRE-01 | Phase 96 | Pending |
| PRE-02 | Phase 96 | Pending |
| PRE-03 | Phase 96 | Pending |
| PRE-04 | Phase 97 | Pending |
| PRE-05 | Phase 97 | Pending |
| PRE-06 | Phase 97 | Pending |
| PRE-07 | Phase 97 | Pending |
| PRE-08 | Phase 97 | Pending |

**Coverage:**
- v23.0 requirements: 12 total
- Mapped to phases: 12
- Unmapped: 0 ✓

---
*Requirements defined: 2026-04-09*
