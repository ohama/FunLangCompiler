# Phase 59: 중첩 모듈 Qualified Access - Research

**Researched:** 2026-04-01
**Domain:** F# compiler — module elaboration, qualified name resolution, AST pattern matching
**Confidence:** HIGH

## Summary

Phase 59 adds support for `Outer.Inner.value` qualified access on nested modules and `open Outer.Inner` lookup by full path key. The work is entirely within `Elaboration.fs` (and minor verification in `Program.fs`). No external libraries are involved — this is pure compiler internals surgery.

The current implementation only handles single-level module access (`Module.member`). Nested modules flatten with only the innermost module name as prefix (`Inner_foo` instead of `Outer_Inner_foo`). This causes two failures: (1) `Outer.Inner.foo` qualified access fails because the AST becomes `FieldAccess(FieldAccess(Constructor("Outer"), "Inner"), "foo")` which falls through to the record FieldAccess arm and errors, and (2) `open Outer.Inner` currently uses `List.last` to look up by `"Inner"` key, which works coincidentally now but would conflict with another module also named `Inner` under a different outer module.

The fix requires four coordinated changes: updating `flattenDecls` to pass full path prefix through recursion, updating `collectModuleMembers` to register nested modules under full-path keys (dot-separated for `open` lookup), adding a new FieldAccess elaboration pattern for nested qualified access chains, and updating the `open` path lookup to use the full dot-joined key.

**Primary recommendation:** Make `flattenDecls` and `collectModuleMembers` pass the accumulated full path through recursion, then add a single recursive helper to flatten nested `FieldAccess(FieldAccess(...))` chains into `Var("Outer_Inner_foo")`.

## Standard Stack

This phase uses no external libraries. All work is in-compiler F# code.

### Core
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| `flattenDecls` | `Elaboration.fs:4278` | Flattens nested ModuleDecl into flat LetDecl list with prefixed names | Phase 25/35 foundation — all module naming flows through here |
| `collectModuleMembers` | `Elaboration.fs:4251` | First-pass scan to build module→member map for `open` resolution | Phase 41 — provides the member list that OpenDecl emits as aliases |
| `elaborateExpr` FieldAccess arm | `Elaboration.fs:3232` | Desugars `M.x` → `Var("M_x")` at elaboration time | Phase 25/35 — single-level qualified access |
| `elaborateExpr` App/FieldAccess arm | `Elaboration.fs:2503` | Desugars `M.f arg` → `App(Var("M_f"), arg)` | Phase 25/35 — qualified function call |

### No Installation Required
This is a pure code change to existing F# source files.

## Architecture Patterns

### Current Data Flow

```
Source: module Outer = module Inner = let foo = 42

collectModuleMembers (scan "Outer" [...])
  → scan "Inner" [LetDecl("foo")]
  → result: Map ["Inner" → ["Inner_foo"]]    ← BUG: key is "Inner", not "Outer.Inner"
                                               ← BUG: value is "Inner_foo", not "Outer_Inner_foo"

flattenDecls "" [ModuleDecl("Outer", [ModuleDecl("Inner", [LetDecl("foo")])])]
  → flattenDecls "Outer" [ModuleDecl("Inner", [LetDecl("foo")])]
  → flattenDecls "Inner" [LetDecl("foo")]    ← BUG: modName is "Inner", drops "Outer" prefix
  → [LetDecl("Inner_foo", ...)]              ← BUG: should be "Outer_Inner_foo"
```

### Required Data Flow

```
collectModuleMembers (scan "" "" [...])  ← needs (outerPath: string, innerName: string)
  → scan "Outer" [ModuleDecl("Inner", ...)]
  → scan "Outer.Inner" [LetDecl("foo")]
  → qualifiedName = "Outer_Inner_foo"
  → result: Map ["Outer.Inner" → ["Outer_Inner_foo"]]  ← key is dot-joined full path

flattenDecls "" [ModuleDecl("Outer", ...)]
  → flattenDecls "Outer" [ModuleDecl("Inner", ...)]
  → flattenDecls "Outer_Inner" [LetDecl("foo")]  ← full prefix passed down
  → [LetDecl("Outer_Inner_foo", ...)]
```

