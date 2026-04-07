#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_XXXXXX)
cat > "$D/test.fun" << 'FUNEOF'
type Color = Red | Green | Blue
type Opt = None | Some of int

let _ =
    // String equality (backward compat)
    println (to_string ("hello" = "hello"))
    println (to_string ("hello" = "world"))
    // Tuple equality
    println (to_string ((1, "a") = (1, "a")))
    println (to_string ((1, "a") = (1, "b")))
    // List equality
    println (to_string ([1; 2; 3] = [1; 2; 3]))
    println (to_string ([1; 2] = [1; 2; 3]))
    println (to_string ([1; 2; 3] = [1; 2]))
    // ADT equality (nullary)
    println (to_string (Red = Red))
    println (to_string (Red = Green))
    // ADT equality (with data)
    println (to_string (Some 1 = Some 1))
    println (to_string (Some 1 = Some 2))
    println (to_string (Some 1 = None))
    // Nested structural equality
    println (to_string ((1, [2; 3]) = (1, [2; 3])))
    println (to_string ((1, [2; 3]) = (1, [2; 4])))
    // Inequality operator
    println (to_string ("hello" <> "world"))
    println (to_string ([1; 2] <> [1; 2]))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
