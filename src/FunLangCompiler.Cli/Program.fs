module Program

open System
open System.IO
open System.Diagnostics
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
    if Path.IsPathRooted importPath then importPath
    else
        let dir = Path.GetDirectoryName(Path.GetFullPath(importingFile))
        Path.GetFullPath(Path.Combine(dir, importPath))

/// Recursively expand FileImportDecl nodes into inline declarations.
/// visitedFiles: absolute paths currently on the import stack (cycle detection).
/// emittedFiles: absolute paths already fully expanded (diamond import dedup).
/// Returns the expanded Decl list with FileImportDecl nodes replaced.
let rec private expandImports (visitedFiles: System.Collections.Generic.HashSet<string>)
                               (emittedFiles: System.Collections.Generic.HashSet<string>)
                               (currentFile: string)
                               (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun decl ->
        match decl with
        | Ast.Decl.FileImportDecl(importPath, _span) ->
            let resolvedPath = resolveImportPath importPath currentFile
            // Phase 66: Diamond import dedup — skip files already fully expanded
            if emittedFiles.Contains resolvedPath then []
            else
            if visitedFiles.Contains resolvedPath then
                failwithf "Circular import detected: %s is already being imported" resolvedPath
            if not (File.Exists resolvedPath) then
                failwithf "Import not found: %s (resolved to %s from %s)" importPath resolvedPath currentFile
            visitedFiles.Add resolvedPath |> ignore
            try
                let src = File.ReadAllText resolvedPath
                let importedModule =
                    try parseProgram src resolvedPath
                    with ex ->
                        // Phase 66: Prepend filename to parse/indent errors from imported files
                        let msg = ex.Message
                        if msg.Contains(resolvedPath) then reraise ()
                        else failwithf "%s: %s" resolvedPath msg
                let importedDecls =
                    match importedModule with
                    | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) -> ds
                    | Ast.EmptyModule _ -> []
                let result = expandImports visitedFiles emittedFiles resolvedPath importedDecls
                emittedFiles.Add resolvedPath |> ignore
                result
            finally
                visitedFiles.Remove resolvedPath |> ignore
        | Ast.Decl.ModuleDecl(name, innerDecls, s) ->
            [Ast.Decl.ModuleDecl(name, expandImports visitedFiles emittedFiles currentFile innerDecls, s)]
        | other -> [other])

/// Compile a single .fun file to a native binary.
/// Phase 105: Type check diagnostic options (Issue #25).
/// All default to false → existing silent fallback behaviour preserved.
type TypeCheckOptions = {
    /// Print type errors as warnings to stderr but continue compiling (annotationMap empty fallback).
    ShowTypecheck: bool
    /// Halt compilation on any type error (exit 1, no codegen).
    StrictTypecheck: bool
    /// Emit `[Diag] annotationMap entries: N (status)` to stderr after type check.
    DiagnosticAnnotations: bool
}

let defaultTypeCheckOptions = {
    ShowTypecheck = false
    StrictTypecheck = false
    DiagnosticAnnotations = false
}

/// Phase 105: Run FunLang type check, optionally suppressing stderr.
/// Returns Ok annotationMap on success or Error message on failure.
/// When `suppressStderr` is true, stderr from typeCheckFile is discarded.
let private runTypeCheck (suppressStderr: bool) (inputPath: string) : Result<Map<Ast.Span, Type.Type>, string> =
    let savedErr = System.Console.Error
    if suppressStderr then System.Console.SetError(System.IO.TextWriter.Null)
    try
        try
            let typedModule = ExportApi.typeCheckFile (Path.GetFullPath(inputPath))
            Ok typedModule.AnnotationMap
        with ex ->
            Error ex.Message
    finally
        if suppressStderr then System.Console.SetError(savedErr)

/// Phase 105: --check mode — run type check only, skip codegen and linking.
/// Exit 0 on clean type check, 1 on error.
let checkOnly (inputPath: string) : int =
    if not (File.Exists inputPath) then
        eprintfn "Error: file not found: %s" inputPath
        1
    else
        match runTypeCheck false inputPath with
        | Ok annotationMap ->
            printfn "OK: %s (%d annotation entries)" inputPath (annotationMap |> Map.count)
            0
        | Error msg ->
            eprintfn "%s" msg
            1

