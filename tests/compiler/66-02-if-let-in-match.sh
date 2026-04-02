#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue7_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
type Err = InternalError of string
type Result<'a> = Ok of 'a | Err of Err

let parseWithInput (input : string) (rest : string list) : Result<string * string> =
    match rest with
    | ["-o"; output] ->
        Ok (input, output)
    | [] ->
        let baseName =
            if String.endsWith input ".funl" then input.[0 .. String.length input - 6]
            else input
        Ok (input, baseName ^^ "_lex.fun")
    | _ ->
        Err (InternalError ("Unexpected: " ^^ String.concat " " rest))

let _ =
    match parseWithInput "test.funl" [] with
    | Ok (i, o) -> printfn "%s -> %s" i o
    | Err _ -> println "error"
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
