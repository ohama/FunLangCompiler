module Pipeline

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open MlirIR

/// Resolve an LLVM tool binary by name.
/// Search order:
///   1. $LLVM_BIN_DIR/<name>            (explicit override)
///   2. /opt/homebrew/opt/llvm/bin/<name> (macOS Homebrew)
///   3. /usr/local/bin/<name>            (Linux / manual install)
let private resolveTool (name: string) : string =
    let candidates =
        [ let envDir = Environment.GetEnvironmentVariable("LLVM_BIN_DIR")
          if not (String.IsNullOrEmpty envDir) then
              yield Path.Combine(envDir, name)
          yield Path.Combine("/opt/homebrew/opt/llvm/bin", name)
          yield Path.Combine("/usr/local/bin", name) ]
    candidates
    |> List.tryFind File.Exists
    |> Option.defaultValue (Path.Combine("/usr/local/bin", name))  // last resort; will fail with clear message

let private MlirOpt       = resolveTool "mlir-opt"
let private MlirTranslate = resolveTool "mlir-translate"
let private Clang         = resolveTool "clang"

// Platform-aware Boehm GC link flags.
// macOS (Homebrew): bdw-gc installs to /opt/homebrew/opt/bdw-gc/lib (not on default linker path).
// Linux: system install at /usr/lib; -lgc alone is sufficient.
let private gcLinkFlags =
    if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
        "-L/opt/homebrew/opt/bdw-gc/lib -lgc"
    else
        "-lgc"

// Path to lang_runtime.c — lives alongside Pipeline.fs in the compiler source directory.
let private runtimeSrc =
    Path.Combine(__SOURCE_DIRECTORY__, "lang_runtime.c")

// Platform-aware GC include flag for compiling lang_runtime.c.
// macOS (Homebrew): bdw-gc header is in /opt/homebrew/opt/bdw-gc/include.
// Linux: GC header is on default include path.
let private gcIncludeFlag =
    if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
        "-I/opt/homebrew/opt/bdw-gc/include"
    else
        ""

type CompileError =
    | MlirOptFailed   of exitCode: int * stderr: string * mlirFile: string
    | TranslateFailed of exitCode: int * stderr: string * mlirFile: string
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

/// Compile an MlirModule to a native binary at outputPath.
/// optimizationLevel: 0=none, 1=basic, 2=default, 3=aggressive (maps to clang -O0/-O1/-O2/-O3)
/// Uses temp files for intermediate stages; cleans them up on success or failure.
let compile (m: MlirModule) (outputPath: string) (optimizationLevel: int) : Result<unit, CompileError> =
    let optFlag = sprintf "-O%d" (max 0 (min 3 optimizationLevel))
    let mlirFile  = Path.GetTempFileName() + ".mlir"
    let lowered   = Path.GetTempFileName() + ".mlir"
    let llFile    = Path.GetTempFileName() + ".ll"

    // Step 1: Serialize MlirModule to .mlir text
    let mlirText = Printer.printModule m
    File.WriteAllText(mlirFile, mlirText)

    let cleanup keepMlir =
        // On failure, preserve mlirFile for debugging
        if not keepMlir then
            if File.Exists mlirFile then File.Delete mlirFile
        for f in [ lowered; llFile ] do
            if File.Exists f then File.Delete f

    // Step 2: Lower with mlir-opt (arith→cf→func→llvm + reconcile)
    let optArgs = sprintf "%s %s -o %s" loweringPasses mlirFile lowered
    match runTool MlirOpt optArgs with
    | Error (code, err) ->
        cleanup true
        Error (MlirOptFailed (code, err, mlirFile))
    | Ok () ->

    // Step 3: Translate to LLVM IR
    let translateArgs = sprintf "--mlir-to-llvmir %s -o %s" lowered llFile
    match runTool MlirTranslate translateArgs with
    | Error (code, err) ->
        cleanup true
        Error (TranslateFailed (code, err, mlirFile))
    | Ok () ->

    // Step 4: Compile lang_runtime.c to a temp object file (with optimization)
    let runtimeObj = Path.ChangeExtension(llFile, ".runtime.o")
    let compileRuntimeArgs = sprintf "-c %s %s %s -o %s" optFlag runtimeSrc gcIncludeFlag runtimeObj
    match runTool Clang compileRuntimeArgs with
    | Error (code, err) ->
        cleanup false
        Error (ClangFailed (code, err))
    | Ok () ->

    // Step 5: Compile to native binary (link Boehm GC and runtime object, with optimization)
    let clangArgs = sprintf "%s -Wno-override-module %s %s %s -o %s" optFlag llFile runtimeObj gcLinkFlags outputPath
    match runTool Clang clangArgs with
    | Error (code, err) ->
        cleanup false
        Error (ClangFailed (code, err))
    | Ok () ->
        cleanup false
        Ok ()
