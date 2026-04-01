# Phase 53: Prelude Sync & E2E Tests - Research

**Researched:** 2026-04-01
**Domain:** FunLangCompiler Prelude file sync + typeclass E2E compilation
**Confidence:** HIGH

## Summary

Phase 53 has three distinct parts: (1) copy `Typeclass.fun` from FunLang's Prelude to FunLangCompiler's Prelude, (2) register it in the CLI's ordered-load list, and (3) write E2E tests for `show`/`eq`/`deriving Show`. The first two parts are mechanical file operations. The third part requires that `show` and `eq` actually compile end-to-end — they do, because `elaborateTypeclasses` (Phase 52) already converts `InstanceDecl` methods into `LetDecl` bindings, so after Prelude loads, `show` and `eq` become ordinary functions available to user code.

The `deriving Show` requirement is more complex. FunLangCompiler has no TypeCheck pass; `elaborateTypeclasses` currently drops `DerivingDecl` nodes with `[]`. For `deriving Show MyType` to work, `elaborateTypeclasses` must be enhanced: it needs a first pass to collect constructor lists from `TypeDecl` nodes, then generate the `show` function body as a `LetDecl` on the second pass. This is exactly what FunLang's `TypeCheck.fs` does (lines 1153–1212), but FunLangCompiler must replicate that AST-generation logic inside `elaborateTypeclasses` since it has no type checker.

The `show` and `eq` function name collision is not a problem: the Prelude defines `show` for each primitive type as separate `LetDecl` bindings (the last one wins), and `deriving Show` for a user ADT generates another `show` binding that shadows the prelude-level `show`. This matches FunLang's evaluation semantics exactly.

**Primary recommendation:** Three-task plan: (1) copy Typeclass.fun + add to ordered list, (2) enhance `elaborateTypeclasses` for `DerivingDecl` → `LetDecl` generation, (3) write E2E tests 53-01 through 53-05.

## Standard Stack

This is an internal codebase change — no external libraries are involved.

### Core
| File | Purpose | What Changes |
|------|---------|--------------|
| `Prelude/Typeclass.fun` | Typeclass prelude (new file, copied from FunLang) | Create |
| `src/FunLangCompiler.Cli/Program.fs` | Add `"Typeclass.fun"` to ordered prelude load list | ~1 line |
| `src/FunLangCompiler.Compiler/Elaboration.fs` | Enhance `elaborateTypeclasses` to handle `DerivingDecl` | ~30 lines |
| `tests/compiler/53-01-*.flt/.fun` through `53-05-*.flt/.fun` | E2E test files for show/eq/deriving | New files |

### Supporting
| Tool | Purpose |
|------|---------|
| `dotnet build src/FunLangCompiler.Cli/` | Verify full project compiles |
| `cd tests/compiler && flit 53-*.flt` (or equivalent runner) | Run the new E2E tests |

## Architecture Patterns

### Recommended Project Structure

No new directories needed. Changes touch existing files plus new test files:

```
Prelude/
└── Typeclass.fun            # PRE-01: copied verbatim from FunLang/Prelude/Typeclass.fun

src/FunLangCompiler.Cli/
└── Program.fs               # PRE-02: "Typeclass.fun" added to ordered array

src/FunLangCompiler.Compiler/
└── Elaboration.fs           # TEST-03 prerequisite: DerivingDecl expansion

tests/compiler/
├── 53-01-show-int.flt/.fun
├── 53-02-show-string.flt/.fun
├── 53-03-eq-int.flt/.fun
├── 53-04-eq-string.flt/.fun
└── 53-05-deriving-show.flt/.fun
```

### Pattern 1: Typeclass.fun Content (exact copy from FunLang)

The file to place at `Prelude/Typeclass.fun` (verified from `deps/FunLang/Prelude/Typeclass.fun`):

