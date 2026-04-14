# Phase 108: Heap-aware Equal/NotEqual Coercion (retroactive)

## Goal
`Equal`/`NotEqual` dispatch 가 Ptr ↔ I64 mismatch 시 I64 로 demote 하지 않고 Ptr 로 promote 하여 `lang_generic_eq` (structural equality) 사용.

## 배경 (Issue #28 해결과 함께)

이전 동작:
- 한쪽 operand = Ptr, 다른쪽 = I64 → PtrToInt 로 demote → raw pointer 값 비교 → 다른 LangString* 주소는 false
- 결과: `ident = "rule"` 이 false 반환 (두 string 모두 valid LangString 이지만 다른 heap 주소)

새 동작:
- 한쪽이 Ptr 이거나 `isStringExpr` true → 양쪽을 Ptr 로 promote → `lang_generic_eq` 호출 → structural 비교
- 결과: 같은 내용의 string 이면 true

## 구현

`src/FunLangCompiler.Compiler/Elaboration.fs` 의 `Equal` 및 `NotEqual` arm 재작성. `heapCompare` 조건:
```fsharp
let heapCompare = lv.Type = Ptr || rv.Type = Ptr || lhsIsStr || rhsIsStr
```

## 검증
- 단독 재현 + FunLexYacc `ident = "rule"` 동작 확인
- 전체 E2E 273/273 통과

## 릴리스
v0.1.11 (2026-04-14)