### AST Structure for Nested Qualified Access

```
Source: Outer.Inner.foo

Parser produces (left-associative dot):
  FieldAccess(
    FieldAccess(Constructor("Outer", None, _), "Inner", _),
    "foo",
    _
  )

Source: Outer.Inner.f arg

App(
  FieldAccess(
    FieldAccess(Constructor("Outer", None, _), "Inner", _),
    "f",
    _
  ),
  argExpr,
  _
)
```

### Pattern 1: Recursive Path Flattener Helper

A private helper that walks a nested `FieldAccess(FieldAccess(...Constructor...))` chain and collects segments:

```fsharp
// Attempt to decode a nested module qualified access chain.
// Returns Some (segments) where segments = ["Outer"; "Inner"; "foo"]
// Returns None if the expression is not a pure module-access chain.
let rec private tryDecodeModulePath (expr: Expr) : string list option =
    match expr with
    | Constructor(name, None, _) -> Some [name]
    | FieldAccess(inner, field, _) ->
        match tryDecodeModulePath inner with
        | Some segments -> Some (segments @ [field])
        | None -> None
    | _ -> None
```

This helper is used in the `FieldAccess` and `App(FieldAccess(...))` elaboration arms to detect nested qualified accesses before falling through to the record FieldAccess arm.

### Pattern 2: flattenDecls Full-Path Threading

Change the `modName` parameter to carry the full underscore-joined prefix:

```fsharp
// Before:
| Ast.Decl.ModuleDecl(name, innerDecls, _) -> flattenDecls moduleMembers name innerDecls

// After:
| Ast.Decl.ModuleDecl(name, innerDecls, _) ->
    let childPrefix = if modName = "" then name else modName + "_" + name
    flattenDecls moduleMembers childPrefix innerDecls
```

Single-level modules are unaffected: when `modName = ""` and `name = "List"`, `childPrefix = "List"` — same as before.

### Pattern 3: collectModuleMembers Full-Path Threading

Change `scan` to carry both the dot-path key (for `open` lookup) and the underscore prefix (for member name generation):

```fsharp
let rec scan (dotPath: string) (underPath: string) (ds: Ast.Decl list) =
    for d in ds do
        match d with
        | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
            let childDot   = if dotPath   = "" then name else dotPath   + "." + name
            let childUnder = if underPath = "" then name else underPath + "_" + name
            scan childDot childUnder innerDecls
        | Ast.Decl.LetDecl(name, _, _) when underPath <> "" && name <> "_" ->
            let qualifiedName = underPath + "_" + name
            let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
            result <- Map.add dotPath (existing @ [qualifiedName]) result
        ...
scan "" "" decls
```

Key: the map key is `dotPath` (e.g., `"Outer.Inner"`) because `open Outer.Inner` joins the path with dots. The member names use `underPath` prefix (e.g., `"Outer_Inner_foo"`).

### Pattern 4: OpenDecl Lookup Fix

Change the `flattenDecls` OpenDecl arm to use the full dot-joined path as the key:

```fsharp
| Ast.Decl.OpenDecl(path, s) when not (List.isEmpty path) ->
    // Join ALL segments with "." for the map key (previously used only List.last)
    let openedKey = path |> String.concat "."
    match Map.tryFind openedKey moduleMembers with
    | Some qualifiedNames ->
        qualifiedNames |> List.map (fun qualifiedName ->
            // Strip the underscore prefix to get the short name
            // qualifiedName = "Outer_Inner_foo", openedKey = "Outer.Inner"
            // Need to strip the underPath prefix, not the dotPath key
            let underscorePrefix = openedKey.Replace(".", "_")
            let shortName = qualifiedName.Substring(underscorePrefix.Length + 1)
            Ast.Decl.LetDecl(shortName, Ast.Var(qualifiedName, s), s))
    | None -> []
```

