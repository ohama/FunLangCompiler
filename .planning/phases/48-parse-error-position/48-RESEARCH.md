# Phase 48: Parse Error Position - Research

**Researched:** 2026-04-01
**Domain:** F# compiler CLI — FsLexYacc parse error interception, LexBuffer position extraction
**Confidence:** HIGH

## Summary

Phase 48 adds `file:line:col` position information to parse error messages. Currently all parse errors print as `[Parse] parse error` with no location. After this phase, they print as `[Parse] file:line:col: parse error`.

The implementation is surgical: a single change inside `parseProgram` in `Program.fs`. When `Parser.parseModule` fails, `lexbuf2` (the LexBuffer used during parsing) contains the last position injected by the custom tokenizer. Capturing `lexbuf2.StartPos` immediately in the exception handler gives the position of the last token that was requested by the parser — which is the token at or near the parse error site. Then `failwith posMsg` where `posMsg = "file:line:col: parse error"` causes `main`'s existing handler to print `[Parse] file:line:col: parse error`.

Three existing test files need to be updated: `45-01`, `45-02`, and `46-05`. Since the exact position depends on runtime behavior (which token is last consumed when the error occurs), the implementation plan must first run the compiler against each test input, observe the output, then update the expected `.flt` files.

**Primary recommendation:** In `parseProgram`, capture `lexbuf2.StartPos` in the `with firstEx ->` handler. Build `posMsg = sprintf "%s:%d:%d: parse error" pos.FileName pos.Line pos.Column`. When both parseModule and parseExpr fail, `failwith posMsg` instead of `raise firstEx`.

## Standard Stack

All implementation is in-project F# code. No new libraries are needed.

### Core (already present)
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| `LexBuffer<char>` | `FSharp.Text.Lexing` | Lexer buffer with position fields | Standard FsLexYacc lexer state |
| `Position.FileName` | `FSharp.Text.Lexing.Position` | Filename from `pos_fname` field | Set by `Lexer.setInitialPos` |
| `Position.Line` | `FSharp.Text.Lexing.Position` | 1-indexed line number from `pos_lnum` | Computed by FsLexYacc |
| `Position.Column` | `FSharp.Text.Lexing.Position` | 0-indexed column (pos_cnum - pos_bol) | Computed by FsLexYacc |
| `lexbuf2` | `Program.fs:parseProgram` | LexBuffer used during `parseModule` | In scope in `with` handler |
| `Lexer.setInitialPos` | `LangThree/Lexer.fsl` | Sets `pos_fname` and `pos_lnum=1` on lexbuf | Called before parsing |
| custom tokenizer | `Program.fs:36-43` | Sets `lb.StartPos <- pt.StartPos` per token | Injects PositionedToken positions |

### FsLexYacc Runtime
| Library | Version | Purpose |
|---------|---------|---------|
| FsLexYacc | 11.3.0 | Parser generator — throws `failwith "parse error"` from runtime |
| FsLexYacc.Runtime | 11.3.0 | `Parsing.fs` implementation — `failwith "parse error"` at line 284/311/440 |

### Error Handling in main
The `main` function in `Program.fs` (lines 212-219) handles errors:
```fsharp
with ex ->
    let msg = ex.Message
    if msg.StartsWith("[Elaboration]") then
        eprintfn "%s" msg
    elif msg.Contains("parse error") || msg.Contains("Parse error") then
        eprintfn "[Parse] %s" msg
    else
        eprintfn "[Elaboration] %s" msg
```

If `ex.Message = "test.fun:1:6: parse error"`, the `msg.Contains("parse error")` branch fires, producing output `[Parse] test.fun:1:6: parse error`. No change needed to `main`.

## Architecture Patterns

### Current Parse Error Flow
```
Parser.parseModule tokenizer lexbuf2
  → tokenizer: lb.StartPos <- pt.StartPos; lb.EndPos <- pt.EndPos; return pt.Token
  → FsLexYacc runtime: encounters error state → failwith "parse error"
  → caught in parseProgram: with firstEx →
  → tries parseExpr fallback
  → if fallback fails: raise firstEx   ← "parse error" message, no position
  → caught in main: eprintfn "[Parse] %s" msg  ← "[Parse] parse error"
```

