# Requirements: LangBackend

**Defined:** 2026-03-26
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements (Complete)

### Infrastructure

- [x] **INFRA-01**: MlirIR 정의 — MLIR 개념을 F# DU로 모델링한 컴파일러 내부 IR
- [x] **INFRA-02**: MlirIR → `.mlir` 텍스트 프린터
- [x] **INFRA-03**: `mlir-opt` lowering 파이프라인
- [x] **INFRA-04**: `mlir-translate` + `clang` shell pipeline
- [x] **INFRA-05**: E2E 스모크 테스트

### Scalar Codegen

- [x] **SCALAR-01**: 정수 리터럴 → `arith.constant` (i64)
- [x] **SCALAR-02**: 산술 연산 → `arith.addi/subi/muli/divsi`
- [x] **SCALAR-03**: 비교 연산 → `arith.cmpi`
- [x] **SCALAR-04**: 불리언 리터럴 → `arith.constant i1`
- [x] **SCALAR-05**: 논리 연산 (&&, ||) → `cf` 분기

### Control Flow & Bindings

- [x] **CTRL-01**: `if-else` → `cf.cond_br` + basic block arguments
- [x] **CTRL-02**: `let` 바인딩 → SSA value 이름 바인딩
- [x] **CTRL-03**: 변수 참조 → SSA value 조회

### Elaboration

- [x] **ELAB-01**: LangThree AST → MlirIR 변환 패스
- [x] **ELAB-02**: `let rec` elaboration → MlirIR `FuncOp`
- [x] **ELAB-03**: Lambda elaboration → MlirIR closure 표현
- [x] **ELAB-04**: 함수 적용 → direct/indirect call 구분

### CLI & Testing

- [x] **CLI-01**: `.lt` 파일 → 네이티브 바이너리 CLI
- [x] **CLI-02**: LangThree `.fsproj` project reference
- [x] **TEST-01**: FsLit 파일 기반 테스트
- [x] **TEST-02**: 각 기능 카테고리별 `.flt` 테스트 파일

## v2 Requirements

### GC Runtime

- [ ] **GC-01**: Boehm GC 런타임 통합 — `GC_INIT()` + `GC_malloc` 외부 함수 선언, `-lgc` 링크
- [ ] **GC-02**: v1 클로저 `llvm.alloca` → `GC_malloc` 마이그레이션 (힙 탈출 안전성)
- [ ] **GC-03**: `print` / `println` 빌트인 — `printf` libc 호출로 stdout 출력

### String

- [ ] **STR-01**: 문자열 리터럴 → 힙 할당 `{i64 length, ptr data}` 구조체
- [ ] **STR-02**: 문자열 동등 비교 (`=`, `<>`) → `strcmp` libc 호출
- [ ] **STR-03**: `string_length` 빌트인
- [ ] **STR-04**: `string_concat` 빌트인 (+ 연산자 포함)
- [ ] **STR-05**: `to_string` 빌트인 (int/bool → string)

### Tuple

- [ ] **TUP-01**: 튜플 생성 → GC_malloc'd 구조체 (N개 포인터 필드)
- [ ] **TUP-02**: `let (a, b) = ...` 튜플 디스트럭처링 → GEP + load
- [ ] **TUP-03**: 튜플 패턴 매칭 (match에서 TuplePat)

### List

- [ ] **LIST-01**: 빈 리스트 `[]` → null 포인터
- [ ] **LIST-02**: cons `h :: t` → GC_malloc'd cons cell `{head: ptr, tail: ptr}`
- [ ] **LIST-03**: 리스트 리터럴 `[e1; e2; ...]` → 중첩 cons로 디슈가
- [ ] **LIST-04**: 리스트 패턴 매칭 (`[]` / `h :: t`) → null check + GEP

### Pattern Matching

- [ ] **PAT-01**: `match` 식 → 결정 트리 기반 cf.cond_br 체인 컴파일
- [ ] **PAT-02**: 상수 패턴 (int, bool) → `arith.cmpi eq` 비교
- [ ] **PAT-03**: 문자열 상수 패턴 → `strcmp` 비교
- [ ] **PAT-04**: 와일드카드/변수 패턴 → 무조건 매치 + 바인딩
- [ ] **PAT-05**: 비소진 매치 런타임 에러 → `@lang_match_failure` 호출

## Out of Scope

| Feature | Reason |
|---------|--------|
| Precise/moving GC | Boehm 보수적 GC로 충분, 정밀 GC는 수개월 작업 |
| 참조 카운팅 | 순환 참조 누수, Boehm이 투명하게 처리 |
| 언박싱 튜플 최적화 | 균일 박싱 표현으로 v2 단순성 유지, v3로 미룸 |
| 문자열 인터닝 | v2에서 모든 문자열 독립 할당, strcmp로 비교 |
| ADT/GADT 컴파일 | v3 범위 |
| 레코드 타입 | v3 범위 |
| 예외 처리 (raise/try-with) | setjmp/longjmp 복잡도, v3 범위 |
| or-패턴 (P1 \| P2) | 결정 트리 분기 공유 복잡도, v2에서 미지원 |
| when 가드 | 패턴 매칭 후 조건 분기, v2에서 미지원 |
| char 타입 컴파일 | LangThree에서 사용 빈도 낮음, v3 미룸 |
| tail call optimization | LLVM 최선-노력 TCO에 의존, 명시적 보장 안 함 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| GC-01 | Phase 7 | Pending |
| GC-02 | Phase 7 | Pending |
| GC-03 | Phase 7 | Pending |
| STR-01 | Phase 8 | Pending |
| STR-02 | Phase 8 | Pending |
| STR-03 | Phase 8 | Pending |
| STR-04 | Phase 8 | Pending |
| STR-05 | Phase 8 | Pending |
| TUP-01 | Phase 9 | Pending |
| TUP-02 | Phase 9 | Pending |
| TUP-03 | Phase 9 | Pending |
| LIST-01 | Phase 10 | Pending |
| LIST-02 | Phase 10 | Pending |
| LIST-03 | Phase 10 | Pending |
| LIST-04 | Phase 10 | Pending |
| PAT-01 | Phase 11 | Pending |
| PAT-02 | Phase 11 | Pending |
| PAT-03 | Phase 11 | Pending |
| PAT-04 | Phase 11 | Pending |
| PAT-05 | Phase 11 | Pending |

**Coverage:**
- v2 requirements: 20 total
- Mapped to phases: 20
- Unmapped: 0 ✓

---
*Requirements defined: 2026-03-26*
*Last updated: 2026-03-26 after v2.0 milestone definition*
