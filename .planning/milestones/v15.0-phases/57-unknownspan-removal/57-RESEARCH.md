# Phase 57: unknownSpan 전면 제거 - Research

**Researched:** 2026-04-01
**Domain:** F# compiler / AST Span propagation / Error diagnostic location
**Confidence:** HIGH

## Summary

This phase removes all 11 occurrences of `Ast.unknownSpan` (10 in Elaboration.fs, 1 in Program.fs)
and replaces each with a real AST `Span` extracted from the surrounding pattern-match context.
The `unknownSpan` sentinel produces `<unknown>:0:0:0` positions in error messages; replacing it with
real spans makes every elaboration error report a correct `file:line:col`.

The codebase already has the full infrastructure in place: `failWithSpan` (Elaboration.fs line 63),
`Ast.spanOf`, `Ast.patternSpanOf`, `Ast.declSpanOf`, `Ast.moduleSpanOf`, and `Ast.mkSpan`.
Every fix is a one-liner pattern-variable capture change — no new helper functions are needed.

Each fix follows the same pattern: change `_` to a named span variable in the surrounding
discriminated-union match, then pass that variable instead of `Ast.unknownSpan`.

**Primary recommendation:** Capture span from surrounding match arms directly; never compute a
synthetic span when the surrounding node already carries one.

## Standard Stack

This is a brownfield F# compiler project. No external packages are needed for this phase.

### Core
| Tool | Version | Purpose | Why Standard |
|------|---------|---------|--------------|
| F# pattern matching | F# 8 / .NET 10 | Bind span from DU match | Only mechanism available |
| `Ast.spanOf` | FunLang AST | Extract span from any Expr | Exhaustive match, all Expr variants |
| `Ast.moduleSpanOf` | FunLang AST | Extract span from Module | Covers all Module variants |
| `failWithSpan` | Elaboration.fs:63 | Format `file:line:col: msg` | Already in place since v11.0 |
| FsLit `.flt` / `.fun` pair | Project test runner | E2E error-message tests | Pattern used for phases 44–46 |

### Supporting
| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `Ast.patternSpanOf` | FunLang AST | Span from Pattern | When fixing span in pattern context |
| `Ast.declSpanOf` | FunLang AST | Span from Decl | When propagating module-level span |
| `CHECK-RE:` directive | FsLit runner | Regex match on stderr | For error tests that check file:line:col |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Capturing span from match arm | Computing span from sub-expressions | Capture is simpler and correct |
| Using first-decl span for SPAN-07 | Using module span | Module span matches user intent better for empty-module edge case |

**Installation:** No new packages. Build with `dotnet build`.

## Architecture Patterns

### Recommended Project Structure

No structural changes. All edits are in:
```
src/FunLangCompiler.Compiler/Elaboration.fs   # 10 unknownSpan occurrences
src/FunLangCompiler.Cli/Program.fs            # 1 unknownSpan occurrence
tests/compiler/57-*.flt                       # new E2E tests
tests/compiler/57-*.fun                       # companion source files
```

### Pattern 1: Bind Span from Outer App Match (SPAN-01, SPAN-02)

**What:** The outer `App(_, _, span)` is already available in the match arm; `_` discards it.
**When to use:** All printfn/eprintfn desugar arms that use `let s = Ast.unknownSpan`.

Before:
```fsharp
// SPAN-01: printfn 2-arg
| App (App (App (Var ("printfn", _), String (fmt, _), _), arg1Expr, _), arg2Expr, _) ->
    let s = Ast.unknownSpan
    ...

// SPAN-02: eprintfn 1-arg
| App (Var ("eprintfn", _), String (fmt, _), _) ->
    let s = Ast.unknownSpan
    ...
```

After:
```fsharp
// SPAN-01: printfn 2-arg — bind outermost App span
| App (App (App (Var ("printfn", _), String (fmt, _), _), arg1Expr, _), arg2Expr, s) ->
    // s is now the real span of the printfn call site
    ...

// SPAN-02: eprintfn 1-arg
| App (Var ("eprintfn", _), String (fmt, _), s) ->
    // s is the real span
    ...
```

Apply the same transformation to the 3 printfn arms (2-arg, 1-arg, 0-arg) and 1 eprintfn arm.

