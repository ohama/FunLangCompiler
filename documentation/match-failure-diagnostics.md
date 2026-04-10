# Match Failure Diagnostics (Phase 99)

## 에러 메시지

FunLang 프로그램에서 `match` 표현식이 모든 케이스를 커버하지 못하면, 런타임에 다음과 같은 에러가 출력됩니다:

```
Fatal: non-exhaustive match at src/Parser.fun:42 (value=4303998752)
Backtrace (most recent call last):
  0: @_fnc_entry
  1: @parseExpr
  2: @parseAtom
```

### 각 항목 설명

| 항목 | 설명 |
|------|------|
| `at src/Parser.fun:42` | match 표현식이 위치한 소스 파일과 줄 번호 |
| `value=4303998752` | match 대상(scrutinee) 값의 i64 표현. ADT는 힙 포인터, 정수는 tagged value `(n<<1)\|1` |
| `Backtrace` | 에러 발생 시점의 함수 호출 스택 (아래→위 순서) |

### value 해석 방법

| 값 형태 | 의미 |
|---------|------|
| 홀수 (예: `value=7`) | Tagged 정수. 실제 값 = `(value - 1) / 2` = 3 |
| 짝수, 큰 수 (예: `value=4303998752`) | 힙 포인터 (ADT 생성자, 레코드, 문자열 등) |
| `value=0` | 빈 리스트 `[]` 또는 `None` |

## 디버깅 방법

### 1. 소스 위치 확인

에러 메시지의 파일:줄 번호를 열어 해당 `match` 문을 확인합니다:

```fun
// src/Parser.fun:42
match token with
| Plus -> ...
| Minus -> ...
// ← 여기서 다른 토큰이 들어오면 match 실패
```

### 2. 누락된 패턴 추가

`_` (와일드카드) 패턴으로 나머지 케이스를 처리합니다:

```fun
match token with
| Plus -> ...
| Minus -> ...
| _ -> failwith "unexpected token"
```

### 3. --trace 플래그로 상세 추적

함수 진입 순서까지 확인하려면 `--trace`로 컴파일합니다:

```bash
fnc --trace myapp.fun -o myapp
./myapp 2>trace.log
tail -20 trace.log
```

stderr에 `[TRACE] @funcName` 형태로 모든 함수 진입이 기록됩니다.

## 기술 세부사항

### 콜 스택 구현

- 런타임에 고정 크기(256) 콜 스택 유지
- 각 함수 진입 시 `lang_trace_push(funcName)` 호출
- 각 함수 리턴 시 `lang_trace_pop()` 호출
- match 실패 시 `lang_match_failure(location, value)` 가 스택을 출력하고 `exit(1)`

### 오버헤드

- 콜 스택 push/pop: 함수 호출당 포인터 쓰기 1회 + 정수 증감 1회
- 소스 위치 문자열: 바이너리 크기에 match 표현식 수만큼의 string constant 추가
- `--trace` 없이 컴파일 시 stderr 출력 없음 (콜 스택은 항상 유지)

## 관련 항목

- [CHANGELOG.md](../CHANGELOG.md) — v0.1.0 릴리스 노트
- `--trace` 플래그 — Phase 98에서 추가
- `dbg expr` — 개별 값 디버깅 (stderr에 `[file:line] value` 출력)
