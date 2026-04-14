# Phase 109: Duplicate Top-level Definition Warning

## Goal
동일 이름의 top-level `let` 정의가 여러 파일에 존재할 때 **경고** 출력. Silent ML last-wins shadowing 으로 인한 버그 (FunLexYacc#2 / FunLangCompiler#28 funyacc variant) 를 조기 발견.

## 배경

2026-04-14 분석:
- FunLexYacc 의 `GrammarParser.fun:60` 과 `YaccEmit.fun:240` 에 동일 이름 `intToStr` 정의 존재
- 하나는 `sprintf "%c"` (char), 다른 하나는 `to_string` (decimal)
- Import 순서상 YaccEmit 가 GrammarParser 를 shadow → runtime 에 의도치 않은 동작
- FunLangCompiler 가 shadowing 을 silent 처리하여 사용자가 MLIR 까지 내려가야 발견 가능

## Success Criteria

1. 동일 이름의 top-level `let` 바인딩이 2회 이상 등장 시 stderr 로 경고 출력:
   ```
   [Warning] Duplicate top-level definition 'intToStr':
     - src/funyacc/GrammarParser.fun:60
     - src/funyacc/YaccEmit.fun:240
     The latter will shadow the former.
   ```
2. `let rec` 의 self-reference 는 duplicate 아님 (same definition).
3. Module 내부의 같은 이름 (`module X = ... let f ...` + `module Y = ... let f ...`) 은 name-mangled 되므로 duplicate 아님.
4. `open` 으로 import 된 모듈의 함수와 top-level 바인딩이 동일 이름이어도 module 경로가 다르면 duplicate 아님.
5. `--strict-duplicates` 플래그로 warning → error 승격 옵션.
6. 기존 273 E2E 테스트 회귀 없음.
7. 신규 E2E 테스트 추가: 두 파일에서 동일 이름 정의 시 경고 emit 검증.

## 설계

### 탐지 위치
`prePassDecls` 또는 `elaborateProgram` 초기. 모든 top-level `LetDecl` / `LetRecDecl` 이름을 수집하며 중복 감지.

### 수집 데이터 구조
```fsharp
type DefOccurrence = { Name: string; Span: Ast.Span }
let mutable seenDefs : Map<string, DefOccurrence list> = Map.empty
```

각 `LetDecl(name, _, span)` 처리 시:
```fsharp
let occ = { Name = name; Span = span }
seenDefs <- seenDefs |> Map.update name (fun existing ->
    match existing with
    | Some xs -> Some (occ :: xs)
    | None -> Some [occ])
```

### 경고 출력
후처리에서 value 가 2개 이상인 entry 를 찾아 stderr 에 출력.

### 예외 처리
- `_` underscore 이름은 제외 (anonymous binding)
- 이름이 `__` 로 시작하는 compiler-internal 은 제외
- Prelude 모듈 내부의 함수는 제외 (module scoping)

### CLI 플래그
```
--strict-duplicates   Duplicate top-level definitions become hard errors (exit 1)
```

기본 동작: warning 만 emit. 사용자가 strict 모드 opt-in 가능.

## Tasks

### T1: Duplicate detection 로직 (ElabProgram.fs 또는 Elaboration.fs)
- `prePassDecls` 또는 동등 단계에서 이름 수집
- 중복 entry 찾아 경고 출력

### T2: CLI 플래그 `--strict-duplicates`
- Program.fs 의 parseArgs 에 추가
- TypeCheckOptions record 에 `StrictDuplicates: bool` 추가 또는 별도 플래그
- 경고 발견 + strict 모드 → exit 1

### T3: `--help` 업데이트

### T4: E2E 테스트
- `103-01-duplicate-def-warning.flt` — 두 파일에서 동일 이름 정의 시 stderr 에 경고 emit
- `103-02-strict-duplicates-error.flt` — `--strict-duplicates` 시 exit 1

### T5: 회귀 검증
- 기존 273 테스트 통과
- 신규 2개 추가 → 275 total

## 엣지 케이스

- 실제 user-intended shadowing (예: 개발 중 교체) — 경고만 (error 로 가지 말 것)
- Prelude 에 동일 이름이 있을 때 (Core.not vs 사용자 not) — 현재 명시적으로 module 로 분리되어 있어 충돌 없음
- `let rec ... and ...` 형태 — 각각 독립 바인딩

## 관련

- ohama/FunLangCompiler#28 (funyacc variant, FunLexYacc#2 가 근본 원인인데 사용자가 처음엔 컴파일러 버그로 오진단. 경고가 있었다면 즉시 발견했을 것)
- ohama/FunLexYacc#2 (이 phase 의 직접적 motivator)

## Risk

- 대량 경고 발생 가능 — Prelude 자체가 중복 이름을 가질 수 있음 (예: Prelude/List.fun 의 `map` 과 Prelude/Array.fun 의 `map`). 단, module 로 scope 되어 있으면 문제 없음. module-internal 이름 mangling 후 비교 필요.
- False positive 방지: 경고 대상은 **top-level (module 밖)** 정의만.

## Plans

- [ ] T1-T5 합쳐 단일 plan (109-01)
