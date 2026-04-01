# Phase 44: Error Location Foundation - Research

**Researched:** 2026-03-31
**Domain:** F# compiler error reporting with source locations
**Confidence:** HIGH

## Summary

This phase adds source location (file:line:col) to all Elaboration error messages in FunLangCompiler. The codebase already has all the infrastructure needed -- the `Span` type is defined in LangThree's `Ast.fs`, every AST node carries a span, and helper functions `formatSpan`, `spanOf`, and `patternSpanOf` already exist. The work is purely mechanical: create a `failWithSpan` helper and convert each `failwithf` site to use it, changing wildcard `_` span matches to named bindings.

There are exactly 22 `failwithf`/`failwith` sites in Elaboration.fs. Of these, 4 are internal-only errors in `resolveAccessor`/`resolveAccessorTyped` variants ("Root not in cache") that indicate compiler bugs rather than user-facing errors -- these can be left as-is or converted at low priority. The remaining 18 are user-facing errors that should use `failWithSpan`.

**Primary recommendation:** Create a `failWithSpan` helper that formats `"file:line:col: message"` using the existing `Ast.Span` type, then systematically convert each `failwithf` site by capturing the span from the pattern match instead of discarding it with `_`.

## Standard Stack

### Core (Already Available)

| Component | Location | Purpose | Status |
|-----------|----------|---------|--------|
| `Ast.Span` | LangThree/Ast.fs:6 | Source location record (FileName, StartLine, StartColumn, EndLine, EndColumn) | Already defined |
| `Ast.formatSpan` | LangThree/Ast.fs:35 | Format span as `file:line:col-col` string | Already defined |
| `Ast.spanOf` | LangThree/Ast.fs:307 | Extract span from any `Expr` node | Already defined |
| `Ast.patternSpanOf` | LangThree/Ast.fs:336 | Extract span from any `Pattern` node | Already defined |
| `Ast.unknownSpan` | LangThree/Ast.fs:24 | Sentinel for synthetic/builtin definitions | Already defined |

### Nothing New Required

No new libraries or dependencies are needed. Everything is in LangThree's `Ast` module, already referenced by `FunLangCompiler.Compiler.fsproj`.

## Architecture Patterns

### Pattern: failWithSpan Helper

The helper should be placed at the top of `Elaboration.fs` (after the `open` declarations and type definitions, before `testPattern`). It should use a simple format matching the requirement "file:line:col: message":

```fsharp
/// Raise an error with source location in "file:line:col: message" format.
let private failWithSpan (span: Ast.Span) (fmt: Printf.StringFormat<'a, string>) : 'a =
    Printf.ksprintf (fun msg ->
        let loc = sprintf "%s:%d:%d" span.FileName span.StartLine span.StartColumn
        failwith (sprintf "%s: %s" loc msg)
    ) fmt
```

Key design decisions:
- Uses `Printf.ksprintf` so callers can use `%s`, `%A`, etc. format specifiers (matching existing `failwithf` usage)
- Output format is `file:line:col: message` (not the more complex `file:line:col-endcol` from `formatSpan`)
- The return type is `'a` (bottom type via exception) so it works in any expression position

### Pattern: Capturing Spans from Wildcards

Current code discards spans:
```fsharp
| Var (name, _) ->                    // span discarded
    ...
    | None -> failwithf "Elaboration: unbound variable '%s'" name
```

Target code captures spans:
```fsharp
| Var (name, span) ->                 // span captured
    ...
    | None -> failWithSpan span "unbound variable '%s'" name
```

For `App` errors, the span comes from the outer match:
```fsharp
| App (funcExpr, argExpr, span) ->    // was: | App (funcExpr, argExpr, _) ->
    ...
    failWithSpan span "unsupported App — '%s' is not a known function" name
```

For pattern errors in `testPattern`, the span comes from the pattern itself:
```fsharp
| _ ->
    failWithSpan (Ast.patternSpanOf pat) "pattern %A not supported in v2" pat
```

### Anti-Patterns to Avoid

- **Using `formatSpan` directly:** The requirement says "file:line:col" format, not the range format from `formatSpan` (which produces `file:line:col-col`). Use a simpler format.
- **Changing the exception type:** Keep using `failwith`/`System.Exception`. Do NOT introduce a custom exception type -- that would be a larger refactor outside scope.
- **Removing the "Elaboration:" prefix entirely:** The prefix helps distinguish elaboration errors from parse errors. Keep it or use a consistent shorter prefix.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Span extraction from Expr | Manual pattern match | `Ast.spanOf expr` | Already handles all 30+ Expr variants |
| Span extraction from Pattern | Manual pattern match | `Ast.patternSpanOf pat` | Already handles all 9 Pattern variants |
| Span formatting | Custom string builder | Simple `sprintf "%s:%d:%d"` | Requirement wants simple file:line:col, not range |