Note: `shortName` extraction must strip the underscore prefix, not the dot key. `"Outer_Inner_foo"` with openedKey `"Outer.Inner"` → underscorePrefix `"Outer_Inner"` → shortName `"foo"`.

### Pattern 5: Nested FieldAccess Elaboration Arms

Two new arms needed in `elaborateExpr` — insert BEFORE the existing `FieldAccess(Constructor(...))` arm at line 3232:

**Arm A: Nested qualified value access — `Outer.Inner.foo`**
```fsharp
// Phase 59: Nested qualified value access — Outer.Inner.foo → Var("Outer_Inner_foo")
| FieldAccess(innerExpr, memberName, span)
    when (tryDecodeModulePath innerExpr).IsSome ->
    match tryDecodeModulePath innerExpr with
    | Some segments ->
        // segments = ["Outer"; "Inner"], memberName = "foo"
        let allSegments = segments @ [memberName]
        // Check if it's a type constructor (last segment in TypeEnv)
        if Map.containsKey memberName env.TypeEnv then
            elaborateExpr env (Constructor(memberName, None, span))
        else
            let varName = allSegments |> String.concat "_"
            elaborateExpr env (Var(varName, span))
    | None -> failwith "unreachable"
```

**Arm B: Nested qualified function call — `Outer.Inner.f arg`**
```fsharp
// Phase 59: Nested qualified function call — Outer.Inner.f arg → App(Var("Outer_Inner_f"), arg)
| App(FieldAccess(innerExpr, memberName, fspan), argExpr, span)
    when (tryDecodeModulePath innerExpr).IsSome
      && not (Map.containsKey memberName env.TypeEnv) ->
    match tryDecodeModulePath innerExpr with
    | Some segments ->
        let varName = (segments @ [memberName]) |> String.concat "_"
        elaborateExpr env (App(Var(varName, fspan), argExpr, span))
    | None -> failwith "unreachable"
```

Insert App arm before `App(FieldAccess(Constructor(...)))` at line 2503, and FieldAccess arm before `FieldAccess(Constructor(...))` at line 3232.

### Anti-Patterns to Avoid

- **Changing the existing single-level arms:** The `FieldAccess(Constructor(modName, None, _), memberName, span)` arm at line 3232 handles `List.map`, `Option.map`, etc. Do NOT change it. New nested arms must guard with `when (tryDecodeModulePath innerExpr).IsSome` but NOT match the single-level case `Constructor(...)` directly.
- **Using `List.last` for `open` lookup:** The current code uses `path |> List.last` which is buggy for nested paths. Replace with `String.concat "."`.
- **Forgetting the App/FieldAccess arm:** The App pattern at line 2503 also needs a nested variant. Without it, `Outer.Inner.f arg` won't work as a function call (it'll hit the generic App arm instead).
- **Off-by-one in shortName extraction:** When stripping the prefix for `open` aliasing, use the underscore version of the path (not the dot version) plus 1 for the separator. E.g., `"Outer_Inner_foo"`.Substring(`"Outer_Inner"`.Length + 1) = `"foo"`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Path-to-underscore conversion | Custom segment joiner | `String.concat "_"` | Already available in F# stdlib |
| Dot-path joining | Custom joiner | `String.concat "."` | Already available |
| Nested AST traversal | Custom walker | `tryDecodeModulePath` recursive helper | Simple recursive pattern match is sufficient |

**Key insight:** The entire nested module qualified access problem reduces to: (a) ensuring the emitted names use the full path as prefix, and (b) decoding the left-associative FieldAccess chain back into that full path at elaboration time. Both are trivial once the conceptual model is clear.

## Common Pitfalls

### Pitfall 1: Single-Level Module Regression
**What goes wrong:** Changing `flattenDecls` to use `modName + "_" + name` as childPrefix instead of just `name` causes ALL single-level modules to double-prefix. E.g., `module List = let map = ...` produces `List_map` (correct). If the logic is wrong, it might produce `_List_map` or fail.
**Why it happens:** The guard `if modName = "" then name else modName + "_" + name` is correct. The edge case is the top-level call with `modName = ""`.
**How to avoid:** Verify single-level: when entering `ModuleDecl("List", ...)` from top level with `modName=""`, childPrefix should be `"List"`. When entering `ModuleDecl("Inner", ...)` from within `modName="Outer"`, childPrefix should be `"Outer_Inner"`.
**Warning signs:** Test 25-03 (`open Utils`) or any Prelude-using test fails.

