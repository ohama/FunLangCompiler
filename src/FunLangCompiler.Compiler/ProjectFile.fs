module ProjectFile

open System
open System.IO

/// A build/test target entry from funproj.toml
type Target = { Name: string; Main: string }

/// Parsed representation of a funproj.toml file
type FunProjConfig = {
    Name: string option
    PreludePath: string option
    Executables: Target list
    Tests: Target list
    ProjectDir: string
}

// ---------------------------------------------------------------------------
// TOML subset parser
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type private Section =
    | None
    | Project
    | Executable
    | Test

/// Strip a quoted string value from a TOML value token, e.g. `"foo"` -> `foo`.
let private stripQuotes (s: string) =
    let s = s.Trim()
    if s.Length >= 2 && s.[0] = '"' && s.[s.Length - 1] = '"' then
        s.[1 .. s.Length - 2]
    else
        s

/// Parse a `key = "value"` line. Returns `Some (key, value)` or `None`.
let private tryParseKeyValue (line: string) : (string * string) option =
    let idx = line.IndexOf('=')
    if idx < 0 then
        None
    else
        let key   = line.[..idx - 1].Trim()
        let value = line.[idx + 1..].Trim() |> stripQuotes
        Some (key, value)

/// Parse funproj.toml content into a FunProjConfig.
/// projectDir should be the directory containing the funproj.toml file.
let parseFunProj (content: string) (projectDir: string) : FunProjConfig =
    let lines = content.Split([| '\n'; '\r' |], StringSplitOptions.None)

    let mutable section     = Section.None
    let mutable projName    : string option = None
    let mutable prelude     : string option = None
    let mutable executables : Target list   = []
    let mutable tests       : Target list   = []

    // Accumulators for the target currently being built
    let mutable curName : string option = None
    let mutable curMain : string option = None

    let finishTarget () =
        match section with
        | Section.Executable ->
            match curName, curMain with
            | Some n, Some m -> executables <- executables @ [{ Name = n; Main = m }]
            | _ -> ()
        | Section.Test ->
            match curName, curMain with
            | Some n, Some m -> tests <- tests @ [{ Name = n; Main = m }]
            | _ -> ()
        | _ -> ()
        curName <- None
        curMain <- None

    for rawLine in lines do
        let line = rawLine.Trim()
        if line = "" || line.StartsWith("#") then
            ()  // skip blank lines and comments
        elif line = "[project]" then
            finishTarget ()
            section <- Section.Project
        elif line = "[[executable]]" then
            finishTarget ()
            section <- Section.Executable
        elif line = "[[test]]" then
            finishTarget ()
            section <- Section.Test
        elif line.StartsWith("[") then
            // Unknown section — treat as none
            finishTarget ()
            section <- Section.None
        else
            match tryParseKeyValue line with
            | None -> ()
            | Some (key, value) ->
                match section with
                | Section.Project ->
                    match key with
                    | "name"    -> projName <- Some value
                    | "prelude" -> prelude  <- Some value
                    | _         -> ()   // unknown key ignored
                | Section.Executable | Section.Test ->
                    match key with
                    | "name" -> curName <- Some value
                    | "main" -> curMain <- Some value
                    | _      -> ()
                | Section.None -> ()    // unknown section ignored

    // Flush last in-progress target
    finishTarget ()

    { Name        = projName
      PreludePath = prelude
      Executables = executables
      Tests       = tests
      ProjectDir  = projectDir }

// ---------------------------------------------------------------------------
// File-system helpers
// ---------------------------------------------------------------------------

/// Walk up the directory tree from the current directory looking for a
/// funproj.toml file. Returns Some fullPath on the first match, None if none
/// found before the filesystem root.
let findFunProj () : string option =
    let rec loop (dir: string) =
        if String.IsNullOrEmpty dir then None
        else
            let candidate = Path.Combine(dir, "funproj.toml")
            if File.Exists(candidate) then Some candidate
            else
                let parent = Path.GetDirectoryName(dir)
                if parent = dir then None   // reached filesystem root
                else loop parent
    loop (Directory.GetCurrentDirectory())

/// Resolve a target's Main path to an absolute path relative to the project
/// directory.
let resolveTarget (config: FunProjConfig) (target: Target) : string =
    Path.GetFullPath(Path.Combine(config.ProjectDir, target.Main))
