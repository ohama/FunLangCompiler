# Phase 45: Error Preservation - Research

**Researched:** 2026-03-31
**Domain:** Parser error handling, MLIR temp file lifecycle, F# exception patterns
**Confidence:** HIGH

## Summary

This phase addresses two independent problems: (1) parser fallback silently swallows error messages, and (2) MLIR temp files are deleted even when compilation fails, making debugging impossible.

The parser issue is in `Program.fs:parseProgram` (lines 33-52). When `Parser.parseModule` fails, the `with ex ->` handler prints a DEBUG message to stderr but then silently retries via `parseExpr`. The original parse error (which may be the real problem) is lost. The fix is straightforward: capture the exception, and if the fallback also fails, surface the original error. If the fallback succeeds, optionally warn.

The MLIR temp file issue is in `Pipeline.fs:compile` (lines 77-114). The `finally` block unconditionally deletes all temp files. When `mlir-opt` or `mlir-translate` fails, the user gets an error message but cannot inspect the `.mlir` file that caused the failure. The fix: only delete temp files on success; on failure, preserve them and include the path in the error message.

**Primary recommendation:** These are two independent, small changes -- one in Program.fs (parser error surfacing) and one in Pipeline.fs (conditional temp file cleanup). No new libraries needed.

## Standard Stack

No new libraries required. This phase modifies existing code only.

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| FSharp.Text.Lexing | 11.3.0 | FsLexYacc runtime (parser/lexer) | Already in use; parser exceptions come from here |

### Supporting
None needed.

## Architecture Patterns

### Pattern 1: Parser Fallback with Error Preservation

**What:** When `parseModule` fails and we fall back to `parseExpr`, preserve the original error for diagnostics.

**Current code (Program.fs:33-52):**
```fsharp
try
    Parser.parseModule tokenizer lexbuf2
with ex ->
    // DEBUG prints to stderr, then silently falls back
    eprintfn "DEBUG parseModule failed at token ~%d/%d: %s" idx arr.Length ex.Message
    // ... token context dump ...
    let expr = parseExpr src filename     // fallback -- if THIS also fails, user sees parseExpr error, not the original
    Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)
```

**Recommended pattern:**
```fsharp
let moduleParseResult =
    try
        Ok (Parser.parseModule tokenizer lexbuf2)
    with ex ->
        Error ex

match moduleParseResult with
| Ok m -> m
| Error moduleEx ->
    try
        let expr = parseExpr src filename
        // Fallback succeeded -- emit warning with original error
        eprintfn "Warning: module parse failed (%s), fell back to expression parse" moduleEx.Message
        Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)
    with _exprEx ->
        // Both failed -- surface the MODULE error (more informative)
        raise moduleEx
```

**Key insight:** The original `moduleEx.Message` contains "syntax error" from FsLexYacc. This is the message that should be surfaced when both parses fail.

### Pattern 2: Conditional Temp File Cleanup in Pipeline

**What:** Only delete MLIR temp files on successful compilation; preserve them on failure.

**Current code (Pipeline.fs:110-114):**
```fsharp
finally
    // DEBUG: copy mlir to /tmp/debug_last.mlir before deleting
    if File.Exists mlirFile then File.Copy(mlirFile, "/tmp/debug_last.mlir", true)
    for f in [ mlirFile; lowered; llFile ] do
        if File.Exists f then File.Delete f
```

**Recommended pattern:**
```fsharp
// Change from try/finally to explicit cleanup
let result = 
    // ... pipeline steps that return Result ...

match result with
| Ok () ->
    // Clean up all temp files on success
    for f in [ mlirFile; lowered; llFile ] do
        if File.Exists f then File.Delete f
    Ok ()
| Error err ->
    // Preserve .mlir files for debugging; clean only .ll
    // (The .ll file is less useful for debugging MLIR issues)
    if File.Exists llFile then File.Delete llFile
    Error err
```

Then in Program.fs error handling, include the file path:
```fsharp
| Error (Pipeline.MlirOptFailed (code, err)) ->
    eprintfn "mlir-opt failed (exit %d):\n%s" code err
    eprintfn "Preserved MLIR file: %s" mlirFilePath
    1
```

This requires Pipeline.compile to return the temp file path alongside the error. Two approaches:
- (A) Change CompileError to carry the file path: `MlirOptFailed of exitCode: int * stderr: string * mlirFile: string`
- (B) Return a tuple: `Result<unit, CompileError * string option>`

Approach (A) is cleaner since only MLIR-related errors have a file to preserve.

### Anti-Patterns to Avoid

