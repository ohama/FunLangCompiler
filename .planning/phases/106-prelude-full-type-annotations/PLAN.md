# Phase 106: Prelude Full Type Annotations

## Goal
Phase 102에서 누락된 11개 Prelude 파일에 명시적 타입 어노테이션 추가.

## 실행

FunLang 캐노니컬 syntax 채택 (lowercase prefix/postfix):
- `'a hashset`, `'a queue`, `'a mutablelist`, `'a array` (postfix)
- `hashtable<'k, 'v>`, `result<'a, 'b>` (lowercase angle brackets)
- `'a option` (postfix)
- `stringbuilder` (no generic)
- `()` 단위 인자는 어노테이션 없이 사용

각 파일을 FunLang의 `deps/FunLang/Prelude/` 와 동일한 시그니처로 작성, FunLangCompiler 전용 함수만 추가 보존.

## 작업한 파일

- Core.fun: id, const, compose, flip, apply, ^^, |>, >>, <<, <|, not, min, max, abs, fst, snd, ignore
- Int.fun: parse, toString
- Array.fun: 12개 함수 (ofSeq만 untyped)
- HashSet.fun: 6개 함수 (toList compiler-only)
- Hashtable.fun: 8개 함수 (tryGetValue는 컴파일러 transform 차이로 untyped 유지)
- MutableList.fun: 6개 함수 (toList compiler-only)
- Queue.fun: 4개 함수
- StringBuilder.fun: 3개 함수
- List.fun: 50+ 함수 모두 어노테이션 (ofSeq만 untyped)
- Option.fun: 12개 함수
- Result.fun: 10개 함수

## 시행착오

1. 처음 `HashSet<'a>` 형식 사용 → FunLang 타입 체커 reject (`'a hashset` vs `HashSet<'a>` 불일치)
2. FunLang의 `deps/FunLang/Prelude/` 참조하여 lowercase 형식으로 통일
3. `let create () : T = ...` 형식이 일부 지원되지 않아 `let create () = ...` 로 변경
4. tryGetValue는 FunLang(`(bool * 'v)`) vs FunLangCompiler(`'v option`) 차이로 untyped 유지

## 검증

- 컴파일러 빌드 성공
- `--check`로 Prelude 타입 체크 clean
- E2E 271/271 통과