/// Prelude loading: walkUp from input path finds filesystem Prelude/ (enables hot-editing
/// during development). Falls back to embedded resources (Phase 103).
/// inputPath: path to the .fun source file
/// outputPath: path for the output binary
/// optLevel: optimization level 0-3
let compileFile (inputPath: string) (outputPath: string) (optLevel: int) (traceEnabled: bool) (logEnabled: bool) (tcOpts: TypeCheckOptions) : int =
    try
        let src = File.ReadAllText(inputPath)

        // Phase 35: Auto-load Prelude modules
        // Phase 103: Fall back to embedded resources when filesystem Prelude/ is not found.
        let preludeOrder = [| "Typeclass.fun"; "Core.fun"; "Option.fun"; "Result.fun"; "String.fun"; "Char.fun"; "Int.fun";
                              "Hashtable.fun"; "HashSet.fun"; "MutableList.fun"; "Queue.fun";
                              "StringBuilder.fun"; "List.fun"; "Array.fun" |]
        let preludeSrc =
            // Search for Prelude/ starting from the input file's directory and walking up.
            // Enables hot-editing Prelude source without rebuilding the compiler.
            let findPreludeDir () =
                let inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath))
                let rec walkUp (dir: string) =
                    if dir = null || dir = "" then ""
                    else
                        let candidate = Path.Combine(dir, "Prelude")
                        if Directory.Exists candidate then candidate
                        else
                            let parent = Path.GetDirectoryName(dir)
                            if parent = dir then ""
                            else walkUp parent
                walkUp inputDir
            let loadEmbedded () =
                // Phase 103: Load from embedded resources in the assembly.
                let asm = Reflection.Assembly.GetExecutingAssembly()
                preludeOrder
                |> Array.choose (fun f ->
                    let resourceName = "Prelude." + f
                    use stream = asm.GetManifestResourceStream(resourceName)
                    if isNull stream then None
                    else
                        use reader = new IO.StreamReader(stream)
                        Some (reader.ReadToEnd()))
                |> String.concat "\n"
            let dir = findPreludeDir ()
            if dir <> "" then
                // Filesystem Prelude takes priority (enables hot-editing without rebuild)
                preludeOrder
                |> Array.choose (fun f ->
                    let path = Path.Combine(dir, f)
                    if File.Exists path then Some (File.ReadAllText path) else None)
                |> String.concat "\n"
            else loadEmbedded ()

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
                let emitted = System.Collections.Generic.HashSet<string>()
                visited.Add(Path.GetFullPath(inputPath)) |> ignore
                Ast.Module(expandImports visited emitted inputPath ds, s)
            | Ast.NamedModule(nm, ds, s) ->
                let visited = System.Collections.Generic.HashSet<string>()
                let emitted = System.Collections.Generic.HashSet<string>()
                visited.Add(Path.GetFullPath(inputPath)) |> ignore
                Ast.NamedModule(nm, expandImports visited emitted inputPath ds, s)
            | other -> other
        // Phase 84/85: Apply fixity rewrite — resolve operator precedence from #[left N] / #[right N] attributes
        let fixityAst =
            let decls = match expandedAst with | Ast.Module(ds, _) | Ast.NamedModule(_, ds, _) -> ds | Ast.EmptyModule _ -> []
            let fixEnv = FixityEnv.collectFixity Map.empty decls
            FixityEnv.rewriteFixity fixEnv expandedAst
        // Phase 52: Transform typeclass declarations before elaboration
        let tcAst =
            match fixityAst with
            | Ast.Module(ds, s) -> Ast.Module(ElabProgram.elaborateTypeclasses ds, s)
            | Ast.NamedModule(n, ds, s) -> Ast.NamedModule(n, ElabProgram.elaborateTypeclasses ds, s)
            | Ast.EmptyModule s -> Ast.EmptyModule s
        // Phase 67: Run FunLang type inference to get per-expression type annotations.
        // Phase 105 (Issue #25): Diagnostic CLI modes control stderr suppression and error handling.
        //   Default                    : silent fallback (suppress stderr, swallow errors → empty map)
        //   --show-typecheck           : surface stderr + warning, continue with empty map fallback
        //   --strict-typecheck         : surface stderr, halt with exit 1 on any type error
        //   --diagnostic-annotations   : append entry-count + status line to stderr
        let suppress = not (tcOpts.ShowTypecheck || tcOpts.StrictTypecheck)
        let tcResult = runTypeCheck suppress inputPath
        let annotationMap =
            match tcResult with
            | Ok m -> m
            | Error msg ->
                if tcOpts.StrictTypecheck then
                    eprintfn "%s" msg
                    eprintfn "[strict-typecheck] aborting due to type errors"
                    exit 1
                if tcOpts.ShowTypecheck then
                    eprintfn "[Warning] type check failed; continuing with empty annotationMap"
                Map.empty
        if tcOpts.DiagnosticAnnotations then
            let status =
                match tcResult with
                | Ok _ -> "typecheck clean"
                | Error _ -> "typecheck failed — field disambiguation will use fallback"
            eprintfn "[Diag] annotationMap entries: %d (%s)" (Map.count annotationMap) status
        // Phase 102/103: FunLang's typeCheckFile uses absolute paths in span FileNames,
        // but FunLangCompiler's AST uses the user-supplied path (typically relative).
        // Rewrite annotationMap keys to use inputPath so FieldAccess span lookups match.
        let fullInputPath = Path.GetFullPath(inputPath)
        let annotationMap =
            annotationMap
            |> Map.toSeq
            |> Seq.map (fun (span, ty) ->
                let normalized =
                    if span.FileName = fullInputPath then { span with FileName = inputPath }
                    else span
                (normalized, ty))
            |> Map.ofSeq
        let mlirMod = ElabProgram.elaborateProgram tcAst annotationMap logEnabled
        let mlirMod = ElabProgram.insertCallStack mlirMod
        let mlirMod = if traceEnabled then ElabProgram.insertTraceEntries mlirMod else mlirMod
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
        elif msg.Contains("parse error") || msg.Contains("Parse error") || msg.Contains("IndentationError") || msg.Contains("Invalid indentation") then
            eprintfn "[Parse] %s" msg
        else
            eprintfn "[Elaboration] %s" msg
        1

