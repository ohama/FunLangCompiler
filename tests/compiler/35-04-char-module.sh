#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_char_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let _ = println (to_string (Char.isDigit '5'))
let _ = println (to_string (Char.isLetter 'a'))
let _ = println (to_string (Char.isUpper 'A'))
let _ = println (to_string (Char.isLower 'a'))
let _ = println (to_string (Char.toInt 'A'))
let _ = println (to_string (Char.ofInt 66))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
