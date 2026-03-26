module Program

open System

[<EntryPoint>]
let main argv =
    let args = argv |> Array.toList
    let rec findOutput = function
        | "-o" :: out :: _ -> Some out
        | _ :: rest -> findOutput rest
        | [] -> None

    match findOutput args with
    | None ->
        eprintfn "Usage: langbackend <inputfile> -o <outputbinary>"
        1
    | Some outputPath ->
        match Pipeline.compile MlirIR.return42Module outputPath with
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