/// Handle `fnc build [<target>] [-O...]`
let private handleBuild (optLevel: int) (args: string list) : int =
    match ProjectFile.findFunProj () with
    | None ->
        eprintfn "Error: funproj.toml not found (searched from current directory upward)"
        1
    | Some projPath ->
        let content = File.ReadAllText(projPath)
        let config = ProjectFile.parseFunProj content (Path.GetDirectoryName(Path.GetFullPath(projPath)))

        let targets =
            match args with
            | [] -> Some config.Executables
            | [name] ->
                match config.Executables |> List.tryFind (fun t -> t.Name = name) with
                | Some t -> Some [t]
                | None ->
                    eprintfn "Error: no executable target named '%s'" name
                    None
            | _ -> Some config.Executables

        match targets with
        | None -> 1
        | Some ts ->
            Directory.CreateDirectory(Path.Combine(config.ProjectDir, "build")) |> ignore

            let mutable exitCode = 0
            for target in ts do
                if exitCode = 0 then
                    let srcPath = ProjectFile.resolveTarget config target
                    if not (File.Exists srcPath) then
                        eprintfn "Error: target file not found: %s" srcPath
                        exitCode <- 1
                    else
                        let outputPath = Path.Combine(config.ProjectDir, "build", target.Name)
                        let sw = Stopwatch.StartNew()
                        let result = compileFile srcPath outputPath optLevel false false defaultTypeCheckOptions
                        if result = 0 then
                            printfn "OK: %s -> build/%s (%.1fs)" target.Name target.Name sw.Elapsed.TotalSeconds
                        else
                            exitCode <- result
            exitCode

