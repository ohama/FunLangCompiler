# Error Message Improvements — 종합 조사

**Date:** 2026-04-01
**Context:** FunLang Span fix 적용 후, 에러 메시지 품질 향상을 위한 전체 현황 및 개선 방안

---

## 1. 현재 상태

### 1.1 동작하는 것
- **Span 위치 전파**: FunLang v9.1 (Phase 69)에서 `PositionedToken` + `filterPositioned` 추가. FunLangCompiler에서 이를 사용하도록 수정 완료 (2026-04-01). 에러 메시지에 실제 `file:line:col` 표시.
- **에러 카테고리**: `[Parse]`, `[Elaboration]`, `[Compile]` 접두사로 에러 단계 구분.
- **Context Hints**: Record/field/function 에러에 사용 가능한 타입/필드/함수 목록 표시.
- **MLIR 파일 보존**: mlir-opt/translate 실패 시 `.mlir` 파일 삭제하지 않고 경로 표시.
- **파서 에러 보존**: `parseModule` → `parseExpr` fallback 시 원래 에러(`firstEx`) 보존.

### 1.2 문제가 있는 것

| 문제 | 심각도 | 원인 | 영향 |
|------|--------|------|------|
| **Prelude 줄 번호 오프셋** | 높음 | Prelude 161줄이 소스 앞에 연결됨 | 유저 코드 1행이 174행으로 표시 |
| **파서 에러 메시지 부실** | 중간 | FsLexYacc가 "parse error"만 반환 | 위치/원인 정보 없음 |
| **`unknownSpan` 잔존** | 낮음 | Elaboration.fs 7곳에서 사용 | 특정 에러에서 위치 표시 불가 |
| **비교 람다 unboxing 버그** | 중간 | boxed ptr에 arith.cmpi 적용 | `List.choose (fun x -> if x > 2 ...)` 실패 |
| **fslit `%input` temp 경로** | 낮음 | /var/folders/.../T/에 생성 | Prelude 미로딩으로 테스트 결과 달라짐 |

---

## 2. Prelude 줄 번호 오프셋 (최우선)

### 2.1 문제

```
Prelude (161줄) + "\n" (12개, 모듈 간 구분자) = 173줄 offset
유저 소스 1행 → 에러 메시지 174행
```

유저가 `file.fun:174:17: unbound variable 'y'`를 보면 자신의 코드에서 해당 줄을 찾을 수 없다.

### 2.2 해결 방안

#### 방안 A: 줄 번호 보정 (FunLangCompiler 수정, 권장)

Prelude 줄 수를 기록하고, 에러 메시지의 줄 번호에서 빼는 방식.

**수정 대상:** `src/FunLangCompiler.Cli/Program.fs`

```fsharp
// Prelude를 앞에 붙일 때 줄 수를 기록
let preludeLineCount =
    if preludeSrc = "" then 0
    else preludeSrc.Split('\n').Length + 1  // +1 for the joining "\n"

let combinedSrc = if preludeSrc = "" then src else preludeSrc + "\n" + src

// 에러 처리 시 줄 번호 보정
with ex ->
    let msg = adjustLineNumber ex.Message preludeLineCount
    ...
```

`adjustLineNumber`는 에러 메시지의 `file:LINE:COL:` 패턴에서 LINE을 `LINE - preludeLineCount`로 치환.

**장점:** FunLang/fslit 수정 불필요, 가장 간단
**단점:** 에러 메시지 파싱에 의존, Prelude 내부 에러는 음수 줄 번호 발생 가능

#### 방안 B: lexbuf 시작 위치 조정 (FunLang 활용)

`Lexer.setInitialPos`를 호출할 때, Prelude 뒤의 유저 코드 시작 위치를 기록하고, 유저 코드 부분만 별도 lexbuf로 파싱하는 대신 **lexbuf.StartPos를 음수 offset으로 설정**.

**문제:** FsLexYacc의 `Position`은 `Line`이 0부터 시작하므로 음수 불가. 실질적으로 불가능.