```fun
typeclass Show 'a =
    | show : 'a -> string

instance Show int =
    let show x = to_string x

instance Show bool =
    let show x = if x then "true" else "false"

instance Show string =
    let show x = x

instance Show char =
    let show x = to_string x

typeclass Eq 'a =
    | eq : 'a -> 'a -> bool

instance Eq int =
    let eq x = fun y -> x = y

instance Eq bool =
    let eq x = fun y -> x = y

instance Eq string =
    let eq x = fun y -> x = y

instance Eq char =
    let eq x = fun y -> x = y
```

After `elaborateTypeclasses`, this becomes four `show` LetDecl bindings and four `eq` LetDecl bindings. The last definition of `show` in scope (Show char) shadows earlier ones, but all four are generated — this matches FunLang's behavior.

### Pattern 2: Ordered Load Array Change

**What:** In `Program.fs` line 167–169, add `"Typeclass.fun"` to the ordered array.

**When:** Typeclass.fun has no constructor dependencies (it uses only builtins: `to_string`, boolean comparisons, string comparisons). It can be loaded first.

```fsharp
// Source: src/FunLangCompiler.Cli/Program.fs lines 167-169
let ordered = [| "Typeclass.fun"; "Core.fun"; "Option.fun"; "Result.fun"; "String.fun"; "Char.fun";
                 "Hashtable.fun"; "HashSet.fun"; "MutableList.fun"; "Queue.fun";
                 "StringBuilder.fun"; "List.fun"; "Array.fun" |]
```

Placing `Typeclass.fun` first is safe: Show/Eq instances use `to_string` (a builtin, always available), string concatenation, and boolean if-then-else — no constructors from Option/Result/List.

### Pattern 3: DerivingDecl Expansion in elaborateTypeclasses

**What:** `deriving Show MyType` (syntax: `DerivingDecl(typeName, classNames, span)`) must generate a `let show x = match x with | C1 -> "C1" | C2 -> "C2" | ...` as a `LetDecl`.

**The challenge:** `elaborateTypeclasses` only sees `Decl list`. To generate the match arms, it needs the constructor list for `typeName`. This requires a pre-pass that scans `TypeDecl` nodes.

**Solution:** Two-pass approach inside `elaborateTypeclasses`:

```fsharp
// Source: adapted from FunLang/src/FunLang/TypeCheck.fs lines 1153-1212
let rec elaborateTypeclasses (decls: Ast.Decl list) : Ast.Decl list =
    // Pass 1: collect constructor info for all TypeDecl nodes
    let ctorMap =
        decls |> List.collect (fun d ->
            match d with
            | Ast.Decl.TypeDecl(Ast.TypeDecl(name, _, ctors, _, _)) ->
                [(name, ctors)]
            | _ -> [])
        |> Map.ofList
    // Pass 2: transform decls
    decls |> List.collect (fun decl ->
        match decl with
        | Ast.Decl.TypeClassDecl _ -> []
        | Ast.Decl.InstanceDecl(_className, _instType, methods, _constraints, span) ->
            methods |> List.map (fun (methodName, methodBody) ->
                Ast.Decl.LetDecl(methodName, methodBody, span))
        | Ast.Decl.ModuleDecl(name, innerDecls, span) ->
            let instanceBindings =
                innerDecls |> List.collect (fun d ->
                    match d with
                    | Ast.Decl.InstanceDecl(_, _, methods, _, ispan) ->
                        methods |> List.map (fun (methodName, methodBody) ->
                            Ast.Decl.LetDecl(methodName, methodBody, ispan))
                    | _ -> [])
            [Ast.Decl.ModuleDecl(name, elaborateTypeclasses innerDecls, span)] @ instanceBindings
        | Ast.Decl.NamespaceDecl(path, innerDecls, span) ->
            [Ast.Decl.NamespaceDecl(path, elaborateTypeclasses innerDecls, span)]
        | Ast.Decl.DerivingDecl(typeName, classNames, span) ->
            classNames |> List.collect (fun className ->
                match className with
                | "Show" ->
                    match Map.tryFind typeName ctorMap with
                    | None -> []  // Unknown type: skip
                    | Some ctors ->
                        let clauses =
                            ctors |> List.map (fun ctor ->
                                match ctor with
                                | Ast.ConstructorDecl(ctorName, None, _) ->
                                    (Ast.VarOrConstructorPat(ctorName, span), None,
                                     Ast.String(ctorName, span))
                                | Ast.ConstructorDecl(ctorName, Some _, _) ->
                                    let vPat = Ast.VarPat("__v", span)
                                    let body = Ast.Add(Ast.String(ctorName + " ", span),
                                                       Ast.App(Ast.Var("show", span), Ast.Var("__v", span), span),
                                                       span)
                                    (Ast.ConstructorPat(ctorName, Some vPat, span), None, body)
                                | Ast.GadtConstructorDecl(ctorName, [], _, _) ->
                                    (Ast.VarOrConstructorPat(ctorName, span), None,
                                     Ast.String(ctorName, span))
                                | Ast.GadtConstructorDecl(ctorName, _, _, _) ->
                                    let vPat = Ast.VarPat("__v", span)
                                    let body = Ast.Add(Ast.String(ctorName + " ", span),
                                                       Ast.App(Ast.Var("show", span), Ast.Var("__v", span), span),
                                                       span)
                                    (Ast.ConstructorPat(ctorName, Some vPat, span), None, body))
                        let matchExpr = Ast.Match(Ast.Var("__x", span), clauses, span)
                        let showBody = Ast.Lambda("__x", matchExpr, span)
                        [Ast.Decl.LetDecl("show", showBody, span)]
                | "Eq" ->
                    match Map.tryFind typeName ctorMap with
                    | None -> []
                    | Some ctors ->
                        let clauses =
                            ctors |> List.map (fun ctor ->
                                match ctor with
                                | Ast.ConstructorDecl(ctorName, None, _) ->
                                    let pat = Ast.TuplePat([Ast.VarOrConstructorPat(ctorName, span); Ast.VarOrConstructorPat(ctorName, span)], span)
                                    (pat, None, Ast.Bool(true, span))
                                | Ast.ConstructorDecl(ctorName, Some _, _) ->
                                    let pat = Ast.TuplePat([Ast.ConstructorPat(ctorName, Some(Ast.VarPat("__a", span)), span);
                                                             Ast.ConstructorPat(ctorName, Some(Ast.VarPat("__b", span)), span)], span)
                                    let body = Ast.App(Ast.App(Ast.Var("eq", span), Ast.Var("__a", span), span), Ast.Var("__b", span), span)
                                    (pat, None, body)
                                | _ -> (Ast.WildcardPat span, None, Ast.Bool(false, span)))
                        let wildcard = (Ast.WildcardPat span, None, Ast.Bool(false, span))
                        let matchExpr = Ast.Match(Ast.Tuple([Ast.Var("__x", span); Ast.Var("__y", span)], span),
                                                   clauses @ [wildcard], span)
                        let eqBody = Ast.Lambda("__x", Ast.Lambda("__y", matchExpr, span), span)
                        [Ast.Decl.LetDecl("eq", eqBody, span)]
                | _ -> [])
        | other -> [other])
```

**Important:** Check the exact pattern constructor name used in FunLangCompiler for nullary constructor patterns. In FunLang TypeCheck.fs it uses `Ast.ConstructorPat(ctorName, None, span)` for nullary. Verify via Ast.fs that this maps correctly.

### Pattern 4: Test File Format (E2E Compilation Tests)

Tests use a `.flt` fixture file and a separate `.fun` source file. Successful compilation tests use the `OUTBIN` pattern:

```
// Test: show on int from Prelude
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %S/53-01-show-int.fun -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
// --- Output:
42
true
hello
0
```

The `%S` placeholder is the directory containing the `.flt` file.

### Anti-Patterns to Avoid