## Common Pitfalls

### Pitfall 1: Printf Format Type Compatibility
**What goes wrong:** `failWithSpan` must accept the same format strings as `failwithf`. If the type signature is wrong, every call site gets a type error.
**Why it happens:** F# format strings (`Printf.StringFormat<'a, 'b>`) have complex types.
**How to avoid:** Use `Printf.ksprintf` which takes a continuation `(string -> 'b)` and a format, matching the exact pattern of `failwithf`.
**Warning signs:** Compiler errors like "This expression was expected to have type 'string' but has type 'Printf.StringFormat<...>'".

### Pitfall 2: Span Variables Shadowing or Unused
**What goes wrong:** Renaming `_` to `span` in a match arm that already has an outer `span` variable causes shadowing.
**Why it happens:** Many nested matches in Elaboration.fs already use `span` as a variable name.
**How to avoid:** Use descriptive names like `span` only at the level where the error occurs. For nested cases, rely on `Ast.spanOf expr` or `Ast.patternSpanOf pat` instead of threading span through.
**Warning signs:** F# compiler warnings about unused variables or shadowing.

### Pitfall 3: resolveAccessor "Root not in cache" Sites
**What goes wrong:** Trying to add spans to the 4 resolveAccessor/resolveAccessorTyped internal errors.
**Why it happens:** These are compiler-internal invariant violations (should never happen in normal operation). The `MatchCompiler.Root` accessor doesn't carry a span.
**How to avoid:** Leave these 4 sites as plain `failwith` -- they are internal assertions, not user-facing errors.
**Warning signs:** N/A

### Pitfall 4: The `sprintf` and `ensureRecordFieldTypes` Errors
**What goes wrong:** Some error sites are inside lambdas passed to `Option.defaultWith`, where the span is from an outer scope.
**Why it happens:** The error is in a closure like `|> Option.defaultWith (fun () -> failwithf ...)`.
**How to avoid:** Capture the span from the enclosing expression match (e.g., `RecordExpr(_, _, span)`) and it will be available in the closure via closure capture.
**Warning signs:** None -- this just works in F# since closures capture outer scope.

## Code Examples

### failWithSpan Implementation

```fsharp
/// Raise an error with source location in "file:line:col: message" format.
/// Matches the signature of failwithf so all existing format strings work.
let private failWithSpan (span: Ast.Span) (fmt: Printf.StringFormat<'a, string>) : 'a =
    Printf.ksprintf (fun msg ->
        let loc = sprintf "%s:%d:%d" span.FileName span.StartLine span.StartColumn
        failwith (sprintf "%s: %s" loc msg)
    ) fmt
```

### Conversion Example: Unbound Variable (line 595-603)

Before:
```fsharp
| Var (name, _) ->
    match Map.tryFind name env.Vars with
    | Some v -> ...
    | None -> failwithf "Elaboration: unbound variable '%s'" name
```

After:
```fsharp
| Var (name, span) ->
    match Map.tryFind name env.Vars with
    | Some v -> ...
    | None -> failWithSpan span "Elaboration: unbound variable '%s'" name
```

### Conversion Example: Pattern Error (line 434-536)

Before:
```fsharp
| _ ->
    failwithf "testPattern: pattern %A not supported in v2" pat
```

After:
```fsharp
| _ ->
    failWithSpan (Ast.patternSpanOf pat) "testPattern: pattern %A not supported in v2" pat
```

### Conversion Example: Closure-scoped Error (line 2986)

Before:
```fsharp
| RecordExpr(_, fields, _) ->
    ...
    |> Option.defaultWith (fun () ->
        failwithf "RecordExpr: cannot resolve record type for fields %A" ...)
```

After:
```fsharp
| RecordExpr(_, fields, span) ->
    ...
    |> Option.defaultWith (fun () ->
        failWithSpan span "RecordExpr: cannot resolve record type for fields %A" ...)
```

## Complete Error Site Inventory

### User-Facing Errors (18 sites -- convert all to failWithSpan)

