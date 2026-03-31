# FunLexYacc Type Annotation Incompatibility Report

**Date:** 2026-03-30 (updated 2026-03-31)
**Context:** Phase 8 (08-07) compilation verification — `make funlex` fails with "parse error"
**Original Root Cause:** ~~LangBackend 문법이 함수 파라미터/반환값 타입 어노테이션을 지원하지 않음~~
**Revised Root Cause (Section 8):** LangThree 파서는 모든 타입 어노테이션을 이미 지원함. 실제 문제는 Elaboration.fs의 패턴 매칭이 `Annot`/`LambdaAnnot` AST 래퍼를 투과하지 못하는 것임.

---

## 1. 문제 요약

FunLexYacc 소스 코드는 F# 스타일 타입 어노테이션을 광범위하게 사용하지만,
LangBackend의 문법(AbstractGrammar.md)에서는 이를 지원하지 않는다.

**영향 범위:** src/ 내 18개 파일, 총 ~656개 어노테이션

---

## 2. 문법 근거 (AbstractGrammar.md 기준) — ⚠️ OUTDATED: Section 8 참조

### 2.1 함수 파라미터 — 타입 어노테이션 불가

```
// AbstractGrammar.md §2 Declarations
decl ::= 'let' IDENT param+ '=' expr
param ::= IDENT                           // ← IDENT만 허용, 타입 어노테이션 없음
```

**FunLexYacc 현재 코드 (파싱 실패):**
```fsharp
let bind (r : Result<'a>) (f : 'a -> Result<'b>) : Result<'b> =
//        ^^^^^^^^^^^^^^^  ^^^^^^^^^^^^^^^^^^^^^^^^  ^^^^^^^^^^^
//        param 타입 어노테이션    param 타입 어노테이션    반환 타입 어노테이션
```

**LangBackend 호환 코드:**
```fsharp
let bind r f =
```

### 2.2 반환 타입 어노테이션 — 문법에 없음

`let` 선언에서 `) : ReturnType =` 형식의 반환 타입 어노테이션은 문법에 정의되어 있지 않다.

### 2.3 람다 파라미터 — 타입 어노테이션 가능 (유일한 예외)

```
// AbstractGrammar.md §3 Expressions
expr ::= 'fun' '(' IDENT ':' type_expr ')' '->' expr    // ← 람다에서만 지원
```

### 2.4 식 수준 타입 어노테이션 — 가능하지만 무시됨

```
// AbstractGrammar.md §3.1 Atomic Expressions
atom ::= '(' expr ':' type_expr ')'     // 코드 생성 시 무시됨
```

### 2.5 제네릭 타입 구문

```
// AbstractGrammar.md §5 Type Expressions
type_expr ::= tuple_type '->' type_expr
atomic_type ::= atomic_type 'list'       // 후위: int list
              | atomic_type IDENT        // 후위 타입 적용: 'a option
              | TYPE_VAR                 // 'a, 'b, ...
```

- **후위(postfix) 스타일만 지원:** `int list`, `string option`
- **`<>` 앵글 브래킷 미지원:** `Result<'a>` ← 파싱 불가
- **올바른 구문:** type 선언에서 `type Result 'a = ...`, 사용 시 후위 스타일

---

## 3. 호환되지 않는 패턴 분류

### 패턴 A: 함수 파라미터 타입 어노테이션 (~437회)

```fsharp
// 현재 (파싱 실패)
let parseLexSpec (fileName : string) (input : string) : Result<LexSpec> =

// 수정 후
let parseLexSpec fileName input =
```

**세부 유형:**

| 유형 | 예시 | 출현 수 (추정) |
|------|------|---------------|
| 단순 타입 | `(c : int)`, `(s : string)` | ~120 |
| 리스트 타입 | `(items : string list)` | ~80 |
| 배열 타입 | `(argv : string array)` | ~30 |
| 옵션 타입 | `(opt : int option)` | ~40 |
| 레코드/ADT 타입 | `(nfa : Nfa)`, `(spec : LexSpec)` | ~90 |
| 함수 타입 | `(f : 'a -> Result<'b>)` | ~30 |
| 튜플 타입 | `(pair : int * string)` | ~25 |
| 제네릭 타입 | `(r : Result<'a>)` | ~22 |

