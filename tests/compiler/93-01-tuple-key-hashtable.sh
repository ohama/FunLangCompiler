#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_prelude_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let _ =
    let ht = Hashtable.create ()
    Hashtable.set ht (1, "a") 100
    Hashtable.set ht (2, "b") 200
    // Retrieve with original keys
    println (to_string (Hashtable.get ht (1, "a")))
    println (to_string (Hashtable.get ht (2, "b")))
    // Structurally equal tuple constructed separately
    let k = (1, "a")
    println (to_string (Hashtable.get ht k))
    // Different tuple is not the same key
    println (to_string (Hashtable.containsKey ht (1, "b")))
    println (to_string (Hashtable.containsKey ht (1, "a")))
    // Overwrite with structurally equal key
    Hashtable.set ht (1, "a") 999
    println (to_string (Hashtable.get ht (1, "a")))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
