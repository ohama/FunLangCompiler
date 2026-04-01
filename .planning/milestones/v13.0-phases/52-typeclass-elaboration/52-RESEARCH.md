# Phase 52: Typeclass Elaboration - Research

**Researched:** 2026-04-01
**Domain:** F# AST transformation — LangThree typeclass/instance declarations → LetDecl bindings
**Confidence:** HIGH

## Summary

Phase 52 implements `elaborateTypeclasses` in FunLangCompiler, which is a pre-processing step that transforms typeclass-related AST nodes into plain `LetDecl` bindings before `elaborateProgram` sees the AST. The canonical reference implementation already exists in LangThree's `Elaborate.fs` (lines 243–273). The transformation is a direct structural traversal with no type inference or name resolution — it is a purely syntactic rewrite.

The LangThree implementation handles four cases: `TypeClassDecl` → `[]` (removed), `InstanceDecl` → one `LetDecl` per method, `ModuleDecl` → recurse + hoist instance bindings to outer scope, `NamespaceDecl` → recurse in-place, `DerivingDecl` → `[]` (removed). FunLangCompiler's `Elaboration.fs` already has stub comments at lines 4101–4103 noting that TypeClassDecl/InstanceDecl/DerivingDecl will be handled here. The `elaborateProgram` function in `Program.fs` (line 206) calls `Elaboration.elaborateProgram expandedAst` — the new `elaborateTypeclasses` call must be inserted between `expandImports` and `elaborateProgram`.

The FunLangCompiler uses LangThree's `Ast` module directly (via project reference), so all AST types — `Ast.Decl.TypeClassDecl`, `Ast.Decl.InstanceDecl`, `Ast.Decl.DerivingDecl`, `Ast.Decl.LetDecl`, `Ast.Decl.ModuleDecl`, `Ast.Decl.NamespaceDecl` — are shared. No new type definitions are needed.

