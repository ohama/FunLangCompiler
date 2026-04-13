# Phase 102 Plan: Prelude Type Annotations

## Goal
Prelude wrapper/identity 함수에 명시적 타입 어노테이션을 부여하여 FunLang 타입 체커가 일관된 타입으로 해석하도록 함.

## Tasks

### T1: Core.fun — char_to_int / int_to_char 어노테이션
- `let char_to_int c = c` → `let char_to_int (c : char) : int = char_to_int c` — 아니, 이건 재귀. identity 유지하면서 타입만 붙이려면:
  - `let char_to_int (c : char) : int = c` — 컴파일러가 char과 int을 런타임에서 동일하게 취급하므로 동작해야 함
  - 대안: FunLang builtin이 이미 TArrow(TChar, TInt)로 등록됨 → Prelude 정의를 제거하고 컴파일러에서 직접 builtin 처리

### T2: Char.fun — 모든 wrapper에 (c : char) 파라미터 어노테이션
- isDigit/isLetter/isUpper/isLower → `(c : char) : bool`
- toUpper/toLower → `(c : char) : char`
- toInt → `(c : char) : int`
- ofInt → `(n : int) : char`

### T3: String.fun — 모든 wrapper 명시 어노테이션
- length: `(s : string) : int`
- concat/join: `(sep : string) (lst : string list) : string`
- endsWith/startsWith/contains: `(s : string) (x : string) : bool`
- trim: `(s : string) : string`
- split: `(s : string) (sep : string) : string list`
- indexOf: `(s : string) (sub : string) : int`
- replace: `(s : string) (old : string) (rep : string) : string`
- toUpper/toLower: `(s : string) : string`
- substring: `(s : string) (start : int) (len : int) : string`

### T4: Int.fun — 검토 및 필요 시 어노테이션 추가

### T5: 회귀 테스트
- `dotnet build src/FunLangCompiler.Cli` 성공
- `dotnet run --project deps/fslit/FsLit/FsLit.fsproj -- tests/compiler/` 264/264 통과

## Verification
전체 E2E 테스트 통과 + FunLang typeCheckFile이 string indexing + char 비교 사용 single-file 예제에서 성공.

## Risk
- Char 런타임 ↔ int 런타임이 동일(I64)이지만 FunLang 타입 체커가 `let char_to_int (c : char) : int = c`를 수락하지 않을 수 있음 (char ≠ int unification 실패). 그 경우 wrapper 정의 제거하고 builtin만 사용.
