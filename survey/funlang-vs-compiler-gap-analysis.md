# FunLang vs FunLangCompiler: Gap Analysis

FunLang (인터프리터) v12.0과 FunLangCompiler v22.0+v13.0-tagged의 기능 차이 분석.

## 요약

| 영역 | FunLang | FunLangCompiler | 상태 |
|------|---------|-----------------|------|
| **Builtin 함수** | 62개 (+ 7 `_str`) | 58개 (통합) | **Parity** |
| **AST 표현식** | 54 variants | 54 variants | **Parity** |
| **타입 클래스** | ✓ 완전 (Hindley-Milner + 딕셔너리) | ✓ 부분 (elaboration만) | **Gap** |
| **Fixity/Infix** | ✓ FixityEnv + Pratt rewrite | ✓ FixityEnv + Pratt rewrite | **Parity** |
| **Typed AST Export** | ✓ AnnotationMap + BindingEnv | ✓ AnnotationMap 사용 | **Parity** |
| **Tagged Representation** | 불필요 (.NET polymorphism) | ✓ v13.0 (LSB dispatch) | **Compiler only** |
| **flt 테스트** | 723개 | 257개 | **Gap** (36%) |
| **Hashtable key 타입** | 무제한 (any Value) | int + string (LSB) | **Gap** |

## 1. Builtin 함수: Parity 달성

### 완전 일치 (58개)

모든 production builtin이 양쪽에서 동일하게 동작:
- String (14): length, concat, sub, contains, startswith, endswith, trim, split, indexof, replace, toupper, tolower, to_int, concat_list
- Char (8): to_int, to_char, is_digit, is_letter, is_upper, is_lower, to_upper, to_lower
- Print (8): print, println, printf, printfn, sprintf, eprint, eprintln, eprintfn
- File I/O (13): read_file, write_file, append_file, file_exists, read_lines, write_lines, stdin_read_line, stdin_read_all, get_env, get_cwd, path_combine, dir_files, get_args
- Array (12): create, get, set, length, of_list, to_list, iter, map, fold, init, sort, of_seq
- Hashtable (8): create, get, set, containsKey, keys, remove, trygetvalue, count
- Collections (12): stringbuilder 3, hashset 4, queue 3, mutablelist 4
- Utility (5): to_string, failwith, dbg, list_sort_by, list_of_seq

### FunLang에만 있는 것 (7개 `_str` 변종)

| 함수 | FunLang | Compiler | 설명 |
|------|---------|----------|------|
| `hashtable_create_str` | ✓ | ✗ | v13.0 unified로 불필요 |
| `hashtable_get_str` | ✓ | ✗ | 〃 |
| `hashtable_set_str` | ✓ | ✗ | 〃 |
| `hashtable_containsKey_str` | ✓ | ✗ | 〃 |
| `hashtable_keys_str` | ✓ | ✗ | 〃 |
| `hashtable_remove_str` | ✓ | ✗ | 〃 |
| `hashtable_trygetvalue_str` | ✓ | ✗ | 〃 |

**의미:** FunLang은 backward compat을 위해 유지. Compiler는 tagged representation으로 통합하여 불필요. Prelude/Hashtable.fun은 양쪽 동일 (통합 API 사용).

## 2. 타입 클래스: 부분 Gap

### FunLang (완전 구현)

- `typeclass Show where show : 'a -> string` — 선언
- `instance Show int where show x = to_string x` — 인스턴스
- `deriving Show` — 자동 유도 (ADT)
- 제약 추론: `Show 'a =>` constraint propagation
- 딕셔너리 elaboration: typeclass → LetDecl 변환
- 모듈 간 ClassEnv/InstanceEnv export
- E0701-E0706 에러 메시지

### FunLangCompiler (부분 구현)

- `elaborateTypeclasses`: TypeClassDecl 제거, InstanceDecl→LetDecl, DerivingDecl→Show/Eq 생성
- Prelude/Typeclass.fun: Show/Eq 빌트인 인스턴스
- **없는 것:**
  - 제약 조건부 인스턴스 (`Show 'a => Show (list 'a)`) — FunLang도 미구현
  - 슈퍼클래스 제약 — FunLang도 미구현
  - Num 타입 클래스 — FunLang도 미구현
  - 타입 클래스 기반 `=` 연산자 — FunLang도 미구현

**결론:** 현재 FunLang과 Compiler 모두 동일한 수준의 typeclass 지원. 양쪽 다 Future에 동일한 항목이 있음.

## 3. Hashtable Key 타입: 근본적 차이

| | FunLang | FunLangCompiler |
|---|---|---|
| int key | ✓ | ✓ |
| string key | ✓ | ✓ |
| tuple key | ✓ (Dictionary<Value,Value>) | ✗ |
| record key | ✓ | ✗ |
| ADT key | ✓ | ✗ |
| 구현 방식 | .NET `Dictionary` polymorphism | C runtime LSB dispatch (int/ptr만) |

**해결 방법:** 힙 블록 header tag byte 도입 → string/tuple/record/list 구분 가능. OCaml 방식. 대규모 작업.

## 4. 테스트 Coverage Gap