- **Deleting temp files in `finally` block:** The current pattern makes it impossible to preserve files on error. Move cleanup out of `finally`.
- **Swallowing exceptions silently:** The current `with ex ->` in parseProgram catches and retries without surfacing the original error. This is the core problem.
- **DEBUG-only diagnostics to stderr:** The `eprintfn "DEBUG ..."` lines should either be proper warnings or removed. User-facing error messages should not have "DEBUG" prefix.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Parser position info | Custom position tracking | FsLexYacc `lexbuf.EndPos` | The lexbuf already has position info at parse failure time |

**Key insight:** The FsLexYacc parser runtime raises exceptions during `Interpret()`. The `lexbuf.EndPos` at the point of failure contains the position of the last successfully consumed token. This can be captured in the `with ex ->` handler to provide line:col info for parse errors, WITHOUT fixing the upstream span-zeroing bug.

## Common Pitfalls

### Pitfall 1: lexbuf Position After Failure
**What goes wrong:** Assuming `lexbuf.EndPos` is meaningless after a parse error.
**Why it happens:** The survey documents that AST node spans are zeroed, leading to the assumption that NO position info is available.
**How to avoid:** The AST spans are zeroed because the *filtered tokenizer* does not update lexbuf positions during successful parsing. But `setInitialPos` does set the filename, and the lexbuf may have *some* position info at failure time depending on how far parsing got. Test this empirically.
**Warning signs:** `:0:0:` in error output.

**Important finding:** In `parseProgram`, a *second* lexbuf (`lexbuf2`) is created at line 34 and has `setInitialPos` called on it. The custom tokenizer at line 36 does NOT advance this lexbuf (it pulls from the pre-computed array). So `lexbuf2.EndPos` will always be at position 1:0. This means we CANNOT get position info from the lexbuf in the current architecture.

**However:** The `idx` variable (line 32) tells us which token in the array caused the failure. We can use the token index to provide "near token N" info, or we could store position info alongside tokens in the filtered array.

### Pitfall 2: Pipeline.compile Return Type Change
**What goes wrong:** Changing the `CompileError` DU breaks existing pattern matches in Program.fs.
**Why it happens:** Adding fields to DU cases is a breaking change if callers destructure them.
**How to avoid:** Update all pattern matches in Program.fs simultaneously when changing CompileError. There are exactly 3 match arms (lines 189, 192, 195).
**Warning signs:** Compiler errors about incomplete pattern matches.

### Pitfall 3: Temp File Accumulation
**What goes wrong:** Preserving temp files on every error leads to /tmp/ filling up.
**Why it happens:** No cleanup mechanism for preserved files.
**How to avoid:** Only preserve the most relevant file (the input .mlir for mlir-opt failures, the lowered .mlir for translate failures). Consider using a deterministic path (e.g., based on input filename) instead of GetTempFileName so repeated runs overwrite.
**Warning signs:** Many .mlir files in /tmp/.

### Pitfall 4: parseExpr Fallback Also Failing
**What goes wrong:** If both `parseModule` and `parseExpr` fail, the user currently sees the `parseExpr` error, which is often less informative.
**Why it happens:** The `with ex ->` handler calls `parseExpr` without its own try/catch, so if parseExpr throws, that exception propagates up instead of the original.
**How to avoid:** Wrap both calls, surface the original (module) error when both fail.

## Code Examples

### Example 1: Capturing Parser Error with Position Context

```fsharp
// In parseProgram, capture the token index at failure for position context
let parseProgram (src: string) (filename: string) : Ast.Module =
    let filteredTokens = lexAndFilter src filename
    let arr = filteredTokens |> Array.ofList
    let mutable idx = 0
    let moduleResult =
        try
            let lexbuf2 = LexBuffer<char>.FromString src
            Lexer.setInitialPos lexbuf2 filename
            let tokenizer (_: LexBuffer<char>) =
                if idx < arr.Length then
                    let tok = arr.[idx]
                    idx <- idx + 1
                    tok
                else Parser.EOF
            Ok (Parser.parseModule tokenizer lexbuf2)
        with ex ->
            Error (ex, idx)
    match moduleResult with
    | Ok m -> m
    | Error (moduleEx, failIdx) ->
        try
            let expr = parseExpr src filename
            eprintfn "Warning: %s: module parse failed near token %d/%d (%s), fell back to expression" filename failIdx arr.Length moduleEx.Message
            Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)
        with _exprEx ->
            // Surface the original module parse error
            failwithf "%s: parse error near token %d/%d: %s" filename failIdx arr.Length moduleEx.Message
```

### Example 2: Pipeline with Conditional Cleanup

