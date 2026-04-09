#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_96_01_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
// findSlash: inner let rec captures string param from outer function
// After lambda lifting, go(s)(i) — s must be recognized as string for s.[i] dispatch
let findSlash (s : string) : int =
    let rec go (i : int) : int =
        if i >= String.length s then -1
        else if s.[i] = 47 then i
        else go (i + 1)
    go 0

let _ =
    println (to_string (findSlash "ab/cdefgh"))
    println (to_string (findSlash "hello/world"))
    println (to_string (findSlash "noslash"))
    println (to_string (findSlash "/start"))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
