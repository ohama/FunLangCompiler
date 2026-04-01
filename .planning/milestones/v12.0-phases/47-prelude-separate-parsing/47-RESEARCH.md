# Phase 47: Prelude Separate Parsing - Research

**Researched:** 2026-04-01
**Domain:** F# compiler CLI — Prelude loading, parsing pipeline, AST merge, span/position tracking
**Confidence:** HIGH

## Summary

Phase 47 fixes a fundamental line-number accuracy problem in error messages. Currently the CLI (Program.fs) concatenates all Prelude source text with a `\n` separator before parsing. Because the Prelude is 172 lines total (161 lines across 12 files + 11 `\n` separators), user code starting at physical line 1 is shifted to global line 174. A user error on their first line appears as `file:174:col` instead of `file:1:col`.

The fix is to parse the Prelude and user code in separate `parseProgram` calls, each with their own `setInitialPos` call setting the correct filename. The two resulting `Ast.Module` values are merged by concatenating their declaration lists before passing to `elaborateProgram`. The Prelude parse uses `"<prelude>"` as the filename so Prelude-internal errors are distinguishable from user code errors.

All span/position data flows through `Ast.Span.FileName` and `Ast.Span.StartLine` — both are set at lex time via `Lexer.setInitialPos`. Since the two parses use separate `LexBuffer` instances with separate initial positions, spans from each parse will have correct independent line numbers.

**Primary recommendation:** In `Program.fs`, replace `combinedSrc` string concatenation with two separate `parseProgram` calls, then merge the resulting `Decl list` values into a single `Ast.Module` before calling `elaborateProgram`.

## Standard Stack

All implementation is in-project F# code. No new libraries are needed.

### Core (already present)
| Component | Location | Purpose |
|-----------|----------|---------|
| `parseProgram` | `Program.fs:29` | Parses a source string as `Ast.Module` with `PositionedToken` IndentFilter |
| `Lexer.setInitialPos` | `LangThree/Lexer.fs:28` | Sets `pos_fname` and `pos_lnum = 1` on lexbuf before parsing |
| `Ast.Module` | `LangThree/Ast.fs:374` | `Module of decls list * Span` — top-level container |
| `Ast.Decl` | `LangThree/Ast.fs:348` | All declaration variants (LetDecl, TypeDecl, ModuleDecl, etc.) |
| `elaborateProgram` | `Elaboration.fs:4199` | Accepts `Ast.Module`, runs prePass + elaboration |
| `failWithSpan` | `Elaboration.fs:63` | Formats `[Elaboration] file:line:col: message` using `Span.FileName` |

### Supporting
| Component | Location | Purpose |
|-----------|----------|---------|
| `Ast.unknownSpan` | `LangThree/Ast.fs:26` | Sentinel span with `FileName = "<unknown>"`, used for synthetic nodes |
| `expandImports` | `Program.fs:65` | Recursively expands `FileImportDecl` — unaffected by this change |
| `prePassDecls` | `Elaboration.fs:4058` | Collects TypeEnv/RecordEnv/ExnTags — works on merged decl list |
| `extractMainExpr` | `Elaboration.fs:4156` | Folds decl list into let-chain `Expr` — works on merged decl list |

**Installation:** No new packages needed.

## Architecture Patterns

### Current Flow (broken)

```
Prelude files (12) → string_concat "\n" → combinedSrc → parseProgram(combinedSrc, inputPath)
                                                         → Ast.Module(allDecls, span)
                                                         → elaborateProgram
```

The single `parseProgram` call uses `inputPath` as filename and starts at line 1. After 172 lines of Prelude, user code begins at line 174. All spans in user code carry `FileName = inputPath, StartLine >= 174`.

### Target Flow (fixed)

```
Prelude files (12) → string_concat "\n" → parseProgram(preludeSrc, "<prelude>")
                                          → Ast.Module(preludeDecls, _)

User file → parseProgram(src, inputPath)
           → Ast.Module(userDecls, userSpan)

merged = Ast.Module(preludeDecls @ userDecls, userSpan)
→ elaborateProgram(merged)
```

Each parse call starts at line 1 with its own `LexBuffer`. Prelude spans carry `FileName = "<prelude>"` and user spans carry `FileName = inputPath` starting from line 1.

### Pattern 1: Separate Parse + Decl List Merge

**What:** Call `parseProgram` twice, extract `Decl list` from each result, concatenate.
**When to use:** Whenever multiple logical source units must be combined for elaboration but need independent span tracking.

