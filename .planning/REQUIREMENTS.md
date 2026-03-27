# Requirements: LangBackend v4.0

**Defined:** 2026-03-27
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements

Requirements for v4.0 milestone. Each maps to roadmap phases.

### ADT Infrastructure

- [ ] **ADT-01**: TypeDecl processing — constructor name → tag index 매핑 (ElabEnv.TypeEnv)
- [ ] **ADT-02**: ExceptionDecl processing — exception constructor → tag 매핑 (ADT와 동일 메커니즘)
- [ ] **ADT-03**: MatchCompiler CtorTag 확장 — AdtCtor, RecordCtor 추가 및 desugarPattern 구현
- [ ] **ADT-04**: elaborateProgram 진입점 — TypeDecl/RecordDecl/ExceptionDecl 선행 처리 후 expression elaboration

### ADT Construction & Pattern Matching

- [ ] **ADT-05**: Nullary constructor compilation — `None`, `Empty` 등 16-byte `{tag, null}` 힙 블록
- [ ] **ADT-06**: Unary constructor compilation — `Some 42` 등 `{tag, payload_ptr}` 힙 블록
- [ ] **ADT-07**: Multi-arg constructor — tuple payload wrapping (e.g., `Pair(1, 2)`)
- [ ] **ADT-08**: ConstructorPat (nullary) — tag 비교만, payload 추출 없음
- [ ] **ADT-09**: ConstructorPat (unary) — tag 비교 + GEP field 1 + load + sub-pattern dispatch
- [ ] **ADT-10**: GADT constructor compilation — frontend 타입 체크 후 backend는 일반 ADT와 동일 처리
- [ ] **ADT-11**: First-class constructor — unary constructor를 lambda로 wrap (e.g., `List.map Some [1;2;3]`)
- [ ] **ADT-12**: Nested ADT pattern matching — multi-level constructor 패턴 (recursive GEP chains)

### Records

- [ ] **REC-01**: RecordDecl processing — field name → index 매핑 (ElabEnv.RecordEnv)
- [ ] **REC-02**: RecordExpr compilation — GC_malloc(n*8) + 선언 순서대로 field store
- [ ] **REC-03**: FieldAccess compilation — GEP(fieldIndex) + load
- [ ] **REC-04**: RecordUpdate compilation — 새 블록 할당 + non-overridden field 복사 + overridden field write
- [ ] **REC-05**: SetField compilation — GEP(fieldIndex) + store, unit(i64=0) 반환
- [ ] **REC-06**: RecordPat compilation — field name 기반 구조 매칭 (TuplePat과 유사)

### Exception Handling

- [ ] **EXN-01**: C runtime 확장 — LangExnFrame struct, lang_try_enter (static inline setjmp), lang_try_exit, lang_throw, lang_current_exception
- [ ] **EXN-02**: Raise compilation — exception DataValue elaborate + @lang_throw 호출 + llvm.unreachable
- [ ] **EXN-03**: TryWith compilation — LangExnFrame 할당 + lang_try_enter + setjmp 분기 + handler decision tree + merge block
- [ ] **EXN-04**: Nested try-with — handler stack 올바른 push/pop 순서
- [ ] **EXN-05**: Exception with payload — `raise (ParseError "bad input")` 등 payload 전달
- [ ] **EXN-06**: Unhandled exception — handler 없을 때 런타임 abort with message
- [ ] **EXN-07**: Exception re-raise — handler miss 시 lang_throw로 재전파
- [ ] **EXN-08**: Exception in handler — handler 내부에서 발생한 exception 올바른 처리

### Regression

- [ ] **REG-01**: 기존 45개 E2E 테스트 전체 통과 유지

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Advanced Features

- **ADV-01**: Stack traces on exceptions (DWARF/frame pointer walking)
- **ADV-02**: Unboxed/specialized ADT representation (monomorphization)
- **ADV-03**: Printf/sprintf for exception messages

## Out of Scope

| Feature | Reason |
|---------|--------|
| C++ exception ABI (_Unwind_RaiseException) | Boehm GC와 호환 불가 |
| ADT memory optimization (unboxed scalars) | v4.0은 correctness 우선, 최적화는 이후 |
| try-finally | LangThree AST에 없음 |
| Module system | v5.0 이후 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| ADT-01 | Phase 16 | Complete |
| ADT-02 | Phase 16 | Complete |
| ADT-03 | Phase 16 | Complete |
| ADT-04 | Phase 16 | Complete |
| ADT-05 | Phase 17 | Complete |
| ADT-06 | Phase 17 | Complete |
| ADT-07 | Phase 17 | Complete |
| ADT-08 | Phase 17 | Complete |
| ADT-09 | Phase 17 | Complete |
| ADT-10 | Phase 17 | Complete |
| ADT-11 | Phase 20 | Pending |
| ADT-12 | Phase 20 | Pending |
| REC-01 | Phase 16 | Complete |
| REC-02 | Phase 18 | Pending |
| REC-03 | Phase 18 | Pending |
| REC-04 | Phase 18 | Pending |
| REC-05 | Phase 18 | Pending |
| REC-06 | Phase 18 | Pending |
| EXN-01 | Phase 19 | Pending |
| EXN-02 | Phase 19 | Pending |
| EXN-03 | Phase 19 | Pending |
| EXN-04 | Phase 19 | Pending |
| EXN-05 | Phase 19 | Pending |
| EXN-06 | Phase 19 | Pending |
| EXN-07 | Phase 20 | Pending |
| EXN-08 | Phase 20 | Pending |
| REG-01 | All | Pending |

**Coverage:**
- v1 requirements: 27 total
- Mapped to phases: 27
- Unmapped: 0 ✓

---
*Requirements defined: 2026-03-27*
*Last updated: 2026-03-27 after initial definition*
