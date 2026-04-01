# Phase 40: Multi-file Import - Research

**Researched:** 2026-03-30
**Domain:** Compiler-side AST flattening / source text expansion for `open "file.fun"`
**Confidence:** HIGH

## Summary

Phase 40 adds `open "file.fun"` support to the FunLangCompiler compiler (no C runtime changes). The AST node `Ast.Decl.FileImportDecl of path: string * Span` already exists in the shared `LangThree/src/LangThree/Ast.fs` and the parser already emits it. The backend's `Elaboration.fs` currently silently drops `FileImportDecl` in both `prePassDecls` and `extractMainExpr`. The fix is entirely in the compiler pipeline: before elaboration, recursively expand each `FileImportDecl` into the declarations of the imported file, tracking a visited-file set to detect cycles.

The LangThree interpreter (`Prelude.fs`) already implements the complete reference pattern: a `HashSet<string>` loading stack for cycle detection, relative path resolution via `Path.GetFullPath(Path.Combine(dir, importPath))`, recursive parse-and-merge, and a `finally`-block to pop the stack on exit. The backend can follow the same pattern but at the Decl-list level (not at evaluation time) — expanding `FileImportDecl` nodes into inline `Decl list` before `elaborateProgram` runs.

The implementation is a single new function (`expandImports`) that walks a `Decl list`, replaces each `FileImportDecl` with the recursively-expanded declarations of the target file, and errors clearly on cycles. Because the expansion happens before `prePassDecls` and `extractMainExpr`, no other part of Elaboration.fs needs changes.

**Primary recommendation:** Add `expandImports` in `Program.fs` (CLI) to recursively flatten `FileImportDecl` nodes into inline declarations before calling `parseProgram`/`elaborateProgram`. Do not modify `Elaboration.fs` internals.

## Standard Stack

This phase uses no new libraries. All tools are already in the project.

### Core
| Tool | Version | Purpose | Why Standard |
|------|---------|---------|--------------|
| `System.IO.Path` | .NET 10 | Absolute path resolution, `GetFullPath`, `Combine`, `GetDirectoryName` | Already used in Program.fs prelude loader |
| `System.Collections.Generic.HashSet<string>` | .NET 10 | O(1) visited-file set for cycle detection | Same as LangThree Prelude.fs `fileLoadingStack` |
| `System.IO.File.Exists` / `File.ReadAllText` | .NET 10 | File presence check and source load | Already used in Program.fs |
| `parseProgram` (existing CLI function) | — | Parse a .fun source string into `Ast.Module` | Already exists in Program.fs |

### Supporting
| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `System.IO.Path.IsPathRooted` | .NET 10 | Distinguish absolute vs relative import paths | Handles edge case where user writes `open "/abs/path.fun"` |

**Installation:** No new packages needed.

## Architecture Patterns

### Recommended Implementation Location

The expansion happens in `Program.fs` (FunLangCompiler.Cli), not inside `Elaboration.fs`. Reason: the expansion is I/O-bound (reads files) and the compiler proper (`Elaboration.fs`) is pure. Keeping I/O at the boundary is cleaner.

```
FunLangCompiler.Cli/
└── Program.fs          # Add expandImports + resolveImportPath here

FunLangCompiler.Compiler/
└── Elaboration.fs      # No changes needed — prePassDecls already handles unknown decls with | _ -> ()
```

### Pattern 1: AST-level Import Expansion (Before Elaboration)

**What:** Walk the `Decl list` of the parsed module. For each `FileImportDecl(path, _)`, resolve the path, parse the target file, recursively expand its imports, and inline the resulting declarations. Use a `HashSet<string>` of absolute paths to detect cycles.

**When to use:** Always. Expansion happens once per compilation, before `elaborateProgram`.

**Example:**
```fsharp
// In Program.fs — called after parseProgram, before elaborateProgram

/// Resolve import path: relative to importing file's directory, absolute as-is.
let private resolveImportPath (importPath: string) (importingFile: string) : string =
    if System.IO.Path.IsPathRooted importPath then importPath
    else
        let dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(importingFile))
        System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, importPath))

/// Recursively expand FileImportDecl nodes into inline declarations.
/// visitedFiles: absolute paths currently on the import stack (cycle detection).
/// Returns the expanded Decl list with FileImportDecl nodes replaced.
let rec private expandImports (visitedFiles: System.Collections.Generic.HashSet<string>)
                               (currentFile: string)
                               (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun decl ->
        match decl with
        | Ast.Decl.FileImportDecl(importPath, span) ->
            let resolvedPath = resolveImportPath importPath currentFile
            if visitedFiles.Contains resolvedPath then
                failwithf "Circular import detected: %s is already being imported" resolvedPath
            if not (System.IO.File.Exists resolvedPath) then
                failwithf "Import not found: %s (resolved from %s in %s)" importPath resolvedPath currentFile
            visitedFiles.Add resolvedPath |> ignore
            try
                let src = System.IO.File.ReadAllText resolvedPath
                let importedModule = parseProgram src resolvedPath
                let importedDecls =
                    match importedModule with
                    | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) | Ast.NamespacedModule(_, ds, _) -> ds
                    | Ast.EmptyModule _ -> []
                // Recursively expand imports within the imported file
                expandImports visitedFiles resolvedPath importedDecls
            finally
                visitedFiles.Remove resolvedPath |> ignore
        | Ast.Decl.ModuleDecl(name, innerDecls, s) ->
            // Expand imports inside module bodies too
            [Ast.Decl.ModuleDecl(name, expandImports visitedFiles currentFile innerDecls, s)]
        | other -> [other])
```