```fsharp
// Parse Prelude separately under synthetic filename
let preludeAst = parseProgram preludeSrc "<prelude>"
let preludeDecls =
    match preludeAst with
    | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) | Ast.NamespacedModule(_, ds, _) -> ds
    | Ast.EmptyModule _ -> []

// Parse user code under actual filename — positions start at 1
let userAst = parseProgram src inputPath
let (userDecls, userSpan) =
    match userAst with
    | Ast.Module(ds, s) -> (ds, s)
    | Ast.NamedModule(_, ds, s) | Ast.NamespacedModule(_, ds, s) -> (ds, s)
    | Ast.EmptyModule s -> ([], s)

// Merge: Prelude declarations come first (they define types/functions user code uses)
let mergedAst = Ast.Module(preludeDecls @ userDecls, userSpan)
```

### Pattern 2: Empty Prelude Guard

**What:** Skip the separate parse when no Prelude is found, identical to current behavior.

```fsharp
let ast =
    if preludeSrc = "" then
        parseProgram src inputPath
    else
        let preludeAst = parseProgram preludeSrc "<prelude>"
        let preludeDecls =
            match preludeAst with
            | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) | Ast.NamespacedModule(_, ds, _) -> ds
            | Ast.EmptyModule _ -> []
        let userAst = parseProgram src inputPath
        let (userDecls, userSpan) =
            match userAst with
            | Ast.Module(ds, s) | Ast.NamedModule(_, ds, s) | Ast.NamespacedModule(_, ds, s) -> (ds, s)
            | Ast.EmptyModule s -> ([], s)
        Ast.Module(preludeDecls @ userDecls, userSpan)
```

### Anti-Patterns to Avoid

- **Changing the Prelude filename to actual file paths:** Using individual Prelude file paths (e.g., `/path/Prelude/List.fun`) would make errors show those paths. Use `"<prelude>"` as a single synthetic path for the entire concatenated Prelude.
- **Merging by re-concatenating source strings:** Any string concat approach re-introduces the line offset problem. Merge at the AST (Decl list) level, never at source text level.
- **Using `Ast.unknownSpan` for the merged module span:** Use the user AST's span so the module span points to user code, not unknown.
- **Modifying `elaborateProgram` or `prePassDecls`:** These functions already accept a `Decl list` and are order-independent for type/exception registration. No changes needed downstream.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Position-tracking parse | Custom lexer position reset | Existing `Lexer.setInitialPos` + separate `LexBuffer` | Already sets `pos_lnum=1`, `pos_fname` per call |
| Span filename injection | Post-parse span rewriting | Correct filename at lex time | Spans are set during tokenization; rewriting is fragile |
| Prelude line offset adjustment | Subtract 173 from `StartLine` | Separate parse | Offset changes when Prelude grows; adjustment would break immediately |

**Key insight:** FsLexYacc bakes position into each `LexBuffer` independently. Two lexbufs = two independent position counters. No arithmetic needed.

## Common Pitfalls

### Pitfall 1: IndentFilter state not reset between parses
**What goes wrong:** `Program.fs:lexAndFilter` captures tokens into an array. Each call creates its own `LexBuffer` and calls `Lexer.setInitialPos` independently. There is no shared state. Two separate calls to `parseProgram` are completely independent.
**How to avoid:** Already safe — `parseProgram` is a pure function with local state only.

### Pitfall 2: NamedModule / NamespacedModule variants
**What goes wrong:** If `parseProgram` returns `Ast.NamedModule` or `Ast.NamespacedModule`, extracting only `Module` would miss those declarations.
**How to avoid:** Handle all four `Ast.Module` variants when extracting `Decl list`. Pattern: `match m with Module(ds,_) | NamedModule(_,ds,_) | NamespacedModule(_,ds,_) -> ds | EmptyModule _ -> []`. This pattern is already used in 4 places in Program.fs.

### Pitfall 3: expandImports runs AFTER the merge
**What goes wrong:** `expandImports` is called on `expandedAst` after parsing. It uses the AST's declaration structure to resolve `FileImportDecl` nodes. Since Prelude declarations come first in the merged list, import resolution still works correctly — Prelude files do not use `FileImportDecl`.
**How to avoid:** Keep `expandImports` call on the merged AST, unchanged.

### Pitfall 4: 7 existing error tests hardcode 3-digit line numbers
**What goes wrong:** After the fix, user code errors will show `file:1:col` instead of `file:174:col`. The following `.flt` files will fail:
- `44-01-error-location-unbound.flt` — expects line 175, user code is at line 2 → new: line 2
- `44-02-error-location-pattern.flt` — expects line 174, user code is at line 1 → new: line 1
- `44-03-error-location-field.flt` — expects line 176, user code is at line 3 → new: line 3
- `46-01-record-type-hint.flt` — expects line 175, user code is at line 2 → new: line 2
- `46-02-field-hint.flt` — expects line 176, user code is at line 3 → new: line 3
- `46-03-function-hint.flt` — expects line 175, user code is at line 2 → new: line 2
- `46-04-error-category-elab.flt` — expects line 174, user code is at line 1 → new: line 1
**How to avoid:** Update all 7 `.flt` files as part of the phase.