### Pattern 2: Propagate Outer App Span to Synthetic String Node (SPAN-03, SPAN-04)

**What:** `show`/`eq` builtins re-elaborate a new `Ast.String(s, Ast.unknownSpan)`. The enclosing
`App` carries the real span.

Before (SPAN-03, line 1498–1500):
```fsharp
| App (Var ("show", _), String (s, _), _) ->
    elaborateExpr env (Ast.String(s, Ast.unknownSpan))
```

After:
```fsharp
| App (Var ("show", _), String (s, _), appSpan) ->
    elaborateExpr env (Ast.String(s, appSpan))
```

Before (SPAN-04, lines 1527–1529):
```fsharp
| App (App (Var ("eq", _), String(ls, _), _), String(rs, _), _) ->
    let (lv, lops) = elaborateExpr env (Ast.String(ls, Ast.unknownSpan))
    let (rv, rops) = elaborateExpr env (Ast.String(rs, Ast.unknownSpan))
```

After:
```fsharp
| App (App (Var ("eq", _), String(ls, lsSpan), _), String(rs, rsSpan), _) ->
    let (lv, lops) = elaborateExpr env (Ast.String(ls, lsSpan))
    let (rv, rops) = elaborateExpr env (Ast.String(rs, rsSpan))
```

Note: For SPAN-04 we can use the spans already on the String literals themselves (`lsSpan`, `rsSpan`),
which is more precise than the outer App span.

### Pattern 3: Bind Lambda Span for Closure Capture Error (SPAN-05)

**What:** The closure-capture error at line 798 is inside the
`Let(name, StripAnnot(Lambda(...)), ...)` arm. The outer Let's span is available by binding the
last field of `Let`.

Before (line 680):
```fsharp
| Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, _) ->
    ...
    | None -> failWithSpan Ast.unknownSpan "Elaboration: closure capture '%s' not found in outer scope" capName
```

After:
```fsharp
| Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, letSpan) ->
    ...
    | None -> failWithSpan letSpan "Elaboration: closure capture '%s' not found in outer scope" capName
```

Binding `letSpan` (the Let declaration span) is the most practical: it points to the line where the
closure function is defined, which is correct for the user.

### Pattern 4: Bind Constructor Span for First-Class Wrapping (SPAN-06)

**What:** `Constructor(name, None, _)` match at line 3146 already has the span as the third field.

Before (line 3153):
```fsharp
| Constructor(name, None, _) ->
    ...
    let s = Ast.unknownSpan
    elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))
```

After:
```fsharp
| Constructor(name, None, ctorSpan) ->
    ...
    let s = ctorSpan
    elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))
```

### Pattern 5: Pass Module Span to extractMainExpr (SPAN-07)

**What:** `extractMainExpr` currently takes `decls: Ast.Decl list` and uses `unknownSpan` as `s`
for all synthetic nodes (empty-module Number, continuation Var/Number). Pass the module span
from `elaborateProgram` (which has `ast: Ast.Module`).

Before:
```fsharp
let private extractMainExpr (decls: Ast.Decl list) : Expr =
    let s = unknownSpan
    ...

// caller in elaborateProgram:
let mainExpr = extractMainExpr decls
```

After:
```fsharp
let private extractMainExpr (moduleSpan: Ast.Span) (decls: Ast.Decl list) : Expr =
    let s = moduleSpan
    ...

// caller:
let mainExpr = extractMainExpr (Ast.moduleSpanOf ast) decls
```

### Pattern 6: Use Expr Span for parseExpr Fallback Wrapping (SPAN-08)

**What:** `Program.fs` line 51 wraps a bare expression in a synthetic `Module`. Use the expr's own
span (via `Ast.spanOf expr`) for the `LetDecl` and `Module`.

Before:
```fsharp
let expr = parseExpr src filename
Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)
```

After:
```fsharp
let expr = parseExpr src filename
let exprSpan = Ast.spanOf expr
Ast.Module([Ast.Decl.LetDecl("_", expr, exprSpan)], exprSpan)
```

### Pattern 7: E2E Test Format for Error Location (TEST-01)

Use the same `.flt` + `.fun` pair pattern established by phases 44–46.