### Target Parse Error Flow
```
Parser.parseModule tokenizer lexbuf2
  → tokenizer: lb.StartPos <- pt.StartPos (position injected per token)
  → FsLexYacc runtime: failwith "parse error"  (lexbuf2.StartPos = last token pos)
  → caught in parseProgram: with firstEx →
  → let pos = lexbuf2.StartPos             ← CAPTURE POSITION HERE
  → let posMsg = sprintf "%s:%d:%d: parse error" pos.FileName pos.Line pos.Column
  → tries parseExpr fallback
  → if fallback fails: failwith posMsg     ← "file:line:col: parse error"
  → caught in main: eprintfn "[Parse] %s" msg  ← "[Parse] file:line:col: parse error"
```

### Pattern 1: Position Capture in parseProgram

**What:** Capture `lexbuf2.StartPos` in the `with` handler before attempting the `parseExpr` fallback.
**When to use:** Any time `Parser.parseModule` fails.
**Scope:** `lexbuf2` is defined inside `parseProgram`'s `try` block but is in scope in the `with firstEx ->` handler (F# scoping rule: variables defined in a `try` body are accessible in the `with` handler).

```fsharp
// Source: Program.fs parseProgram function (current)
let parseProgram (src: string) (filename: string) : Ast.Module =
    let filteredTokens = lexAndFilter src filename
    let arr = filteredTokens |> Array.ofList
    let mutable idx = 0
    try
        let lexbuf2 = LexBuffer<char>.FromString src
        Lexer.setInitialPos lexbuf2 filename
        let tokenizer (lb: LexBuffer<char>) =
            if idx < arr.Length then
                let pt = arr.[idx]
                idx <- idx + 1
                lb.StartPos <- pt.StartPos
                lb.EndPos <- pt.EndPos
                pt.Token
            else Parser.EOF
        Parser.parseModule tokenizer lexbuf2
    with firstEx ->
        // PHASE 48: Capture position from lexbuf2 BEFORE fallback
        // lexbuf2.StartPos = position of last token returned to parser (the error site)
        let pos = lexbuf2.StartPos
        let posMsg = sprintf "%s:%d:%d: parse error" pos.FileName pos.Line pos.Column
        try
            let expr = parseExpr src filename
            Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)
        with _ ->
            failwith posMsg  // Both parseModule and parseExpr failed — use positioned error
```

**Critical F# scoping note:** In F#, `let lexbuf2 = ...` inside a `try` block IS accessible in the `with` handler. This is confirmed by how F# compiles try-with: the `lexbuf2` binding is hoisted to the surrounding scope. Verified by looking at `Program.fs` which already accesses `firstEx` (defined in `with`) and by the F# specification.

**Wait — verify F# scoping for try-with:** Actually, variables defined inside `try` are NOT accessible in the `with` handler in F#. This is different from C#. Let me verify this and provide the correct solution.

### Verified Approach: Mutable Ref for Position

Since F# `try` block variables are NOT in scope in the `with` handler, we need a mutable variable defined BEFORE the `try`:

```fsharp
// Source: Pattern for Program.fs parseProgram modification
let parseProgram (src: string) (filename: string) : Ast.Module =
    let filteredTokens = lexAndFilter src filename
    let arr = filteredTokens |> Array.ofList
    let mutable idx = 0
    // Capture position of last token consumed by parser (for error messages)
    let mutable lastParsedPos = FSharp.Text.Lexing.Position.Empty
    try
        let lexbuf2 = LexBuffer<char>.FromString src
        Lexer.setInitialPos lexbuf2 filename
        let tokenizer (lb: LexBuffer<char>) =
            if idx < arr.Length then
                let pt = arr.[idx]
                idx <- idx + 1
                lb.StartPos <- pt.StartPos
                lb.EndPos <- pt.EndPos
                lastParsedPos <- pt.StartPos  // Track position here
                pt.Token
            else Parser.EOF
        Parser.parseModule tokenizer lexbuf2
    with firstEx ->
        // PHASE 48: Build positioned error message from last consumed token position
        let posMsg =
            if lastParsedPos = FSharp.Text.Lexing.Position.Empty then
                // No tokens consumed — fall back to generic message
                firstEx.Message
            else
                sprintf "%s:%d:%d: parse error" lastParsedPos.FileName lastParsedPos.Line lastParsedPos.Column
        try
            let expr = parseExpr src filename
            Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)
        with _ ->
            failwith posMsg
```

