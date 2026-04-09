# Phase 94: String Parameter Indexing Bug Fix - Research

**Researched:** 2026-04-09
**Domain:** FunLangCompiler codegen — string indexing dispatch, parameter type inference
**Confidence:** HIGH

## Summary

The bug is fully diagnosed with exact root cause identified via MLIR dump analysis, source tracing, and live reproduction. There are TWO independent defects that together cause `s.[i]` to produce wrong output when `s` is a function parameter:

**Root cause chain:**
1. FunLang's own type checker rejects `s.[i]` on string values (`Cannot index into value of type string; expected array or hashtable`). This causes `ExportApi.typeCheckFile` to throw, the `with _ -> Map.empty` catch in `Program.fs` to silence the error, and the `annotationMap` to be `Map.empty`.
2. With an empty `annotationMap`, two heuristics both fail: `isPtrParamBody` does not recognize `IndexGet(Var(param), ...)` as evidence that `param` needs `Ptr` type; and `isStringExpr` does not find `s` in `StringVars` (which is always `Set.empty` in function body envs).
3. As a result: (a) the string parameter is typed as `i64` in the generated MLIR function signature, and (b) `IndexGet` dispatches to `lang_index_get` (the array/hashtable generic) instead of `lang_string_char_at`.
4. `lang_index_get` interprets the string struct as an array: reads `((int64_t*)s)[0]` (heap_tag=1, >= 0), enters array branch, returns `arr[1]` = `length` (untagged). `printfn "%d"` then untags the raw length: `5 >> 1 = 2`.

**Primary recommendation:** Fix two heuristics in `ElabHelpers.fs` and one env-construction site in `Elaboration.fs`. No FunLang repo changes needed for the fix itself. A FunLang issue should be filed separately to add string indexing to the type checker.

## Standard Stack

No new libraries. All changes are within the existing compiler codebase:

| File | Role | Changes Needed |
|------|------|----------------|
| `src/FunLangCompiler.Compiler/ElabHelpers.fs` | Heuristic helpers for type dispatch | TWO changes |
| `src/FunLangCompiler.Compiler/Elaboration.fs` | Body env construction | ONE change per function env site |
| `tests/compiler/94-01-*.flt` + `.sh` | E2E test for the bug scenario | NEW files |

## Architecture Patterns

### How `s.[i]` on a string is supposed to work

```
IndexGet(collExpr, idxExpr, _) elaboration:
  1. Elaborate collExpr → collVal
  2. Elaborate idxExpr → idxVal
  3. Coerce collVal to Ptr if needed
  4. if isStringExpr ... collExpr → call @lang_string_char_at(collPtr, idxVal)
  5. else if idxVal.Type = Ptr → call @lang_index_get_str(...)
  6. else → call @lang_index_get(collPtr, idxVal)   ← BUG: this path is taken for string params
```

`lang_string_char_at(s, tagged_index)`:
- Untags index: `LANG_UNTAG_INT(index)` 
- Returns tagged char code: `LANG_TAG_INT((uint8_t)s->data[index])`

`lang_index_get(ptr, tagged_index)` (WRONG PATH):
- `first_word = ((int64_t*)ptr)[0]`
- For LangString: `first_word = heap_tag = 1` (>= 0) → enters array branch
- Returns `arr[LANG_UNTAG_INT(index) + 1]` = `arr[1]` = `s->length` (raw, untagged)
- Caller sees raw length (e.g., 5 for "hello"), then `printfn "%d"` untags it: `5 >> 1 = 2`

### LangString memory layout

```c
typedef struct LangString_s {
    int64_t heap_tag;  // = LANG_HEAP_TAG_STRING = 1  (offset 0)
    int64_t length;    // string length              (offset 8)
    char*   data;      // pointer to char data       (offset 16)
} LangString;
```

`heap_tag = 1` is positive, so `lang_index_get` misidentifies a string as an array.

### Why `isStringExpr` fails for string parameters

