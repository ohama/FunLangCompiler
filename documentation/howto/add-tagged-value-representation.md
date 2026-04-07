---
created: 2026-04-07
description: OCaml 방식 LSB 1-bit tagging을 MLIR 코드 생성 컴파일러에 도입하는 전체 과정
---

# Tagged Value Representation 도입하기

모든 값을 i64 하나로 표현하되 LSB로 int/pointer를 구분하는 OCaml 방식 tagging을 컴파일러에 도입한다.

## The Insight

컴파일러가 `func.func` (standalone function)로 코드를 생성하면 모든 파라미터가 i64로 전달된다. 이때 원래 I64였는지 Ptr였는지 구분할 수 없다. Tagged representation은 이 문제를 **런타임에서** 해결한다: 정수는 항상 홀수(2n+1), 포인터는 항상 짝수(힙 정렬).

## Why This Matters

타입 정보 없이 int/string을 구분해야 하는 모든 곳에서 컴파일 타임 dispatch가 실패한다. 대표적인 증상: Prelude wrapper 함수를 경유하면 hashtable의 string key가 int로 처리되어 crash.

## Recognition Pattern

- "Prelude wrapper를 거치면 타입 정보가 사라진다"
- "C 런타임 함수에 별도의 `_str` 변종이 필요하다"
- "polymorphic 함수가 monomorphic dispatch에 의존한다"

## The Approach

4단계로 나눈다. 각 단계는 이전 단계에 의존하므로 순서가 중요하다.

### Step 1: IR 인프라 — shift/or ops + 헬퍼 함수

MlirIR에 3개 op 추가, ElabHelpers에 3개 헬퍼 추가:

```fsharp
// MlirIR.fs
| ArithShRSIOp of result: MlirValue * lhs: MlirValue * rhs: MlirValue  // untag: >> 1
| ArithShLIOp  of result: MlirValue * lhs: MlirValue * rhs: MlirValue  // retag: << 1
| ArithOrIOp   of result: MlirValue * lhs: MlirValue * rhs: MlirValue  // retag: | 1

// ElabHelpers.fs
let tagConst (n: int64) : int64 = n * 2L + 1L
let emitUntag (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list  // (val >> 1)
let emitRetag (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list  // (val << 1) | 1
```

### Step 2: 리터럴 + 산술 변환

**리터럴:** `Number(n)` → `ArithConstantOp(v, tagConst(int64 n))`

**산술 보정:**

| 연산 | Tagged 버전 | 추가 비용 |
|------|------------|----------|
| `a + b` | `add a, b` → `sub result, 1` | 1 op |
| `a - b` | `sub a, b` → `add result, 1` | 1 op |
| `a * b` | `untag(a) * untag(b)` → `retag` | 4 ops |
| `a / b` | `untag(a) / untag(b)` → `retag` | 4 ops |
| `a < b` | **그대로** (2a+1 < 2b+1 ⟺ a < b) | 0 ops |
| `-a` | `2 - a` (0이 아님!) | 0 ops |

**단위값(unit):** `0L` → `1L` (tagged 0 = 2*0+1 = 1)

**Truthiness check:** `condVal ne 0L` → `condVal ne 1L` (tagged false = 1)

### Step 3: C 런타임 boundary

C 함수는 raw 정수를 기대하므로 호출 전에 untag, 결과를 retag:

```fsharp
// C 호출 전: tagged → raw
let (rawArg, untagOps) = emitUntag env taggedArg
// C 호출 후: raw → tagged
let (taggedResult, retagOps) = emitRetag env rawResult
```

**주의:** 모든 `ArithConstantOp(_, 0L)`을 바꾸면 안 된다. GC_malloc 크기, ADT tag, GEP offset 등 내부 상수는 raw로 유지.

### Step 4: @main return untag

@main의 반환값은 OS exit code이므로 untag 필요:

```fsharp
let (exitVal, untagOps) = emitUntag env resultVal
```

## 체크리스트

- [ ] ArithShRSIOp/ShLIOp/OrIOp를 MlirIR.fs와 Printer.fs에 추가
- [ ] tagConst/emitUntag/emitRetag를 ElabHelpers.fs에 추가
- [ ] Number/Char 리터럴에 tagConst 적용
- [ ] unit 상수 0L → 1L (tagged zero)
- [ ] 산술 연산에 보정 추가 (+/-1, untag-op-retag)
- [ ] Truthiness check 0L → 1L
- [ ] coerceToI64 I1 branch에 retag 추가
- [ ] And/Or가 I64 tagged를 직접 반환하도록 수정
- [ ] func.func I1 returns를 I64 tagged로 coerce
- [ ] @main return untag
- [ ] C 호출 boundary에 untag/retag 추가
- [ ] 전체 테스트 통과 확인

## Pitfalls

**And/Or terminator 문제:** And/Or는 CfCondBrOp(terminator)로 끝나는 ops를 반환한다. coerceToI64의 retag ops가 terminator 뒤에 오면 MLIR 에러. 해결: And/Or가 I64 tagged를 merge block arg로 직접 반환하도록 수정.

**func.func I1 return:** KnownFuncs func.func 경로에서 body가 I1을 반환하면 함수 signature가 `-> i1`이 된다. 호출 site의 result type과 불일치. 해결: body가 I1이면 coerceToI64 + signature를 I64로 변경.

**KnownFuncs App I1→I64 arg coercion:** DirectCallOp의 인자 coercion에서 I1→I64에 plain ArithExtuIOp를 사용하면 raw 0/1이 전달된다. 해결: coerceToI64 사용 (retag 포함).

## 관련 문서

- `add-lsb-dispatch-to-c-runtime.md` — C 런타임 측 변경
- `extract-elaboration-helpers.md` — 반복 패턴 추출
