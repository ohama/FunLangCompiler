#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_dbg_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
let double x = x + x

let main =
    let x = dbg (double 21)
    let y = dbg (x + 1)
    println (to_string y)
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN"
# Capture stderr, print stdout + stderr markers
"$OUTBIN" 2>/tmp/fnc_dbg_stderr.txt
STDERR=$(cat /tmp/fnc_dbg_stderr.txt)
# Verify stderr contains [file:line] patterns
echo "$STDERR" | grep -q "\[.*:4\] 42" && echo "dbg1: ok" || echo "dbg1: FAIL"
echo "$STDERR" | grep -q "\[.*:5\] 43" && echo "dbg2: ok" || echo "dbg2: FAIL"
rm -rf "$D" "$OUTBIN" /tmp/fnc_dbg_stderr.txt