- **Loading Typeclass.fun after Option.fun:** No benefit and may cause confusion. Load it first since it has zero constructor dependencies.
- **Adding Typeclass.fun to ordered list without copying the file:** The `Array.choose` in Program.fs silently skips missing files — no error. Test would fail silently.
- **Assuming `show` disambiguation works automatically:** There is no dispatch. The last-defined `show` LetDecl wins. For user code that calls `show` on an int after `deriving Show Color`, the `show` binding points to the Color show function. This is FunLang's exact behavior — accept it.
- **Using pattern constructors from Ast that may not exist:** The `VarOrConstructorPat` vs `ConstructorPat` distinction matters. Nullary constructors in patterns: use `Ast.ConstructorPat(name, None, span)` as this is what the parser generates and what Elaboration.fs handles for ADT pattern matching.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Typeclass.fun content | Write from scratch | Copy verbatim from `FunLang/Prelude/Typeclass.fun` | Source of truth is FunLang; any difference = bug |
| DerivingDecl logic | Invent new approach | Port directly from `FunLang/TypeCheck.fs` lines 1153–1212 | Already validated; exact same AST nodes |
| Show function body generation | Custom codegen | Generate `Ast.Lambda("__x", Ast.Match(...))` as FunLang does | Match arms compile cleanly through existing elaborateExpr |

**Key insight:** FunLang already has a validated implementation of every transformation needed here. Direct port > reimplementation.

## Common Pitfalls

### Pitfall 1: Prelude Load Order — Typeclass.fun Must Be In ordered Array

**What goes wrong:** `Typeclass.fun` exists in `Prelude/` but is NOT in the `ordered` array in Program.fs (lines 167–169). The `Array.choose` only loads files in the array. Files not in the array are silently skipped.

**Why it happens:** The ordered array was designed for dependency ordering; Typeclass.fun was not present when it was written.

**How to avoid:** Add `"Typeclass.fun"` as the first entry in the ordered array. Place before Core.fun since Typeclass.fun has no dependencies.

**Warning signs:** Tests 53-01 and 53-02 fail with `[Elaboration] ... unbound variable 'show'` — this means Typeclass.fun is not being loaded even if the file exists.

### Pitfall 2: DerivingDecl Drops Silently (Phase 52 behavior)

**What goes wrong:** `elaborateTypeclasses` currently returns `[]` for `DerivingDecl` nodes. If TEST-03 uses `deriving Show Color`, the `show` function is never generated, and the program will fail with an unbound variable error.

**Why it happens:** Phase 52 comment says `"auto-derivation handled at parse time"` — this was a placeholder. In FunLang, deriving is handled at type-check time (not parse time). FunLangCompiler has no type checker.

**How to avoid:** Implement DerivingDecl expansion in `elaborateTypeclasses` using the two-pass approach described above.

**Warning signs:** Test 53-05 fails with `[Elaboration] ... unbound variable 'show'` even though Typeclass.fun is loaded.

### Pitfall 3: Two-Pass ctorMap Only Sees Current Level

**What goes wrong:** If the `ctorMap` pre-pass in `elaborateTypeclasses` only scans the top-level decls, a `deriving Show MyType` inside a `ModuleDecl` won't find the `TypeDecl` for `MyType` (which might be at top level).

**Why it happens:** The pre-pass scans `decls` at the current level only. The `DerivingDecl` is also at the current level (directly after the TypeDecl), so this works for the standard case.

**How to avoid:** For Phase 53, all test programs use top-level TypeDecl + top-level DerivingDecl. This is sufficient. No need to handle cross-scope deriving for now.

**Warning signs:** Deriving inside a module fails but top-level deriving works.

### Pitfall 4: Broken Prelude/Prelude Symlink

**What goes wrong:** The `Prelude/Prelude` symlink (untracked, broken) resolves to a circular path. It's listed in `git status` as `?? Prelude/Prelude`.

**Why it happens:** The symlink `Prelude/Prelude@ -> ../FunLangCompiler/Prelude` was created previously and is broken.

**How to avoid:** Do NOT remove or modify this symlink in Phase 53 — it's untracked and irrelevant to the task. The Prelude loading in Program.fs uses `walkUp` from the input file's directory to find the `Prelude/` dir — it finds `Prelude/` which is correct.

