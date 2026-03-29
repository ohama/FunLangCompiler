module Program

open System
open FSharp.Text.Lexing

let parseExpr (src: string) (filename: string) : Ast.Expr =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    Parser.start Lexer.tokenize lexbuf

// Tokenize and apply IndentFilter, returning a filtered token list.
// Required for parseModule: the raw Lexer emits NEWLINE tokens that the
// parser grammar expects to be converted to INDENT/DEDENT by IndentFilter.
let private lexAndFilter (src: string) (filename: string) : Parser.token list =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    let rec collect () =
        let tok = Lexer.tokenize lexbuf
        if tok = Parser.EOF then [Parser.EOF]
        else tok :: collect ()
    let rawTokens = collect ()
    LangThree.IndentFilter.filter LangThree.IndentFilter.defaultConfig rawTokens
    |> Seq.toList

// Phase 16: Parse a source file as a module (Ast.Module).
// Uses IndentFilter so that multi-line indented source (with NEWLINE/INDENT/DEDENT)
// is handled correctly. Falls back to parseExpr and wraps in a synthetic Module for
// backward compatibility with bare-expression inputs (e.g. "42", "true").
let parseProgram (src: string) (filename: string) : Ast.Module =
    try
        let filteredTokens = lexAndFilter src filename
        let lexbuf2 = LexBuffer<char>.FromString src
        Lexer.setInitialPos lexbuf2 filename
        let mutable idx = 0
        let tokenizer (_: LexBuffer<char>) =
            if idx < filteredTokens.Length then
                let tok = filteredTokens.[idx]
                idx <- idx + 1
                tok
            else Parser.EOF
        Parser.parseModule tokenizer lexbuf2
    with _ ->
        // Bare expression input (not a valid top-level declaration): parse as Expr
        // and wrap in a synthetic Module so elaborateProgram can process it uniformly.
        let expr = parseExpr src filename
        Ast.Module([Ast.Decl.LetDecl("_", expr, Ast.unknownSpan)], Ast.unknownSpan)

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
        | Ast.Decl.FileImportDecl(importPath, _span) ->
            let resolvedPath = resolveImportPath importPath currentFile
            if visitedFiles.Contains resolvedPath then
                failwithf "Circular import detected: %s is already being imported" resolvedPath
            if not (System.IO.File.Exists resolvedPath) then
                failwithf "Import not found: %s (resolved to %s from %s)" importPath resolvedPath currentFile
            visitedFiles.Add resolvedPath |> ignore
            try
                let src = System.IO.File.ReadAllText resolvedPath
                let importedModule = parseProgram src resolvedPath
                let importedDecls =
                    match importedModule with
                    | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) | Ast.NamespacedModule(_, ds, _) -> ds
                    | Ast.EmptyModule _ -> []
                expandImports visitedFiles resolvedPath importedDecls
            finally
                visitedFiles.Remove resolvedPath |> ignore
        | Ast.Decl.ModuleDecl(name, innerDecls, s) ->
            [Ast.Decl.ModuleDecl(name, expandImports visitedFiles currentFile innerDecls, s)]
        | Ast.Decl.NamespaceDecl(name, innerDecls, s) ->
            [Ast.Decl.NamespaceDecl(name, expandImports visitedFiles currentFile innerDecls, s)]
        | other -> [other])

