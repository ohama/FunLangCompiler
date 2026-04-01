module Program

open System
open FSharp.Text.Lexing
open FunLang.IndentFilter

let parseExpr (src: string) (filename: string) : Ast.Expr =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    Parser.start Lexer.tokenize lexbuf

// Tokenize and apply IndentFilter, capturing lexbuf positions per token.
let private lexAndFilter (src: string) (filename: string) : PositionedToken list =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    let rec collect () =
        let startPos = lexbuf.StartPos
        let tok = Lexer.tokenize lexbuf
        let endPos = lexbuf.EndPos
        if tok = Parser.EOF then
            [{ Token = Parser.EOF; StartPos = startPos; EndPos = endPos }]
        else
            { Token = tok; StartPos = startPos; EndPos = endPos } :: collect ()
    let rawTokens = collect ()
    filterPositioned defaultConfig rawTokens

// Parse a source file as a module with position-preserving IndentFilter.
// Falls back to parseExpr for bare-expression inputs.
let parseProgram (src: string) (filename: string) : Ast.Module =
    let filteredTokens = lexAndFilter src filename
    let arr = filteredTokens |> Array.ofList
    let mutable idx = 0
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
                lastParsedPos <- pt.StartPos
                pt.Token
            else Parser.EOF
        Parser.parseModule tokenizer lexbuf2
    with firstEx ->
        // parseModule failed — try falling back to single-expression mode
        try
            let expr = parseExpr src filename
            let exprSpan = Ast.spanOf expr
            Ast.Module([Ast.Decl.LetDecl("_", expr, exprSpan)], exprSpan)
        with _ ->
            // Both parseModule and parseExpr failed — surface the original error
            // with position information from the last consumed token
            let posMsg =
                if lastParsedPos = FSharp.Text.Lexing.Position.Empty then
                    firstEx.Message
                else
                    sprintf "%s:%d:%d: parse error" lastParsedPos.FileName lastParsedPos.Line lastParsedPos.Column
            failwith posMsg

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
                    | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) -> ds
                    | Ast.EmptyModule _ -> []
                expandImports visitedFiles resolvedPath importedDecls
            finally
                visitedFiles.Remove resolvedPath |> ignore
        | Ast.Decl.ModuleDecl(name, innerDecls, s) ->
            [Ast.Decl.ModuleDecl(name, expandImports visitedFiles currentFile innerDecls, s)]
        | other -> [other])

[<EntryPoint>]
let main argv =
    let args = argv |> Array.toList

    // Parse flags: -o <output>, -O0/-O1/-O2/-O3
    let rec parseArgs args outputOpt optLevel =
        match args with
        | "-o" :: out :: rest -> parseArgs rest (Some out) optLevel
        | "-O0" :: rest -> parseArgs rest outputOpt 0
        | "-O1" :: rest -> parseArgs rest outputOpt 1
        | "-O2" :: rest -> parseArgs rest outputOpt 2
        | "-O3" :: rest -> parseArgs rest outputOpt 3
        | x :: rest ->
            let (o, ol, r) = parseArgs rest outputOpt optLevel
            (o, ol, x :: r)
        | [] -> (outputOpt, optLevel, [])

    let (outputOpt, optLevel, remaining) = parseArgs args None 2

    match remaining with
    | [] ->
        eprintfn "Usage: fnc <file.fun> [-o <output>] [-O0|-O1|-O2|-O3]"
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
                    let ordered = [| "Typeclass.fun"; "Core.fun"; "Option.fun"; "Result.fun"; "String.fun"; "Char.fun";
                                     "Hashtable.fun"; "HashSet.fun"; "MutableList.fun"; "Queue.fun";
                                     "StringBuilder.fun"; "List.fun"; "Array.fun" |]
                    ordered
                    |> Array.choose (fun f ->
                        let path = System.IO.Path.Combine(dir, f)
                        if System.IO.File.Exists path then Some (System.IO.File.ReadAllText path) else None)
                    |> String.concat "\n"

            let ast =
                if preludeSrc = "" then
                    parseProgram src inputPath
                else
                    let preludeAst = parseProgram preludeSrc "<prelude>"
                    let preludeDecls =
                        match preludeAst with
                        | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) -> ds
                        | Ast.EmptyModule _ -> []
                    let userAst = parseProgram src inputPath
                    let (userDecls, userSpan) =
                        match userAst with
                        | Ast.Module(ds, s) | Ast.NamedModule(_, ds, s) -> (ds, s)
                        | Ast.EmptyModule s -> ([], s)
                    Ast.Module(preludeDecls @ userDecls, userSpan)
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
                | other -> other
            // Phase 52: Transform typeclass declarations before elaboration
            let tcAst =
                match expandedAst with
                | Ast.Module(ds, s) -> Ast.Module(Elaboration.elaborateTypeclasses ds, s)
                | Ast.NamedModule(n, ds, s) -> Ast.NamedModule(n, Elaboration.elaborateTypeclasses ds, s)
                | Ast.EmptyModule s -> Ast.EmptyModule s
            let mlirMod = Elaboration.elaborateProgram tcAst
            match Pipeline.compile mlirMod outputPath optLevel with
            | Ok () ->
                0
            | Error (Pipeline.MlirOptFailed (code, err, mlirFile)) ->
                eprintfn "[Compile] mlir-opt failed (exit %d):\n%s\nMLIR file preserved: %s" code err mlirFile
                1
            | Error (Pipeline.TranslateFailed (code, err, mlirFile)) ->
                eprintfn "[Compile] mlir-translate failed (exit %d):\n%s\nMLIR file preserved: %s" code err mlirFile
                1
            | Error (Pipeline.ClangFailed (code, err)) ->
                eprintfn "[Compile] clang failed (exit %d):\n%s" code err
                1
        with ex ->
            let msg = ex.Message
            if msg.StartsWith("[Elaboration]") then
                eprintfn "%s" msg
            elif msg.Contains("parse error") || msg.Contains("Parse error") then
                eprintfn "[Parse] %s" msg
            else
                eprintfn "[Elaboration] %s" msg
            1
