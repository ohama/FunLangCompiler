# Research Summary: v23.0 FunLang v14.0 Sync + Prelude Unification

## Key Findings

### 1. Annotated Prelude는 컴파일러 변경 없이 동작
- FunLang 파서가 project reference로 공유됨 — v14.0 파서가 이미 적용
- `StripAnnot` active pattern이 Annot/LambdaAnnot을 투명하게 제거
- Multi-param 스타일 (`let f (x:int) (y:int) = ...`)도 동일하게 elaboration

### 2. `__pipe_x` 등 특수 파라미터명은 컴파일러에서 참조 없음
- grep 결과 0건 — 단순 역사적 네이밍, 변경 안전

### 3. Prelude 파일별 동기화 전략
- **7 COPY**: Array, Char, Hashtable, Int, Queue, String, StringBuilder
- **2 COPY+APPEND**: HashSet (+keys, toList), MutableList (+toList)
- **5 MANUAL_MERGE**: Core (char_to_int/int_to_char 보존), List, Option, Result, Typeclass (currying 스타일)

### 4. ElabHelpers.fs 2곳 수정 필요
- `typeNeedsPtr` (line 618): THashSet/TQueue/TMutableList/TStringBuilder 누락
- `detectCollectionKind` (line 131): 새 타입 union case 누락 (경고 없는 silent bug)

### 5. Issue #22: String 파라미터 인덱싱 버그
- 함수 파라미터로 받은 string의 `s.[i]`가 잘못된 값 반환
- FunLexYacc 런타임 블로킹 — 최우선 수정 필요

## Confidence

| Area | Level |
|------|-------|
| Prelude annotated 호환성 | HIGH |
| Trivial copy (7 files) | HIGH |
| COPY+APPEND (2 files) | HIGH |
| MANUAL_MERGE currying 스타일 | MEDIUM (실험적 검증 필요) |
| ElabHelpers.fs 수정 | HIGH |
| Issue #22 | LOW (근본 원인 조사 필요) |
