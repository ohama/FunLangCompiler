/// E2E test for ProjectFile.parseFunProj
/// Exercises [project], [[executable]], [[test]] parsing and resolveTarget.
module ProjFileTest

open ProjectFile

let inline assertEq name expected actual =
    if expected <> actual then
        eprintfn "FAIL [%s]: expected %A but got %A" name expected actual
        exit 1

let inline assertTrue name cond =
    if not cond then
        eprintfn "FAIL [%s]: condition is false" name
        exit 1

// ---------------------------------------------------------------------------
// Test 1: Full parse with multiple sections
// ---------------------------------------------------------------------------

let toml1 = """
[project]
name = "demo"

[[executable]]
name = "app"
main = "src/main.fun"

[[test]]
name = "unit"
main = "tests/unit.fun"

[[test]]
name = "integ"
main = "tests/integ.fun"
"""

let cfg1 = parseFunProj toml1 "/tmp/proj"

assertEq "cfg1.Name"        (Some "demo")       cfg1.Name
assertEq "cfg1.Executables.Length" 1             cfg1.Executables.Length
assertEq "cfg1.exe[0].Name" "app"                cfg1.Executables.[0].Name
assertEq "cfg1.exe[0].Main" "src/main.fun"       cfg1.Executables.[0].Main
assertEq "cfg1.Tests.Length" 2                   cfg1.Tests.Length
assertEq "cfg1.test[0].Name" "unit"              cfg1.Tests.[0].Name
assertEq "cfg1.test[0].Main" "tests/unit.fun"    cfg1.Tests.[0].Main
assertEq "cfg1.test[1].Name" "integ"             cfg1.Tests.[1].Name
assertEq "cfg1.test[1].Main" "tests/integ.fun"   cfg1.Tests.[1].Main
assertEq "cfg1.ProjectDir"  "/tmp/proj"          cfg1.ProjectDir

// ---------------------------------------------------------------------------
// Test 2: resolveTarget produces absolute path
// ---------------------------------------------------------------------------

let resolved = resolveTarget cfg1 cfg1.Executables.[0]
assertTrue "resolved ends with src/main.fun" (resolved.EndsWith("src/main.fun"))
assertTrue "resolved is absolute" (System.IO.Path.IsPathRooted resolved)

// ---------------------------------------------------------------------------
// Test 3: Missing optional fields parse as None
// ---------------------------------------------------------------------------

let toml2 = """
[[executable]]
name = "minimal"
main = "main.fun"
"""

let cfg2 = parseFunProj toml2 "/tmp/proj2"

assertEq "cfg2.Name"        None    cfg2.Name
assertEq "cfg2.Executables.Length" 1 cfg2.Executables.Length
assertEq "cfg2.Tests.Length" 0      cfg2.Tests.Length

// ---------------------------------------------------------------------------
// Test 4: Comments and blank lines are ignored
// ---------------------------------------------------------------------------

let toml3 = """
# This is a comment
[project]
# another comment
name = "commented"

# blank lines above and below

[[test]]
name = "t"
main = "t.fun"
"""

let cfg3 = parseFunProj toml3 "/tmp/proj3"
assertEq "cfg3.Name"        (Some "commented") cfg3.Name
assertEq "cfg3.Tests.Length" 1                 cfg3.Tests.Length

// ---------------------------------------------------------------------------
// Test 5: Whitespace around = sign
// ---------------------------------------------------------------------------

let toml4 = """
[project]
name="tight"
"""

let cfg4 = parseFunProj toml4 "/tmp/proj4"
assertEq "cfg4.Name (tight)"     (Some "tight")             cfg4.Name

// ---------------------------------------------------------------------------
// All done
// ---------------------------------------------------------------------------

printfn "ALL TESTS PASSED"
