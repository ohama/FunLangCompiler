# Requirements: LangBackend v10.0

**Defined:** 2026-03-30
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v10 Requirements

Requirements for FunLexYacc 네이티브 컴파일 지원. Each maps to roadmap phases.

### Runtime 확장

- [x] **RT-01**: Hashtable이 문자열 키를 지원한다 — C runtime hash/compare가 string 구조체를 올바르게 처리
- [x] **RT-02**: Hashtable 문자열 키로 get/set/containsKey/remove가 정상 동작한다
- [x] **RT-03**: 컴파일된 바이너리가 CLI 인자를 `get_args ()` 로 string list로 받을 수 있다
- [x] **RT-04**: `@main` 시그니처가 `(i64, ptr) -> i64` (argc, argv)를 받는다
- [x] **RT-05**: `sprintf "%d" n` 이 포맷된 문자열을 반환한다
- [x] **RT-06**: `sprintf` 가 `%d`, `%s`, `%x`, `%02x`, `%c` 포맷 지정자를 지원한다
- [x] **RT-07**: `printfn "%d states" n` 이 포맷된 문자열을 stdout에 출력한다
- [x] **RT-08**: `sprintf` 다중 인자 포맷 (`sprintf "%s=%d" name value`)을 지원한다

### 컴파일러 기능

- [x] **COMP-01**: `open "file.fun"` 이 임포트 파일의 모든 top-level 바인딩을 현재 스코프에 가져온다
- [x] **COMP-02**: 멀티파일 import가 재귀적으로 동작한다 (A가 B를 open, B가 C를 open)
- [x] **COMP-03**: 순환 import 시 명확한 에러 메시지를 출력한다
- [x] **COMP-04**: 상대 경로 import가 현재 파일 기준으로 resolve 된다

### 버그 수정

- [x] **FIX-01**: for-in 루프 내에서 `let mut` 변수 캡처가 segfault 없이 동작한다
- [x] **FIX-02**: 두 개의 연속 `if` 표현식이 유효한 MLIR을 생성한다
- [x] **FIX-03**: Bool 반환 모듈 함수가 조건문에서 `<> 0` 없이 직접 사용 가능하다

## Future Requirements

### FunLexYacc 통합 검증

- **FLY-01**: FunLexYacc 전체 소스 (19개 .fun 파일)가 LangBackend로 컴파일된다
- **FLY-02**: 컴파일된 funlex/funyacc 바이너리가 테스트 문법 파일을 처리한다
- **FLY-03**: `Unchecked.defaultof` 대체 패턴 지원

## Out of Scope

| Feature | Reason |
|---------|--------|
| REPL | 인터프리터가 이미 존재함 |
| tail call optimization | LLVM 자동 처리에 의존 |
| MlirIR optimization passes | correctness 우선 |
| incremental/separate compilation | 별도 링커 필요, 멀티파일 import로 대체 |
| sprintf 패딩/정렬 (%8s, %-10d) | snprintf 위임으로 자동 지원될 수 있으나 필수는 아님 |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| FIX-01 | Phase 36 | Complete |
| FIX-02 | Phase 36 | Complete |
| FIX-03 | Phase 36 | Complete |
| RT-01 | Phase 37 | Complete |
| RT-02 | Phase 37 | Complete |
| RT-03 | Phase 38 | Complete |
| RT-04 | Phase 38 | Complete |
| RT-05 | Phase 39 | Complete |
| RT-06 | Phase 39 | Complete |
| RT-07 | Phase 39 | Complete |
| RT-08 | Phase 39 | Complete |
| COMP-01 | Phase 40 | Complete |
| COMP-02 | Phase 40 | Complete |
| COMP-03 | Phase 40 | Complete |
| COMP-04 | Phase 40 | Complete |

**Coverage:**
- v10 requirements: 15 total
- Mapped to phases: 15
- Unmapped: 0

---
*Requirements defined: 2026-03-30*
*Last updated: 2026-03-30 after roadmap creation*