Test file naming: `57-01-<description>.flt` and `57-01-<description>.fun`

Test file structure (CHECK-RE for error tests, CHECK for output tests):
```
// Test: <description>
// --- Command: bash -c 'cd %S && dotnet run --project ../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- 57-01-<description>.fun 2>&1; echo $?'
// --- Input:
// --- Output:
CHECK-RE: \[Elaboration\] 57-01-<description>\.fun:\d+:\d+: <error message>
1
```

The `CHECK-RE:` directive matches stderr with a regex. `\d+:\d+` matches any line:col, so tests
are not brittle to exact position. Exit code `1` is the last line.

### Anti-Patterns to Avoid

- **Using `Ast.mkSpan`:** Do not construct a new Span from lexer Positions. Every fix here binds
  an existing span from the surrounding pattern match — no new spans need to be synthesized.
- **Passing `Ast.unknownSpan` as fallback:** If a span cannot be obtained from the match arm,
  find the next outer context that has one. There is always an outer node with a real span in
  this codebase.
- **Changing test infrastructure:** The FsLit runner and `CHECK-RE:` format are established
  and stable. Do not invent a new test mechanism.
- **Using `Ast.spanOf expr` when a nearer span is available:** Prefer the span of the node at
  the match site, not the root expr span.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Span extraction from Expr | Custom recursive walk | `Ast.spanOf` | Already exhaustive for all Expr variants |
| Span extraction from Pattern | Custom logic | `Ast.patternSpanOf` | Already exhaustive |
| Span extraction from Module | Custom match | `Ast.moduleSpanOf` | Covers all Module variants including EmptyModule |
| Error formatting | Custom `sprintf` | `failWithSpan` | Already handles `file:line:col: message` |

**Key insight:** All span-related utilities are already present in the FunLang AST module.
Every `unknownSpan` in Elaboration.fs has an enclosing pattern with a real span available.

## Common Pitfalls

### Pitfall 1: Nested StripAnnot in Lambda Pattern Hides Span
**What goes wrong:** The SPAN-05 arm uses `StripAnnot (Lambda (...))` which does not bind the
Lambda's own span. Trying to bind the inner lambda span requires extra pattern nesting.
**Why it happens:** `StripAnnot` is a custom active pattern that strips `Annot`/`LambdaAnnot`
wrappers, so the matched Lambda is not the literal DU case.
**How to avoid:** Bind the outer `Let`'s span (the 4th field) instead — simpler and just as
accurate for error diagnostics (points to the function definition line).
**Warning signs:** If you see `StripAnnot (Lambda (..., lambdaSpan))` — this won't compile
because `StripAnnot` is an active pattern, not a constructor. Use the enclosing Let span.

### Pitfall 2: extractMainExpr Signature Change Must Be Propagated
**What goes wrong:** Adding `moduleSpan` parameter to `extractMainExpr` requires updating the
call site in `elaborateProgram`.
**Why it happens:** F# private function signatures are not checked at call sites automatically
when you refactor.
**How to avoid:** Change the signature and immediately fix the call site in the same edit pass.
The only call site is in `elaborateProgram` (line 4450).
**Warning signs:** Build error "This value is not a function" at line 4450.

### Pitfall 3: SPAN-04 — Two Separate String Literal Spans
**What goes wrong:** Developers might use the outer App span for both string literals in the
`eq` arm, when the strings have their own individual spans.
**Why it happens:** The outer App span refers to the whole `eq ls rs` expression.
**How to avoid:** Bind `String(ls, lsSpan)` and `String(rs, rsSpan)` in the pattern — each
string literal already carries its own span from parsing.
**Warning signs:** Both error messages point to the same column when the strings are on
different positions.

### Pitfall 4: SPAN-08 Module-level unknownSpan Used Twice
**What goes wrong:** Line 51 in Program.fs uses `Ast.unknownSpan` twice — once for the
`LetDecl` span and once for the `Module` span. Both must be replaced.
**Why it happens:** The pattern creates a synthetic wrapper; both spans were left as unknown.
**How to avoid:** Compute `exprSpan = Ast.spanOf expr` once and use it for both.
**Warning signs:** One of the two unknownSpan usages remains after the fix — the grep for
`unknownSpan` should return 0 results after the phase is complete.