/// Handle `fnc test [<target>] [-O...]`
let private handleTest (optLevel: int) (args: string list) : int =
    match ProjectFile.findFunProj () with
    | None ->
        eprintfn "Error: funproj.toml not found (searched from current directory upward)"
        1
    | Some projPath ->
        let content = File.ReadAllText(projPath)
        let config = ProjectFile.parseFunProj content (Path.GetDirectoryName(Path.GetFullPath(projPath)))

        let targets =
            match args with
            | [] -> Some config.Tests
            | [name] ->
                match config.Tests |> List.tryFind (fun t -> t.Name = name) with
                | Some t -> Some [t]
                | None ->
                    eprintfn "Error: no test target named '%s'" name
                    None
            | _ -> Some config.Tests

        match targets with
        | None -> 1
        | Some ts ->
            Directory.CreateDirectory(Path.Combine(config.ProjectDir, "build")) |> ignore

            let mutable passCount = 0
            let mutable totalCount = 0

            for target in ts do
                totalCount <- totalCount + 1
                let srcPath = ProjectFile.resolveTarget config target
                if not (File.Exists srcPath) then
                    eprintfn "Error: target file not found: %s" srcPath
                    printfn "FAIL: %s (source not found)" target.Name
                else
                    let outputPath = Path.Combine(config.ProjectDir, "build", target.Name)
                    let sw = Stopwatch.StartNew()
                    let compileResult = compileFile srcPath outputPath optLevel false false defaultTypeCheckOptions
                    if compileResult <> 0 then
                        printfn "FAIL: %s (compile error)" target.Name
                    else
                        let psi = ProcessStartInfo(outputPath)
                        psi.UseShellExecute <- false
                        let proc = Process.Start(psi)
                        proc.WaitForExit()
                        let elapsed = sw.Elapsed.TotalSeconds
                        if proc.ExitCode = 0 then
                            passCount <- passCount + 1
                            printfn "PASS: %s (%.1fs)" target.Name elapsed
                        else
                            printfn "FAIL: %s (exit %d, %.1fs)" target.Name proc.ExitCode elapsed

            printfn "%d/%d tests passed" passCount totalCount
            if passCount = totalCount then 0 else 1

