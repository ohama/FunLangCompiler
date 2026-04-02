#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue9_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let run (inputPath : string) (outputPath : string) : int =
    if not (file_exists inputPath) then
        println ("Cannot read: " ^^ inputPath)
        1
    else
        let text = read_file inputPath
        println ("Read " ^^ to_string (String.length text) ^^ " bytes")
        0

let _ =
    let code = run "nonexistent.txt" "out.txt"
    println (to_string code)
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