### Pitfall 5: Test Matching printfn/eprintfn Error is Difficult
**What goes wrong:** printfn/eprintfn desugars successfully in normal operation; the unknownSpan
in those arms only matters if an error is thrown during the desugared elaboration.
**Why it happens:** The desugar arms just set `let s = ...` and pass it to synthetic nodes —
there is no error throw in the arms themselves. The span propagates into recursive
`elaborateExpr` calls.
**How to avoid:** For TEST-01, focus on testable error paths:
  - SPAN-05 (closure capture not found) — directly triggers `failWithSpan`
  - SPAN-06 (first-class constructor) — can test correct span in generated code, or skip
  - SPAN-07 (extractMainExpr) — test empty module or minimal module behavior
  For SPAN-01/02/03/04, the span propagates into synthetic AST nodes used in recursive calls;
  a test that triggers an error inside desugared printfn is complex. These are best covered by
  verifying no `unknownSpan` remains via grep.

## Code Examples

### Binding span from outermost App (SPAN-01 pattern)
```fsharp
// Source: Elaboration.fs lines 2188–2203 (to be changed)
// printfn 2-arg: bind last field of outermost App
| App (App (App (Var ("printfn", _), String (fmt, _), _), arg1Expr, _), arg2Expr, s) ->
    let sprintfExpr = App(App(App(Var("sprintf", s), String(fmt, s), s), arg1Expr, s), arg2Expr, s)
    elaborateExpr env (App(Var("println", s), sprintfExpr, s))

// printfn 1-arg:
| App (App (Var ("printfn", _), String (fmt, _), _), argExpr, s) ->
    let sprintfExpr = App(App(Var("sprintf", s), String(fmt, s), s), argExpr, s)
    elaborateExpr env (App(Var("println", s), sprintfExpr, s))

// printfn 0-arg:
| App (Var ("printfn", _), String (fmt, _), s) ->
    elaborateExpr env (App(Var("println", s), String(fmt, s), s))

// eprintfn 1-arg (SPAN-02):
| App (Var ("eprintfn", _), String (fmt, _), s) ->
    elaborateExpr env (App(Var("eprintln", s), String(fmt, s), s))
```

### Using string literal's own span (SPAN-04 pattern)
```fsharp
// Source: Elaboration.fs lines 1526–1529 (to be changed)
| App (App (Var ("eq", _), String(ls, lsSpan), _), String(rs, rsSpan), _) ->
    let (lv, lops) = elaborateExpr env (Ast.String(ls, lsSpan))
    let (rv, rops) = elaborateExpr env (Ast.String(rs, rsSpan))
```

### Binding Let span for closure error (SPAN-05 pattern)
```fsharp
// Source: Elaboration.fs line 680 (to be changed)
| Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, letSpan) ->
    ...
    // line 798:
    | None -> failWithSpan letSpan "Elaboration: closure capture '%s' not found in outer scope" capName
```

### Constructor span binding (SPAN-06 pattern)
```fsharp
// Source: Elaboration.fs line 3146 (to be changed)
| Constructor(name, None, ctorSpan) ->
    let info = Map.find name env.TypeEnv
    if info.Arity >= 1 then
        let n = env.Counter.Value
        env.Counter.Value <- n + 1
        let paramName = sprintf "__ctor_%d_%s" n name
        let s = ctorSpan   // was: Ast.unknownSpan
        elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))
```

### extractMainExpr signature change (SPAN-07 pattern)
```fsharp
// Source: Elaboration.fs line 4313 (to be changed)
let private extractMainExpr (moduleSpan: Ast.Span) (decls: Ast.Decl list) : Expr =
    let s = moduleSpan   // was: unknownSpan
    ...

// Call site at line 4450:
let mainExpr = extractMainExpr (Ast.moduleSpanOf ast) decls
```

### Program.fs parseExpr fallback (SPAN-08 pattern)
```fsharp
// Source: Program.fs line 50–51 (to be changed)
let expr = parseExpr src filename
let exprSpan = Ast.spanOf expr
Ast.Module([Ast.Decl.LetDecl("_", expr, exprSpan)], exprSpan)
```

