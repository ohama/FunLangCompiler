# Phase 107: Strict Field Disambiguation (Option A)

## Goal
Record field 접근 시 last-wins fallback 제거. `x.start` 같은 접근에서 여러 record 타입이 `start` 필드를 공유하면 annotationMap에 `x`의 타입이 있어야 함. 없으면 명확한 에러.

## 현재 동작

`src/FunLangCompiler.Compiler/Elaboration.fs` FieldAccess arm:

```fsharp
| _ when candidates.Length > 1 ->
    // Try annotationMap for record expression type
    let inferredIdx =
        match Map.tryFind (Ast.spanOf recExpr) env.AnnotationMap with
        | Some (Type.TData(typeName, _)) ->
            candidates |> List.tryPick (fun (tn, idx) -> if tn = typeName then Some idx else None)
        | _ -> None
    match inferredIdx with
    | Some idx -> idx
    | None ->
        // Fallback: pick the LAST candidate (approximates "most recent declaration wins")
        snd (List.last candidates)   // ← 이 heuristic 제거
```

## 변경

`None` 분기를 error로 교체:

```fsharp
| None ->
    let candidateNames = candidates |> List.map fst |> String.concat ", "
    failWithSpan faSpan
        "Ambiguous field access: '%s' is defined in [%s]. \
         The record type at this access site must be known at compile time. \
         Add a type annotation to the record expression (e.g., `(x : Foo).%s`) \
         or to the enclosing let/function parameter."
        fieldName candidateNames fieldName
```

추가: annotation이 있지만 candidates에 없는 경우도 명확히 처리 (현재는 `List.tryPick` 실패 시 None으로 폴백).

## 작업

### T1: Elaboration.fs FieldAccess arm 수정
- `snd (List.last candidates)` 폴백 제거
- `None` 분기에 명확한 에러 메시지
- annotation이 non-candidate type인 경우 에러

### T2: E2E 테스트 추가
- `102-01-ambiguous-field-with-annot.flt` — annotation 있으면 정상 작동
- `102-02-ambiguous-field-without-annot.flt` — annotation 없으면 에러 메시지

### T3: 기존 271 테스트 회귀 없음 확인

## 검증

- 모든 기존 테스트 통과 (ambiguous field + annotation 없는 테스트가 있으면 재작성 필요할 수 있음)
- `--check` clean
- Multi-file record 예제 (Phase 103 검증용 `/tmp/mf/`)에서 정상 작동

## Risk

- 기존 테스트 중 "fallback 덕에 돌아가던" 케이스가 있으면 깨짐
- 현재 파악상 존재하지 않으나 구현 중 발견 가능성
- 깨지는 테스트는 명시적 annotation 추가로 해결
