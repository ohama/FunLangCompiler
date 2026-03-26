module Program

open System
open FSharp.Text.Lexing

let parseExpr (src: string) (filename: string) : Ast.Expr =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    Parser.start Lexer.tokenize lexbuf

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
            let expr = parseExpr src inputPath
            let mlirMod = Elaboration.elaborateModule expr
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