[<EntryPoint>]
let main argv =
    let args = argv |> Array.toList

    // Parse -o flag if present
    let rec parseArgs args =
        match args with
        | "-o" :: out :: rest -> (Some out, rest)
        | x :: rest ->
            let (o, r) = parseArgs rest
            (o, x :: r)
        | [] -> (None, [])

    let (outputOpt, remaining) = parseArgs args

    match remaining with
    | [] ->
        eprintfn "Usage: langbackend <file.lt> [-o <output>]"
        1
    | inputPath :: _ ->
        // Derive output path: explicit -o takes priority, else strip .lt from filename
        let outputPath =
            match outputOpt with
            | Some o -> o
            | None ->
                let basename = System.IO.Path.GetFileNameWithoutExtension(inputPath)
                basename

        // Check input file exists
        if not (System.IO.File.Exists(inputPath)) then
            eprintfn "Error: file not found: %s" inputPath
            1
        else

        // Parse, elaborate, compile — with error handling
        try
            let src = System.IO.File.ReadAllText(inputPath)

            // Phase 35: Auto-load Prelude modules
            let preludeSrc =
                let findPreludeDir () =
                    // Search for Prelude/ starting from the input file's directory and walking up.
                    // This ensures prelude loading is scoped to the project that owns the input file,
                    // not the arbitrary CWD. Users place Prelude/ in their project root; tools
                    // that compile temp files in /tmp/ do not pick up a project's Prelude/.
                    let inputDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(inputPath))
                    let rec walkUp (dir: string) =
                        if dir = null || dir = "" then ""
                        else
                            let candidate = System.IO.Path.Combine(dir, "Prelude")
                            if System.IO.Directory.Exists candidate then candidate
                            else
                                let parent = System.IO.Path.GetDirectoryName(dir)
                                if parent = dir then ""  // reached filesystem root
                                else walkUp parent
                    let fromInput = walkUp inputDir
                    if fromInput <> "" then fromInput
                    else
                        // Fallback: check assembly directory/Prelude
                        let asmDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location)
                        let asmCandidate = System.IO.Path.Combine(asmDir, "Prelude")
                        if System.IO.Directory.Exists asmCandidate then asmCandidate
                        else ""
                let dir = findPreludeDir ()
                if dir = "" then ""
                else
                    // Explicit load order: Option/Result before List (List uses None/Some constructors)
                    let ordered = [| "Option.fun"; "Result.fun"; "String.fun"; "Char.fun";
                                     "Hashtable.fun"; "StringBuilder.fun"; "List.fun"; "Array.fun" |]
                    ordered
                    |> Array.choose (fun f ->
                        let path = System.IO.Path.Combine(dir, f)
                        if System.IO.File.Exists path then Some (System.IO.File.ReadAllText path) else None)
                    |> String.concat "\n"

            let combinedSrc = if preludeSrc = "" then src else preludeSrc + "\n" + src
            let ast = parseProgram combinedSrc inputPath
            let expandedAst =
                match ast with
                | Ast.Module(ds, s) ->
                    let visited = System.Collections.Generic.HashSet<string>()
                    visited.Add(System.IO.Path.GetFullPath(inputPath)) |> ignore
                    Ast.Module(expandImports visited inputPath ds, s)
                | Ast.NamedModule(nm, ds, s) ->
                    let visited = System.Collections.Generic.HashSet<string>()
                    visited.Add(System.IO.Path.GetFullPath(inputPath)) |> ignore
                    Ast.NamedModule(nm, expandImports visited inputPath ds, s)
                | Ast.NamespacedModule(nm, ds, s) ->
                    let visited = System.Collections.Generic.HashSet<string>()
                    visited.Add(System.IO.Path.GetFullPath(inputPath)) |> ignore
                    Ast.NamespacedModule(nm, expandImports visited inputPath ds, s)
                | other -> other
            let mlirMod = Elaboration.elaborateProgram expandedAst
            match Pipeline.compile mlirMod outputPath with
            | Ok () ->
                0
            | Error (Pipeline.MlirOptFailed (code, err)) ->
                eprintfn "mlir-opt failed (exit %d):\n%s" code err
                1
            | Error (Pipeline.TranslateFailed (code, err)) ->
                eprintfn "mlir-translate failed (exit %d):\n%s" code err
                1
            | Error (Pipeline.ClangFailed (code, err)) ->
                eprintfn "clang failed (exit %d):\n%s" code err
                1
        with ex ->
            eprintfn "Error: %s" ex.Message
            1