### Pitfall 5: 44-02 test contains inline span struct in expected output
**What goes wrong:** `44-02-error-location-pattern.flt` contains a multi-line expected output with the span struct printed inline:
```
StartLine = 174
StartColumn = 4
```
After the fix, these become `StartLine = 1` / `StartColumn = 4`.
**How to avoid:** Update both the line reference and the embedded span struct in the expected output.

### Pitfall 6: `<prelude>` filename in Prelude-internal errors
**What goes wrong:** If the Prelude itself has a parse error, the error will show `<prelude>:line:col`. This is intentional (requirement LINE-02) but the angle brackets may confuse shell output or test matchers.
**How to avoid:** `<prelude>` is safe in error strings. No special escaping needed in F# string formatting.

## Code Examples

### Decl extraction helper (reuse existing pattern)

```fsharp
// Source: Program.fs:80-83 (expandImports already uses this exact pattern)
let getDecls (m: Ast.Module) : Ast.Decl list =
    match m with
    | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) | Ast.NamespacedModule(_, ds, _) -> ds
    | Ast.EmptyModule _ -> []
```

### Span extraction from Module

```fsharp
// Source: Ast.fs:398 (moduleSpanOf)
let moduleSpanOf (m: Ast.Module) : Ast.Span =
    match m with
    | Ast.Module(_, s) | Ast.EmptyModule s -> s
    | Ast.NamedModule(_, _, s) -> s
    | Ast.NamespacedModule(_, _, s) -> s
```

### failWithSpan format (reference for test expectations)

```fsharp
// Source: Elaboration.fs:63-67
let inline private failWithSpan (span: Ast.Span) fmt =
    Printf.ksprintf (fun msg ->
        let loc = sprintf "%s:%d:%d" span.FileName span.StartLine span.StartColumn
        failwith (sprintf "[Elaboration] %s: %s" loc msg)
    ) fmt
// Result: "[Elaboration] myfile.fun:1:17: Elaboration: unbound variable 'y'"
```

## State of the Art

| Old Approach | Current Approach | Change | Impact |
|--------------|------------------|--------|--------|
| String concat prelude + user | Separate parse + AST merge | Phase 47 | User errors show line 1, not line 174 |
| All positions relative to combined string | Positions relative to each source independently | Phase 47 | Correct per-file line numbers |

**Currently broken (pre-Phase-47):**
- User code line 1 error: `file:174:col` (off by 173)
- User code line 2 error: `file:175:col` (off by 173)
- Pattern errors embed the wrong `StartLine` in the printed span struct

## Open Questions

1. **Prelude parse error behavior**
   - What we know: If the Prelude has a syntax error, `parseProgram` throws. Currently this propagates as a `[Parse]` error.
   - What's unclear: Should Prelude parse errors be silently ignored (with empty decls) or surfaced? The ROADMAP says LINE-02 requires `<prelude>` path for Prelude errors, implying they should surface.
   - Recommendation: Let Prelude parse errors propagate normally. The `<prelude>` filename in the span will naturally satisfy LINE-02.

2. **Parser fallback behavior for Prelude**
   - What we know: `parseProgram` has a fallback path that tries `parseExpr` when `parseModule` fails, wrapping in a synthetic `LetDecl("_", expr, unknownSpan)`.
   - What's unclear: The Prelude is always a module, never a bare expression. The fallback should never trigger.
   - Recommendation: No special handling needed for Prelude parsing.

## Sources

### Primary (HIGH confidence)
- Direct codebase inspection — `Program.fs` (all 208 lines), `Elaboration.fs` (lines 1-70, 4058-4256), `Ast.fs` (all 403 lines), `LangThree/Lexer.fs` (lines 1-36), `LangThree/IndentFilter.fs` (lines 1-51), `LangThree/Prelude.fs` (all 317 lines)
- Test files inspected — `44-01`, `44-02`, `44-03`, `46-01`, `46-02`, `46-03`, `46-04`, `46-05` `.flt`/`.fun` pairs

### Secondary (HIGH confidence)
- Line count verification: Prelude files sum to 161 lines + 11 `\n` separators = 172 total → user code starts at line 174, confirmed against test expectation `44-01-error-location-unbound.flt` which expects line 175 for user code line 2.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all components are in-repo, directly inspected
- Architecture: HIGH — the merge point is `Program.fs:169` (`combinedSrc`), the fix is surgical
- Pitfalls: HIGH — all 7 affected test files identified with exact old→new line number mappings

**Research date:** 2026-04-01
**Valid until:** Until Prelude files are added/removed (stable)
