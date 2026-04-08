#!/bin/bash
set -e
PROJROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D=$(mktemp -d /tmp/fnc_nested_XXXXXX)
ln -s "$PROJROOT/Prelude" "$D/Prelude"
cat > "$D/test.fun" << 'FUNEOF'
// Test 1: while + if-then + continuation
let test1 (_u : int) : int =
    let mutable i = 0
    while i < 3 do
        if i = 1 then println "hit"
        i <- i + 1
    i

// Test 2: while + if-then-else + continuation
let test2 (_u : int) : int =
    let mutable i = 0
    let mutable sum = 0
    while i < 5 do
        if i % 2 = 0 then sum <- sum + i
        else sum <- sum + 1
        i <- i + 1
    sum

// Test 3: nested while + continuation
let test3 (_u : int) : int =
    let mutable i = 0
    let mutable total = 0
    while i < 3 do
        let mutable j = 0
        while j < 3 do
            total <- total + 1
            j <- j + 1
        i <- i + 1
    total

// Test 4: while + && + if-then + mutable bool
let test4 (_u : int) : int =
    let mutable i = 0
    let mutable stop = false
    let mutable count = 0
    while i < 10 && not stop do
        if i = 5 then stop <- true
        count <- count + 1
        i <- i + 1
    count

// Test 5: while > if-then > nested while
let test5 (_u : int) : int =
    let mutable i = 0
    let mutable total = 0
    while i < 3 do
        if i > 0 then
            let mutable j = 0
            while j < i do
                total <- total + 1
                j <- j + 1
        i <- i + 1
    total

// Test 6: multiple if-thens in sequence inside while
let test6 (_u : int) : int =
    let mutable i = 0
    let mutable a = 0
    let mutable b = 0
    while i < 5 do
        if i < 2 then a <- a + 1
        if i > 2 then b <- b + 1
        i <- i + 1
    a * 10 + b

// Test 7: while + || + if-then + nested for
let test7 (_u : int) : int =
    let mutable i = 0
    let mutable total = 0
    while i < 5 || total = 0 do
        if i > 2 then
            for x in [1; 2; 3] do
                total <- total + x
        i <- i + 1
    total

let _ =
    println (to_string (test1 0))
    println (to_string (test2 0))
    println (to_string (test3 0))
    println (to_string (test4 0))
    println (to_string (test5 0))
    println (to_string (test6 0))
    println (to_string (test7 0))
FUNEOF
OUTBIN=$(mktemp /tmp/fnc_XXXXXX)
dotnet run --project "$PROJROOT/src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj" -- "$D/test.fun" -o "$OUTBIN" 2>/dev/null
chmod +x "$OUTBIN" && "$OUTBIN"
EC=$?
rm -rf "$D" "$OUTBIN"
exit $EC
