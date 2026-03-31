# LangThree Span Zeroing Bug — 수정 가이드

**Date:** 2026-03-31
**Context:** LangBackend v11.0 Phase 44에서 발견. `failWithSpan`이 `:0:0:` 출력.
**Root Cause:** `parseModuleFromString`이 새 lexbuf를 생성하여 파서에 전달. 토큰의 위치 정보가 전파되지 않음.

---

## 1. 문제 요약

LangThree 파서가 생성하는 AST 노드의 Span이 모두 `{FileName=""; StartLine=0; StartColumn=0}` 또는 `{FileName=filename; StartLine=1; StartColumn=0}`으로 채워짐. 실제 소스 코드의 행/열 위치가 반영되지 않음.

**영향:** LangBackend의 에러 메시지에 소스 위치를 표시할 수 없음.

---

## 2. 근본 원인: `Program.fs` lines 31-43

```fsharp
let parseModuleFromString (input: string) (filename: string) : Module =
    let filteredTokens = lexAndFilter input filename      // (1) 렉싱 + 필터링
    let lexbuf = LexBuffer<char>.FromString input         // (2) ★ 새 lexbuf 생성
    Lexer.setInitialPos lexbuf filename                   // (3) filename만 설정
    let mutable index = 0
    let tokenizer (_lexbuf: LexBuffer<_>) =               // (4) ★ lexbuf 무시
        if index < filteredTokens.Length then
            let tok = filteredTokens.[index]
            index <- index + 1
            tok
        else Parser.EOF
    Parser.parseModule tokenizer lexbuf                   // (5) ★ 미진행 lexbuf 전달
```

**문제:**
1. `lexAndFilter`에서 토큰을 렉싱할 때 사용한 lexbuf는 버려짐
2. 새 lexbuf가 생성되지만 실제로 토큰을 렉싱하지 않음 (위치 미진행)
3. 커스텀 `tokenizer`는 리스트에서 토큰을 꺼내지만, lexbuf의 `StartPos`/`EndPos`를 업데이트하지 않음
4. FsLexYacc 파서는 `parseState.InputStartPosition(n)`으로 위치를 조회하는데, lexbuf가 진행되지 않았으므로 항상 초기값 반환

**핵심:** 토큰 → 필터 → 파서 경로에서 **위치 정보가 손실**됨.

---

## 3. 정상 동작 흐름 (FsLexYacc 설계 의도)

```
lexbuf → Lexer.tokenize → 토큰 반환 + lexbuf.EndPos 업데이트
                                ↓
                     Parser가 토큰 shift 시 lexbuf 위치를 내부 테이블에 기록
                                ↓
                     ruleSpan(parseState, 1, 3) → 내부 테이블에서 위치 조회
```

LangThree는 IndentFilter를 위해 이 흐름을 끊음:
```
lexbuf₁ → 전체 렉싱 → 토큰 리스트 → IndentFilter → 필터된 토큰 리스트
                                                          ↓
lexbuf₂ (새로 생성, 미진행) → 파서에 전달 ← 필터된 토큰 (위치 없음)
```

---

## 4. 수정 방안

### 방안 A: 토큰에 위치 정보 임베딩 (권장)

**개요:** 각 토큰에 `Position` 정보를 첨부하고, 커스텀 tokenizer가 토큰을 반환할 때 lexbuf의 위치를 업데이트.

**수정 대상:**
1. `Lexer.fsl` — 렉싱 시 각 토큰의 `(startPos, endPos)` 기록
2. `Program.fs` — `lexAndFilter`가 `(token * Position * Position) list` 반환
3. `IndentFilter.fs` — 위치 정보를 보존하며 토큰 필터링
4. `Program.fs` — 커스텀 tokenizer가 토큰 반환 시 `lexbuf.StartPos`/`EndPos` 업데이트

**구체적 수정:**

#### Step 1: 위치 포함 토큰 타입

```fsharp
// Program.fs 또는 별도 모듈
type PositionedToken = {
    Token: Parser.token
    StartPos: Position
    EndPos: Position
}
```

#### Step 2: lexAndFilter 수정

```fsharp
let lexAndFilter (input: string) (filename: string) : PositionedToken list =
    let lexbuf = LexBuffer<char>.FromString input
    Lexer.setInitialPos lexbuf filename
    let rec collect () =
        let startPos = lexbuf.StartPos
        let tok = Lexer.tokenize lexbuf
        let endPos = lexbuf.EndPos
        if tok = Parser.EOF then
            [{ Token = Parser.EOF; StartPos = startPos; EndPos = endPos }]
        else
            { Token = tok; StartPos = startPos; EndPos = endPos } :: collect ()
    let rawTokens = collect ()
    filterWithPositions defaultConfig rawTokens  // IndentFilter도 위치 보존
```