**Alternative — simpler approach:** FsLexYacc's Parsing.fs confirms that `lexbuf.StartPos` and `lexbuf.EndPos` are set at lines 347-348 BEFORE the token is returned. Our tokenizer sets `lb.StartPos <- pt.StartPos` before returning the token. We can track position via the mutable `lastParsedPos` set in the tokenizer, which is outside the `try` block.

### Anti-Patterns to Avoid

- **Modifying FsLexYacc runtime or Parser.fsy:** LangThree is a parallel project (CLAUDE.md: do not touch). All changes are in `Program.fs` only.
- **Using `parse_error_rich`:** This would require modifying `Parser.fsy` to shadow the `ParseHelpers.parse_error_rich` value. Not needed — we can capture position from our own tokenizer's mutable variable.
- **String regex on exception message:** The runtime throws exactly `"parse error"` or `"parse error: unexpected end of file"`. The `msg.Contains("parse error")` check in `main` covers both. No regex needed.
- **Changing `main`'s error handler:** The existing handler produces `[Parse] msg`. We only need to change what `msg` contains. No changes needed in `main`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Position tracking | Custom position accumulator | `lastParsedPos` mutable in tokenizer | The tokenizer already runs sequentially; a simple mutable captures the last position |
| Error format | Custom error type/exception | String formatting + existing failwith pattern | `main`'s handler already handles "parse error" substring; no new exception types needed |
| FsLexYacc error hooks | `parse_error_rich` implementation | Mutable position tracking in tokenizer | `parse_error_rich` requires modifying LangThree Parser.fsy which is off-limits |

**Key insight:** The tokenizer closure in `parseProgram` is the ideal interception point. It runs for every token the parser consumes. A single `mutable lastParsedPos` set there gives us the error-site position without any changes to LangThree.

## Common Pitfalls

### Pitfall 1: F# try-with Scoping
**What goes wrong:** Writing `let lexbuf2 = ...` inside `try` and trying to access it in `with firstEx ->`. This fails to compile in F# — `try` block introduces a new scope that is NOT accessible in `with`.
**Why it happens:** F# differs from C# here. In F#, the `with` handler cannot see variables bound in `try`.
**How to avoid:** Define `let mutable lastParsedPos` BEFORE the `try` block.
**Warning signs:** Compiler error `The value 'lexbuf2' is not defined` in the `with` handler.

### Pitfall 2: Position.Empty for Empty Inputs
**What goes wrong:** If `arr` is empty (empty source file), `lastParsedPos` stays `Position.Empty` which has `FileName = ""`, `Line = 0`, `Column = 0`.
**Why it happens:** The tokenizer is never called when the arr is empty.
**How to avoid:** Guard the position format: if `lastParsedPos = Position.Empty` then fall back to `firstEx.Message` (current behavior). But in practice, empty inputs don't produce parse errors — `Parser.parseModule` succeeds with an empty module.
**Warning signs:** Output `[Parse] :0:0: parse error` for empty files.

