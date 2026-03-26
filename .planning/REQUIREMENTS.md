# Requirements: LangBackend v3.0

**Defined:** 2026-03-26
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v3 Requirements

### Operators

- [ ] **OP-01**: Modulo 연산자 (`%`) → `arith.remsi`
- [ ] **OP-02**: Char 리터럴 (`'A'`) → `int64` 변환 후 I64 처리
- [ ] **OP-03**: PipeRight (`x |> f`) → `App(f, x)` 디슈가
- [ ] **OP-04**: ComposeRight (`f >> g`) → `fun x -> g (f x)` 클로저 생성
- [ ] **OP-05**: ComposeLeft (`f << g`) → `fun x -> f (g x)` 클로저 생성

### Pattern Matching Extensions

- [ ] **PAT-06**: `when` 가드 — 패턴 매치 후 조건식 평가, false면 다음 arm
- [ ] **PAT-07**: OrPat (`| P1 | P2 -> body`) — 여러 패턴이 같은 body 공유
- [ ] **PAT-08**: ConstPat(CharConst) — char 리터럴 패턴 매칭 (`arith.cmpi eq`)

### Builtins

- [ ] **BLT-01**: `failwith msg` — stderr에 메시지 출력 후 exit(1)
- [ ] **BLT-02**: `string_sub s start len` — 부분 문자열 추출
- [ ] **BLT-03**: `string_contains s sub` — 부분 문자열 포함 여부 (bool)
- [ ] **BLT-04**: `string_to_int s` — 문자열 → 정수 변환 (`atoi`)
- [ ] **BLT-05**: `char_to_int c` — char → int 변환
- [ ] **BLT-06**: `int_to_char n` — int → char 변환
- [ ] **BLT-07**: `print`/`println` 변수 문자열 지원 (현재 리터럴만)

### Range

- [ ] **RNG-01**: `[start..stop]` — 정수 리스트 생성 (start부터 stop까지)
- [ ] **RNG-02**: `[start..step..stop]` — step 간격 정수 리스트 생성

## Future Requirements (v4.0)

### ADT (대수 데이터 타입)

- **ADT-01**: type 선언 (Constructor 정의)
- **ADT-02**: Constructor 식 (값 생성)
- **ADT-03**: ConstructorPat (패턴 매칭)

### Records

- **REC-01**: RecordExpr (레코드 생성)
- **REC-02**: FieldAccess (필드 접근)
- **REC-03**: RecordUpdate (레코드 갱신)
- **REC-04**: SetField (뮤터블 필드)

### Exceptions

- **EXN-01**: Raise (예외 발생)
- **EXN-02**: TryWith (예외 처리)

## Out of Scope

| Feature | Reason |
|---------|--------|
| REPL | 인터프리터가 이미 존재함 |
| Module 시스템 | v4.0 이후 |
| Array/Hashtable | v4.0 이후 |
| File I/O 빌트인 | v4.0 이후 |
| stdin/stdout 고급 I/O | v4.0 이후 |
| printf/sprintf 포맷 문자열 | 복잡도 높음, v4.0 이후 |
| TCO | LLVM 자동 처리에 의존 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| OP-01 | Phase 12 | Pending |
| OP-02 | Phase 12 | Pending |
| OP-03 | Phase 12 | Pending |
| OP-04 | Phase 12 | Pending |
| OP-05 | Phase 12 | Pending |
| PAT-06 | Phase 13 | Pending |
| PAT-07 | Phase 13 | Pending |
| PAT-08 | Phase 13 | Pending |
| BLT-01 | Phase 14 | Pending |
| BLT-02 | Phase 14 | Pending |
| BLT-03 | Phase 14 | Pending |
| BLT-04 | Phase 14 | Pending |
| BLT-05 | Phase 14 | Pending |
| BLT-06 | Phase 14 | Pending |
| BLT-07 | Phase 14 | Pending |
| RNG-01 | Phase 15 | Pending |
| RNG-02 | Phase 15 | Pending |

**Coverage:**
- v3 requirements: 17 total
- Mapped to phases: 17
- Unmapped: 0 ✓

---
*Requirements defined: 2026-03-26*
