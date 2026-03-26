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

    let rec parseArgs args =
        match args with
        | "-o" :: out :: rest -> (Some out, rest)
        | x :: rest ->
            let (o, r) = parseArgs rest
            (o, x :: r)
        | [] -> (None, [])

    let (outputOpt, remaining) = parseArgs args

    match outputOpt, remaining with
    | None, _ ->
        eprintfn "Usage: langbackend <inputfile> -o <outputbinary>"
        1
    | _, [] ->
        eprintfn "Usage: langbackend <inputfile> -o <outputbinary>"
        1
    | Some outputPath, inputPath :: _ ->
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
