---
created: 2026-04-06
description: MLIR 컴파일러에서 direct-call 함수를 값으로 사용할 때 자동으로 closure를 생성하는 eta-expansion 구현
---

# KnownFuncs 자동 Eta-Expansion으로 First-Class Functions 지원하기

Direct-call 함수(KnownFuncs)를 값으로 넘기려 할 때 "unbound variable" 에러가 발생한다. `Var(name)` 처리에서 KnownFuncs를 자동 감지하여 closure로 wrapping하면 해결된다.

## The Insight

MLIR 기반 컴파일러에서 함수는 두 가지 형태로 존재한다:

| | KnownFuncs | Vars |
|---|---|---|
| **용도** | 직접 호출 (func.call) | 값으로 참조 (SSA value) |
| **호출** | `App(Var("f"), arg)` → DirectCallOp | closure indirect call |
| **참조** | 이름만 알면 됨 | SSA value가 있어야 함 |

`5 |> double`에서 `double`은 `|>`의 **인자**로 넘어간다. 인자는 SSA value여야 하므로 Vars에 있어야 한다. 하지만 `double`은 KnownFuncs에만 있다. 이 gap을 **eta-expansion**이 메운다: `double` → `fun x -> double x` (closure).

## Why This Matters

이 변환이 없으면:
```
[Elaboration] test.fun:4:7: Elaboration: undefined function '|>'
   Did you mean: dbg?
```

또는:
```
[Elaboration] test.fun:3:8: Elaboration: unbound variable 'double'
```

모든 higher-order function 패턴이 실패한다:
- `5 |> double` — pipe operator
- `List.map double xs` — 함수를 인자로 전달
- `apply double 5` — 일반적인 higher-order 호출

## Recognition Pattern

- "unbound variable" 에러인데, 해당 이름이 분명히 정의된 함수일 때
- 함수를 다른 함수의 인자로 넘기는 코드가 실패할 때
- Lambda로 감싸면 (`fun x -> double x`) 동작하지만, 함수 이름 직접 전달은 실패할 때

## The Approach

### Step 1: Var 처리 수정

`Var(name)` elaboration에서 Vars에 없을 때 바로 에러를 내지 않고, KnownFuncs를 확인한다:

```fsharp
| Var (name, span) ->
    match Map.tryFind name env.Vars with
    | Some v ->
        if Set.contains name env.MutableVars then
            let loaded = { Name = freshName env; Type = I64 }
            (loaded, [LlvmLoadOp(loaded, v)])
        else
            (v, [])
    | None ->
        // KnownFuncs에 있으면 자동 eta-expand
        match Map.tryFind name env.KnownFuncs with
        | Some sig_ when sig_.ClosureInfo.IsNone ->
            let n = env.Counter.Value
            env.Counter.Value <- n + 1
            let etaParam = sprintf "__eta_%d" n
            elaborateExpr env
                (Lambda(etaParam,
                    App(Var(name, span), Var(etaParam, span), span),
                    span))
        | _ -> failWithSpan span "Elaboration: unbound variable '%s'" name
```

### Step 2: 그게 전부다

**핵심**: AST 레벨에서 `Var("double")` → `Lambda("__eta_0", App(Var("double"), Var("__eta_0")))` 으로 변환하면, 기존의 Lambda elaboration이 closure를 생성하고, App(Var("double"), ...) 는 기존의 direct call로 처리된다.

새로운 코드 생성 로직이 전혀 필요 없다. 기존 Lambda와 App 처리가 모든 일을 한다.

### Step 3: ClosureInfo 체크

`sig_.ClosureInfo.IsNone` — direct-call 함수만 eta-expand한다. Closure-maker 함수(2-arg 함수의 outer function)는 이미 closure를 생성하는 특별한 calling convention이 있으므로 다르게 처리된다.

## Example

```fsharp
// 이 모든 패턴이 동작한다:

let double x = x * 2
let add1 x = x + 1

// pipe operator (Prelude 함수)
let r1 = 5 |> double |> add1    // 11

// 함수를 인자로 전달
let apply f x = f x
let r2 = apply double 5          // 10

// compose operator (Prelude 함수)  
let f = double >> add1
let r3 = f 3                     // 7
```

내부적으로 `5 |> double`은:
1. `|>`는 Prelude 함수: `let (|>) x f = f x`
2. `App(App(Var("|>"), 5), Var("double"))` 로 파싱
3. `Var("double")` → `Lambda("__eta_0", App(Var("double"), Var("__eta_0")))` (eta-expand)
4. Lambda가 closure 생성, `|>` 가 이 closure를 `f`로 받아 `f x` 호출

## 체크리스트

- [ ] `ClosureInfo.IsNone` 조건으로 direct-call만 eta-expand하는지 확인
- [ ] Counter 증가로 고유한 parameter 이름이 생성되는지 확인
- [ ] 기존 `App(Var(name), arg)` 패턴 (direct call)은 영향받지 않는지 확인
- [ ] `let f = double` (Let-Var alias) 패턴이 여전히 동작하는지 확인

## 관련 문서

- `add-lambda-lifting-pass.md` - nested let rec에서 eta-expand된 함수를 parameter로 전달
- `add-let-normalization-pass.md` - eta-expand로 생성된 closure가 control-flow 내에서 사용될 때