#### 방안 C: Prelude 별도 파싱 (가장 견고)

Prelude와 유저 코드를 **별도로 파싱**한 뒤 AST를 합치는 방식.

```fsharp
// 현재: 문자열을 합쳐서 한 번에 파싱
let combinedSrc = preludeSrc + "\n" + src
let ast = parseProgram combinedSrc inputPath

// 개선: 별도 파싱 후 AST 합치기
let preludeAst = parseProgram preludeSrc "<prelude>"
let userAst = parseProgram src inputPath
let mergedAst = mergeModules preludeAst userAst
```

**장점:** 줄 번호가 정확, Prelude 에러와 유저 에러 구분 가능
**단점:** AST 합치기 로직 필요 (Module decl 리스트 concat), 이름 충돌 처리 고려

**구체적 구현:**

```fsharp
let mergeModules (prelude: Ast.Module) (user: Ast.Module) : Ast.Module =
    let preludeDecls =
        match prelude with
        | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) | Ast.NamespacedModule(_, ds, _) -> ds
        | Ast.EmptyModule _ -> []
    match user with
    | Ast.Module(ds, s) -> Ast.Module(preludeDecls @ ds, s)
    | Ast.NamedModule(n, ds, s) -> Ast.NamedModule(n, preludeDecls @ ds, s)
    | Ast.NamespacedModule(n, ds, s) -> Ast.NamespacedModule(n, preludeDecls @ ds, s)
    | Ast.EmptyModule s -> Ast.Module(preludeDecls, s)
```

**예상 작업량:** ~30줄, 1시간

---

## 3. 파서 에러 메시지 개선

### 3.1 현재 상태

FsLexYacc 파서는 문법 오류 시 `"parse error"`만 반환. 토큰 종류, 기대 심볼, 위치 정보 없음.

```
[Parse] parse error
```

### 3.2 해결 방안

#### 방안 A: errorHandler 커스텀 (FunLang 수정)

FsLexYacc는 `Parser.parse_error` 이벤트를 제공. 현재 FunLang에서 이를 활용하지 않음.

```fsharp
// Parser.fsy의 %header에 추가
let parse_error_rich (ctxt: ParseErrorContext<token>) =
    let pos = ctxt.ParseState.InputStartPosition(ctxt.ParseState.ResultRange.Length)
    let tok = ctxt.CurrentToken
    failwithf "parse error at %s:%d:%d near token '%A'" filename pos.Line pos.Column tok
```

**문제:** FsLexYacc의 `ParseErrorContext`가 제한적. 기대 토큰 목록은 제공하지 않음.

#### 방안 B: 에러 시 토큰 위치 포함 (FunLangCompiler 수정)

현재 `parseProgram`에서 파서 예외를 잡을 때, 마지막으로 처리한 토큰의 위치를 기록하여 포함:

```fsharp
let mutable lastPos = Position.Empty
let tokenizer (lb: LexBuffer<char>) =
    if idx < arr.Length then
        let pt = arr.[idx]
        idx <- idx + 1
        lb.StartPos <- pt.StartPos
        lb.EndPos <- pt.EndPos
        lastPos <- pt.StartPos  // 마지막 토큰 위치 기록
        pt.Token
    else Parser.EOF

try
    Parser.parseModule tokenizer lexbuf2
with ex ->
    failwithf "%s:%d:%d: %s" filename lastPos.Line lastPos.Column ex.Message
```

**장점:** FunLang 수정 불필요
**예상 작업량:** ~10줄

---

## 4. `unknownSpan` 잔존 (7곳)

### Elaboration.fs 내 `unknownSpan` 사용처

| 줄 | 용도 | 해결 가능성 |
|----|------|------------|
| 798 | 클로저 캡처 실패 | 외부 스코프 바인딩의 Span 전달로 해결 가능 |
| 2041-2053 | Prelude 빌트인 elaboration | Prelude AST 노드의 Span 사용 가능 (방안 C 적용 시) |
| 2150 | 빌트인 elaboration | 상동 |
| 3004 | 패턴 매칭 내부 | 패턴 노드의 Span 전달로 해결 가능 |
| 4157 | 최상위 레벨 | elaborateProgram 호출 시 모듈 Span 사용 |