### 패턴 B: 반환 타입 어노테이션 (~219회)

```fsharp
// 현재 (파싱 실패)
let isEmpty (cset : Cset) : bool =

// 수정 후
let isEmpty cset =
```

### 패턴 C: 앵글 브래킷 제네릭 구문 (~55회)

```fsharp
// 현재 (파싱 실패)
type Result<'a> =
let bind (r : Result<'a>) (f : 'a -> Result<'b>) : Result<'b> =

// 수정 후 — type 선언
type Result 'a =

// 수정 후 — 사용 시 (어노테이션 자체가 제거되므로 대부분 해당 없음)
```

---

## 4. 파일별 영향 분석

| 파일 | 파라미터 | 반환 | 합계 | 심각도 |
|------|---------|------|------|--------|
| funyacc/YaccEmit.fun | 70 | 32 | **102** | CRITICAL |
| funlex/LexParser.fun | 43 | 35 | **78** | CRITICAL |
| funyacc/GrammarParser.fun | 41 | 32 | **73** | CRITICAL |
| funlex/LexEmit.fun | 37 | 15 | **52** | HEAVY |
| funlex/Nfa.fun | 36 | 11 | **47** | HEAVY |
| funyacc/ParserTables.fun | 37 | 11 | **48** | HEAVY |
| common/Cset.fun | 27 | 14 | **41** | HEAVY |
| funyacc/Ielr.fun | 28 | 5 | **33** | HEAVY |
| funlex/Dfa.fun | 20 | 11 | **31** | HEAVY |
| funlex/DfaMin.fun | 18 | 7 | **25** | MODERATE |
| common/ErrorInfo.fun | 13 | 8 | **21** | MODERATE |
| funyacc/FirstFollow.fun | 13 | 8 | **21** | MODERATE |
| funyacc/Lalr.fun | 14 | 5 | **19** | MODERATE |
| funyacc/Lr0.fun | 11 | 7 | **18** | MODERATE |
| common/Symtab.fun | 8 | 6 | **14** | MODERATE |
| funyacc/FunyaccMain.fun | 9 | 4 | **13** | MODERATE |
| funlex/FunlexMain.fun | 8 | 4 | **12** | MODERATE |
| common/Diagnostics.fun | 4 | 4 | **8** | LOW |
| funlex/LexSyntax.fun | 0 | 0 | **0** | CLEAN |
| funyacc/GrammarSyntax.fun | 0 | 0 | **0** | CLEAN |
| **합계** | **~437** | **~219** | **~656** | |

상위 5개 파일이 전체의 53.7% (352/656)를 차지한다.

---

## 5. 마이그레이션 전략 — ⚠️ OUTDATED: 어노테이션 제거 불필요, Section 8 참조

### 5.1 필수 변환

| 변환 | Before | After | 비고 |
|------|--------|-------|------|
| 파라미터 타입 제거 | `let f (x : int) =` | `let f x =` | 437회 |
| 반환 타입 제거 | `let f x : int =` | `let f x =` | 219회 |
| 제네릭 `<>` → 공백 | `type Result<'a> =` | `type Result 'a =` | type 선언만 |
| named DU fields 제거 | `of loc: SrcLoc * msg: string` | `of SrcLoc * string` | ErrorInfo.fun |

### 5.2 자동화 가능성

- **파라미터 타입 어노테이션:** 정규식으로 `(IDENT : TYPE)` → `IDENT` 변환 가능하나, 중첩 괄호/함수 타입/튜플 타입 때문에 단순 정규식으로는 불완전
- **반환 타입 어노테이션:** `) : TYPE =` 패턴 탐지 — 타입 표현식이 복잡할 수 있어 수동 검증 필요
- **권장:** 파일별 수동 변환 + 컴파일 검증 반복

