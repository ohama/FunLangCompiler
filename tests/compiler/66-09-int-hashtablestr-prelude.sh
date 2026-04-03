#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_prelude_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let _ =
    // Int module
    println (to_string (Int.parse "42"))
    println (Int.toString 100)
    // String-key Hashtable: createStr + indexing syntax for auto dispatch
    let ht = Hashtable.createStr ()
    ht.["hello"] <- 1
    ht.["world"] <- 2
    println (to_string ht.["hello"])
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