#### Step 3: IndentFilter 수정

IndentFilter가 `PositionedToken list`를 받아서 `PositionedToken list`를 반환하도록 변경.
삽입되는 INDENT/DEDENT/SEMICOLON 토큰에는 **직전 토큰의 위치**를 복사.

#### Step 4: 커스텀 tokenizer에서 lexbuf 위치 업데이트

```fsharp
let parseModuleFromString (input: string) (filename: string) : Module =
    let filteredTokens = lexAndFilter input filename
    let lexbuf = LexBuffer<char>.FromString input
    Lexer.setInitialPos lexbuf filename
    let mutable index = 0
    let tokenizer (lb: LexBuffer<_>) =
        if index < filteredTokens.Length then
            let pt = filteredTokens.[index]
            index <- index + 1
            lb.StartPos <- pt.StartPos    // ★ 위치 업데이트
            lb.EndPos <- pt.EndPos        // ★ 위치 업데이트
            pt.Token
        else Parser.EOF
    Parser.parseModule tokenizer lexbuf
```

**이 방안의 핵심:** `lb.StartPos`/`lb.EndPos` 를 토큰 반환 시마다 설정하면, FsLexYacc 파서가 내부 테이블에 올바른 위치를 기록함.

### 방안 B: 최소 수정 (tokenizer만 수정)

IndentFilter를 건드리지 않고, `lexAndFilter`에서 위치 맵을 만들어 tokenizer에서 사용.

```fsharp
let lexAndFilterWithPositions (input: string) (filename: string) =
    let lexbuf = LexBuffer<char>.FromString input
    Lexer.setInitialPos lexbuf filename
    let positions = System.Collections.Generic.List<Position * Position>()
    let rec collect () =
        let startPos = lexbuf.StartPos
        let tok = Lexer.tokenize lexbuf
        let endPos = lexbuf.EndPos
        positions.Add(startPos, endPos)
        if tok = Parser.EOF then [Parser.EOF]
        else tok :: collect ()
    let rawTokens = collect ()
    let filtered = filter defaultConfig rawTokens |> Seq.toList
    // 주의: filtered 토큰 수 != rawTokens 수 (INDENT/DEDENT 추가됨)
    // → 매핑이 깨짐. 이 방안은 IndentFilter가 토큰을 추가/제거하므로 불완전.
    (filtered, positions)
```

**단점:** IndentFilter가 토큰을 삽입/제거하므로 위치 인덱스가 일대일 대응되지 않음. **방안 A가 더 안정적.**

---

## 5. 수정 범위 요약

| 파일 | 수정 내용 | 난이도 |
|------|----------|--------|
| `Program.fs` | `lexAndFilter` + `parseModuleFromString` + `parseExprFromString` | 중 |
| `IndentFilter.fs` | `PositionedToken` 지원 또는 위치 보존 필터링 | 중~상 |
| `Lexer.fsl` | (수정 불필요 — 이미 lexbuf 위치를 올바르게 업데이트) | - |
| `Parser.fsy` | (수정 불필요 — ruleSpan/symSpan이 이미 올바르게 구현됨) | - |
| `Ast.fs` | (수정 불필요 — Span/mkSpan이 올바르게 정의됨) | - |

**예상 작업량:** 방안 A 기준 ~100줄 수정, 1-2시간

---

## 6. 검증 방법

수정 후 LangBackend에서 확인:

```bash
# 1. 에러 메시지에 실제 위치가 나오는지 확인
echo 'let x = 42
let _ = println (to_string y)' > /tmp/test_span.lt
dotnet run --project src/LangBackend.Cli/LangBackend.Cli.fsproj -- /tmp/test_span.lt 2>&1
# 예상: /tmp/test_span.lt:2:26: Elaboration: unbound variable 'y'
# 현재: :0:0: Elaboration: unbound variable 'y'
```

---

## 7. 참고: LangBackend 측 인프라는 완성

LangBackend의 `failWithSpan` (Elaboration.fs:63)은 Span 필드를 올바르게 읽음:
```fsharp
let inline failWithSpan (span: Ast.Span) fmt =
    Printf.ksprintf (fun msg ->
        failwith (sprintf "%s:%d:%d: %s" span.FileName span.StartLine span.StartColumn msg)
    ) fmt
```

LangThree가 Span에 올바른 값을 채우면, LangBackend의 에러 메시지에 자동으로 정확한 위치가 표시됨.

---

*이 문서는 LangBackend v11.0 Phase 44 검증 과정에서 작성됨.*
*LangThree 수정 시 이 가이드를 참조할 것.*