### 5.3 주의사항

1. **타입 어노테이션 제거 시 의미 변화 없음** — LangBackend는 타입 추론 기반이므로 어노테이션은 문서화 목적
2. **코드 가독성 저하** — 타입 정보가 사라지면 코드 이해가 어려워짐. 주석으로 보완 권장
3. **`private` 키워드** — AbstractGrammar.md에 `let private` 구문이 없으므로 이것도 제거 필요할 수 있음
4. **named DU fields** — `of loc: SrcLoc * msg: string` 형식은 ErrorInfo.fun에서만 사용, `of SrcLoc * string`으로 변환 필요

---

## 6. 추가 발견: `let private` 호환성

FunLexYacc에서 `let private` 사용 현황 확인 필요:

```
// F# 스타일
let private parseArgs (argv : string list) : Result<string * string> =

// LangBackend 문법에 'private'가 없다면:
let parseArgs argv =
```

---

## 7. 대안: LangBackend 파서 확장 — ⚠️ OUTDATED: 파서는 이미 지원함, Section 8 참조

타입 어노테이션 제거 대신 LangBackend 파서를 확장하는 방안:

```
// 현재
param ::= IDENT

// 확장안
param ::= IDENT
        | '(' IDENT ':' type_expr ')'
```

**장점:** FunLexYacc 소스 수정 불필요, F# 호환성 유지
**단점:** LangBackend 파서/AST 수정 필요, 타입 정보 처리 로직 추가

반환 타입 어노테이션도 유사하게 확장 가능:
```
decl ::= 'let' IDENT param+ ':' type_expr '=' expr    // 반환 타입 포함
```

---

*이 문서는 Phase 8 (08-07) 컴파일 검증 과정에서 발견된 문제를 기록한다.*

---

## 8. 조사 결과 (2026-03-31 업데이트)

### 8.1 핵심 발견: 파서는 이미 모든 형태를 지원한다

**이 문서의 전제 — "LangBackend 문법이 타입 어노테이션을 지원하지 않음" — 는 틀렸다.**

LangBackend는 LangThree의 파서(`Parser.fsy`)를 그대로 재사용하며, LangThree 파서는 이미 아래 구문을 **완전히** 지원한다:

| 구문 | Parser.fsy 위치 | AST 노드 | 지원 여부 |
|------|----------------|----------|----------|
| `let f (x : int) = ...` | lines 211-225, 716-723 | `LambdaAnnot` | ✅ 파싱 OK |
| `let f x : int = ...` | lines 149-156, 711-723 | `Annot` | ✅ 파싱 OK |
| `type Box<'a> = ...` | lines 569-585 | `TypeDecl` | ✅ 파싱 OK |
| `Box<int>` (타입 인자) | line 545 | `TEData` | ✅ 파싱 OK |
| `(x : 'a)` (타입 변수) | everywhere | `TEVar` | ✅ 파싱 OK |
| mixed params `let f x (y:int) z =` | lines 761-762 | `MixedParam` | ✅ 파싱 OK |

**AbstractGrammar.md는 실제 파서와 동기화되지 않은 구식 문서다.** 실제 문법은 Parser.fsy가 정의한다.

### 8.2 실제 문제: Elaboration.fs 패턴 매칭

파서는 정상 동작하지만, **Elaboration.fs의 패턴 매칭이 `Annot`/`LambdaAnnot` 래퍼를 투과하지 못한다.** 이것이 실제 컴파일 실패의 원인이다.

#### 문제 1: Two-Lambda KnownFuncs 인식 실패 (line 531)

```fsharp
// Elaboration.fs:531 — 2-인자 함수를 직접 호출 함수로 인식하는 패턴
| Let (name, Lambda (outerParam, Lambda (innerParam, innerBody, _), _), inExpr, _) ->
    // → 직접 호출 가능한 FuncOp 생성 (효율적)
```

