#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue6_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let rec collect (items : int list) (acc : int) : int =
    match items with
    | [] -> acc
    | x :: rest ->
        if (x = 95 || (x >= 97 && x <= 122) || (x >= 65 && x <= 90)) then
            collect rest (acc + 1)
        else
            collect rest acc

let _ =
    let result = collect [97; 66; 95; 0; 122; 200] 0
    println (to_string result)
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