`isStringExpr` (ElabHelpers.fs line 118-127):
```fsharp
let rec isStringExpr stringVars stringFields annotationMap expr =
    match checkInferredType annotationMap ((=) Type.TString) expr with
    | Some result -> result   // ← would work IF annotationMap had the Var's span
    | None ->
    match expr with
    | Ast.String _ -> true
    | Ast.Var (name, _) -> Set.contains name stringVars  // ← stringVars is Set.empty!
    | Ast.Annot (inner, _, _) -> isStringExpr stringVars stringFields annotationMap inner
    | Ast.FieldAccess (_, fieldName, _) -> Set.contains fieldName stringFields
    | _ -> false
```

When `annotationMap = Map.empty` (because FunLang type checker rejected `s.[i]`), the heuristic path is taken. Function body environments always initialize `StringVars = Set.empty`. The parameter `s` is NEVER added to `StringVars`.

### Why the AnnotationMap is empty

`Program.fs` calls `ExportApi.typeCheckFile` which internally runs FunLang's type checker. FunLang's checker does NOT recognize `s.[i]` as valid for string values — it reports `error[E0471]: Cannot index into value of type string; expected array or hashtable`. This causes `typeCheckFile` to throw an exception. The `with _ -> Map.empty` catch in Program.fs silences the error and returns an empty map.

The fix for this root cause requires a FunLang repo issue (adding `IndexGet` on string as a valid type-level operation). However, the COMPILER fix is entirely self-contained.

### Why `isPtrParamBody` fails for string parameters

`isPtrParamBody "s" (Annot(IndexGet(Var("s"), 0), TInt, sp))` — the `hasParamPtrUse` inner function does not have a case for `IndexGet`. It falls through to `_ -> false`. So the heuristic incorrectly concludes that `s` does NOT need a Ptr parameter type.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| String char code from byte | Custom encoding | `lang_string_char_at` already exists | Handles tagging, bounds |
| Type detection | Ad-hoc AST walk | Extend `isStringExpr` | Consistent dispatch |
| Explicit type annotation check | Full type inference | Match on `Ast.LambdaAnnot` node | Already in AST |

## Common Pitfalls

### Pitfall 1: Fixing only the dispatch (Bug B) without the parameter type (Bug A)

**What goes wrong:** If only `isStringExpr` is fixed but `isPtrParamBody` is not, the parameter is still typed `i64` in the MLIR function signature. The coerce `llvm.inttoptr %arg0 : i64 to !llvm.ptr` loses the pointer provenance. Under some LLVM optimizations this can cause subtle miscompilation.

**How to avoid:** Fix BOTH: `isPtrParamBody` (heuristic for parameter type) AND the `StringVars` population in function body env construction.

### Pitfall 2: Over-broad `isPtrParamBody` fix

**What goes wrong:** Adding `IndexGet(Var(v), _, _) when v = paramName -> true` makes the parameter Ptr — which is correct for string. But `IndexGet` on a parameter is also used for arrays (array element access). Arrays are also Ptr-typed, so the same fix works. No over-broadness issue here.

### Pitfall 3: Fixing `StringVars` in one env-construction site but not all

**What goes wrong:** There are multiple places where function body envs are created:
- Line 340 (single-param `Let` → Lambda)
- Line 117 (two-param Lambda → closure)
- Line 792 (LetRec bindings)

The primary bug is at the single-param case (line 340). The two-param closure case (line 117) stores the outer param in the closure env as `i64` (uniform ABI), not directly accessible via `StringVars`. The LetRec case (line 792) has access to `_paramTypeAnnot : TypeExpr option` from the parser — use this.

**How to avoid:** Fix the single-param case (line 340) and the LetRec case (line 792) for `StringVars`. The closure case (line 117) uses a different ABI and is lower priority.

### Pitfall 4: Checking explicit string annotation only (missing inferred string params)

**What goes wrong:** If the fix only handles `LambdaAnnot("s", TEName "string", ...)`, it misses cases where the parameter type is inferred as string (no explicit annotation). Example: `let f s = s.[0]` where `s` is inferred to be a string.

**How to avoid:** ALSO check the AnnotationMap (when non-empty). Use the existing `isPtrParamTyped` result: if `paramType = Ptr` AND the AnnotationMap says `TArrow(TString, _)` at `lamSpan`, then `paramIsString = true`. Use this as the first check, falling back to explicit annotation detection.

### Pitfall 5: AnnotationMap span mismatch