대부분은 호출부의 AST 노드에서 Span을 꺼내서 전달하면 해결 가능.

---

## 5. 비교 람다 Unboxing 버그

### 5.1 현상

```fsharp
List.choose (fun x -> if x > 2 then Some x else None) [1; 2; 3; 4]
```

→ `arith.cmpi sgt, %t0, %t1 : !llvm.ptr` — boxed pointer에 정수 비교 적용.

### 5.2 원인

`choose`의 람다 `f`는 `h :: t` 패턴에서 `h`를 받음. 리스트 원소는 boxed `!llvm.ptr`. 람다 내부에서 `x > 2` 비교 시 unboxing (`inttoptr`/`ptrtoint`) 없이 직접 `arith.cmpi` 호출.

`tryFind (fun x -> x > 2)`는 동작하는데 `choose`만 실패하는 이유:
- `tryFind`는 `pred h`를 호출하고 결과를 `if`로 분기 (bool)
- `choose`는 `f h`를 호출하고 결과를 `match`로 ADT 분기 (Option)
- `choose` 경로에서 `f`의 반환값이 ADT (`Some x`)이고, `x`는 unboxed 상태에서 다시 `Some`으로 boxing되는 과정에서 타입 불일치 발생

### 5.3 해결 방향

Elaboration.fs의 람다 elaboration에서 비교 연산자(`>`, `<`, `=` 등)의 피연산자 타입을 확인하고, `!llvm.ptr`이면 `ptrtoint`로 unboxing 후 비교해야 함. 이는 Elaboration의 타입 추론/변환 로직 개선이 필요하며, 별도 phase에서 다룰 사안.

---

## 6. fslit 개선 사항

### 6.1 이미 지원하는 기능

fslit (`deps/fslit/`)의 Checker.fs에서:

| 디렉티브 | 용도 | 예시 |
|----------|------|------|
| `CHECK-RE: <regex>` | 정규식 매칭 | `CHECK-RE: \[Elaboration\] .*:\d+:\d+:` |
| `CONTAINS: <text>` | 부분 문자열 매칭 | `CONTAINS: unbound variable 'y'` |
| `CHECK-NOT: <text>` | 출력에 없어야 함 | `CHECK-NOT: :0:0:` |
| `CHECK-NEXT: <text>` | 다음 줄 정확 매칭 | 순서 보장 |

### 6.2 에러 테스트에 적용

현재 `.flt` 파일은 정확한 줄 번호를 기대하므로 Prelude 변경 시 깨짐. `CHECK-RE:`를 사용하면:

```
// --- Output:
CHECK-RE: \[Elaboration\] .*:\d+:\d+: Elaboration: unbound variable 'y'
1
```

이렇게 하면 줄 번호가 변해도 테스트가 유지됨.

### 6.3 `%input` temp 경로 문제

- `%input`은 `/var/folders/.../T/fslit_XXXXXXXX_input`에 생성 (Substitution.fs:16)
- 이 경로에서 `Prelude/` 디렉토리를 찾을 수 없음
- **해결:** 에러 테스트는 `.fun` 파일을 직접 참조 (`%S/test.fun` 방식)하여 Prelude 로딩 보장. 이미 적용 완료.

### 6.4 잠재적 fslit 개선

| 기능 | 설명 | 필요성 |
|------|------|--------|
| `%input` 경로 커스텀 | `.flt` 파일 디렉토리에 temp 생성 | `%S/test.fun` 방식으로 우회 가능 |
| `CHECK-RE:` line suffix | `CHECK-RE:` + 일반 텍스트 조합 | 이미 full regex로 충분 |

---

## 7. FunLang 모듈 관련 업데이트