| Line | Current Error | Span Source |
|------|--------------|-------------|
| 474 | ConsPat head must be VarPat | `Ast.patternSpanOf hPat` |
| 475 | ConsPat tail must be VarPat | `Ast.patternSpanOf tPat` |
| 531 | TuplePat sub-pattern not supported | `Ast.patternSpanOf subPat` |
| 536 | pattern not supported in v2 | `Ast.patternSpanOf pat` |
| 603 | unbound variable | Capture from `Var(name, span)` |
| 625 | unbound mutable variable in Assign | Capture from `Assign(name, _, span)` |
| 739 | closure capture not found | Needs outer expr span or env threading |
| 963 | unsupported sub-pattern in TuplePat | `Ast.patternSpanOf pat` |
| 2048 | sprintf unsupported 2-arg specifier | Capture from enclosing `App(_, _, span)` |
| 2366 | unsupported App -- not a known function | Capture from `App(_, _, span)` |
| 2394 | unsupported App -- unsupported type | Capture from `App(_, _, span)` |
| 2691 | ensureRecordFieldTypes: cannot resolve | Capture from enclosing expr match |
| 2986 | RecordExpr: cannot resolve record type | Capture from `RecordExpr(_, _, span)` |
| 3026 | FieldAccess: unknown field | Capture from `FieldAccess(_, _, span)` |
| 3049 | RecordUpdate: cannot resolve record type | Capture from `RecordUpdate(_, _, span)` |
| 3086 | SetField: unknown field | Capture from `SetField(_, _, _, span)` |
| 3382 | ensureRecordFieldTypes2: cannot resolve | Capture from enclosing TryWith expr |
| 3811 | unsupported expression | `Ast.spanOf expr` |

### Internal Assertion Errors (4 sites -- leave as-is)

| Line | Error | Reason to Skip |
|------|-------|----------------|
| 2506 | resolveAccessor: Root not in cache | Compiler invariant, not user-facing |
| 2554 | resolveAccessorTyped: Root not in cache | Compiler invariant, not user-facing |
| 3232 | resolveAccessor2: Root not in cache | Compiler invariant, not user-facing |
| 3267 | resolveAccessorTyped2: Root not in cache | Compiler invariant, not user-facing |

## Testing Strategy

### Test Approach
The `.flt` test format supports testing error output via stderr. Create test files that trigger specific errors and verify the output includes file:line:col.

### Example Test: Unbound Variable
```
// --- Command: bash -c 'dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %S/44-01-error-location-unbound.fun 2>&1; echo $?'
// --- Input:
// --- Output:
tests/compiler/44-01-error-location-unbound.fun:1:1: Elaboration: unbound variable 'x'
1
```

Source file `44-01-error-location-unbound.fun`:
```
x
```

### Key Verification Points
1. Error message starts with `filename:line:col:`
2. Line numbers are 1-based (as provided by the parser)
3. Column numbers match the span from the parser
4. The error message text after the location is preserved

## Open Questions

1. **Error message prefix consistency:**
   - What we know: Current errors use mixed prefixes ("Elaboration:", "testPattern:", "sprintf:", "RecordExpr:", etc.)
   - What's unclear: Should these be unified to a single prefix, or kept as-is?
   - Recommendation: Keep existing prefixes for now -- the primary goal is adding locations, not reformatting messages.

2. **Line 739 closure capture error span:**
   - What we know: The closure capture error is inside a nested loop processing captures. The enclosing Lambda's span is available but requires threading through the capture-processing code.
   - What's unclear: Exact variable path to get span at that point.
   - Recommendation: Use `Ast.spanOf` on the Lambda expression, or capture the span from the Lambda match arm and thread it into the capture loop.

3. **Line 2048 sprintf error span:**
   - What we know: This is deep inside a large pattern match for `App(Var("sprintf", _), ...)` chains.
   - What's unclear: The exact outer match arm that provides the App span.
   - Recommendation: Trace up the match nesting to find the `App(_, _, span)` and capture it.

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` -- Span type definition, formatSpan, spanOf, patternSpanOf
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` -- All 22 failwithf/failwith sites examined
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Diagnostic.fs` -- Reference for how LangThree handles error formatting (more complex than needed here)

### Secondary (MEDIUM confidence)
- F# Printf module documentation -- `Printf.ksprintf` signature and usage

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- all components already exist in the codebase
- Architecture: HIGH -- straightforward helper + mechanical conversion
- Pitfalls: HIGH -- identified from direct code inspection
- Error site inventory: HIGH -- grep-verified, line numbers confirmed

**Research date:** 2026-03-31
**Valid until:** Stable -- no external dependencies, valid as long as Elaboration.fs structure unchanged
