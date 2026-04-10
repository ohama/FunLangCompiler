#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue23_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
type Option 'a = None | Some of 'a

let _ =
    let ht = hashtable_create ()
    hashtable_set ht "hello" 42
    hashtable_set ht "world" 99
    let r1 = hashtable_trygetvalue ht "hello"
    let r2 = hashtable_trygetvalue ht "missing"
    let r3 = hashtable_trygetvalue ht "world"
    let v1 = match r1 with | Some v -> v | None -> -1
    let v2 = match r2 with | Some v -> v | None -> -1
    let v3 = match r3 with | Some v -> v | None -> -1
    printfn "%d" v1
    printfn "%d" v2
    printfn "%d" v3
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" >/dev/null 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