**Primary recommendation:** Add `elaborateTypeclasses` to `Elaboration.fs` as a public function (verbatim copy of LangThree's logic adapted for the `Ast.Decl.` prefix style), then wire it in `Program.fs` between `expandImports` and `elaborateProgram`.

## Standard Stack

This is an internal codebase transformation — no external libraries are involved.

### Core
| File | Purpose | What Changes |
|------|---------|--------------|
| `src/FunLangCompiler.Compiler/Elaboration.fs` | Add `elaborateTypeclasses` function (public) | New function, ~25 lines |
| `src/FunLangCompiler.Cli/Program.fs` | Wire `elaborateTypeclasses` into compile pipeline | Add one call at line ~206 |

### Supporting
| Tool | Purpose |
|------|---------|
| `dotnet build src/FunLangCompiler.Cli/` | Verify the full project compiles |
| `tests/compiler/52-*.flt + *.fun` | E2E tests confirming the transformation |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Adding to `Elaboration.fs` | Adding to `Program.fs` directly | Program.fs is the CLI driver; Elaboration.fs is the correct home for compiler passes — keep function in Elaboration.fs for consistency with `elaborateProgram` |
| Separate new `Typeclass.fs` | Inline in Elaboration.fs | Not needed; function is small (~25 lines); no new types required |

## Architecture Patterns

### Recommended Project Structure

No new files needed. Changes touch exactly two existing files:

```
src/FunLangCompiler.Compiler/
└── Elaboration.fs      # Add elaborateTypeclasses before elaborateProgram

src/FunLangCompiler.Cli/
└── Program.fs          # Wire elaborateTypeclasses between expandImports and elaborateProgram
```

### Pattern 1: elaborateTypeclasses — Direct Port from LangThree

**What:** A `List.collect` traversal that filters/transforms Decl nodes before `elaborateProgram`.
**When to use:** Called once in `Program.fs` on the `Ast.Module` after import expansion, before `elaborateProgram`.

The LangThree implementation (verbatim, `Elaborate.fs` lines 248–273):

```fsharp
// Source: /Users/ohama/vibe-coding/LangThree/src/LangThree/Elaborate.fs:248
let rec elaborateTypeclasses (decls: Decl list) : Decl list =
    decls |> List.collect (fun decl ->
        match decl with
        | TypeClassDecl(_, _, _, _, _) ->
            []
        | InstanceDecl(_className, _instType, methods, _constraints, span) ->
            methods |> List.map (fun (methodName, methodBody) ->
                LetDecl(methodName, methodBody, span))
        | ModuleDecl(name, innerDecls, span) ->
            let instanceBindings =
                innerDecls |> List.collect (fun d ->
                    match d with
                    | InstanceDecl(_, _, methods, _, ispan) ->
                        methods |> List.map (fun (methodName, methodBody) ->
                            LetDecl(methodName, methodBody, ispan))
                    | _ -> [])
            [ModuleDecl(name, elaborateTypeclasses innerDecls, span)] @ instanceBindings
        | NamespaceDecl(path, innerDecls, span) ->
            [NamespaceDecl(path, elaborateTypeclasses innerDecls, span)]
        | DerivingDecl(_, _, _) ->
            []
        | other -> [other])
```

In FunLangCompiler's `Elaboration.fs`, use the `Ast.Decl.` prefix:

```fsharp
// Source: adapated from /Users/ohama/vibe-coding/LangThree/src/LangThree/Elaborate.fs
let rec elaborateTypeclasses (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun decl ->
        match decl with
        | Ast.Decl.TypeClassDecl _ ->
            []
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
        | Ast.Decl.DerivingDecl _ ->
            []
        | other -> [other])
```

### Pattern 2: Pipeline Wiring in Program.fs

**What:** Modify `elaborateProgram` call site to run `elaborateTypeclasses` first.
**When to use:** Applies after `expandImports`, before `elaborateProgram`.

Current code in `Program.fs` (line 206):
```fsharp
let mlirMod = Elaboration.elaborateProgram expandedAst
```

New code:
```fsharp
let tcExpandedAst =
    match expandedAst with
    | Ast.Module(ds, s) -> Ast.Module(Elaboration.elaborateTypeclasses ds, s)
    | Ast.NamedModule(nm, ds, s) -> Ast.NamedModule(nm, Elaboration.elaborateTypeclasses ds, s)
    | Ast.NamespacedModule(nm, ds, s) -> Ast.NamespacedModule(nm, Elaboration.elaborateTypeclasses ds, s)
    | other -> other
let mlirMod = Elaboration.elaborateProgram tcExpandedAst
```

### Pattern 3: InstanceDecl Method Name — No Mangling

**What:** In LangThree's `elaborateTypeclasses`, method names are used **as-is** from the instance declaration. There is NO name mangling in the elaboration step. The method name in `InstanceDecl.methods` (e.g., `"show"` in `instance Show int = let show x = ...`) becomes `LetDecl("show", ...)`.

**Key insight:** The method names are already mangled at the **parser/typecheck** level in LangThree — the instance method name is literally what the programmer wrote. `elaborateTypeclasses` is a pure desugaring step that does not rename anything.

Example:
```
instance Show int =
    let show x = to_string x
```
Becomes: `LetDecl("show", Lambda("x", App(Var "to_string", Var "x")), span)`

### Anti-Patterns to Avoid

- **Name mangling in elaborateTypeclasses:** Do NOT add `className + "_" + typeName` prefixes. LangThree's implementation uses raw method names. Mangling happens elsewhere (if at all).
- **Modifying NamespacedModule (Module variant):** `NamespacedModule` is a `Module` variant (top-level container), not a `Decl`. `elaborateTypeclasses` operates on `Decl list`. The Module variant unwrapping happens in `Program.fs` when extracting `ds`, not inside `elaborateTypeclasses`.
- **Putting elaborateTypeclasses logic in Program.fs:** The function belongs in `Elaboration.fs` for consistency with `elaborateProgram`. Program.fs should only call it.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| TypeClass → LetDecl transformation | Custom name-mangling logic | Direct port of LangThree's elaborateTypeclasses | LangThree's version is tested; same Ast types are shared |
| Module recursion | Ad-hoc recursive descent | `List.collect` with pattern match | Matches the LangThree pattern exactly |

**Key insight:** This transformation is already solved in LangThree. Port it directly; do not invent a new approach.

## Common Pitfalls

### Pitfall 1: Wrong Placement of elaborateTypeclasses
**What goes wrong:** Calling `elaborateTypeclasses` inside `elaborateProgram` instead of before it causes `prePassDecls` to still see `TypeClassDecl`/`InstanceDecl` nodes (currently ignored with `| _ -> ()`). While it won't crash, the pipeline would have dead code and potential ordering issues.
**Why it happens:** The natural instinct is to add the call inside `elaborateProgram`.
**How to avoid:** The call must be in `Program.fs`, on the `expandedAst` value, BEFORE passing to `elaborateProgram`. This matches the TC-05 requirement explicitly.
**Warning signs:** TypeClassDecl stub comments at lines 4101–4103 in `Elaboration.fs` would never be hit.

### Pitfall 2: Forgetting the Module-Level Instance Hoist
**What goes wrong:** Instances declared inside `ModuleDecl` bodies would not be accessible at the outer scope. LangThree's logic explicitly hoists instance bindings out of the module:
```fsharp
[ModuleDecl(name, elaborateTypeclasses innerDecls, span)] @ instanceBindings
```
If only recursing (without hoisting), `let result = show Red` at top scope fails to find `show`.
**Why it happens:** Forgetting the `@ instanceBindings` suffix.
**How to avoid:** Copy the `instanceBindings` extraction pattern exactly from LangThree (lines 262–267).
**Warning signs:** Test `typeclass-module-instance.flt` fails (LangThree test: module-scoped instance accessible globally).

### Pitfall 3: Ast.Decl. Qualification
**What goes wrong:** LangThree's `Elaborate.fs` uses `open Ast` and thus writes bare names like `LetDecl(...)`. In FunLangCompiler's `Elaboration.fs`, the module opens `Ast` at line 3 — so bare names like `LetDecl` refer to the expression-level `Ast.LetDecl`, NOT to `Ast.Decl.LetDecl`. Using the wrong qualifier will cause a compile error or wrong DU case.
**Why it happens:** LangThree's `Decl` type has both an `Ast.Expr` and `Ast.Decl` (the module-level DU). In LangThree `Elaborate.fs`, `open Ast` imports both. In FunLangCompiler `Elaboration.fs`, `open Ast` brings `Expr` cases into scope, but `Decl` cases require `Ast.Decl.` prefix (or `open Ast.Decl`).
**How to avoid:** Use `Ast.Decl.TypeClassDecl`, `Ast.Decl.InstanceDecl`, `Ast.Decl.LetDecl`, etc. throughout the implementation. Verify by checking existing patterns at lines 4095–4103 in `Elaboration.fs` (they already use `Ast.Decl.` prefix).
**Warning signs:** F# compile error "This expression was expected to have type 'Expr' but here has type 'Decl'".

### Pitfall 4: Forgetting the `NamespaceDecl` Recursion Case
**What goes wrong:** Without the `NamespaceDecl` recursion case, instances nested inside a namespace block are not elaborated.
**Why it happens:** `NamespaceDecl` is easy to overlook since it's a less common construct than `ModuleDecl`.
**How to avoid:** Include both `ModuleDecl` and `NamespaceDecl` cases in `elaborateTypeclasses` — exactly as LangThree does.
**Warning signs:** TC-04 test fails.

## Code Examples

### elaborateTypeclasses — Complete Implementation for FunLangCompiler

```fsharp
// Source: adapted from /Users/ohama/vibe-coding/LangThree/src/LangThree/Elaborate.fs:248
// Phase 52: Elaborate typeclass declarations into ordinary let-bindings.
// - TypeClassDecl: removed (not needed by elaborateProgram)
// - InstanceDecl(cls, ty, methods, constraints, span): each method → LetDecl(methodName, methodBody, span)
// - ModuleDecl: recurse into body; hoist instance method bindings to outer scope (instances are global)
// - NamespaceDecl: recurse into body
// - DerivingDecl: removed (not needed by elaborateProgram)
// - All other decls: pass through unchanged
let rec elaborateTypeclasses (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun decl ->
        match decl with
        | Ast.Decl.TypeClassDecl _ ->
            []
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
        | Ast.Decl.DerivingDecl _ ->
            []
        | other -> [other])
```

### Program.fs Wiring — Between expandImports and elaborateProgram

```fsharp
// Source: Program.fs, replace line 206
let tcExpandedAst =
    match expandedAst with
    | Ast.Module(ds, s) -> Ast.Module(Elaboration.elaborateTypeclasses ds, s)
    | Ast.NamedModule(nm, ds, s) -> Ast.NamedModule(nm, Elaboration.elaborateTypeclasses ds, s)
    | Ast.NamespacedModule(nm, ds, s) -> Ast.NamespacedModule(nm, Elaboration.elaborateTypeclasses ds, s)
    | other -> other
let mlirMod = Elaboration.elaborateProgram tcExpandedAst
```

### E2E Test: Basic TypeClass + Instance

```fun
// tests/compiler/52-01-typeclass-basic.fun
typeclass Show 'a =
    | show : 'a -> string

instance Show int =
    let show x = to_string x

let _ = show 42
```

Expected compiled output:
```
42
```

### E2E Test: TypeClass Nodes Removed (TypeClassDecl removed)

```fun
// tests/compiler/52-02-typeclass-removed.fun
typeclass Eq 'a =
    | equal : 'a -> 'a -> bool

let _ = 0
```

Expected output: `0` (TypeClassDecl removed, no crash)

### E2E Test: Module-Scoped Instance Hoisted

```fun
// tests/compiler/52-03-module-instance.fun
typeclass Render 'a =
    | render : 'a -> string

module A =
    instance Render int =
        let render x = "rendered:" + to_string x

let result = render 42
let _ = result
```

Expected output: `rendered:42`

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| TypeClassDecl/InstanceDecl silently ignored (lines 4101–4103 in Elaboration.fs) | `elaborateTypeclasses` desugars to LetDecl before `elaborateProgram` | Phase 52 | Programs using typeclasses will compile correctly |
| `elaborateProgram` receives raw typeclass AST nodes | `elaborateProgram` receives only LetDecl-equivalent bindings | Phase 52 | No changes needed in elaborateProgram itself |

**Not needed (deferred to Phase 53):**
- Prelude typeclass definitions (Show, Eq, etc.) — Phase 53 syncs Prelude/Typeclass.fun
- Type inference for typeclass constraints — LangThree handles this; FunLangCompiler is compilation only

## Open Questions

1. **Placement of elaborateTypeclasses in Elaboration.fs**
   - What we know: The function should be public (called from Program.fs). It should appear near `elaborateProgram`.
   - What's unclear: Whether to place it immediately before `elaborateProgram` (line ~4208) or at the top of the "program-level" section after `prePassDecls`.
   - Recommendation: Place it immediately before `elaborateProgram` with a Phase 52 comment. This keeps the entry-point functions together.

2. **DerivingDecl interaction with prePassDecls**
   - What we know: `prePassDecls` at line 4103 already ignores `DerivingDecl` with `| _ -> ()`.
   - What's unclear: Whether after `elaborateTypeclasses` runs, `prePassDecls` will still encounter `DerivingDecl` nodes (it should not, since they're removed).
   - Recommendation: This is safe — after elaboration, no DerivingDecl nodes remain. The existing `_ -> ()` fallthrough is harmless.

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Elaborate.fs` lines 243–273 — exact `elaborateTypeclasses` implementation
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` lines 368–371 — `TypeClassDecl`, `InstanceDecl`, `DerivingDecl` field layouts
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Cli/Program.fs` lines 191–206 — current pipeline structure
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` lines 4095–4103, 4208–4218 — existing stub comments and `elaborateProgram` entry point

### Secondary (MEDIUM confidence)
- LangThree typeclass test files in `/Users/ohama/vibe-coding/LangThree/tests/flt/file/typeclass/` — verified transformation behavior and expected outputs for test design
- Phase 51 RESEARCH.md (`.planning/phases/51-ast-structure-sync/51-RESEARCH.md`) — confirmed AST field layouts match LangThree exactly

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — direct inspection of both source files; no external dependencies
- Architecture: HIGH — transformation is a direct port; insertion point is unambiguous in Program.fs
- Pitfalls: HIGH — all pitfalls verified by reading actual code (Ast.Decl. prefix, module hoisting)

**Research date:** 2026-04-01
**Valid until:** 2026-05-01 (stable internal codebase; no version drift risk for 30 days)