**Integration in `main`:**
```fsharp
// After parsing the top-level file:
let ast = parseProgram combinedSrc inputPath
let expandedAst =
    let decls =
        match ast with
        | Ast.Module(ds, s) ->
            let visited = System.Collections.Generic.HashSet<string>()
            visited.Add(System.IO.Path.GetFullPath(inputPath)) |> ignore
            Ast.Module(expandImports visited inputPath ds, s)
        | other -> other
    decls
let mlirMod = Elaboration.elaborateProgram expandedAst
```

### Pattern 2: Cycle Detection with HashSet Stack

**What:** The `HashSet<string>` carries the current import chain (files actively being loaded). Push before recursing, pop in `finally`. The check `visitedFiles.Contains resolvedPath` at entry detects cycles.

**When to use:** Every recursive call to `expandImports`.

**Example:**
```fsharp
// Source: LangThree/src/LangThree/Prelude.fs lines 79-114
let private fileLoadingStack = System.Collections.Generic.HashSet<string>()

// At entry:
if fileLoadingStack.Contains resolvedPath then
    raise (TypeException { Kind = CircularModuleDependency [resolvedPath]; ... })
fileLoadingStack.Add resolvedPath |> ignore
try
    // ... process ...
finally
    fileLoadingStack.Remove resolvedPath |> ignore
```

### Anti-Patterns to Avoid

- **Cycle detection via Set not stack:** A `Set<string>` of all-ever-seen files (like a cache) would prevent legitimate re-use where A and B both import C. Use a stack (`HashSet` pushed/popped per traversal path), not a global visited set.
- **String concatenation instead of AST expansion:** The prelude uses `string.concat "\n"` (source text concatenation). For user imports this is fragile — if the imported file uses `open "relative.fun"` paths, those paths resolve against the wrong directory after concatenation. Expand at the AST/Decl level with the file path tracked per node.
- **Modifying Elaboration.fs internals:** Do not change `prePassDecls` or `extractMainExpr` to understand `FileImportDecl`. Expand before those functions run so they only ever see clean flat Decl lists.
- **Skipping the `finally` pop:** If the finally is missing, a file that fails to parse leaves itself on the stack, making all subsequent imports of it appear as cycles.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Path resolution | Manual string splitting | `System.IO.Path.GetFullPath(Path.Combine(dir, importPath))` | Handles `../`, `.`, symlinks, platform separators |
| File existence check | Try/catch ReadAllText | `File.Exists` before `ReadAllText` | Gives cleaner error message with the resolved path |
| Cycle detection | Counting visits | `HashSet<string>` push/pop | O(1), handles diamond imports (A→B, A→C, both→D) correctly |

**Key insight:** Diamond imports (A imports B and C, both import D) are NOT cycles. Only a file appearing in its own transitive import chain is a cycle. The push/pop HashSet correctly distinguishes these cases; a simple "already visited" set does not.

## Common Pitfalls

### Pitfall 1: Diamond Import False Positive
**What goes wrong:** A.fun imports both B.fun and C.fun; both B.fun and C.fun import D.fun. If using a global "already-seen" set, D.fun appears to be a cycle when C processes it (B already added it).
**Why it happens:** Confusing "currently on the stack" with "ever visited".
**How to avoid:** Use a per-traversal-path `HashSet` that is pushed/popped (not a cache). D.fun is removed from the set when B.fun finishes loading it, so C.fun can load it cleanly.
**Warning signs:** Test where A imports B and C, both import shared D — if D's bindings appear only once or an error fires, the set model is wrong.

### Pitfall 2: Relative Path Resolves Against CWD Not Importer
**What goes wrong:** `open "utils.fun"` inside `/project/src/main.fun` resolves to `./utils.fun` (CWD) instead of `/project/src/utils.fun`.
**Why it happens:** Using `System.IO.Path.GetFullPath(importPath)` without prepending the importing file's directory.
**How to avoid:** Always extract `GetDirectoryName(GetFullPath(currentFile))` and `Combine` with the import path.
**Warning signs:** Import works when CWD equals the file's directory but fails when running from another location.

### Pitfall 3: Prelude Source String Concat Plus FileImportDecl
**What goes wrong:** The current pipeline concatenates prelude source + user source into `combinedSrc`, then parses as one module. After expansion, imported files get their own absolute `inputPath` passed to `parseProgram`, but the `combinedSrc` prefix (prelude text) is in the outer file's span — not in imported files.
**Why it happens:** Imported files should be parsed standalone (no prelude prefix), because the prelude bindings are already injected via the outer `combinedSrc`.
**How to avoid:** In `expandImports`, parse imported files with `parseProgram src resolvedPath` (no prelude prefix). Prelude is already in the outer declaration list.
**Warning signs:** Duplicate `None`/`Some` constructor definitions at link time if prelude is concatenated for imports too.