### Pitfall 2: Open Single-Segment Regression
**What goes wrong:** After changing collectModuleMembers to use dotPath as map key, `open Utils` (single segment) must still work. The key `"Utils"` is a valid dot-path for a single segment.
**Why it happens:** `["Utils"] |> String.concat "." = "Utils"` — same as before. No regression.
**How to avoid:** Verify test 25-03 still passes.
**Warning signs:** `open Utils` stops resolving member names.

### Pitfall 3: shortName Extraction Off-by-One
**What goes wrong:** When generating `LetDecl(shortName, ...)` in OpenDecl handling, the short name is extracted by stripping the prefix from the qualified name. Using the dot-path length instead of underscore-path length gives wrong results.
**Why it happens:** `openedKey = "Outer.Inner"` (length 11), but `qualifiedName = "Outer_Inner_foo"` — the prefix is `"Outer_Inner"` (length 11, same length coincidentally for this example, but wrong in general if module names have different lengths).
**How to avoid:** Always convert the dot-path to underscore path before measuring: `let underPrefix = openedKey.Replace(".", "_")`.
**Warning signs:** `open Outer.Inner; double 5` resolves to wrong name or fails with unknown variable.

### Pitfall 4: Constructor False Positive in tryDecodeModulePath
**What goes wrong:** `Constructor("Some", Some argExpr, _)` has `arg = Some`, not `None`. The guard `Constructor(name, None, _)` correctly excludes data constructors with arguments. But if the helper isn't careful, `Some.value` could match.
**Why it happens:** `Constructor("Some", None, _)` is the pattern for a bare constructor name used as a module prefix (no argument). This is correct — module names parse as `Constructor(name, None, _)`.
**How to avoid:** The `tryDecodeModulePath` helper matches `Constructor(name, None, _)` — the `None` guard is critical. Keep it.
**Warning signs:** `Option.Some 5` misbehaves.

### Pitfall 5: Test File Must Be Added
**What goes wrong:** Writing the E2E test `.flt` file is required for TEST-02 compliance. The file must be at `tests/compiler/59-01-nested-module-access.flt`.
**Why it happens:** The test runner discovers test files by filesystem glob.
**How to avoid:** Create the `.flt` file as part of the implementation.
**Warning signs:** Phase marked incomplete, no new test covering nested access.

## Code Examples

### Complete collectModuleMembers Rewrite

```fsharp
// Phase 41/59: collectModuleMembers — first pass scan to build a map from dot-path module key
// to list of underscore-qualified member names.
// e.g., "Outer.Inner" -> ["Outer_Inner_foo"; "Outer_Inner_bar"]
//        "List"        -> ["List_map"; "List_filter"; ...]
// Used by flattenDecls to resolve OpenDecl at compile time.
let private collectModuleMembers (decls: Ast.Decl list) : Map<string, string list> =
    let mutable result = Map.empty<string, string list>
    let rec scan (dotPath: string) (underPath: string) (ds: Ast.Decl list) =
        for d in ds do
            match d with
            | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
                let childDot   = if dotPath   = "" then name else dotPath   + "." + name
                let childUnder = if underPath = "" then name else underPath + "_" + name
                scan childDot childUnder innerDecls
            | Ast.Decl.LetDecl(name, _, _) when underPath <> "" && name <> "_" ->
                let qualifiedName = underPath + "_" + name
                let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
                result <- Map.add dotPath (existing @ [qualifiedName]) result
            | Ast.Decl.LetRecDecl(bindings, _) when underPath <> "" ->
                for (name, _, _, _, _) in bindings do
                    let qualifiedName = underPath + "_" + name
                    let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
                    result <- Map.add dotPath (existing @ [qualifiedName]) result
            | _ -> ()
    scan "" "" decls
    result
```

