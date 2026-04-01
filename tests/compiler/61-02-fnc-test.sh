#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnctest_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
printf '[project]\nname = "test"\n\n[[test]]\nname = "unit"\nmain = "test_unit.fun"\n' > "$D/funproj.toml"
printf 'let _ = println "all good"\n' > "$D/test_unit.fun"
cd "$D"
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- test 2>&1 | sed 's/ ([0-9.]*s)//'
EC=$?
rm -rf "$D"
exit $EC
