---
created: 2026-04-06
description: MLIR 기반 컴파일러에서 nested let rec의 outer scope capture를 해결하는 Lambda Lifting AST pass 구현
---

# MLIR 컴파일러에 Lambda Lifting Pass 추가하기

MLIR func.func는 enclosing scope의 SSA 값을 참조할 수 없다. Nested let rec가 outer scope 변수를 캡처하면, 그 변수를 explicit parameter로 끌어올려야 한다.

## The Insight

MLIR의 func.func는 독립적인 function region이다. 일반 프로그래밍 언어처럼 "closure"가 아니라, 모든 입력이 parameter로 명시되어야 한다. Nested recursive function이 outer scope를 참조하는 코드를 이 모델에 맞추려면, 참조하는 변수를 parameter로 끌어올리는 **Lambda Lifting** (Johnsson 1985) 변환이 필요하다.

핵심은 "무엇이 local variable이고 무엇이 global function인가"를 정확히 구분하는 것이다. Global function은 어떤 func.func에서든 직접 호출 가능하므로 lifting 대상이 아니다.

## Why This Matters

이 변환이 없으면:
```
[Elaboration] test.fun:5:17: unbound variable 'n'
```
Nested let rec의 body가 standalone func.func로 컴파일될 때, outer scope의 `n`을 찾지 못한다.

## Recognition Pattern

- Nested `let rec` 안에서 enclosing function의 parameter나 local variable을 참조할 때
- "unbound variable" 에러가 함수 정의 내부에서 발생할 때
- Prelude 같은 라이브러리 코드가 nested helper를 사용하는 패턴을 지원해야 할 때

## The Approach

AST-to-AST transformation pass로 구현한다. Elaboration(IR 생성) 전에 AST를 순회하면서:

1. **locally-bound variables를 추적**하며 트리를 순회
2. LetRec를 만나면 body의 free variables ∩ localVars = **captures** 계산
3. captures가 있으면 explicit parameter로 추가 + 모든 호출부도 rewrite

### Step 1: localVars 추적 규칙 정의

이것이 가장 중요하고 가장 실수하기 쉬운 부분이다.

```
추적 O (localVars에 추가):
  - Lambda parameter: fun x -> ...  → x
  - Let (non-function) binding: let y = expr → y
  - LetMut binding: let mutable z = ... → z
  - LetPat binding: let (a, b) = ... → a, b

추적 X (localVars에 추가하지 않음):
  - Let-Lambda binding: let f x = ...  → f  (KnownFuncs로 등록됨)
  - LetRec binding names: let rec g i = ... → g  (KnownFuncs로 등록됨)
  - Let-Var (open alias): let fst = Core_fst  → fst  (KnownFunc 참조)
```

**핵심 실수 사례**: `open Module`이 생성하는 alias는 `Let("name", Var("Module_name"), body)` 형태다. `Var`는 Lambda가 아니므로 단순히 "Lambda가 아니면 local"으로 판단하면, 모든 open alias가 capture 대상이 되어 대규모 실패가 발생한다.

```fsharp
let isFunction =
    match bind with
    | Lambda _ | LambdaAnnot _ -> true
    | Annot(Lambda _, _, _) -> true
    | Var _ -> true  // open-alias: Let(shortName, Var(qualifiedName))
    | _ -> false
let localVars' = if isFunction then localVars else Set.add name localVars
```

### Step 2: Captures 계산

LetRec의 각 binding에 대해:

```fsharp
let boundInBinding = Set.union bindingNames (Set.singleton param)
let freeInBody = freeVars boundInBinding body
let captures = Set.intersect freeInBody localVars
```

Mutual recursion의 경우, 모든 binding의 captures를 union한다.

### Step 3: 변환 적용

Original:
```fsharp
let rec init n f =
    let rec helper i = if i = n then [] else f i :: helper (i + 1)
    helper 0
```

Transformed:
```fsharp
let rec init n f =
    let rec helper n f i = if i = n then [] else helper n f (i + 1)
    helper n f 0
```

**같은 이름을 사용**하는 것이 핵심이다. Capture된 `n`, `f`를 parameter로 추가할 때 이름을 바꾸지 않는다. Parameter가 outer scope의 같은 이름을 shadow하므로, body 내부의 참조가 자연스럽게 parameter를 가리킨다.

Self-reference rewrite: body와 continuation에서 `Var("helper")`를 `App(App(Var("helper"), Var("n")), Var("f"))`로 교체한다.

### Step 4: 파이프라인에 추가

```fsharp
let mainExpr = extractMainExpr (moduleSpanOf ast) decls
let mainExpr = LambdaLift.liftExpr mainExpr    // ← 여기
let mainExpr = LetNormalize.normalizeExpr mainExpr
// ... elaborateExpr env mainExpr
```

## Example

```fsharp
// Before: "unbound variable 'pred'" 에러
let partition pred xs =
    let rec go yes no = fun xs ->
        match xs with
        | [] -> (reverse [] yes, reverse [] no)
        | h :: t -> if pred h then go (h :: yes) no t else go yes (h :: no) t
    go [] [] xs

// After lambda lifting (자동 변환):
// go는 pred를 explicit parameter로 받음
// go의 모든 재귀 호출에 pred가 전달됨
```

## 체크리스트

- [ ] freeVars 함수가 모든 AST 노드를 커버하는지 확인
- [ ] Let-Lambda, LetRec, open-alias를 localVars에서 **제외**했는지 확인
- [ ] Mutual recursion (let rec ... and ...) 시 captures를 union했는지 확인
- [ ] `prependCaptures`가 shadowing을 올바르게 처리하는지 확인
- [ ] 전체 E2E 테스트 통과 확인

## 관련 문서

- `add-let-normalization-pass.md` - 같은 세션에서 추가한 또 다른 AST pass
- `add-auto-eta-expansion.md` - Lambda lifting과 함께 first-class function 지원
