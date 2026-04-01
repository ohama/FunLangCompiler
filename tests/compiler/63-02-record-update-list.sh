#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue3_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
type Item = {
    id     : int;
    name   : string;
    mutable trans : (int * int) list;
    flag1  : int option;
    flag2  : int option;
}

let rec update (key : int) (v : int) (lst : Item list) : Item list =
    match lst with
    | [] -> failwith "not found"
    | ds :: rest ->
        if ds.id = key then
            { ds with trans = (v, v) :: ds.trans } :: rest
        else
            ds :: update key v rest

let main =
    let items = [{ id = 0; name = "a"; trans = []; flag1 = None; flag2 = None };
                 { id = 1; name = "b"; trans = []; flag1 = None; flag2 = None }]
    let result = update 1 42 items
    match result with
    | _ :: ds :: _ ->
        match ds.trans with
        | (x, _) :: _ -> println (to_string x)
        | [] -> println "empty"
    | _ -> println "fail"
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