### 7.1 Span Position Fix (Phase 69, v9.1)

| 커밋 | 내용 |
|------|------|
| `64d77f8` | `PositionedToken` 타입 + `filterPositioned` 함수를 IndentFilter.fs에 추가 |
| `988098b` | `lexAndFilter`가 `PositionedToken list` 반환, `parseModuleFromString`에서 `lb.StartPos`/`lb.EndPos` 업데이트 |

**핵심 변경:**
- `IndentFilter.fs`: `PositionedToken = { Token; StartPos; EndPos }` 레코드 추가, `filterPositioned`는 토큰 필터링 시 위치 정보를 보존 (삽입되는 INDENT/DEDENT에는 직전 토큰의 위치 복사)
- `Program.fs`: 렉싱 시 `lexbuf.StartPos`/`EndPos` 캡처, 파서에 토큰 전달 시 `lb.StartPos`/`lb.EndPos` 설정

### 7.2 기타 모듈 관련 (FunLang v10.0)

| Phase | 내용 | FunLangCompiler 영향 |
|-------|------|-----------------|
| 71-74 | Typeclass/Instance (파싱→타입체커→elaboration→빌트인) | FunLangCompiler 미사용 (Elaboration에서 무시) |
| 68 | `ProjectFile.fs` (funproj.toml) | FunLangCompiler 미사용 |
| 66 | `LetRecContinuation` (expression-level let rec) | FunLangCompiler에서 이미 지원 |

---

## 8. 권장 실행 순서

### Phase A: Prelude 별도 파싱 (줄 번호 정확성)
- **대상:** `src/FunLangCompiler.Cli/Program.fs`
- **작업:** Prelude와 유저 코드를 별도 파싱 → AST merge
- **효과:** 에러 메시지의 줄 번호가 유저 소스 기준으로 정확
- **예상:** ~30줄, 테스트 업데이트 포함 2시간

### Phase B: 파서 에러 위치 추가
- **대상:** `src/FunLangCompiler.Cli/Program.fs`의 `parseProgram`
- **작업:** 마지막 토큰 위치를 파서 에러에 포함
- **효과:** `[Parse] file:42:5: parse error` 형태로 개선
- **예상:** ~10줄

### Phase C: 에러 테스트를 CHECK-RE로 전환
- **대상:** `tests/compiler/44-*.flt`, `46-*.flt`
- **작업:** 줄 번호 하드코딩 → `CHECK-RE:` 정규식 매칭
- **효과:** Prelude 변경에도 테스트 안정적
- **예상:** 8개 파일, 30분

### Phase D: unboxing 비교 버그 수정 (별도 milestone)
- **대상:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **작업:** 비교 연산자 elaboration 시 타입 확인/unboxing
- **효과:** `List.choose (fun x -> if x > 2 ...)` 동작
- **예상:** 조사 + 구현 4시간

---

## 9. 파일 참조

| 파일 | 프로젝트 | 역할 |
|------|---------|------|
| `src/FunLangCompiler.Cli/Program.fs` | FunLangCompiler | 파싱, Prelude 로딩, 에러 출력 |
| `src/FunLangCompiler.Compiler/Elaboration.fs` | FunLangCompiler | failWithSpan, 에러 메시지 생성 |
| `src/FunLangCompiler.Compiler/Pipeline.fs` | FunLangCompiler | MLIR 에러 보존 |
| `IndentFilter.fs` | FunLang | PositionedToken, filterPositioned |
| `Program.fs` | FunLang | parseModuleFromString (Span 전파) |
| `Parser.fsy` | FunLang | ruleSpan/symSpan (245개 사용처, unknownSpan 0) |
| `FsLit/src/Checker.fs` | fslit | CHECK-RE/CONTAINS/CHECK-NOT 매칭 |
| `FsLit/src/Substitution.fs` | fslit | %S/%input 치환 |

---

*이 문서는 FunLangCompiler v11.0 완료 후 에러 메시지 품질 향상을 위한 종합 조사 결과임.*