### Complete flattenDecls Rewrite (ModuleDecl arm only)

```fsharp
// In flattenDecls, change the ModuleDecl arm:
| Ast.Decl.ModuleDecl(name, innerDecls, _) ->
    let childPrefix = if modName = "" then name else modName + "_" + name
    flattenDecls moduleMembers childPrefix innerDecls
```

The LetDecl and LetRecDecl arms remain unchanged — they already use `modName + "_" + name` which is now the full prefix.

### Complete OpenDecl arm in flattenDecls

```fsharp
| Ast.Decl.OpenDecl(path, s) when not (List.isEmpty path) ->
    // Join ALL path segments with "." to form the map key (e.g., "Outer.Inner")
    // Previously used List.last which only worked for single-level open.
    let openedKey = path |> String.concat "."
    match Map.tryFind openedKey moduleMembers with
    | Some qualifiedNames ->
        let underscorePrefix = openedKey.Replace(".", "_")
        qualifiedNames |> List.map (fun qualifiedName ->
            let shortName = qualifiedName.Substring(underscorePrefix.Length + 1)
            Ast.Decl.LetDecl(shortName, Ast.Var(qualifiedName, s), s))
    | None -> []
```

### tryDecodeModulePath Helper

```fsharp
// Phase 59: Decode a nested module path expression into a segment list.
// FieldAccess(FieldAccess(Constructor("Outer"), "Inner"), "foo") → Some ["Outer"; "Inner"; "foo"]
// Returns None if innermost expression is not a no-arg Constructor (i.e., it's a real record).
let rec private tryDecodeModulePath (expr: Expr) : string list option =
    match expr with
    | Constructor(name, None, _) -> Some [name]
    | FieldAccess(inner, field, _) ->
        match tryDecodeModulePath inner with
        | Some segments -> Some (segments @ [field])
        | None -> None
    | _ -> None
```

### New FieldAccess Elaboration Arm (insert before line 3232)

```fsharp
// Phase 59: Nested qualified access — Outer.Inner.foo → Var("Outer_Inner_foo")
// Handles chains of any depth. Must come BEFORE the single-level Constructor arm.
| FieldAccess(innerExpr, memberName, span)
    when (match innerExpr with
          | FieldAccess _ -> (tryDecodeModulePath innerExpr).IsSome
          | _ -> false) ->
    let segments = (tryDecodeModulePath innerExpr).Value @ [memberName]
    if Map.containsKey memberName env.TypeEnv then
        elaborateExpr env (Constructor(memberName, None, span))
    else
        elaborateExpr env (Var(segments |> String.concat "_", span))
```

Note: The guard restricts to `FieldAccess` inner expressions only (not `Constructor`), so the single-level arm `FieldAccess(Constructor(...), ...)` is NOT caught by this new arm. Alternatively, a simpler guard:

```fsharp
| FieldAccess(FieldAccess(_, _, _) as innerExpr, memberName, span)
    when (tryDecodeModulePath innerExpr).IsSome ->
    let segments = (tryDecodeModulePath innerExpr).Value @ [memberName]
    if Map.containsKey memberName env.TypeEnv then
        elaborateExpr env (Constructor(memberName, None, span))
    else
        elaborateExpr env (Var(segments |> String.concat "_", span))
```

This pattern uses `FieldAccess(FieldAccess(...) as innerExpr, ...)` to match only when the inner expression is itself a FieldAccess — i.e., at least 2-deep nesting.

### New App/FieldAccess Elaboration Arm (insert before line 2503)

```fsharp
// Phase 59: Nested qualified function call — Outer.Inner.f arg → App(Var("Outer_Inner_f"), arg)
| App(FieldAccess(FieldAccess(_, _, _) as innerExpr, memberName, fspan), argExpr, span)
    when (tryDecodeModulePath innerExpr).IsSome
      && not (Map.containsKey memberName env.TypeEnv) ->
    let segments = (tryDecodeModulePath innerExpr).Value @ [memberName]
    let varName = segments |> String.concat "_"
    elaborateExpr env (App(Var(varName, fspan), argExpr, span))
```

