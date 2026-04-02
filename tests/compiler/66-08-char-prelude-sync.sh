#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_char_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let _ =
    println (to_string (Char.isDigit '9'))
    println (to_string (Char.isDigit 'a'))
    println (to_string (Char.isLetter 'Z'))
    println (to_string (Char.isUpper 'a'))
    println (to_string (Char.isLower 'a'))
    println (to_string (Char.toUpper 'b'))
    println (to_string (Char.toLower 'B'))
    println (to_string (Char.toInt 'A'))
    println (to_string (Char.ofInt 90))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