**Warning signs:** If the symlink interferes, directory listing would show `Typeclass.fun` not found even after copying.

### Pitfall 5: ConstructorPat vs VarOrConstructorPat for Nullary Patterns

**What goes wrong:** In generated match arms for `deriving Show`, nullary constructors need the right pattern AST node. Using wrong pattern type causes pattern matching compilation to fail.

**Why it happens:** FunLang TypeCheck.fs uses `Ast.ConstructorPat(ctorName, None, span)` for nullary constructors in patterns. This should also work in FunLangCompiler's elaboration.

**How to avoid:** Verify against `Ast.fs` and existing FunLangCompiler test fixtures that `ConstructorPat(name, None, span)` is the correct AST node for nullary constructor patterns. Existing tests like `35-08-list-tryfind-choose.fun` use `| Some v ->` and `| None ->` patterns which resolve to `ConstructorPat("Some", Some vPat, ...)` and `ConstructorPat("None", None, ...)`.

**Warning signs:** DerivingDecl test compiles but produces wrong output (match falls through to wrong arm).

## Code Examples

### E2E Test 53-01: show on int

**Source** (`tests/compiler/53-01-show-int.fun`):
```fun
let s = show 42
let _ = println s
```

**Fixture** (`tests/compiler/53-01-show-int.flt`):
```
// Test: show on int from Prelude
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %S/53-01-show-int.fun -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
// --- Output:
42
0
```

### E2E Test 53-02: show on string

**Source** (`tests/compiler/53-02-show-string.fun`):
```fun
let s = show "hello"
let _ = println s
```

**Expected output:** `hello\n0`

### E2E Test 53-03: eq on int

**Source** (`tests/compiler/53-03-eq-int.fun`):
```fun
let a = eq 42 42
let b = eq 42 43
let _ = println (to_string a)
let _ = println (to_string b)
```

**Expected output:** `true\nfalse\n0`

### E2E Test 53-04: eq on string

**Source** (`tests/compiler/53-04-eq-string.fun`):
```fun
let a = eq "hello" "hello"
let b = eq "hello" "world"
let _ = println (to_string a)
let _ = println (to_string b)
```

**Expected output:** `true\nfalse\n0`

### E2E Test 53-05: deriving Show

**Source** (`tests/compiler/53-05-deriving-show.fun`):
```fun
type Color = | Red | Green | Blue
deriving Show Color

let _ = println (show Red)
let _ = println (show Green)
let _ = println (show Blue)
```

**Expected output:** `Red\nGreen\nBlue\n0`

### The Show Instance as LetDecl (after elaborateTypeclasses)

```fsharp
// instance Show int = let show x = to_string x
// Becomes:
Ast.Decl.LetDecl("show", Ast.Lambda("x", Ast.App(Ast.Var("to_string"), Ast.Var("x"), span), span), span)

// instance Eq int = let eq x = fun y -> x = y
// Becomes:
Ast.Decl.LetDecl("eq", Ast.Lambda("x", Ast.Lambda("y", Ast.Eq(Ast.Var("x"), Ast.Var("y"), span), span), span), span)
```

### Prelude Ordered Array (after change)