```fsharp
type CompileError =
    | MlirOptFailed   of exitCode: int * stderr: string * mlirFile: string
    | TranslateFailed  of exitCode: int * stderr: string * mlirFile: string
    | ClangFailed      of exitCode: int * stderr: string

let compile (m: MlirModule) (outputPath: string) : Result<unit, CompileError> =
    let mlirFile  = Path.GetTempFileName() + ".mlir"
    let lowered   = Path.GetTempFileName() + ".mlir"
    let llFile    = Path.GetTempFileName() + ".ll"
    let cleanup files = for f in files do if File.Exists f then File.Delete f

    let mlirText = Printer.printModule m
    File.WriteAllText(mlirFile, mlirText)

    let optArgs = sprintf "%s %s -o %s" loweringPasses mlirFile lowered
    match runTool MlirOpt optArgs with
    | Error (code, err) ->
        cleanup [ lowered; llFile ]  // keep mlirFile
        Error (MlirOptFailed (code, err, mlirFile))
    | Ok () ->
    let translateArgs = sprintf "--mlir-to-llvmir %s -o %s" lowered llFile
    match runTool MlirTranslate translateArgs with
    | Error (code, err) ->
        cleanup [ llFile ]  // keep mlirFile and lowered
        Error (TranslateFailed (code, err, lowered))
    | Ok () ->
    // ... clang steps ...
    // On full success, clean everything
    cleanup [ mlirFile; lowered; llFile ]
    Ok ()
```

### Example 3: Program.fs Error Reporting with File Paths

```fsharp
| Error (Pipeline.MlirOptFailed (code, err, mlirFile)) ->
    eprintfn "mlir-opt failed (exit %d):\n%s" code err
    eprintfn "MLIR file preserved at: %s" mlirFile
    1
| Error (Pipeline.TranslateFailed (code, err, mlirFile)) ->
    eprintfn "mlir-translate failed (exit %d):\n%s" code err
    eprintfn "MLIR file preserved at: %s" mlirFile
    1
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Silent fallback (parseModule -> parseExpr) | Preserve original error, warn on fallback | Phase 45 | Users see actual parse errors |
| Unconditional temp file deletion | Conditional cleanup (keep on error) | Phase 45 | Users can inspect failed .mlir |
| DEBUG eprintfn in parser | Structured warning/error messages | Phase 45 | Clean user-facing output |

## Open Questions

1. **Token-to-position mapping**
   - What we know: The `idx` variable tells us which token failed. The filtered token list has token types but no attached positions.
   - What's unclear: Whether we should attempt to compute line:col from the token index (would require storing positions alongside filtered tokens -- a larger change).
   - Recommendation: For Phase 45, use "near token N of M" as position info for parse errors. True line:col for parse errors requires fixing FunLang's span propagation (a larger upstream effort documented in survey/langthree-span-zeroing-fix.md). Mark as a future enhancement.

2. **Deterministic vs random temp file paths**
   - What we know: `Path.GetTempFileName()` creates random names in /tmp.
   - What's unclear: Whether to use a deterministic name based on input file (e.g., `/tmp/langbackend-<basename>.mlir`).
   - Recommendation: Keep random names but add `.langbackend` suffix for easy identification. The error message includes the exact path, so discoverability is not an issue.

3. **Runtime object temp file cleanup**
   - What we know: Pipeline also creates `runtimeObj` (line 99) which is not in the cleanup list.
   - What's unclear: Whether this is already cleaned up elsewhere or is a pre-existing leak.
   - Recommendation: Include `runtimeObj` in cleanup logic during this phase.

## Sources

### Primary (HIGH confidence)
- `src/FunLangCompiler.Cli/Program.fs` -- Parser invocation and fallback logic (lines 29-52)
- `src/FunLangCompiler.Compiler/Pipeline.fs` -- MLIR temp file management (lines 77-114)
- `fslexyacc.runtime/11.3.0 (NuGet) Parsing.fs` -- FsLexYacc exception types (`RecoverableParseError`, `failwith "parse error"`)
- `survey/langthree-span-zeroing-fix.md` -- Upstream span zeroing analysis

### Secondary (MEDIUM confidence)
- `.planning/phases/44-error-location-foundation/44-VERIFICATION.md` -- Phase 44 findings on span zeroing

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - No new dependencies, purely code changes
- Architecture: HIGH - Both changes are small, well-scoped, and the current code is fully understood
- Pitfalls: HIGH - All pitfalls identified from direct code reading, not speculation

**Research date:** 2026-03-31
**Valid until:** Indefinite (codebase-specific findings, not library-version-dependent)