### E2E test for closure capture error (TEST-01 example)
```
// tests/compiler/57-01-closure-capture-span.flt
// Test: Closure capture error includes source location
// --- Command: bash -c 'cd %S && dotnet run --project ../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- 57-01-closure-capture-span.fun 2>&1; echo $?'
// --- Input:
// --- Output:
CHECK-RE: \[Elaboration\] 57-01-closure-capture-span\.fun:\d+:\d+: Elaboration: closure capture
1
```

The `.fun` source file should contain a construct that triggers the closure capture failure, but
this error path (`None` from `Map.tryFind capName env.Vars`) is an internal invariant violation —
it cannot easily be triggered from user code. Consider testing SPAN-06 and SPAN-07 instead for
TEST-01, or write a grep-based verification test.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `unknownSpan` everywhere | Real spans from AST | Phase 57 | Errors show correct file:line:col |
| `failwith` (no location) | `failWithSpan` | v11.0 | Infrastructure ready since v11.0 |
| `unknownSpan` in desugar | Outer App span | Phase 57 | Desugar errors traceable to source |

**Deprecated/outdated:**
- `Ast.unknownSpan` usage in Elaboration.fs/Program.fs: Replaced entirely in this phase.
  After this phase, `unknownSpan` is only valid for truly synthetic prelude/built-in nodes
  that have no source origin (e.g., prelude declarations from `<prelude>` filename).

## Open Questions

1. **SPAN-05: Closure capture failure is an invariant error, hard to trigger from user code**
   - What we know: `failWithSpan Ast.unknownSpan` at line 798 fires when `Map.tryFind capName env.Vars` returns `None`, which should be impossible if the FV analysis is correct.
   - What's unclear: Can this path be triggered from a user-written program to write an E2E test?
   - Recommendation: Fix the span (bind `letSpan`) but for TEST-01, skip E2E testing this specific
     path. Use a grep assertion instead: `grep -r unknownSpan src/` should return 0 results.

2. **SPAN-07: What does an empty-module span look like?**
   - What we know: `Ast.EmptyModule span` carries a span from the parser. The module span for an
     empty file will be the file start position.
   - What's unclear: Whether `extractMainExpr` on an empty decls list is ever reached in practice
     (since even a bare expression goes through the parseExpr fallback).
   - Recommendation: Fix it anyway for correctness. Test with a minimal non-empty module.

3. **Test coverage for SPAN-01/02/03/04 (desugar spans)**
   - What we know: These spans flow into synthetic AST nodes passed to recursive `elaborateExpr`.
     They are not used in any `failWithSpan` directly.
   - What's unclear: Whether a subsequent error (e.g., unbound variable inside a desugared sprintf
     call) would surface the correct span.
   - Recommendation: Verify by grep (no `unknownSpan` remaining) rather than E2E test. The
     desugared nodes' spans are forwarded into sub-elaboration where any error will use `Ast.spanOf`
     on the sub-expression, which will carry the correct span from the outer App.

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` —
  direct inspection of all 10 `unknownSpan` occurrences with surrounding context
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Cli/Program.fs` — line 51
- `/Users/ohama/vibe-coding/FunLangCompiler/deps/FunLang/src/FunLang/Ast.fs` — Span type,
  `unknownSpan`, `spanOf`, `patternSpanOf`, `declSpanOf`, `moduleSpanOf`, `mkSpan`
- `/Users/ohama/vibe-coding/FunLangCompiler/tests/compiler/44-*.flt`, `46-*.flt` — E2E test format

### Secondary (MEDIUM confidence)
- Prior phase context: `failWithSpan` infrastructure complete since v11.0 (per requirements)
- Phase 44/45/46 test patterns — confirmed by reading actual test files

### Tertiary (LOW confidence)
- None

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — inspected actual code, no ambiguity
- Architecture: HIGH — exact line numbers confirmed, fix patterns are mechanical
- Pitfalls: HIGH — StripAnnot pattern limitation confirmed by reading active pattern definition
- Test format: HIGH — confirmed by reading 44-01, 44-02, 46-04, 46-05 test files

**Research date:** 2026-04-01
**Valid until:** Stable — this is a brownfield fix with no moving dependencies. Valid indefinitely
until Elaboration.fs or Ast.fs structures change.
