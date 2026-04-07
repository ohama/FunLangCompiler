---
created: 2026-04-06
description: MLIR 컴파일러에서 control-flow sub-expression이 operand 위치에 올 때 발생하는 terminator 배치 문제를 해결하는 partial ANF pass 구현
---

# MLIR 컴파일러에 Let-Normalization (Partial ANF) Pass 추가하기

MLIR의 기본 block은 terminator(cf.br, cf.cond_br)로 끝나야 한다. Compound expression의 operand가 제어 흐름(if, match)을 생성하면 terminator가 block 중간에 놓여 검증 실패한다. Partial ANF 변환으로 이를 해결한다.

## The Insight

MLIR의 basic block은 반드시 **하나의 terminator op**으로 끝나야 하고, 그 뒤에 다른 op이 올 수 없다. 표현식 `acc :: (match xs with ...)` 를 순서대로 elaborate하면:

1. `acc` elaborate → ops
2. `match xs with ...` elaborate → ops + **cf.cond_br** (terminator) + side blocks
3. Cons alloc ops → **terminator 뒤에 배치됨** → MLIR 검증 실패

이 문제의 근본 원인: 표현식 기반 언어에서는 어디든 제어 흐름이 올 수 있지만, MLIR은 제어 흐름과 연산을 엄격히 분리한다.

해결은 간단하다: **제어 흐름을 생성하는 sub-expression을 let-binding으로 추출**한다. 이것이 A-Normal Form (ANF)의 핵심 아이디어이며, 전체 ANF가 아니라 필요한 경우만 변환하므로 "partial ANF"이다.

## Why This Matters

이 변환이 없으면:
```
mlir-opt error: operation with block successors must terminate its parent block
    cf.cond_br %t7, ^match_yes2, ^match_no3
    ^
```

If, Match, And, Or 등이 Cons, Add, Tuple 등의 operand 위치에 오면 항상 이 에러가 발생한다. 개별 expression에 대해 각각 "merge block continuation" 핸들링을 추가할 수도 있지만, AST pass 하나로 모든 경우를 일반적으로 해결하는 것이 훨씬 낫다.

## Recognition Pattern

- `operation with block successors must terminate its parent block` MLIR 에러
- Compound expression(Cons, Add, Tuple 등)의 operand에 If/Match/And/Or가 올 때
- 새로운 expression form을 추가할 때마다 merge block 처리를 반복하게 될 때

## The Approach

### Step 1: "Complex expression" 정의

제어 흐름을 생성하는 expression을 식별한다:

```fsharp
let isComplexExpr (e: Expr) : bool =
    match e with
    | If _ | Match _ | And _ | Or _ | TryWith _ -> true
    | _ -> false
```

이것이 정확히 MLIR에서 **multiple basic blocks를 생성**하는 expression들이다.

### Step 2: letBind 헬퍼

Complex expression을 let-binding으로 추출하는 함수:

```fsharp
let letBind (span: Span) (e: Expr) : Expr * (Expr -> Expr) =
    if isComplexExpr e then
        let name = freshName ()  // "__anf_0", "__anf_1", ...
        (Var(name, span), fun body -> Let(name, e, body, span))
    else
        (e, id)  // simple expression은 그대로
```

반환값: (대체할 변수, body를 감쌀 wrapper). `id`는 wrapper가 필요없을 때.

### Step 3: 각 compound expression에 적용

```fsharp
| Cons(head, tail, s) ->
    let head' = norm head
    let tail' = norm tail
    let (hv, hwrap) = letBind s head'
    let (tv, twrap) = letBind s tail'
    hwrap (twrap (Cons(hv, tv, s)))
```

변환 예:
```
// Before
acc :: (match xs with | [] -> [] | h :: t -> scan f (f acc h) t)

// After
let __anf_0 = match xs with | [] -> [] | h :: t -> scan f (f acc h) t
acc :: __anf_0
```

### Step 4: 재귀적으로 모든 노드 처리

- **Compound expressions** (Add, Cons, Tuple, Equal, ...): operand를 letBind로 감쌈
- **Binding forms** (Let, Lambda, LetRec, ...): sub-expression에 재귀
- **Control flow** (If, Match, And, Or): branch 내부에 재귀 (자기 자신은 추출 대상)
- **Leaves** (Var, Number, ...): 그대로

### Step 5: 파이프라인에 추가

```fsharp
let mainExpr = LambdaLift.liftExpr mainExpr
let mainExpr = LetNormalize.normalizeExpr mainExpr  // ← 여기
// ... elaborateExpr env mainExpr
```

Lambda Lifting 이후, Elaboration 이전에 실행한다.

## Example

```fsharp
// Before: MLIR "block successors must terminate parent block" 에러
let rec scan f acc xs =
    acc :: (match xs with
           | [] -> []
           | h :: t -> scan f (f acc h) t)

// After let-normalization (자동 변환):
let rec scan f acc xs =
    let __anf_0 = match xs with
                  | [] -> []
                  | h :: t -> scan f (f acc h) t
    acc :: __anf_0
```

Match가 먼저 완료되고 결과가 `__anf_0`에 바인딩된 후, Cons가 실행된다. Terminator가 block 중간에 놓이는 문제가 사라진다.

## 체크리스트

- [ ] isComplexExpr에 모든 control-flow expression이 포함되었는지 확인
- [ ] Simple expression (Var, Number, App 등)은 letBind하지 않는지 확인
- [ ] Binding forms (Let, Lambda 등)에서는 letBind 없이 재귀만 하는지 확인
- [ ] Counter가 호출마다 리셋되는지 확인 (이름 충돌 방지)
- [ ] 전체 E2E 테스트 통과 확인

## 관련 문서

- `add-lambda-lifting-pass.md` - 같은 세션에서 추가한 또 다른 AST pass
- `add-auto-eta-expansion.md` - elaboration 시 KnownFuncs를 closure로 자동 변환
