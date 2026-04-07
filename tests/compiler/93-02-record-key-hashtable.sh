#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_prelude_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
type Point = { x: int; y: int }

let _ =
    let ht = Hashtable.create ()
    Hashtable.set ht { x = 1; y = 2 } "origin-near"
    Hashtable.set ht { x = 10; y = 20 } "far-away"
    // Retrieve with structurally equal record
    println (Hashtable.get ht { x = 1; y = 2 })
    println (Hashtable.get ht { x = 10; y = 20 })
    // containsKey
    println (to_string (Hashtable.containsKey ht { x = 1; y = 2 }))
    println (to_string (Hashtable.containsKey ht { x = 5; y = 6 }))
    // Overwrite
    Hashtable.set ht { x = 1; y = 2 } "updated"
    println (Hashtable.get ht { x = 1; y = 2 })
    println (to_string (Hashtable.count ht))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