// Phase 66: Run main logic on a thread with larger stack (64MB) to handle deep Prelude nesting.
// The Prelude's module declarations create deeply nested LetPat chains that exceed the default
// 1MB/.NET stack during recursive elaboration.
let private mainImpl (argv: string[]) =
    let args = argv |> Array.toList

    // Parse flags. Order-independent; unknown tokens fall through to positional args.
    // Phase 105: typecheck mode flags carried in a mutable record for simplicity.
    let mutable outputOpt: string option = None
    let mutable optLevel = 2
    let mutable traceEnabled = false
    let mutable logEnabled = false
    let mutable helpRequested = false
    let mutable checkOnlyRequested = false
    let mutable tcShow = false
    let mutable tcStrict = false
    let mutable tcDiag = false
    let mutable remaining: string list = []

    let rec consume = function
        | [] -> ()
        | "-o" :: out :: rest -> outputOpt <- Some out; consume rest
        | "-O0" :: rest -> optLevel <- 0; consume rest
        | "-O1" :: rest -> optLevel <- 1; consume rest
        | "-O2" :: rest -> optLevel <- 2; consume rest
        | "-O3" :: rest -> optLevel <- 3; consume rest
        | "--trace" :: rest -> traceEnabled <- true; consume rest
        | "--log" :: rest -> logEnabled <- true; consume rest
        | ("-h" | "--help") :: rest -> helpRequested <- true; consume rest
        | "--check" :: rest -> checkOnlyRequested <- true; consume rest
        | "--show-typecheck" :: rest -> tcShow <- true; consume rest
        | ("--strict" | "--strict-typecheck") :: rest -> tcStrict <- true; consume rest
        | "--diagnostic-annotations" :: rest -> tcDiag <- true; consume rest
        | x :: rest -> remaining <- remaining @ [x]; consume rest

    consume args

    let tcOpts = {
        ShowTypecheck = tcShow
        StrictTypecheck = tcStrict
        DiagnosticAnnotations = tcDiag
    }

    let printHelp () =
        printfn "fnc — FunLangCompiler: FunLang source → native binary"
        printfn ""
        printfn "USAGE"
        printfn "  fnc <file.fun> [-o <output>] [-O0|-O1|-O2|-O3] [--trace] [--log]"
        printfn "  fnc --check <file.fun>"
        printfn "  fnc build [<target>] [-O0|-O1|-O2|-O3]"
        printfn "  fnc test  [<target>] [-O0|-O1|-O2|-O3]"
        printfn ""
        printfn "OPTIONS"
        printfn "  -o <output>       Output binary path (default: input basename without extension)"
        printfn "  -O0..-O3          Optimization level (default: -O2)"
        printfn "  --trace           Emit '[TRACE] @funcName' to stderr on every function entry"
        printfn "  --log             Enable log/logf builtin output (default: silent)"
        printfn "  -h, --help        Show this help message"
        printfn ""
        printfn "DIAGNOSTICS — TYPE CHECK CONTROL (default: silent fallback)"
        printfn "  --check                       Type-check only; skip codegen and linking"
        printfn "                                  Exit 0 on clean type check, 1 on type errors"
        printfn "  --show-typecheck              Surface type errors as warnings; continue compile"
        printfn "                                  with empty annotationMap fallback"
        printfn "  --strict-typecheck, --strict  Halt compilation (exit 1) on any type error"
        printfn "  --diagnostic-annotations      Emit '[Diag] annotationMap entries: N' to stderr"
        printfn ""
        printfn "BUILTINS — I/O"
        printfn "  print s           Write string to stdout (no newline)"
        printfn "  println s         Write string to stdout with trailing newline"
        printfn "  printf fmt ...    Formatted stdout (no newline); e.g. printf \"%%d\" 42"
        printfn "  printfn fmt ...   Formatted stdout with trailing newline"
        printfn "  eprint s          Write string to stderr (no newline)"
        printfn "  eprintln s        Write string to stderr with trailing newline"
        printfn "  eprintf fmt ...   Formatted stderr (no newline)"
        printfn "  eprintfn fmt ...  Formatted stderr with trailing newline"
        printfn "  sprintf fmt ...   Format to string (no output)"
        printfn ""
        printfn "BUILTINS — DEBUG (gated by --log flag)"
        printfn "  log s             stderr + newline IF --log, else no-op (argument discarded)"
        printfn "  logf fmt ...      Formatted stderr + newline IF --log, else no-op"
        printfn ""
        printfn "BUILTINS — DIAGNOSTICS"
        printfn "  dbg e             Prints '[file:line] value' to stderr, returns e unchanged"
        printfn "  failwith msg      Abort with message + backtrace"

    if helpRequested then
        printHelp ()
        0
    elif checkOnlyRequested then
        match remaining with
        | inputPath :: _ -> checkOnly inputPath
        | [] ->
            eprintfn "Usage: fnc --check <file.fun>"
            1
    else
    match remaining with
    | "build" :: rest -> handleBuild optLevel rest
    | "test" :: rest  -> handleTest optLevel rest
    | inputPath :: _ ->
        // Derive output path: explicit -o takes priority, else strip extension from filename
        let outputPath =
            match outputOpt with
            | Some o -> o
            | None ->
                let basename = Path.GetFileNameWithoutExtension(inputPath)
                basename

        // Check input file exists
        if not (File.Exists(inputPath)) then
            eprintfn "Error: file not found: %s" inputPath
            1
        else
            compileFile inputPath outputPath optLevel traceEnabled logEnabled tcOpts
    | [] ->
        eprintfn "Usage: fnc <file.fun> [-o <output>] [-O0|-O1|-O2|-O3] [--trace] [--log]"
        eprintfn "       fnc --check <file.fun>   (type-check only)"
        eprintfn "       fnc build [<target>] [-O0|-O1|-O2|-O3]"
        eprintfn "       fnc test [<target>] [-O0|-O1|-O2|-O3]"
        eprintfn "       fnc --help   (for full documentation)"
        1

[<EntryPoint>]
let main argv =
    let mutable exitCode = 0
    let thread = System.Threading.Thread(
        (fun () -> exitCode <- mainImpl argv),
        64 * 1024 * 1024)  // 64MB stack
    thread.Start()
    thread.Join()
    exitCode
