---
created: 2026-04-07
description: Elaboration.fs의 반복 패턴을 ElabHelpers.fs 헬퍼로 추출하는 방법론
---

# Elaboration 패턴 추출하기

Elaboration.fs에서 반복되는 코드 패턴을 식별하고 ElabHelpers.fs 헬퍼 함수로 추출한다.

## The Insight

Elaboration.fs는 4000줄 이상의 패턴 매칭으로 구성된다. 같은 "elaborate → coerce → call C → convert result" 패턴이 수십 곳에서 반복된다. 이 패턴들을 헬퍼로 추출하면 버그 수정이 한 곳에서 이루어지고, 새 builtin 추가도 1줄로 가능해진다.

## Why This Matters

반복 패턴을 방치하면: tagged representation 같은 전역 변환 시 수십 곳을 수동 수정해야 하고, 한 곳이라도 빠지면 런타임 crash. 헬퍼로 추출하면 변환이 헬퍼 한 곳에서 끝난다.

## Recognition Pattern

- 같은 3-5줄 시퀀스가 3곳 이상에서 나타남
- C 함수 이름만 다르고 나머지가 동일한 블록
- `if val.Type = I64 then ... else ...` 분기가 여러 곳에서 반복

## The Approach

### Step 1: 패턴 식별

Elaboration.fs에서 반복을 찾는 검색 패턴:

```bash
# 같은 coercion 패턴 찾기
grep -n "coerceToPtrArg\|coerceToI64\|emitUntag\|emitRetag" Elaboration.fs | head -30

# inline coercion (헬퍼 미사용)
grep -n "LlvmIntToPtrOp\|LlvmPtrToIntOp\|ArithExtuIOp" Elaboration.fs

# C 함수 호출 패턴
grep -n "LlvmCallOp.*@lang_" Elaboration.fs | wc -l
```

### Step 2: 추출 대상 분류

| 패턴 | 헬퍼 이름 | 적용 횟수 |
|------|-----------|----------|
| I64→I1 truthiness | `coerceToI1` | 5회 |
| 2-arg string predicate (C returns 0/1) | `emitStrPredicate` | 3회 |
| 1-arg char predicate (untag + C call) | `emitCharPredicate` | 4회 |
| inline I64→Ptr | `coerceToPtrArg` (기존) | 7회 |
| inline I1→I64 | `coerceToI64` (기존) | 4회 |

### Step 3: 헬퍼 작성 규칙

**위치:** ElabHelpers.fs, `freshName` 정의 이후

**시그니처 패턴:**
```fsharp
// 단순 coercion (expr 불필요)
let helperName (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list

// C 호출 포함 (expr 필요 — elaborateExpr를 인자로 받음)
let helperName (env: ElabEnv) (cFunc: string) (expr: Ast.Expr)
    (elaborateExpr: ElabEnv -> Ast.Expr -> MlirValue * MlirOp list)
    : MlirValue * MlirOp list
```

**`elaborateExpr`를 인자로 받는 이유:** ElabHelpers.fs는 Elaboration.fs보다 먼저 컴파일된다. `elaborateExpr`를 직접 참조할 수 없으므로 함수 인자로 주입한다.

### Step 4: 적용

```fsharp
// ❌ BEFORE: 15줄 인라인
| App (App (Var ("string_contains", _), strExpr, _), subExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let (subVal, subOps) = elaborateExpr env subExpr
    let (strPtr, strCoerce) = coerceToPtrArg env strVal
    let (subPtr, subCoerce) = coerceToPtrArg env subVal
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_string_contains", [strPtr; subPtr])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, strOps @ subOps @ strCoerce @ subCoerce @ ops)

// ✅ AFTER: 1줄
| App (App (Var ("string_contains", _), strExpr, _), subExpr, _) ->
    emitStrPredicate env "@lang_string_contains" strExpr subExpr elaborateExpr
```

## Example: coerceToI1

```fsharp
// ElabHelpers.fs
let coerceToI1 (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list =
    if v.Type = I1 then (v, [])
    else
        let taggedFalse = { Name = freshName env; Type = I64 }
        let boolVal = { Name = freshName env; Type = I1 }
        (boolVal, [ArithConstantOp(taggedFalse, 1L); ArithCmpIOp(boolVal, "ne", v, taggedFalse)])

// Elaboration.fs — 적용
// ❌ BEFORE
let (i1CondVal, coerceCondOps) =
    if condVal.Type = I64 then
        let zeroVal = { Name = freshName env; Type = I64 }
        let boolVal = { Name = freshName env; Type = I1  }
        (boolVal, [ArithConstantOp(zeroVal, 1L); ArithCmpIOp(boolVal, "ne", condVal, zeroVal)])
    else (condVal, [])

// ✅ AFTER
let (i1CondVal, coerceCondOps) = coerceToI1 env condVal
```

## 체크리스트

- [ ] 반복 패턴 3회 이상 나타나는 것 식별
- [ ] ElabHelpers.fs에 헬퍼 추가 (`freshName` 이후 위치)
- [ ] elaborateExpr 필요 시 함수 인자로 주입
- [ ] Elaboration.fs에서 인라인 코드를 헬퍼 호출로 교체
- [ ] 빌드 + 전체 테스트 통과 확인

## Pitfalls

**F# 컴파일 순서:** `.fsproj`의 `<Compile Include>` 순서가 중요하다. ElabHelpers.fs는 Elaboration.fs보다 위에 있어야 한다.

**`freshName` 의존성:** 헬퍼 함수를 `freshName` 정의 이전에 배치하면 컴파일 에러. 반드시 이후에 배치.

## 관련 문서

- `add-tagged-value-representation.md` — tagging이 헬퍼 추출의 동기
