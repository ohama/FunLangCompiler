---
created: 2026-04-09
description: AnnotationMap이 비어있을 때 heuristic fallback chain 설계와 디버깅
---

# 컴파일러 Heuristic 디스패치 디버깅

타입 정보 없이 AST 구조만으로 코드 생성을 결정하는 heuristic이 실패할 때, 체계적으로 모든 경로를 커버하는 방법.

## The Insight

컴파일러의 heuristic 디스패치는 **레이어드 fallback chain**이다. 한 레이어가 실패하면 다음 레이어로 넘어가는데, 버그는 항상 "이 경로에서는 fallback이 없다"는 형태로 나타난다. 수정도 한 곳이 아니라 **모든 경로에서 동일 패턴을 적용**해야 한다.

## Why This Matters

FunLangCompiler에서 `isStringExpr`은 string 변수를 식별하여 `lang_string_char_at` vs `lang_index_get`을 선택한다. 이 판단이 틀리면 string을 array로 해석하여 heap_tag(1)를 length로, length(5)를 첫 원소로 읽는다 — silent wrong result.

증상: `s.[0]`이 `104`('h') 대신 `2`(= 5 >> 1)를 반환. 에러 없이 잘못된 값.

## Recognition Pattern

다음 조건이 모두 참일 때 이 패턴을 의심한다:

- 같은 코드가 **let binding에서는 동작**하지만 **함수 파라미터에서는 실패**
- 또는 **1-param 함수에서는 동작**하지만 **2-param에서는 실패**
- 또는 **non-recursive에서는 동작**하지만 **let rec에서는 실패**
- 컴파일 에러 없이 런타임에 잘못된 값

## The Approach

### Step 1: Fallback chain 전체를 나열한다

```
isStringExpr 판단 순서:
1. AnnotationMap (타입 추론 결과)     ← 비어있으면 실패
2. StringVars (환경에 등록된 변수)    ← 등록 안 됐으면 실패
3. String literal 체크                ← 변수는 해당 없음
4. FieldAccess (StringFields)         ← 파라미터는 해당 없음
5. → false (fallback 없음 = 버그)
```

### Step 2: 각 코드 경로에서 chain의 어느 레이어가 작동하는지 확인한다

```
let-bound string:     AnnotationMap ✓ (타입 체크 성공)
함수 param string:    AnnotationMap ✗ (FunLang이 s.[i] 거부) → StringVars 필요
2-param closure:      StringVars ✗ (inner env에 전파 안 됨) → 전파 필요
let rec + capture:    StringVars ✗ (lambda lift가 annotation 제거) → env fallback 필요
```

### Step 3: 모든 env 생성 지점을 찾아 동일 패턴을 적용한다

```bash
grep -n "StringVars = Set.empty" src/FunLangCompiler.Compiler/Elaboration.fs
```

발견된 모든 지점에서 "이 env에서 string 변수가 필요한가?"를 묻는다.

### Step 4: capture load 타입도 확인한다

string 변수가 closure에 capture될 때 `capType`이 `I64`로 설정되면 ptr provenance를 잃는다:

```fsharp
// ❌ BAD: string capture를 I64로 load
let capType = if Set.contains capName env.MutableVars then Ptr else I64

// ✅ GOOD: string도 Ptr로 load
let capType = if Set.contains capName env.MutableVars || Set.contains capName env.StringVars then Ptr else I64
```

## Example

Issue #22의 3단계 수정 과정:

```fsharp
// Phase 1: isPtrParamBody에 IndexGet case 추가
// → 파라미터 타입이 i64 대신 !llvm.ptr로 설정됨
| IndexGet(Var(v, _), _, _) when v = paramName -> true

// Phase 2: StringVars에 파라미터 등록 (4개 경로)
// → isStringExpr가 string 파라미터를 인식
// 경로 1: Let-Lambda single-param (line ~352)
// 경로 2: Let-Lambda 2-param inner env (line ~164)
// 경로 3: LetRec single-param (line ~829)
// 경로 4: General Lambda closure (line ~2577) — env.StringVars 전파

// Phase 3: Lambda lift fallback (line ~802)
// → lift된 param은 annotation이 없으므로 env.StringVars로 판단
let paramIsString =
    match Map.tryFind bindingSpan env.AnnotationMap with
    | Some (Type.TArrow(Type.TString, _)) -> true
    | _ ->
    match paramTypeAnnot with
    | Some TEString -> true
    | _ -> Set.contains param env.StringVars  // ← 최종 fallback
```

## 체크리스트

- [ ] `grep "StringVars = Set.empty"` — 모든 env 생성 지점 확인
- [ ] 각 지점에서 string 파라미터/capture가 필요한지 판단
- [ ] capture load의 `capType`에서 `StringVars` 체크 추가
- [ ] 1-param, 2-param, let rec, closure capture 4가지 경로 테스트
- [ ] `-O0`과 `-O2` 둘 다 테스트 (LLVM 최적화가 masking할 수 있음)

## 관련 문서

- `add-lambda-lifting-pass.md` - Lambda lifting이 annotation을 제거하는 이유
- `extract-elaboration-helpers.md` - isStringExpr, isPtrParamBody 위치
