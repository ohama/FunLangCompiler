#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_issue8_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/a.fun" << 'FUNEOF'
type Cset = (int * int) list
let empty : Cset = []
FUNEOF
cat > "$D/b.fun" << 'FUNEOF'
open "a.fun"
type Nfa = { mutable n : int }
let alloc (nfa : Nfa) : int =
    let id = nfa.n
    nfa.n <- nfa.n + 1
    id
let build3 (nfa : Nfa) (x : int) (flag : bool) : int =
    let s = alloc nfa
    if flag then s + x else s
FUNEOF
cat > "$D/c.fun" << 'FUNEOF'
open "b.fun"
let rec useIt (nfa : Nfa) (xs : int list) : int =
    match xs with
    | [] -> 0
    | x :: rest -> build3 nfa x true + useIt nfa rest
FUNEOF
cat > "$D/d.fun" << 'FUNEOF'
open "b.fun"
open "c.fun"
let rec useIt2 (nfa : Nfa) (xs : int list) : int =
    match xs with
    | [] -> 0
    | x :: rest -> build3 nfa x false + useIt2 nfa rest
FUNEOF
cat > "$D/main.fun" << 'FUNEOF'
open "b.fun"
open "c.fun"
open "d.fun"
let _ =
    let nfa = { n = 0 }
    println (to_string (useIt nfa [1;2;3] + useIt2 nfa [4;5;6]))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/main.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
