#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue6b_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
type PS = { mutable pos : int; src : string }

let peek (ps : PS) : int =
    if ps.pos >= String.length ps.src then -1
    else ps.src.[ps.pos]

let advance (ps : PS) : unit =
    ps.pos <- ps.pos + 1

let rec collect (ps : PS) (acc : string list) : string list =
    let c = peek ps
    if c = 61 then acc
    else if (c = 95 || (c >= 97 && c <= 122) || (c >= 65 && c <= 90)) then
        advance ps
        collect ps (acc ++ [to_string (int_to_char c)])
    else
        acc

let _ =
    let ps = { pos = 0; src = "abc=" }
    let result = collect ps []
    println (to_string (List.length result))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
