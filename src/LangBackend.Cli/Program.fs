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
            let ast = parseProgram src inputPath
            let mlirMod = Elaboration.elaborateProgram ast
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