**What goes wrong:** If the fix checks `Map.tryFind lamSpan annotationMap` for `TArrow(TString, _)`, it assumes the lambda's span in the elaboration AST matches the span annotated by FunLang's type checker. This is true ONLY if type checking succeeds (annotationMap non-empty) AND the two parsers produce matching spans. Currently, for string-param indexing, annotationMap IS empty. So this path is only useful for cases where type checking succeeds on the whole file.

**How to avoid:** Always use explicit annotation detection as the FALLBACK when annotationMap is empty.

## Code Examples

### The fixed `isStringExpr` (no change needed here — fix is in env construction)

The `isStringExpr` function itself is correct; the bug is that `stringVars` is empty for function params.

### Fix 1: `isPtrParamBody` in `ElabHelpers.fs`

Add `IndexGet` case in `hasParamPtrUse` inside `isPtrParamBody`:

```fsharp
// Source: src/FunLangCompiler.Compiler/ElabHelpers.fs, isPtrParamBody
| IndexGet(Var(v, _), _, _) when v = paramName -> true
// ↑ Add this line: string indexing on param proves param is Ptr (string is Ptr)
// Insert before the "recurse through let bindings" comment (~line 580)
```

### Fix 2: `StringVars` population in `Elaboration.fs` (single-param Lambda)

In the `Let (name, StripAnnot (Lambda (param, body, lamSpan)), ...)` case, determine if the parameter is a string and populate `StringVars`:

```fsharp
// Source: src/FunLangCompiler.Compiler/Elaboration.fs ~line 355
// After computing paramType, determine if param is a string:
let paramIsString =
    // First check: AnnotationMap (works when type check succeeds on the whole file)
    match Map.tryFind lamSpan env.AnnotationMap with
    | Some (Type.TArrow(Type.TString, _)) -> true
    | _ ->
    // Second check: explicit LambdaAnnot type annotation in the AST
    // (works even when type check fails due to string indexing error)
    match bindExprBeforeStripping with
    | Ast.LambdaAnnot(p, Ast.TEName "string", _, _) when p = param -> true
    | _ -> false
// Then:
StringVars = if paramIsString then Set.singleton param else Set.empty
```

**Critical implementation note:** To access `bindExprBeforeStripping`, the `Let` match must be restructured to not use the `StripAnnot` active pattern inline. Instead:

```fsharp
| Let (name, bindExprOrig, inExpr, _)
    when (match stripAnnot bindExprOrig with
          | Lambda (param, body, _) ->
              freeVars (Set.singleton param) body
              |> Set.filter (fun v -> Map.containsKey v env.Vars)
              |> Set.isEmpty
          | _ -> false) &&
         (match stripAnnot bindExprOrig with Lambda _ -> true | _ -> false) ->
    let (Lambda (param, body, lamSpan)) = stripAnnot bindExprOrig
    ...
    let paramIsString =
        match Map.tryFind lamSpan env.AnnotationMap with
        | Some (Type.TArrow(Type.TString, _)) -> true
        | _ ->
        match bindExprOrig with
        | Ast.LambdaAnnot(p, Ast.TEName "string", _, _) when p = param -> true
        | _ -> false
```

### Fix 3: `StringVars` in LetRec case

The LetRec case at line 792 has access to `_paramTypeAnnot : TypeExpr option` from the parser. Use it:

```fsharp
// Source: src/FunLangCompiler.Compiler/Elaboration.fs ~line 772
bindings |> List.map (fun (name, param, paramTypeAnnot, body, bindingSpan) ->
    ...
    let paramIsString =
        match Map.tryFind bindingSpan env.AnnotationMap with
        | Some (Type.TArrow(Type.TString, _)) -> true
        | _ ->
        match paramTypeAnnot with
        | Some (Ast.TEName "string") -> true
        | _ -> false
    ...
    (name, param, body, paramType, sig_, shortNameAlias, paramIsString))
// Then in bodyEnv: StringVars = if paramIsString then Set.singleton param else Set.empty
```

### E2E test structure

Test file `tests/compiler/94-01-string-param-indexing.flt`:

```
// Test: Issue #22 repro — string parameter indexing must return correct char code
// --- Command: bash %S/94-01-string-param-indexing.sh
// --- Input:
// --- Output:
104
104
104
```

