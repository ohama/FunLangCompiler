module Pipeline

open System.Diagnostics
open System.IO
open MlirIR

// Absolute tool paths — verified on this machine (LLVM 20.1.4)
[<Literal>]
let private MlirOpt       = "/usr/local/bin/mlir-opt"
[<Literal>]
let private MlirTranslate = "/usr/local/bin/mlir-translate"
[<Literal>]
let private Clang         = "/usr/local/bin/clang"

type CompileError =
    | MlirOptFailed   of exitCode: int * stderr: string
    | TranslateFailed of exitCode: int * stderr: string
    | ClangFailed     of exitCode: int * stderr: string

/// Run an external tool and wait for completion.
/// Returns Ok () on exit 0, Error (exitCode, stderr) otherwise.
let private runTool (program: string) (args: string) : Result<unit, int * string> =
    let psi = ProcessStartInfo(program, args)
    psi.RedirectStandardError <- true
    psi.UseShellExecute        <- false
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    // Read stderr to end BEFORE WaitForExit to avoid deadlock on large output
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode = 0 then Ok ()
    else Error (proc.ExitCode, stderr)

// LLVM 20 pass order: arith BEFORE func (PR #120548)
// reconcile-unrealized-casts MUST be last
let private loweringPasses =
    "--convert-arith-to-llvm --convert-cf-to-llvm --convert-func-to-llvm --reconcile-unrealized-casts"

/// Compile an MlirModule to a native ELF binary at outputPath.
/// Uses temp files for intermediate stages; cleans them up on success or failure.
let compile (m: MlirModule) (outputPath: string) : Result<unit, CompileError> =
    let mlirFile  = Path.GetTempFileName() + ".mlir"
    let lowered   = Path.GetTempFileName() + ".mlir"
    let llFile    = Path.GetTempFileName() + ".ll"
    try
        // Step 1: Serialize MlirModule to .mlir text
        let mlirText = Printer.printModule m
        File.WriteAllText(mlirFile, mlirText)

        // Step 2: Lower with mlir-opt (arith→cf→func→llvm + reconcile)
        let optArgs = sprintf "%s %s -o %s" loweringPasses mlirFile lowered
        match runTool MlirOpt optArgs with
        | Error (code, err) -> Error (MlirOptFailed (code, err))
        | Ok () ->

        // Step 3: Translate to LLVM IR
        let translateArgs = sprintf "--mlir-to-llvmir %s -o %s" lowered llFile
        match runTool MlirTranslate translateArgs with
        | Error (code, err) -> Error (TranslateFailed (code, err))
        | Ok () ->

        // Step 4: Compile to native binary
        let clangArgs = sprintf "-Wno-override-module %s -o %s" llFile outputPath
        match runTool Clang clangArgs with
        | Error (code, err) -> Error (ClangFailed (code, err))
        | Ok () -> Ok ()
    finally
        for f in [ mlirFile; lowered; llFile ] do
            if File.Exists f then File.Delete f
