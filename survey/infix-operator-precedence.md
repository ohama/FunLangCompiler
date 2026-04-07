# Infix Operator Precedence — 언어별 조사

## 1. Haskell — 명시적 fixity 선언

```haskell
infixl 6 +      -- left-assoc, precedence 6
infixr 5 :      -- right-assoc, precedence 5
infix  4 ==     -- non-assoc, precedence 4
infixl 1 >>=    -- very low, left-assoc (monadic bind)
infixr 0 $      -- lowest, right-assoc (application)
```

- 0(최저) ~ 9(최고) 레벨
- 선언 없으면 기본 `infixl 9`
- **장점**: 완전한 자유도
- **단점**: 선언 필요, 모듈 간 충돌 가능

## 2. OCaml / F# — 첫 문자로 결정

```
|, &    → 비교 레벨 (낮음)
@, ^    → 연결 레벨 (중간, right-assoc)
+, -    → 덧셈 레벨
*, /, % → 곱셈 레벨
**      → 거듭제곱 (높음, right-assoc)
```

- 사용자가 우선순위를 변경할 수 없음
- **장점**: 선언 불필요, 직관적
- **단점**: `|>`가 `|`로 시작 → 비교 레벨에 배치 (F#은 특수 처리)

## 3. Swift — precedencegroup

```swift
precedencegroup PipePrecedence {
    associativity: left
    lowerThan: ComparisonPrecedence
}
infix operator |>: PipePrecedence
func |> <A, B>(x: A, f: (A) -> B) -> B { return f(x) }
```

- 그룹 정의 + 연산자 할당
- `higherThan` / `lowerThan`으로 상대적 순서
- **장점**: 타입 안전, 명확한 관계
- **단점**: 장황

## 4. Scala — 첫 문자 + 특수 규칙

```
|          → 최저
^          → 
&          →
= !        →
< >        →
:          → right-assoc if ends with :
+ -        →
* / %      →
other      → 최고
```

- `:` 로 끝나면 right-assoc
- **장점**: 선언 불필요
- **단점**: 유연성 제한

## 5. Agda — 완전 자유

```agda
infixl 6 _+_
infixr 5 _∷_
```

- mixfix 문법 지원 (`if_then_else_`)
- 가장 유연하지만 가장 복잡

---

## FunLang 현재 구조

OCaml/F# 스타일. Lexer가 첫 문자로 INFIXOP 레벨 결정:

| 첫 문자 | 토큰 | 결합 | 레벨 |
|---------|------|------|------|
| `= < > \| & $ !` | INFIXOP0 | left | 비교 |
| `@ ^` | INFIXOP1 | right | 연결 |
| `+ -` | INFIXOP2 | left | 덧셈 |
| `* / %` | INFIXOP3 | left | 곱셈 |
| `**` | INFIXOP4 | right | 거듭제곱 |

## 문제: `|>`, `>>`, `<<`를 INFIXOP으로 이동하면

| 연산자 | 첫 문자 | 현재 INFIXOP | 필요한 우선순위 |
|--------|--------|-------------|--------------|
| `\|>` | `\|` | INFIXOP0 (비교) | **최저** (모든 것보다 낮아야) |
| `>>` | `>` | INFIXOP0 (비교) | 비교보다 낮아야 |
| `<<` | `<` | INFIXOP0 (비교) | `>>`와 같은 레벨, right-assoc |

INFIXOP0은 비교 레벨 → `x + 1 |> f`가 `(x + 1) |> f`가 아닌 `x + (1 |> f)`로 파싱될 위험.

## 제안: FunLang에 도입할 수 있는 방법

### Option A: 특수 INFIXOP 레벨 추가 (최소 변경)

```
%left INFIXOP_PIPE       // |> — 최저
%left INFIXOP_COMPOSE_L  // >>
%right INFIXOP_COMPOSE_R // <<
%left INFIXOP0           // = < > | & $ !
%right INFIXOP1          // @ ^
...
```

Lexer에서 `|>`, `>>`, `<<`를 특수 토큰으로 분류. 나머지는 현재 INFIXOP 체계 유지.

**장점**: 최소 변경, 기존 코드 영향 없음
**단점**: 새 연산자마다 Lexer/Parser 수정 필요

### Option B: Haskell 스타일 fixity 선언 도입

```fun
infixl 1 |>
infixl 2 >>
infixr 2 <<
infixl 6 +
infixl 7 *
```

**장점**: 완전한 자유도, 사용자 정의 가능
**단점**: 구현 복잡 (fixity 선언 파싱, 우선순위 테이블 동적 생성, operator shunting yard)

### Option C: F# 호환 (현실적 추천)

F#의 실제 우선순위 체계를 그대로 따름:

| F# 우선순위 | 연산자 | FunLang 대응 |
|------------|--------|-------------|
| lowest | `\|>` | 새 레벨 추가 |
| low | `>>`, `<<` | 새 레벨 추가 |
| comparison | `= < > \| &` | INFIXOP0 유지 |
| concat | `@ ^` | INFIXOP1 유지 |
| additive | `+ -` | INFIXOP2 유지 |
| multiplicative | `* / %` | INFIXOP3 유지 |
| power | `**` | INFIXOP4 유지 |

Lexer에서 `|>`, `>>`, `<<` 만 특수 처리하고, 나머지는 첫 문자 규칙 유지.

---

## 추천: Option A (특수 레벨) + 장기 Option B (fixity 선언)

1. **즉시**: `|>`, `>>`, `<<`를 위한 INFIXOP_PIPE/INFIXOP_COMPOSE 레벨 추가
2. **장기**: Haskell 스타일 `infixl N op` 선언 도입 검토

*2026-04-03*
