# Requirements: LangBackend v13.0

**Defined:** 2026-04-01
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v13 Requirements

LangThree v10.0-v12.0에서 변경된 AST 구조와 Typeclass 기능을 LangBackend 컴파일러에 반영한다.

### AST 동기화

- [ ] **AST-01**: TypeDecl 패턴매치가 5-field (name, typeParams, ctors, deriving, span) 구조를 처리한다
- [ ] **AST-02**: TypeClassDecl 패턴매치가 superclasses 필드를 포함한 구조를 처리한다
- [ ] **AST-03**: InstanceDecl 패턴매치가 constraints 필드를 포함한 구조를 처리한다
- [ ] **AST-04**: DerivingDecl이 elaboration에서 무시(skip)된다
- [ ] **AST-05**: 기존 E2E 테스트가 AST 변경 후에도 모두 통과한다

### Typeclass 컴파일

- [ ] **TC-01**: elaborateTypeclasses 함수가 TypeClassDecl을 decl 목록에서 제거한다
- [ ] **TC-02**: elaborateTypeclasses 함수가 InstanceDecl을 LetDecl 바인딩으로 변환한다
- [ ] **TC-03**: elaborateTypeclasses 함수가 DerivingDecl을 decl 목록에서 제거한다
- [ ] **TC-04**: elaborateTypeclasses가 ModuleDecl/NamespacedModule 내부를 재귀 처리한다
- [ ] **TC-05**: elaborateTypeclasses가 parseProgram 후 elaborateProgram 전에 호출된다

### Prelude 동기화

- [ ] **PRE-01**: Prelude/Typeclass.fun이 LangThree에서 복사되어 포함된다
- [ ] **PRE-02**: CLI가 Typeclass.fun을 Prelude 로딩에 포함한다

### E2E 테스트

- [ ] **TEST-01**: show 함수를 사용하는 기본 typeclass 테스트가 통과한다
- [ ] **TEST-02**: eq 함수를 사용하는 기본 typeclass 테스트가 통과한다
- [ ] **TEST-03**: deriving Show 자동 생성 테스트가 통과한다

## Future Requirements

- **TC-ADV-01**: Constrained instance 컴파일 (Show 'a => Show (list 'a))
- **TC-ADV-02**: Superclass constraint 컴파일 (Eq 'a => Ord 'a)
- **ERR-01**: 소스 코드 스니펫 + ^^^ 밑줄 에러 보고
- **ERR-02**: "Did you mean?" 유사 이름 제안
- **ERR-03**: 멀티 에러 보고 (첫 에러에서 안 멈춤)

## Out of Scope

| Feature | Reason |
|---------|--------|
| Constrained instances | v13에서는 기본 인스턴스만, constrained는 Future |
| Superclass constraints | 기본 typeclass만, superclass는 Future |
| 에러 보고 강화 | LangThree 인터프리터 쪽 기능, LangBackend는 독자 에러 시스템 |
| LangThree 수정 | 병렬 작업 중, 절대 수정 금지 |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| AST-01 | - | Pending |
| AST-02 | - | Pending |
| AST-03 | - | Pending |
| AST-04 | - | Pending |
| AST-05 | - | Pending |
| TC-01 | - | Pending |
| TC-02 | - | Pending |
| TC-03 | - | Pending |
| TC-04 | - | Pending |
| TC-05 | - | Pending |
| PRE-01 | - | Pending |
| PRE-02 | - | Pending |
| TEST-01 | - | Pending |
| TEST-02 | - | Pending |
| TEST-03 | - | Pending |

**Coverage:**
- v13 requirements: 15 total
- Mapped to phases: 0
- Unmapped: 15

---
*Requirements defined: 2026-04-01*
