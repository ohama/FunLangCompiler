# Requirements: LangBackend

**Defined:** 2026-03-26
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements

### Infrastructure

- [ ] **INFRA-01**: MlirIR 정의 — MLIR 개념(Region, Block, Op, Value, Type)을 F# DU로 모델링한 컴파일러 내부 IR
- [ ] **INFRA-02**: MlirIR → `.mlir` 텍스트 프린터 (P/Invoke 없음, 순수 문자열 생성)
- [ ] **INFRA-03**: `mlir-opt` 호출로 lowering 파이프라인 (`arith→cf→func→llvm→reconcile-unrealized-casts`)
- [ ] **INFRA-04**: `mlir-translate --mlir-to-llvmir` + `clang` shell pipeline으로 바이너리 생성
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

### Elaboration

- [ ] **ELAB-01**: LangThree AST → MlirIR 변환 패스 (Elaboration) — 타입 정보, 자유변수 집합, 호출 종류 포함
- [ ] **ELAB-02**: `let rec` (캡처 없는 known function) elaboration → MlirIR `FuncOp`
- [ ] **ELAB-03**: Lambda (자유변수 캡처) elaboration → MlirIR closure 표현 (`{fn_ptr, env...}`)
- [ ] **ELAB-04**: 함수 적용 elaboration → direct call 또는 indirect call 구분

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
| P/Invoke MLIR C API 바인딩 | MLIR 텍스트 포맷 직접 생성으로 대체, 소유권/ABI 문제 없음 |
| MlirIR optimization passes | v1에서는 correctness 우선, 최적화는 v2 이후 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01 | Phase 1 | Complete |
| INFRA-02 | Phase 1 | Complete |
| INFRA-03 | Phase 1 | Complete |
| INFRA-04 | Phase 1 | Complete |
| INFRA-05 | Phase 1 | Complete |
| CLI-02 | Phase 1 | Complete |
| TEST-01 | Phase 1 | Complete |
| SCALAR-01 | Phase 2 | Complete |
| SCALAR-02 | Phase 2 | Complete |
| CTRL-02 | Phase 2 | Complete |
| CTRL-03 | Phase 2 | Complete |
| ELAB-01 | Phase 2 | Complete |
| SCALAR-03 | Phase 3 | Complete |
| SCALAR-04 | Phase 3 | Complete |
| SCALAR-05 | Phase 3 | Complete |
| CTRL-01 | Phase 3 | Complete |
| ELAB-02 | Phase 4 | Complete |
| ELAB-03 | Phase 5 | Complete |
| ELAB-04 | Phase 5 | Complete |
| TEST-02 | Phase 5 | Complete |
| CLI-01 | Phase 6 | Pending |

**Coverage:**
- v1 requirements: 21 total
- Mapped to phases: 21
- Unmapped: 0 ✓

---
*Requirements defined: 2026-03-26*
*Last updated: 2026-03-26 after MlirIR design revision (Elaboration pass + MlirIR DU as explicit compiler IR)*