```fsharp
// Source: src/FunLangCompiler.Cli/Program.fs lines 167-169
let ordered = [| "Typeclass.fun"; "Core.fun"; "Option.fun"; "Result.fun"; "String.fun"; "Char.fun";
                 "Hashtable.fun"; "HashSet.fun"; "MutableList.fun"; "Queue.fun";
                 "StringBuilder.fun"; "List.fun"; "Array.fun" |]
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| DerivingDecl dropped (`[]`) | DerivingDecl generates LetDecl | Phase 53 | `deriving Show MyType` compiles |
| No Typeclass prelude | Typeclass.fun in Prelude + ordered list | Phase 53 | `show`/`eq` available in all programs |
| 12 Prelude modules | 13 Prelude modules | Phase 53 | +1 Typeclass.fun |

**Current state (before Phase 53):**
- `DerivingDecl _ -> []` in `elaborateTypeclasses` (line 4232 of Elaboration.fs)
- `Typeclass.fun` does NOT exist in `FunLangCompiler/Prelude/`
- ordered array does NOT include `"Typeclass.fun"`

## Open Questions

1. **ConstructorPat nullary pattern in generated show match arms**
   - What we know: FunLang TypeCheck.fs uses `Ast.ConstructorPat(ctorName, None, span)` for nullary constructors in generated patterns.
   - What's unclear: Whether FunLangCompiler's Elaboration.fs handles `ConstructorPat("Red", None, span)` correctly in generated match expressions (it should — existing ADT tests use this).
   - Recommendation: Verify by checking one existing test that has nullary constructor patterns, e.g., `Option.None` in any test. Elaborate the DerivingDecl Show generation; if any test fails, add a debug print to see what pattern type is generated.

2. **show function shadowing: which instance wins**
   - What we know: After loading Typeclass.fun + user code with `deriving Show Color`, there will be multiple `let show = ...` bindings. The last binding wins.
   - What's unclear: For TEST-01 (show on int), the prelude defines Show int last... but Show char comes last in Typeclass.fun. So `show 42` would call the char's show function (which calls `to_string` too).
   - Recommendation: This is actually fine since `to_string` works for both int and char in FunLangCompiler (it dispatches on type). The test 53-01 expects `show 42` → `"42"`. Since all primitive Show instances call `to_string x`, they all produce the same result regardless of which binding is active. Verify empirically.
   - If this is a problem: Test can instead define its own show inline to bypass the prelude shadowing issue.

3. **GadtConstructorDecl in DerivingDecl expansion**
   - What we know: `GadtConstructorDecl` has a different field layout than `ConstructorDecl`.
   - What's unclear: Whether any Phase 53 test will use GADT types with deriving. Tests 53-05 uses a simple nullary ADT (`Color`), so GadtConstructorDecl is not needed for the required tests.
   - Recommendation: Handle `GadtConstructorDecl` gracefully (skip or treat as unary) but don't block Phase 53 on it. Add a comment: `// GadtConstructorDecl: skip for now`.

## Sources

### Primary (HIGH confidence)
- `deps/FunLang/Prelude/Typeclass.fun` — exact content to copy (verified, 30 lines)
- `src/FunLangCompiler.Cli/Program.fs` lines 167–174 — ordered prelude array (verified)
- `src/FunLangCompiler.Compiler/Elaboration.fs` line 4232 — `DerivingDecl _ -> []` current behavior (verified)
- `deps/FunLang/src/FunLang/TypeCheck.fs` lines 1153–1212 — reference implementation of DerivingDecl expansion
- `deps/FunLang/tests/flt/file/typeclass/typeclass-deriving-show.flt` — verified expected outputs for deriving Show
- `deps/FunLang/tests/flt/file/typeclass/typeclass-deriving-eq.flt` — verified expected outputs for deriving Eq

### Secondary (MEDIUM confidence)
- `tests/compiler/35-08-list-tryfind-choose.flt` — verified OUTBIN test pattern for successful compilation tests
- `deps/FunLang/src/FunLang/Elaborate.fs` lines 248–273 — confirms `DerivingDecl` is dropped in eval pipeline (type check handles it); FunLangCompiler has no type check, so must handle here

## Metadata

**Confidence breakdown:**
- Prelude file copy (PRE-01): HIGH — exact file exists, exact content known
- Ordered list update (PRE-02): HIGH — exact line in Program.fs found, dependency analysis complete
- show/eq E2E tests (TEST-01, TEST-02, TEST-03, TEST-04): HIGH — test format confirmed, expected values known
- DerivingDecl expansion (TEST-05 prerequisite): HIGH — reference implementation in FunLang TypeCheck.fs lines 1153–1212 is directly portable; AST types are shared

**Research date:** 2026-04-01
**Valid until:** 2026-05-01 (stable internal codebase)