Test script `tests/compiler/94-01-string-param-indexing.sh`:

```bash
#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue22_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let test1 (s : string) : int = s.[0]

let _ =
    let s = "hello"
    println (to_string (s.[0]))          // local: 104 ✓
    println (to_string (test1 "hello"))  // func: 104 ✓ (was 2)
    println (to_string (test1 s))        // func2: 104 ✓ (was 2)
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
```

## State of the Art

| Old Approach | Current Approach | Phase | Impact |
|--------------|------------------|-------|--------|
| No string indexing | `s.[i]` via `isStringExpr` dispatch | Phase 66 | Added string char-at |
| Uses AnnotationMap exclusively | Heuristic fallback | Phase 67 | Needed for type-check failures |
| No `IndexGet` in `isPtrParamBody` | Missing case | Phase 94 bug | Param typed as `i64` |
| `StringVars = Set.empty` always | Needs param population | Phase 94 bug | Wrong dispatch |

## Open Questions

1. **Inferred string params (no explicit annotation)**
   - What we know: If `let f s = s.[0]` where `s` is inferred as string, the explicit annotation check won't help
   - What's unclear: Whether FunLang type checker would accept this case (probably fails the same way — `s.[i]` on any string variable is rejected)
   - Recommendation: Fix the explicit-annotation case first (covers Issue #22). File a FunLang issue for the general fix.

2. **Multi-param functions with string params**
   - What we know: `let f a (s : string) b = s.[0]` uses two-param Lambda path (closure ABI) — different code path not covered by Fix 2
   - What's unclear: Whether this pattern appears in FunLexYacc's parsing functions
   - Recommendation: Verify if FunLexYacc uses single-param string functions primarily. Cover the two-param closure case if needed.

3. **LetRec string params**
   - What we know: Fix 3 handles `let rec f (s : string) = s.[i]`
   - Recommendation: Include Fix 3 in the same PR.

4. **FunLang issue for type checker**
   - What we know: FunLang's type checker should accept `s.[i]` for `s : string`
   - Recommendation: File a FunLang issue. When fixed, the AnnotationMap path will be the primary fix and the heuristic fallback becomes secondary.

## Sources

### Primary (HIGH confidence)
- Live MLIR dump of reproduction case — confirmed `@test1(%arg0: i64)` and `@lang_index_get` call
- `src/FunLangCompiler.Compiler/Elaboration.fs` lines 1131-1149 — IndexGet dispatch code
- `src/FunLangCompiler.Compiler/ElabHelpers.fs` lines 118-127 — `isStringExpr` implementation
- `src/FunLangCompiler.Compiler/ElabHelpers.fs` lines 527-612 — `isPtrParamBody` implementation
- `src/FunLangCompiler.Compiler/lang_runtime.c` lines 107-110, 775-787 — `lang_string_char_at` and `lang_index_get`
- Debug output confirming `annotationMap size: 0` and FunLang type error message

### Secondary (HIGH confidence)
- `deps/FunLang/src/FunLang/ExportApi.fs` — typeCheckFile throws on type error, confirms empty map mechanism
- `src/FunLangCompiler.Cli/Program.fs` lines 209-219 — `with _ -> Map.empty` catch
- `tests/compiler/66-03-string-char-indexing.sh` — existing passing test uses record field path (different code path)

### Tertiary (MEDIUM confidence)
- `deps/FunLang/src/FunLang/Bidir.fs` line 179-184 — Var annotations would work IF type check succeeded
- `deps/FunLang/src/FunLang/Parser.fs` lines 3978-3979 — how parser constructs `LambdaAnnot` + `Annot(body, retType)` for `let f (s:T):R = body`

## Metadata

**Confidence breakdown:**
- Root cause: HIGH — confirmed via live MLIR dump and debug output
- Fix approach (two heuristics): HIGH — complete trace of both failure modes
- Fix implementation details: MEDIUM — F# active-pattern restructuring needs care
- LetRec fix: HIGH — `paramTypeAnnot` is available in the binding tuple

**Research date:** 2026-04-09
**Valid until:** Stable (no fast-moving dependencies)