### E2E Test File Content

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
module Outer =
    module Inner =
        let value = 42
        let double x = x + x

let a = Outer.Inner.value
let b = Outer.Inner.double 5
let _ = println (to_string a)
let _ = println (to_string b)
// --- Output:
42
10
0
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
module Outer =
    module Inner =
        let value = 99
        let triple x = x + x + x

open Outer.Inner
let _ = println (to_string value)
let _ = println (to_string (triple 4))
// --- Output:
99
12
0
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Only innermost module name as prefix (`Inner_foo`) | Full path prefix (`Outer_Inner_foo`) | Phase 59 | Enables multi-level qualified access |
| `List.last` for `open` path lookup | `String.concat "."` for full key | Phase 59 | Fixes ambiguity with same-named inner modules |
| No nested FieldAccess arm | `FieldAccess(FieldAccess(...))` pattern | Phase 59 | `Outer.Inner.foo` now desugars correctly |

**Deprecated/outdated after Phase 59:**
- `path |> List.last` in OpenDecl arm: replaced by `path |> String.concat "."`

## Open Questions

1. **Three-level nesting (`A.B.C.foo`)**
   - What we know: `tryDecodeModulePath` is recursive — handles arbitrary depth by design
   - What's unclear: No test requires 3+ levels; requirements mention only 2-level (Outer.Inner)
   - Recommendation: Implement generically with the recursive helper. No extra work needed if `tryDecodeModulePath` is used.

2. **Constructor qualified access in nested modules (`Outer.Inner.Ctor`)**
   - What we know: The existing single-level arm checks `Map.containsKey memberName env.TypeEnv` to decide constructor vs variable
   - What's unclear: Requirements don't mention nested constructors
   - Recommendation: Include the TypeEnv check in the new nested arm for correctness. Phase requirements only test value/function access.

3. **Program.fs expandImports impact**
   - What we know: `expandImports` recursively expands `FileImportDecl` and rewraps `ModuleDecl` at line 96. It does NOT touch module names or create qualified names — that's all in `collectModuleMembers`/`flattenDecls`.
   - What's unclear: Whether any import-based test uses nested modules
   - Recommendation: No changes to `Program.fs` needed for Phase 59. The `expandImports` function preserves the AST structure faithfully; the naming fix in `Elaboration.fs` handles all cases.

## Sources

### Primary (HIGH confidence)
- Direct code inspection of `Elaboration.fs` lines 4248-4297 (collectModuleMembers, flattenDecls) — source of truth
- Direct code inspection of `Elaboration.fs` lines 3228-3261 (FieldAccess elaboration arms) — source of truth
- Direct code inspection of `Elaboration.fs` lines 2499-2505 (App/FieldAccess elaboration arm) — source of truth
- Direct code inspection of `deps/FunLang/src/FunLang/Parser.fsy` lines 370, 422 — confirmed left-associative FieldAccess parse
- Direct code inspection of `deps/FunLang/src/FunLang/Ast.fs` lines 91, 97 — confirmed Constructor and FieldAccess AST shapes
- Direct code inspection of `tests/compiler/58-01-open-multi-segment.flt` — confirmed current open multi-segment behavior

### Secondary (MEDIUM confidence)
- Analysis of `tests/compiler/25-*.flt` tests — confirmed single-level module behavior that must not regress
- Analysis of existing 232 test files — count confirmed, pattern of module usage understood

## Metadata

**Confidence breakdown:**
- Code paths and change locations: HIGH — all key functions read directly from source
- AST structure for nested access: HIGH — parser grammar and AST DU confirmed
- Regression risk for single-level modules: HIGH — analyzed invariant (modName="" guard unchanged)
- shortName extraction math: HIGH — verified with concrete example
- Program.fs impact: HIGH — expandImports only rewraps ModuleDecl structure, no naming

**Research date:** 2026-04-01
**Valid until:** Until Elaboration.fs module handling is refactored (stable — these functions haven't changed since Phase 41/58)