| 영역 | FunLang | Compiler | Coverage |
|------|---------|----------|----------|
| 기본 타입/연산 | 260 | 92 | 35% |
| 패턴 매칭 | 83 | 34 | 41% |
| 모듈/임포트 | 52 | 18 | 35% |
| 컬렉션 | 89 | 32 | 36% |
| 제어 흐름 | 73 | 28 | 38% |
| 타입 클래스 | 47 | 12 | 26% |
| 기타 | 119 | 41 | 34% |
| **합계** | **723** | **257** | **36%** |

**Gap 원인:** FunLang은 각 phase마다 10-20개 flt 테스트 추가. Compiler는 핵심 케이스만 테스트.

## 5. 언어 기능 Parity

### 완전 일치

| 기능 | FunLang | Compiler |
|------|---------|----------|
| 들여쓰기 기반 파싱 | ✓ (IndentFilter) | ✓ (FunLang frontend 재사용) |
| ADT + GADT | ✓ | ✓ |
| Records + mutable fields | ✓ | ✓ |
| 모듈 시스템 | ✓ | ✓ |
| 예외 처리 | ✓ | ✓ (setjmp/longjmp) |
| 패턴 매칭 | ✓ (decision tree) | ✓ (decision tree) |
| 파이프/합성 연산자 | ✓ (Prelude) | ✓ (Prelude) |
| Mutable variables | ✓ | ✓ (GC ref cell) |
| While/For/For-in loops | ✓ | ✓ |
| File import (open "file") | ✓ | ✓ |
| 중첩 모듈 qualified access | ✓ | ✓ |
| funproj.toml 빌드 시스템 | ✓ | ✓ |
| InfixDecl + FixityEnv | ✓ | ✓ |
| dbg 디버깅 | ✓ | ✓ |

### Compiler에만 있는 것

| 기능 | 설명 |
|------|------|
| Tagged representation | LSB 1-bit tagging (int=2n+1) |
| MLIR → LLVM 파이프라인 | 네이티브 바이너리 생성 |
| Boehm GC | 보수적 GC 통합 |
| LambdaLift / LetNormalize | 컴파일러 전용 AST 패스 |
| -O2/-O3 최적화 | MLIR opt 파이프라인 |

### FunLang에만 있는 것

| 기능 | 설명 | Compiler 필요성 |
|------|------|----------------|
| REPL | 대화형 실행 | 낮음 (인터프리터 사용) |
| Trampoline TCO | 꼬리 호출 최적화 | 불필요 (LLVM 자동) |
| --emit-typed-ast | JSON 타입 정보 출력 | 낮음 (디버깅용) |
| --check / --deps | 타입 체크만 / 의존성 트리 | 낮음 |

## 6. 남은 작업 우선순위

### High Priority (사용자 영향 큼)

| # | 작업 | 설명 | 난이도 |
|---|------|------|--------|
| 1 | **테스트 Coverage 확대** | 723 → 257 gap. FunLang flt를 Compiler에 포팅 | 중 |
| 2 | **HashSet LSB 통합** | Hashtable과 동일한 패턴 적용. raw int 저장 → tagged 저장 | 소 |

### Medium Priority (코드 품질)

| # | 작업 | 설명 | 난이도 |
|---|------|------|--------|
| 3 | **C boundary untag 제거** | C 런타임이 tagged 값을 직접 처리하도록 이동. Elaboration.fs ~50곳 단순화 | 중 |
| 4 | **`_str` builtin 정리 (FunLang)** | FunLang에서 `_str` 변종을 alias로 변환. 현재는 별도 구현 | 소 |
| 5 | **mutablelist_get/set 에러 힌트 추가** | Elaboration.fs 에러 suggestion list에 누락 | 극소 |

### Low Priority (미래 확장)

| # | 작업 | 설명 | 난이도 |
|---|------|------|--------|
| 6 | **Generic equality/hash** | 힙 블록 header tag → 구조적 비교 | 대 |
| 7 | **제약 조건부 인스턴스** | `Show 'a => Show (list 'a)` | 대 (FunLang 먼저) |
| 8 | **Num 타입 클래스** | `+` `-` `*` 마이그레이션 | 중 (FunLang 먼저) |

## 7. FunLang에서 아직 미구현인 것

FunLang Future 목록 (양쪽 모두 미구현):

| 기능 | 설명 | 영향 |
|------|------|------|
| 제약 조건부 인스턴스 | `Show 'a => Show (list 'a)` | 타입 클래스 실용성 |
| 슈퍼클래스 제약 | `class Eq a => Ord a` | 타입 클래스 계층 |
| Num 타입 클래스 | `+` `-` `*`를 typeclass method로 | 연산자 다형성 |
| Eq 타입 클래스 마이그레이션 | `=` 연산자를 typeclass 기반으로 | 사용자 정의 동등성 |
| derive 자동 유도 | `deriving (Show, Eq, Ord)` | 보일러플레이트 제거 |
| 증분 빌드 | mtime/hash 캐시 | 빌드 성능 |
| Computation expressions | `computation { ... }` | 모나딕 프로그래밍 |
| do binding | `do! expr` | computation expr 내부 |
| Seq expressions | `seq { yield ...; yield! ... }` | 지연 시퀀스 |

**결론:** 이 기능들은 FunLang(인터프리터)에서 먼저 구현 → FunLangCompiler에 포팅하는 순서.

---
*2026-04-07 — FunLang v12.0 vs FunLangCompiler v22.0+v13.0 기준*