### Pitfall 4: ModuleDecl Inner Declarations Not Expanded
**What goes wrong:** `open "utils.fun"` inside a `module M = ...` block is not expanded because `expandImports` doesn't recurse into `ModuleDecl` inner decls.
**Why it happens:** Only top-level `FileImportDecl` nodes are handled.
**How to avoid:** Add a `ModuleDecl` arm in `expandImports` that recursively expands inner decls (see Pattern 1 above).
**Warning signs:** Import inside a module silently does nothing.

### Pitfall 5: Error Message Missing Resolved Path
**What goes wrong:** Error says `Import not found: utils.fun` — user cannot tell which directory was searched.
**Why it happens:** Error uses the raw `importPath` string from the AST.
**How to avoid:** Include both `importPath` (what the user wrote) and `resolvedPath` (what was searched) in the error message.
**Warning signs:** User gets import-not-found error with no actionable path information.

## Code Examples

### Resolve Import Path (from LangThree reference)
```fsharp
// Source: LangThree/src/LangThree/TypeCheck.fs lines 727-732
let resolveImportPath (importPath: string) (importingFile: string) : string =
    if Path.IsPathRooted importPath then
        importPath
    else
        let dir = Path.GetDirectoryName(Path.GetFullPath(importingFile))
        Path.GetFullPath(Path.Combine(dir, importPath))
```

### Cycle Detection (from LangThree reference)
```fsharp
// Source: LangThree/src/LangThree/Prelude.fs lines 79-114
let private fileLoadingStack = System.Collections.Generic.HashSet<string>()

let rec loadAndTypeCheckFileImpl resolvedPath ... =
    if fileLoadingStack.Contains resolvedPath then
        raise (TypeException { Kind = CircularModuleDependency [resolvedPath]; ... })
    if not (File.Exists resolvedPath) then
        raise (TypeException { Kind = UnresolvedModule resolvedPath; ... })
    fileLoadingStack.Add resolvedPath |> ignore
    try
        // ... load, parse, merge ...
    finally
        fileLoadingStack.Remove resolvedPath |> ignore
```

### Test File Format (matching existing .flt conventions)
```
// --- Command: bash -c 'mkdir -p /tmp/langtest_XXXXXX && ...'
// --- Input:
open "utils.fun"
let _ = println (to_string (add 1 2))
// --- Output:
3
0
```

For multi-file tests, use a temp directory + write both files before compiling:
```
// --- Command: bash -c 'D=$(mktemp -d) && cat > $D/utils.fun << '"'"'EOF'"'"'\nlet add a b = a + b\nEOF\nOUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- $D/main.fun -o $OUTBIN && $OUTBIN; echo $?; rm -rf $D $OUTBIN'
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| String concat for prelude | String concat works for static known-order files | Phase 35 | Works because prelude order is fixed |
| FileImportDecl silently dropped | FileImportDecl expanded at AST level | Phase 40 (this phase) | Enables user-defined multi-file programs |

**Deprecated/outdated:**
- String-concat approach for user imports: fragile with relative paths inside imported files. AST-level expansion is the correct pattern.

## Open Questions

1. **Test harness for multi-file .flt tests**
   - What we know: Current `.flt` tests use a single input block. Multi-file tests need temp directory setup.
   - What's unclear: Whether the existing test runner supports creating multiple files in a temp dir, or if a shell heredoc approach is needed.
   - Recommendation: Use the bash heredoc pattern in the Command line to write utils.fun to a temp dir, then compile and run main.fun. Keep it simple; no test framework changes needed.

2. **Should expandImports recurse into NamespaceDecl?**
   - What we know: `NamespaceDecl` has inner decls just like `ModuleDecl`. If a user nests a `FileImportDecl` inside a `NamespaceDecl`, it would be silently dropped.
   - What's unclear: Whether real-world usage puts `open "file"` inside namespaces.
   - Recommendation: Add a `NamespaceDecl` arm to `expandImports` matching the `ModuleDecl` arm for completeness (two-line addition).

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Prelude.fs` — Full reference implementation of file import loading, cycle detection, path resolution
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/TypeCheck.fs` lines 725-733 — `resolveImportPath` function
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` lines 362-365 — `FileImportDecl` AST node definition
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` lines 3630-3733 — `prePassDecls`, `flattenDecls`, `extractMainExpr` — all silently ignore `FileImportDecl`
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Cli/Program.fs` — Current pipeline: string concat prelude → parseProgram → elaborateProgram

### Secondary (MEDIUM confidence)
- LangThree Parser.fs lines 3301/3313 — confirms parser emits `FileImportDecl` for `open "string"` syntax

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new libraries, all existing .NET 10 APIs already in project
- Architecture: HIGH — reference implementation exists verbatim in LangThree; pattern is identical
- Pitfalls: HIGH — all pitfalls sourced from reading actual code paths, not speculation

**Research date:** 2026-03-30
**Valid until:** 2026-04-30 (compiler architecture is stable)
