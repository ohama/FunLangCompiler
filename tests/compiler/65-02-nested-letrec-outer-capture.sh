#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue5_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let base = 10

let add_base (a : int) (b : int) : int = a + b + base

let main =
    let rec loop1 (n : int) : int =
        if n = 0 then 0
        else add_base n 1 + loop1 (n - 1)
    let result1 = loop1 3
    let rec loop2 (n : int) : int =
        if n = 0 then 0
        else add_base n 2 + loop2 (n - 1)
    let result2 = loop2 2
    println (to_string (result1 + result2))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