`let f (x : int) y = x + y` 의 파서 출력:
```
Let("f",
  Annot(                          ← 반환 타입 래퍼
    LambdaAnnot("x", TInt,       ← 파라미터 타입 래퍼
      Lambda("y", Add(...)),
    _),
  _),
  continuation, _)
```

**패턴 `Let(name, Lambda(outer, Lambda(inner, ...)))` 에 매칭되지 않는다.**
→ generic `Let(name, bindExpr, ...)` (line 683)으로 fallthrough
→ 클로저로 컴파일됨 (비효율적이고 타입 불일치 발생 가능)

#### 문제 2: LetRec 반환 타입 결정 실패 (line 1018)

```fsharp
// Elaboration.fs:1018
let preReturnType = match body with | Lambda _ -> Ptr | _ -> I64
```

`let rec fact (n : int) : int = ...` → body가 `Annot(LambdaAnnot(...))` 이므로 `Lambda _`에 매칭되지 않음
→ `preReturnType = I64` (잘못됨, Lambda body는 Ptr여야 함)
→ 재귀 호출 시 타입 불일치로 MLIR 검증 실패

#### 문제 3: isListParamBody 패턴 매칭 (line 319)

```fsharp
let private isListParamBody (paramName: string) (bodyExpr: Expr) : bool =
    match bodyExpr with
    | Match(Var(scrutinee, _), clauses, _) when scrutinee = paramName -> ...
    | _ -> false
```

body가 `Annot(Match(...))` 이면 → `false` 반환
→ 리스트 파라미터를 I64로 잘못 판정 → 런타임 segfault

#### 문제 4: App Lambda 인라인 최적화 누락 (line 2142)

```fsharp
| Lambda(param, body, _) ->
    // Inline lambda application: (fun x -> body) arg ≡ let x = arg in body
```

`App(LambdaAnnot("x", type, body, _), arg)` → Lambda에 매칭되지 않음
→ 일반 클로저 호출로 fallback (동작은 하지만 비효율적)

### 8.3 테스트 검증 결과

5개 테스트 파일 (`tests/compiler/43-01` ~ `43-05`) 을 실행한 결과:

| 테스트 | 내용 | 결과 | 원인 |
|--------|------|------|------|
| 43-01 | 파라미터 타입 어노테이션 | 컴파일 OK, 런타임 segfault (exit 139) | 문제 1: 2-lambda KnownFuncs 미인식 → 클로저 ABI 불일치 |
| 43-02 | 반환 타입 어노테이션 | MLIR 검증 실패 (`ptr != i64` type mismatch) | 문제 2: Annot 래퍼가 to_string 인자 타입 추적 방해 |
| 43-03 | 파라미터+반환 복합 | 컴파일 OK, 런타임 segfault (exit 139) | 문제 1+2 복합 |
| 43-04 | `<>` 앵글 브래킷 제네릭 | 컴파일 OK, 런타임 segfault (exit 139) | 동일 근본 원인 |
| 43-05 | 제네릭 타입 변수 `'a` | Elaboration 에러: unbound variable | 어노테이션 래퍼가 let 바인딩 처리 방해 → 후속 변수 해석 실패 |

**공통 근본 원인:** `Annot`/`LambdaAnnot` AST 래퍼가 Elaboration.fs의 핵심 패턴 매칭을 방해한다.

### 8.4 수정 전략 (재평가)

~~Section 5의 "FunLexYacc 소스 코드에서 656개 어노테이션 제거" 전략은 불필요하다.~~
~~Section 7의 "LangBackend 파서 확장" 전략도 불필요하다 — 파서는 이미 지원한다.~~

**실제 필요한 수정: Elaboration.fs에 `Annot`/`LambdaAnnot` 투과 로직 추가**

#### 방법 A: stripAnnot 헬퍼 함수 (권장)

```fsharp
/// Annot/LambdaAnnot 래퍼를 재귀적으로 벗겨내어 내부 표현식을 반환
let rec stripAnnot (expr: Expr) : Expr =
    match expr with
    | Annot (inner, _, _) -> stripAnnot inner
    | LambdaAnnot (param, _, body, span) -> Lambda(param, stripAnnot body, span)
    | _ -> expr
```