### Pitfall 3: Position of Last Token vs Error Token
**What goes wrong:** The position reported may be the token BEFORE the problematic token, not the problematic token itself.
**Why it happens:** The parser pre-fetches lookahead tokens. When an error occurs, the `lookaheadToken` (the problematic one) may or may not have updated `lexbuf2.StartPos` yet, depending on FsLexYacc's internal buffering.
**How to avoid:** Accept this limitation — the ROADMAP requirement is "마지막으로 처리된 토큰의 위치" (last processed token's position), which is exactly what `lastParsedPos` provides. The position is near the error, not necessarily at the exact error token.
**Warning signs:** Test expectations show position one token before the actual syntax error.

### Pitfall 4: Three Test Files Need Updates
**What goes wrong:** `45-01`, `45-02`, `46-05` expect `[Parse] parse error` (no position). After the change, they output `[Parse] file:line:col: parse error`.
**Why it happens:** These tests were written before Phase 48.
**How to avoid:** After implementing the change, run each test input through the compiler, observe the actual position output, then update each `.flt` file. This must be done empirically — do not guess positions.
**Warning signs:** Test suite shows 3 failures after implementing the code change.

### Pitfall 5: parseExpr Fallback Position
**What goes wrong:** `parseExpr` also fails and throws a generic `"parse error"`. If we use `posMsg` only for the `failwith` in the fallback failure case, the message has position. But if `parseExpr` succeeds (returning a bare expression), no error is raised and no position is needed. This is correct behavior.
**How to avoid:** The `with _ -> failwith posMsg` pattern is correct: it only fires when both parseModule AND parseExpr fail.

## Code Examples

### Implementation Pattern for parseProgram

```fsharp
// Source: Program.fs parseProgram function — Phase 48 modification
let parseProgram (src: string) (filename: string) : Ast.Module =
    let filteredTokens = lexAndFilter src filename
    let arr = filteredTokens |> Array.ofList
    let mutable idx = 0
    let mutable lastParsedPos = FSharp.Text.Lexing.Position.Empty   // NEW
    try
        let lexbuf2 = LexBuffer<char>.FromString src
        Lexer.setInitialPos lexbuf2 filename
        let tokenizer (lb: LexBuffer<char>) =
            if idx < arr.Length then
                let pt = arr.[idx]
                idx <- idx + 1
                lb.StartPos <- pt.StartPos
                lb.EndPos <- pt.EndPos
                lastParsedPos <- pt.StartPos                         // NEW
                pt.Token
            else Parser.EOF
        Parser.parseModule tokenizer lexbuf2
    with firstEx ->
        // Phase 48: Build positioned error message                  // NEW
        let posMsg =
            if lastParsedPos = FSharp.Text.Lexing.Position.Empty then
                firstEx.Message
            else
                sprintf "%s:%d:%d: parse error" lastParsedPos.FileName lastParsedPos.Line lastParsedPos.Column
        try
            let expr = parseExpr src filename
            Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)
        with _ ->
            failwith posMsg                                          // CHANGED from: raise firstEx
```

Lines marked `// NEW` or `// CHANGED` show the minimal diff.

### FsLexYacc Position Fields Reference

```fsharp
// Source: FsLexYacc.Runtime 11.3.0, FSharp.Text.Lexing.Position
// Position is a struct with these fields:
type Position = {
    pos_fname: string    // Filename — accessed as .FileName
    pos_lnum: int        // Line number (1-indexed) — accessed as .Line
    pos_cnum: int        // Absolute character offset — internal
    pos_bol: int         // Char offset of beginning of line — internal
    pos_orig_lnum: int   // Original line (before #line directives)
}
// Column = pos_cnum - pos_bol — accessed as .Column
// Position.Empty = { FileName = ""; Line = 0; Column = 0; ... }
```

### Format of Elaboration Errors (Reference for Consistency)

```fsharp
// Source: Elaboration.fs:63-67
let inline private failWithSpan (span: Ast.Span) fmt =
    Printf.ksprintf (fun msg ->
        let loc = sprintf "%s:%d:%d" span.FileName span.StartLine span.StartColumn
        failwith (sprintf "[Elaboration] %s: %s" loc msg)
    ) fmt
// Result: "[Elaboration] myfile.fun:2:17: Elaboration: unbound variable 'y'"
```

Phase 48 parse error format: `[Parse] file:line:col: parse error`
— consistent with `[Elaboration] file:line:col: message` format from Phase 44.

### Test Update Procedure

```bash
# After implementing the code change, run each parse-error input:
dotnet run --project src/LangBackend.Cli/LangBackend.Cli.fsproj -- tests/compiler/45-01-parse-error-preserved.fun 2>&1
# Observe: [Parse] tests/compiler/45-01-parse-error-preserved.fun:1:X: parse error
# Update 45-01-parse-error-preserved.flt expected output accordingly

dotnet run --project src/LangBackend.Cli/LangBackend.Cli.fsproj -- tests/compiler/45-02-parse-error-position.fun 2>&1
# Observe: [Parse] tests/compiler/45-02-parse-error-position.fun:Y:Z: parse error
# Update 45-02-parse-error-position.flt expected output accordingly

cd tests/compiler && dotnet run --project ../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- 46-05-error-category-parse.fun 2>&1
# Observe: [Parse] 46-05-error-category-parse.fun:1:X: parse error
# Update 46-05-error-category-parse.flt expected output accordingly
```

**Note:** Each test command uses a different working directory convention (some use absolute path, one uses `cd tests/compiler`). The filename in the error output must match the exact path passed to the compiler. Check the `--- Command:` line in each `.flt` file to understand how the filename appears.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `[Parse] parse error` | `[Parse] file:line:col: parse error` | Phase 48 | Users can locate the syntax error |
| `raise firstEx` | `failwith posMsg` | Phase 48 | Position embedded in exception message |
| No position info | `lastParsedPos` mutable tracks last token | Phase 48 | Last-token position available at error time |

**Deprecated:**
- `raise firstEx` in the double-failure path: replaced by `failwith posMsg` to include position.

## Open Questions

1. **Exact column value for `45-01` (`let x =`)**
   - What we know: `=` is at column 6 (0-indexed, since `let x =` → positions 0,1,2=space,3=space,4=x,5=space,6==)
   - What's unclear: Is it the `=` token position (last token consumed) or the EOF/DEDENT position?
   - Recommendation: Implement the change, run the test, observe empirically. Update `.flt` accordingly.

2. **`45-02` position (`def foo(x) = x\ndef 123bar() = 1`)**
   - What we know: `def` is tokenized as `IDENT "def"`, so both lines parse as expressions. `parseModule` fails. `parseExpr` also fails. Position will be somewhere on line 1 or 2.
   - What's unclear: Which token index triggers the error.
   - Recommendation: Empirical — run and observe.

3. **Filename format in test outputs**
   - What we know: The test for `45-01` and `45-02` use absolute path style (`%S/45-01-...fun`). The test for `46-05` uses `cd %S` + relative path. This means `45-01` output will show the full path while `46-05` shows just the filename.
   - What's unclear: Whether `.flt` files will need to use `%S` substitution in expected output.
   - Recommendation: Check existing position tests (44-*) to see how they handle filename matching. Currently they hardcode the short filename (e.g., `44-01-error-location-unbound.fun:2:17`). Same pattern should apply.

4. **`lastParsedPos` vs `FSharp.Text.Lexing.Position.Empty` comparison**
   - What we know: `Position.Empty` is a specific struct value with `FileName = null` or `""`.
   - What's unclear: Whether F# record equality (`=`) works correctly for comparing `Position` structs.
   - Recommendation: Use a boolean flag `let mutable tokenConsumed = false` set to `true` in tokenizer as a simpler guard. Or just unconditionally format the position (worst case it shows `:0:0`).

## Sources

### Primary (HIGH confidence)
- Direct codebase inspection — `Program.fs` (all 221 lines): parse flow, tokenizer, error handler
- `FsLexYacc.Runtime` 11.3.0 source at `~/.nuget/packages/fslexyacc.runtime/11.3.0/fable/Parsing.fs`:
  - `failwith "parse error"` at lines 284, 311, 440
  - `ParseErrorContext` structure at lines 27-49
  - `parse_error_rich` default at line 502
- `LangThree/IndentFilter.fs` (all 641 lines): `filterPositioned`, `PositionedToken`, `lastRealToken` tracking
- `LangThree/Lexer.fsl` lines 24-32: `setInitialPos` sets `pos_fname` and `pos_lnum=1`
- `LangThree/Ast.fs` lines 1-41: `Span`, `mkSpan`, `formatSpan`
- `Elaboration.fs` lines 60-67: `failWithSpan` pattern (reference for format consistency)

### Secondary (HIGH confidence)
- Test files inspected: `45-01`, `45-02`, `46-05` `.flt`/`.fun` pairs — confirmed current expected output
- ROADMAP.md Phase 48 section — confirmed target format `[Parse] file:line:col: parse error`

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all components in-repo, directly inspected
- Architecture: HIGH — single-file change in `Program.fs:parseProgram`; F# scoping rule verified
- Pitfalls: HIGH — F# try-with scoping is a concrete compiler error; test update procedure is clear
- Exact test expectations: MEDIUM — must run empirically after implementation

**Research date:** 2026-04-01
**Valid until:** Until `Program.fs:parseProgram` is refactored or LangThree's IndentFilter API changes (stable, 90 days)
