#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_prelude_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let _ =
    let ht = Hashtable.createStr ()
    Hashtable.setStr ht "hello" 1
    Hashtable.setStr ht "world" 2
    println (to_string (Hashtable.getStr ht "hello"))
    println (to_string (Hashtable.count ht))
    println (to_string (Hashtable.containsKeyStr ht "world"))
    Hashtable.removeStr ht "world"
    println (to_string (Hashtable.count ht))
    let keys = Hashtable.keysStr ht
    println (to_string (List.length keys))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