적용 위치 (최소 변경 세트):

| 위치 | 현재 코드 | 수정 |
|------|----------|------|
| line 531 | `Let(name, Lambda(outer, Lambda(inner, ...)))` | `Let(name, (stripAnnot → Lambda(outer, (stripAnnot → Lambda(inner, ...)))))` |
| line 1018 | `match body with \| Lambda _ ->` | `match stripAnnot body with \| Lambda _ ->` |
| line 319 | `match bodyExpr with \| Match(...)` | `match stripAnnot bodyExpr with \| Match(...)` |
| line 139 | `Let(name, e1, e2, _) ->` freeVars | `stripAnnot` 적용하여 Lambda 내부 탐색 |
| line 69 | `isArrayExpr` | `stripAnnot` 적용 |
| line 82 | `detectCollectionKind` | `stripAnnot` 적용 |

#### 방법 B: 각 패턴 매치에 Annot/LambdaAnnot 변종 추가

```fsharp
| Let (name, Lambda (o, Lambda (i, ib, s2), s1), inExpr, s) ->
    // 기존 코드...
| Let (name, Annot(Lambda (o, Lambda (i, ib, s2), s1), _, _), inExpr, s) ->
    // 동일 코드...
| Let (name, LambdaAnnot(o, _, Lambda (i, ib, s2), s1), inExpr, s) ->
    // 동일 코드...
| Let (name, Annot(LambdaAnnot(o, _, Lambda(i, ib, s2), s1), _, _), inExpr, s) ->
    // 동일 코드...
```

**방법 A가 명확히 우월하다** — 코드 중복 없이 모든 Annot 중첩 조합을 처리한다.

### 8.5 영향 범위 재평가

| 항목 | 이전 평가 | 수정된 평가 |
|------|----------|------------|
| FunLexYacc 소스 수정 | 656개 어노테이션 제거 필요 | **불필요** — 소스 코드 그대로 사용 가능 |
| 파서 확장 | param/반환 타입 구문 추가 | **불필요** — 이미 지원됨 |
| `<>` 앵글 브래킷 | 파서 확장 필요 | **불필요** — 이미 지원됨 |
| Elaboration.fs 수정 | (고려되지 않음) | **필요** — `stripAnnot` 추가 + 6~8곳 적용 |
| 예상 작업량 | 18개 파일 수동 수정 | **Elaboration.fs 1개 파일, ~30줄 수정** |
| named DU fields | 수동 변환 필요 | 별도 확인 필요 (Elaboration.fs ADT 핸들러 점검) |
| `let private` | 확인 필요 | LangThree 파서가 지원하면 문제 없음 (별도 확인) |

### 8.6 결론

**이 문서의 Section 2 (문법 근거) 와 Section 5 (마이그레이션 전략) 는 더 이상 유효하지 않다.**

1. LangThree 파서는 파라미터 타입, 반환 타입, 앵글 브래킷 제네릭을 **이미 완전히 지원**한다.
2. LangBackend의 `Annot`/`LambdaAnnot` 핸들러도 존재하지만 (Phase 30, v8.0에서 추가), **단순 pass-through만 하고 주변 패턴 매칭과의 상호작용은 처리하지 않는다.**
3. 수정은 Elaboration.fs에 `stripAnnot` 헬퍼를 추가하고 핵심 패턴 매칭 6~8곳에 적용하는 것으로 충분하다.
4. 이 수정이 완료되면 FunLexYacc 소스 코드의 656개 타입 어노테이션을 **그대로 유지**한 채 컴파일할 수 있다.

**다음 단계:** 이 수정은 v11.0 마일스톤의 한 Phase로 계획할 수 있다. 예상 규모: 1 plan, ~30줄 Elaboration.fs 수정 + 5개 E2E 테스트 통과 검증.
