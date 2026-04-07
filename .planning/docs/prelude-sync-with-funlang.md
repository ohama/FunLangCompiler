# Prelude Sync with FunLang v12.0

## Summary

FunLang v12.0 (issues #6, #7) removed `PipeRight`/`ComposeRight`/`ComposeLeft` AST nodes and
introduced `#[left N]`/`#[right N]` attribute + FixityEnv system. Operators `|>`, `>>`, `<<`, `<|`
are now defined as ordinary functions in `Prelude/Core.fun`.

## Sync Status

| File | Status | Remaining Diff |
|------|--------|----------------|
| **Core.fun** | synced | `char_to_int`/`int_to_char` 추가 (compiler-specific) |
| **List.fun** | **fully synced** | 없음 |
| **Hashtable.fun** | compiler-specific | `*_str` builtins (FunLang에 없음) |
| 기타 14개 | identical | 없음 |

## Changes Applied

### Core.fun — synced
- Added `|>`, `>>`, `<<`, `<|` with `#[left/right N]` attributes (matching FunLang v12.0)
- Kept `char_to_int`/`int_to_char` (compiler-specific; FunLang has them as builtins)

### List.fun — fully synced
- `init`: nested `let rec _init_helper` (FunLang version) — enabled by LambdaLift
- `partition`: nested `let rec go` (FunLang version) — enabled by LambdaLift
- `scan`: `acc :: (match xs with ...)` (FunLang version) — enabled by LetNormalize
- `unzip`: `map fst xs` (FunLang version) — enabled by auto eta-expansion

### Elaboration.fs — PipeRight/ComposeRight/ComposeLeft removed
- Removed 19 lines of desugar code
- These operators are now handled as ordinary Prelude function calls

### Elaboration.fs — auto eta-expansion of KnownFuncs
- `Var(name)` now auto-wraps KnownFuncs as closures when used as values
- `double` in `5 |> double` or `apply double 5` creates `Lambda(__eta_N, App(double, __eta_N))`
- This was the **root blocker** preventing Prelude-based `|>` from working

### LambdaLift.fs — new AST-to-AST transformation pass (MLIR-style)
- Nested LetRec bindings that capture outer scope variables are automatically
  lambda-lifted: captured variables become explicit parameters
- Correctly distinguishes local values (Lambda params, Let-value bindings) from
  functions (Let-Lambda, LetRec, open-aliases) which are KnownFuncs
- Applied after `extractMainExpr`, before `LetNormalize`

### LetNormalize.fs — new AST-to-AST transformation pass (partial ANF)
- Extracts control-flow sub-expressions (If, Match, And, Or, TryWith)
  from operand positions of compound expressions (Cons, Add, Tuple, etc.)
- `acc :: (match ...)` → `let __anf_N = match ... in acc :: __anf_N`
- Prevents MLIR "block successors must terminate parent block" errors
- Applied after `LambdaLift`, before `elaborateExpr`

### ElabHelpers.fs — freeVars cleanup
- Removed `PipeRight`/`ComposeRight`/`ComposeLeft` pattern (deleted AST nodes)

### ElabProgram.fs — InfixDecl support + pass integration
- `collectModuleMembers`: collects InfixDecl names for `open` alias generation
- `flattenDecls`: prefixes InfixDecl names with module name
- `extractMainExpr`: filters and builds InfixDecl as Let bindings
- Pipeline: extractMainExpr → LambdaLift → LetNormalize → elaborateExpr

### Program.fs — FixityEnv integration
- Added `FixityEnv.collectFixity` + `FixityEnv.rewriteFixity` pass after import expansion
- Required for `#[left N]`/`#[right N]` attributes to take effect on operator precedence

## Remaining Differences (compiler-specific)

### Hashtable.fun — string-key hashtable builtins

FunLangCompiler의 Hashtable.fun에는 FunLang에 없는 `*Str` 함수들이 있다:

```fsharp
// FunLangCompiler only
let createStr ()            = hashtable_create_str ()
let getStr ht key           = hashtable_get_str ht key
let setStr ht key value     = hashtable_set_str ht key value
let containsKeyStr ht key   = hashtable_containsKey_str ht key
let keysStr ht              = hashtable_keys_str ht
let removeStr ht key        = hashtable_remove_str ht key
let tryGetValueStr ht key   = hashtable_trygetvalue_str ht key
```

**근본 원인: 네이티브 컴파일러는 타입 소거(type erasure)로 key 타입을 구분할 수 없다.**

| | FunLang (인터프리터) | FunLangCompiler (네이티브) |
|---|---|---|
| int key | `hashtable_set ht 42 val` | `lang_hashtable_set(ptr, i64, i64)` |
| string key | `hashtable_set ht "abc" val` | `lang_hashtable_set(ptr, ???, i64)` — i64? ptr? |
| key 타입 판별 | 런타임에 `VInt` vs `VString` variant 매칭 | **불가능** — 모든 값이 i64 또는 ptr |

FunLang 인터프리터는 모든 값이 `Value` variant (VInt, VString, VList, ...)이므로,
하나의 `Dictionary<Value, Value>`로 int key든 string key든 처리할 수 있다.

FunLangCompiler는 값이 i64 또는 ptr로 컴파일된다. C 런타임의 hashtable은:
- `lang_hashtable_set(ptr, i64, i64)` — int key: key를 i64로 해싱
- `lang_hashtable_set_str(ptr, ptr, i64)` — string key: key를 ptr(string)로 해싱

**두 가지 다른 C 함수가 필요**하다. 같은 테이블에 int key와 string key를 섞을 수 없다.

**Elaboration.fs의 자동 dispatch:**

`hashtable_set`(generic 버전)은 key의 **컴파일 타임 MlirType**으로 dispatch한다:
```fsharp
// Elaboration.fs:1210
match keyVal.Type with
| Ptr ->  // key가 Ptr (string 등) → _str variant 호출
    emitVoidCall env "@lang_hashtable_set_str" [htPtr; keyVal; valI64]
| _ ->    // key가 I64 (int, char 등) → 기본 variant 호출
    emitVoidCall env "@lang_hashtable_set" [htPtr; keyI64; valI64]
```

따라서 유저 코드에서는 `hashtable_set ht "key" val`로 쓰면 자동으로 `_str` 버전이
선택된다. 하지만 `hashtable_keys`처럼 **key 인자가 없는** 함수는 dispatch할 수 없다:
```fsharp
// hashtable_keys ht — key 인자가 없어서 int/str 구분 불가능
// → 명시적으로 hashtable_keys_str ht 를 호출해야 함
```

이것이 Prelude에 `keysStr`, `createStr` 같은 명시적 variant가 필요한 이유다.

**FunLang에서 불필요한 이유:**
인터프리터의 `HashtableValue`는 `Dictionary<Value, Value>`이다. `Value` 타입이 해싱과
동등성 비교를 자체적으로 처리하므로, key 타입에 관계없이 하나의 구현으로 충분하다.

### Core.fun — char_to_int / int_to_char identity functions

```fsharp
let char_to_int c = c
let int_to_char n = n
```

이 두 함수가 identity function(`c = c`, `n = n`)으로 정의되어도 동작하는 이유:

**FunLangCompiler는 char와 int를 동일한 MLIR 타입 `i64`로 표현한다.**

| FunLang (인터프리터) | FunLangCompiler (네이티브) |
|---------------------|--------------------------|
| `char` = 별도 런타임 타입 | `Char('a')` → `ArithConstantOp(v, 97L)` = **i64** |
| `int` = 별도 런타임 타입 | `Number(42)` → `ArithConstantOp(v, 42L)` = **i64** |
| `char_to_int` = 타입 변환 (builtin) | 변환 불필요 — 둘 다 i64 |

Elaboration.fs:14-16에서 `Char` 리터럴의 컴파일:
```fsharp
| Char (c, _) ->
    let v = { Name = freshName env; Type = I64 }
    (v, [ArithConstantOp(v, int64 (int c))])  // 'a' → 97 (i64)
```

`'a'`는 ASCII 코드 97의 i64 정수로 컴파일된다. `42` (int)도 i64이다.
따라서 `char_to_int c = c`는 i64 값을 그대로 반환 — 실제 변환이 없다.

**FunLang 인터프리터에서는 왜 builtin인가:**
인터프리터는 `VChar 'a'`와 `VInt 97`을 다른 variant로 구분한다.
`char_to_int`가 `VChar 'a' → VInt 97` 으로 variant 태그를 바꿔야 하므로 builtin이 필요하다.

**ElabHelpers.fs도 char = int로 취급:**
```fsharp
// typeNeedsPtr: Ptr가 필요한 타입 판별
| Type.TInt | Type.TBool | Type.TChar | Type.TError -> false  // 모두 I64
```

`TChar`는 `TInt`, `TBool`과 같은 그룹 — Ptr가 아닌 I64 타입이다.
