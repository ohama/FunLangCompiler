# Requirements: LangBackend

**Defined:** 2026-03-26
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements

### Infrastructure

- [ ] **INFRA-01**: MLIR C API P/Invoke 바인딩 (`MlirContext`, `MlirModule`, `MlirOperation` 등 핸들 타입)
- [ ] **INFRA-02**: 소유권 추적 래퍼 + `CompilerSession` (destroy 순서 보장)
- [ ] **INFRA-03**: 4단계 lowering 파이프라인 (`arith→cf→func→llvm→reconcile-unrealized-casts`)
- [ ] **INFRA-04**: shell pipeline으로 MLIR → object file → 실행 바이너리 생성
- [ ] **INFRA-05**: E2E 스모크 테스트 (`return 42` 컴파일 및 실행 검증)

### Scalar Codegen

- [ ] **SCALAR-01**: 정수 리터럴 → `arith.constant` (i64)
- [ ] **SCALAR-02**: 산술 연산 (add/sub/mul/div) → `arith.addi/subi/muli/divsi`
- [ ] **SCALAR-03**: 비교 연산 (=, <>, <, >, <=, >=) → `arith.cmpi`
- [ ] **SCALAR-04**: 불리언 리터럴 (true/false) → `arith.constant i1`
- [ ] **SCALAR-05**: 논리 연산 (&&, ||) → `cf` 분기 (단락 평가 보존)

### Control Flow & Bindings

- [ ] **CTRL-01**: `if-else` → `cf.cond_br` + basic block arguments
- [ ] **CTRL-02**: `let` 바인딩 → SSA value 이름 바인딩
- [ ] **CTRL-03**: 변수 참조 → SSA value 조회

### Functions

- [ ] **FUNC-01**: `TypedExpr` 어노테이션 패스 (자유변수 집합, 호출 종류 분석)
- [ ] **FUNC-02**: `let rec` (캡처 없는 known function) → `func.func` + `func.call`
- [ ] **FUNC-03**: Lambda (자유변수 있는 경우) → flat closure struct `{fn_ptr, env...}`
- [ ] **FUNC-04**: 함수 적용 → direct call (known) 또는 indirect call (closure)

### CLI

- [ ] **CLI-01**: `.lt` 파일을 입력받아 실행 바이너리 출력하는 CLI
- [ ] **CLI-02**: LangThree `.fsproj` project reference로 frontend 재사용

### Testing

- [ ] **TEST-01**: FsLit 파일 기반 테스트 — `.flt` 파일에 LangThree 소스를 `-- Input:`으로 작성, 컴파일러 → 실행 → 출력 검증 (`--expr` 사용 안 함)
- [ ] **TEST-02**: 각 기능 카테고리별 `.flt` 테스트 파일 (산술, 비교, if-else, let, let rec, lambda)

## v2 Requirements

### Extended Types

- **TYPES-01**: string 컴파일 지원
- **TYPES-02**: 튜플 컴파일 지원
- **TYPES-03**: 리스트 컴파일 지원 (GC 포함)

### Pattern Matching

- **PAT-01**: 패턴 매칭 컴파일 (튜플/리스트 선행 필요)
- **PAT-02**: ADT/GADT 컴파일

### Runtime

- **RT-01**: 가비지 컬렉터 (Boehm GC 또는 커스텀)
- **RT-02**: 런타임 에러 메시지 (패턴 매칭 실패 등)

## Out of Scope

| Feature | Reason |
|---------|--------|
| REPL (컴파일 모드) | 인터프리터(LangThree)가 이미 존재함 |
| `--expr` 모드 CLI | file 기반 컴파일만 지원, REPL은 LangThree 사용 |
| 커스텀 MLIR dialect | v1에서는 표준 dialects(func/arith/cf/llvm)로 충분 |
| tail call optimization | LLVM이 자동 처리 기대, v1에서는 명시적 보장 안 함 |
| string/tuple/list/pattern | GC/boxing 필요, v2로 미룸 |
| ADT/GADT | LangThree에서도 미구현 |
| Windows/macOS 지원 | Linux x86-64 (WSL2) 우선 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01 | Phase 1 | Pending |
| INFRA-02 | Phase 1 | Pending |
| INFRA-03 | Phase 1 | Pending |
| INFRA-04 | Phase 1 | Pending |
| INFRA-05 | Phase 1 | Pending |
| SCALAR-01 | Phase 2 | Pending |
| SCALAR-02 | Phase 2 | Pending |
| SCALAR-03 | Phase 3 | Pending |
| SCALAR-04 | Phase 3 | Pending |
| SCALAR-05 | Phase 3 | Pending |
| CTRL-01 | Phase 3 | Pending |
| CTRL-02 | Phase 2 | Pending |
| CTRL-03 | Phase 2 | Pending |
| FUNC-01 | Phase 4 | Pending |
| FUNC-02 | Phase 4 | Pending |
| FUNC-03 | Phase 5 | Pending |
| FUNC-04 | Phase 5 | Pending |
| CLI-01 | Phase 6 | Pending |
| CLI-02 | Phase 1 | Pending |
| TEST-01 | Phase 1 | Pending |
| TEST-02 | Phase 2 | Pending |

**Coverage:**
- v1 requirements: 21 total
- Mapped to phases: 21
- Unmapped: 0 ✓

---
*Requirements defined: 2026-03-26*
*Last updated: 2026-03-26 after initial definition*
