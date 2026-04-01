# Phase 25: Module System - Research

**Researched:** 2026-03-27
**Domain:** AST flattening + qualified name desugar in F# compiler backend (Elaboration.fs)
**Confidence:** HIGH

## Summary

Phase 25 implements module system support entirely within `Elaboration.fs` — no new MlirIR DU cases, no runtime C changes, no new NuGet packages. The work is compile-time AST transformation: the backend must process `ModuleDecl` nodes the same way the FunLang interpreter (`Eval.fs`) and type checker (`TypeCheck.fs`) already handle them.

The interpreter in `Eval.fs/evalModuleDecls` is the reference implementation. It recurses into `ModuleDecl` inner decls to register types and execute bindings, treats `OpenDecl` and `NamespaceDecl` as no-ops (or transparent scope flattening), handles `LetPatDecl` by evaluating and binding pattern variables, and resolves qualified names via `rewriteModuleAccess` before expression evaluation. The backend must mirror this behavior at the elaboration level, with two passes: `prePassDecls` (types/records/exceptions registration) and `extractMainExpr` (expression flattening).

The qualified name desugar (MOD-05) is the most subtle requirement. The parser produces `FieldAccess(Constructor("M", None, span), "memberName", span)` for `M.memberName` expressions. The existing `FieldAccess` handler in `elaborateExpr` assumes it is always a record field access and will crash with "unknown field" for any module-qualified name that reaches it. The fix is to add a guard clause **before** the record field handler that detects `FieldAccess(Constructor(name, None, _), field, _)` and resolves it by looking up `name` in `env.Vars` then projecting `field` — but since the type checker's `rewriteModuleAccess` already rewrites these to plain `Var` or `Constructor` nodes at the type-check level, the backend may only need to handle the case where module-qualified names slip through (e.g., inside `ModuleDecl` inner decls before the type checker has run the full rewrite).

**Primary recommendation:** Implement in two tasks — Task 25-01: `prePassDecls` recursion + `extractMainExpr` flattening + `OpenDecl`/`NamespaceDecl` no-ops + `LetPatDecl` support (MOD-01 through MOD-04, MOD-06). Task 25-02: Qualified name desugar in `elaborateExpr` (MOD-05). Write E2E `.flt` tests for each requirement before implementing.

## Standard Stack

This phase adds no new packages. All tooling is already present.

### Core (already present)
| Component | Location | Purpose | Notes |
|-----------|----------|---------|-------|
| `Elaboration.fs` | `src/FunLangCompiler.Compiler/` | Primary change target — prePassDecls, extractMainExpr, elaborateExpr | All 6 requirements land here |
| `Ast.fs` (FunLang) | `../FunLang/src/FunLang/Ast.fs` | Reference for ModuleDecl, OpenDecl, NamespaceDecl, LetPatDecl AST nodes | Read-only |
| `TypeCheck.fs` (FunLang) | `../FunLang/src/FunLang/TypeCheck.fs` | Reference for rewriteModuleAccess logic | Read-only |
| `Eval.fs` (FunLang) | `../FunLang/src/FunLang/Eval.fs` | Reference implementation for module semantics | Read-only |

### Installation
No new packages. Build with existing `dotnet build` and test with `dotnet run`.

## Architecture Patterns

### Pattern 1: prePassDecls Recursion (MOD-01)

**What:** Add a `ModuleDecl` arm to the `for decl in decls do match decl with ...` loop in `prePassDecls`. Recurse into inner decls by calling `prePassDecls` on them and merging results into the mutable maps.

**When to use:** Always — any `ModuleDecl` at the top level or inside another module must have its type/record/exception declarations registered in `TypeEnv`/`RecordEnv`/`ExnTags`.

**Current code (line ~2323-2347):**
```fsharp
let private prePassDecls (decls: Ast.Decl list)
    : Map<string, TypeInfo> * Map<string, Map<string, int>> * Map<string, int> =
    let mutable typeEnv  = Map.empty<string, TypeInfo>
    let mutable recordEnv = Map.empty<string, Map<string, int>>
    let mutable exnTags  = Map.empty<string, int>
    let exnCounter = ref 0
    for decl in decls do
        match decl with
        | Ast.Decl.TypeDecl ... -> ...
        | Ast.Decl.RecordTypeDecl ... -> ...
        | Ast.Decl.ExceptionDecl ... -> ...
        | _ -> ()
    (typeEnv, recordEnv, exnTags)
```

**After fix — add ModuleDecl arm:**
```fsharp
        | Ast.Decl.ModuleDecl(_, innerDecls, _) ->
            let (innerTypeEnv, innerRecordEnv, innerExnTags) = prePassDecls innerDecls
            typeEnv  <- Map.fold (fun acc k v -> Map.add k v acc) typeEnv  innerTypeEnv
            recordEnv <- Map.fold (fun acc k v -> Map.add k v acc) recordEnv innerRecordEnv
            exnTags  <- Map.fold (fun acc k v -> Map.add k v acc) exnTags  innerExnTags
        | Ast.Decl.NamespaceDecl(_, innerDecls, _) ->
            let (innerTypeEnv, innerRecordEnv, innerExnTags) = prePassDecls innerDecls
            typeEnv  <- Map.fold (fun acc k v -> Map.add k v acc) typeEnv  innerTypeEnv
            recordEnv <- Map.fold (fun acc k v -> Map.add k v acc) recordEnv innerRecordEnv
            exnTags  <- Map.fold (fun acc k v -> Map.add k v acc) exnTags  innerExnTags
```

Note: `prePassDecls` is recursive (`let private prePassDecls` called from itself), so F# will need `let rec private prePassDecls` for recursion to work. Currently the function is not `rec` — this must be added.

### Pattern 2: extractMainExpr Flattening (MOD-02)

**What:** The filter in `extractMainExpr` currently passes through only `LetDecl`, `LetRecDecl`, and `LetMutDecl`. It must also recurse into `ModuleDecl` to collect inner bindings and flatten them into the expression chain.

**Current behavior:** `ModuleDecl` hits the `| _ :: rest -> build rest` wildcard arm and is silently skipped. All bindings inside the module are dropped.

**Strategy:** Two options exist:

Option A (simpler): Pre-flatten the decl list before filtering. Add a `flattenDecls` helper that recursively replaces `ModuleDecl(_, innerDecls, _)` with `innerDecls` (and handles `NamespaceDecl` similarly), then pass the flattened list to the existing filter + build.

Option B (inline): Extend the `build` function itself to handle `ModuleDecl` by recursing into inner decls.

Option A is recommended because it keeps `extractMainExpr` logic clean and reuses the existing filter+build machinery unchanged.

**Implementation pattern:**
```fsharp
let private flattenDecls (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(_, innerDecls, _) -> flattenDecls innerDecls
        | Ast.Decl.NamespaceDecl(_, innerDecls, _) -> flattenDecls innerDecls
        | _ -> [d])

let private extractMainExpr (decls: Ast.Decl list) : Expr =
    let flatDecls = flattenDecls decls
    let s = unknownSpan
    let exprDecls =
        flatDecls |> List.filter (fun d ->
            match d with
            | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _ | Ast.Decl.LetMutDecl _
            | Ast.Decl.LetPatDecl _ -> true  // MOD-06: include LetPatDecl
            | _ -> false)
    ...
```

Note: `flattenDecls` is recursive and must be declared `let rec private`.

### Pattern 3: LetPatDecl in extractMainExpr (MOD-06)

**What:** Add `LetPatDecl` to both the filter and the `build` function in `extractMainExpr`.

**Context:** `LetPatDecl(pat, body, span)` represents `let (a, b) = expr` at module level. The type checker (`TypeCheck.fs` line ~735) and interpreter (`Eval.fs` line ~1165) both handle it. The backend currently silently drops it (the `| _ :: rest -> build rest` arm).

**Filter addition:** Add `| Ast.Decl.LetPatDecl _ -> true` to the filter (shown in Pattern 2 above).

**Build addition:**
```fsharp
| Ast.Decl.LetPatDecl(pat, body, _) :: rest ->
    LetPat(pat, body, build rest, s)
```

The `LetPat` expression node is already handled by `elaborateExpr` (lines ~538-600 for WildcardPat, VarPat, TuplePat). This is a safe extension.

### Pattern 4: OpenDecl and NamespaceDecl as No-ops (MOD-03, MOD-04)

**What:** Both are already handled by the wildcard arm in `extractMainExpr`. No action needed for these in `extractMainExpr`. The `prePassDecls` recursion into `NamespaceDecl` (Pattern 1) is the only backend work for `NamespaceDecl`.

`OpenDecl` requires no backend action at all: the type checker's `rewriteModuleAccess` already rewrites all qualified names to their unqualified forms before the AST reaches the backend. By the time `elaborateProgram` is called, any `M.x` that came from an opened module is already a plain `Var("x")`.

### Pattern 5: Qualified Name Desugar in elaborateExpr (MOD-05)

**What:** Expressions like `M.x` parse as `FieldAccess(Constructor("M", None, span), "x", span)`. These reach `elaborateExpr` when the source uses qualified names in expressions that weren't rewritten by the type checker (e.g., qualified names used inside `ModuleDecl` inner decls compiled by the backend, or top-level expressions in programs that skip the type checker path).

**Critical insight:** After `flattenDecls` in `extractMainExpr` (Pattern 2), module inner bindings are inlined. But qualified names within those bindings — `M.x` references — are NOT rewritten by the backend. The type checker rewrites them, but the backend operates on the raw parsed AST (after elaborateProgram receives `Ast.Module`). Therefore the backend must handle `FieldAccess(Constructor(modName, None, _), memberName, _)` in `elaborateExpr`.

**The type checker's approach (reference):**
```fsharp
// TypeCheck.fs rewriteModuleAccess
| FieldAccess(Constructor(modName, None, _), fieldName, span) when Map.containsKey modName modules ->
    let exports = Map.find modName modules
    if Map.containsKey fieldName exports.CtorEnv then
        Constructor(fieldName, None, span)
    else
        Var(fieldName, span)
```

**Backend approach:** The backend does not have a `modules` map. After `flattenDecls`, all module bindings are in scope as plain `Var` names. So `M.x` in a body expression becomes a lookup of `x` in `env.Vars`. The simplest correct approach: add a guard before the record `FieldAccess` handler:

```fsharp
// In elaborateExpr, BEFORE the existing FieldAccess record handler:
| FieldAccess(Constructor(_, None, _), memberName, span) ->
    // Qualified module access M.memberName — after flattening, memberName is in scope as plain Var
    elaborateExpr env (Var(memberName, span))
```

This desugars `M.x` to `Var("x")` and lets the existing `Var` handler resolve it from `env.Vars`. This is safe because: (1) qualified names like `M.x` can only refer to bindings defined inside `M`, and (2) after `flattenDecls`, those bindings are in scope under their unqualified names.

**For constructor applications via qualified access** (`M.Ctor arg`): These parse as `App(FieldAccess(Constructor("M", None, _), "Ctor", _), arg, _)`. The `FieldAccess` guard above will desugar `FieldAccess(Constructor("M"), "Ctor")` → `Var("Ctor", _)`, then the `App` handler sees `App(Var("Ctor"), arg)`. The `Var` handler looks up `"Ctor"` in `env.Vars` — but constructors are looked up in `env.TypeEnv` via the `Constructor` path, not `Var`. This may require `Var("Ctor")` to resolve via `TypeEnv` if the name is a known constructor. Check: the existing `Var` handler does not consult `TypeEnv` for constructors. The proper fix is to desugar `FieldAccess(Constructor(_, None, _), ctorName, _)` to `Constructor(ctorName, None, span)` when `ctorName` is in `env.TypeEnv`.

**Refined guard:**
```fsharp
| FieldAccess(Constructor(_, None, _), memberName, span) ->
    if Map.containsKey memberName env.TypeEnv then
        elaborateExpr env (Constructor(memberName, None, span))
    else
        elaborateExpr env (Var(memberName, span))
```

### Anti-Patterns to Avoid

- **Adding runtime module namespace tracking:** The backend does not need a `modules` map like the type checker. After flatten+desugar, all names are flat.
- **Changing `elaborateProgram` signature:** Keep `elaborateProgram (ast: Ast.Module) : MlirModule` as-is. Only internals change.
- **Making `prePassDecls` non-recursive:** It must be `let rec private` to recurse into nested `ModuleDecl` nodes.
- **Forgetting NamespaceDecl in flattenDecls:** `NamespaceDecl` inner decls also need to be flattened, same as `ModuleDecl`.
- **Order of FieldAccess guards:** The new `FieldAccess(Constructor(_, None, _), ...)` arm MUST come before the existing `FieldAccess(recExpr, fieldName, _)` record handler, or F# will never reach it.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Module-aware name resolution at runtime | Runtime module namespace map | Compile-time flatten + desugar | The type checker already resolved all names; no runtime dispatch needed |
| Custom AST visitor for FieldAccess rewriting | Full rewrite pass in Elaboration.fs | Guard in elaborateExpr's match | One arm + recursive delegation is sufficient |
| New MlirIR DU cases for module ops | ModuleCallOp, ModuleLookupOp, etc. | Nothing — no new IR ops | Module is purely compile-time, emits same MLIR as flat let bindings |

**Key insight:** Modules are a compile-time scoping mechanism. At the MLIR level there is no difference between a flat program and a module program — they produce identical MLIR. The backend's only job is to flatten the AST.

## Common Pitfalls

### Pitfall 1: prePassDecls Is Not rec

**What goes wrong:** Calling `prePassDecls innerDecls` recursively from within `prePassDecls` causes F# compile error "prePassDecls is not defined" because the binding is not `rec`.

**Why it happens:** The function was originally written as `let private prePassDecls` (no `rec`). Adding a recursive call requires changing to `let rec private prePassDecls`.

**How to avoid:** Change the declaration to `let rec private prePassDecls` before adding the `ModuleDecl` arm.

**Warning signs:** F# compile error FS0039 ("The value or constructor 'prePassDecls' is not defined").

### Pitfall 2: flattenDecls Not rec

**What goes wrong:** Same issue — `flattenDecls` is recursive by nature (modules can be nested) and must be declared `let rec`.

**How to avoid:** Always declare `let rec private flattenDecls`.

### Pitfall 3: FieldAccess Guard Pattern Ordering

**What goes wrong:** F# matches arms top-to-bottom. If the new `FieldAccess(Constructor(_, None, _), ...)` guard is placed after the existing `FieldAccess(recExpr, fieldName, _)` arm, the existing arm matches first and the code tries to GEP into a `Constructor` value (wrong), crashing with "unknown field".

**How to avoid:** Place the new guard arm BEFORE the existing record `FieldAccess` handler. In the match expression in `elaborateExpr`, the order must be:

```
...
| FieldAccess(Constructor(_, None, _), memberName, span) ->   // NEW: module access guard
    ...
| FieldAccess(recExpr, fieldName, _) ->                       // EXISTING: record field access
    ...
```

**Warning signs:** Runtime crash "FieldAccess: unknown field 'x'" when `x` is a valid module binding.

### Pitfall 4: LetPatDecl Not in exprDecls Filter

**What goes wrong:** After adding `LetPatDecl` to the `build` function but forgetting to add it to the `exprDecls` filter, `LetPatDecl` nodes never appear in `exprDecls` (they're filtered out) and still get silently dropped.

**How to avoid:** Add `| Ast.Decl.LetPatDecl _ -> true` to the filter in `extractMainExpr` (or the pre-flatten + filter approach in Pattern 2).

**Warning signs:** Module-level `let (a, b) = ...` produces no output; variables `a`, `b` are undefined in later code.

### Pitfall 5: Qualified Constructor Application Resolves Wrong

**What goes wrong:** `M.Red` (nullary constructor) becomes `Var("Red")` via the simple desugar. `Var` handler looks in `env.Vars` but constructors are not stored there — they're in `env.TypeEnv`. The elaboration crashes with "Unknown variable Red".

**How to avoid:** Check `env.TypeEnv` before choosing `Var` vs `Constructor` in the desugar arm (see Pattern 5, Refined guard).

**Warning signs:** Programs using `M.SomeConstructor` fail with "Unknown variable SomeConstructor".

### Pitfall 6: Nested ModuleDecl in flattenDecls

**What goes wrong:** `flattenDecls` recurses into `ModuleDecl` inner decls but the inner decls may themselves contain `ModuleDecl` — a two-level nesting. If `flattenDecls` doesn't call itself on the inner list, second-level modules are not flattened.

**How to avoid:** `flattenDecls` must call `flattenDecls` on the result of recursing into inner decls (i.e., call `flattenDecls innerDecls` not `innerDecls` directly).

## Code Examples

### extractMainExpr with Flattening and LetPatDecl Support

```fsharp
// Source: direct implementation based on current Elaboration.fs (line ~2350) + Eval.fs pattern
let rec private flattenDecls (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(_, innerDecls, _) -> flattenDecls innerDecls
        | Ast.Decl.NamespaceDecl(_, innerDecls, _) -> flattenDecls innerDecls
        | _ -> [d])

let private extractMainExpr (decls: Ast.Decl list) : Expr =
    let s = unknownSpan
    let flatDecls = flattenDecls decls
    let exprDecls =
        flatDecls |> List.filter (fun d ->
            match d with
            | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _ | Ast.Decl.LetMutDecl _
            | Ast.Decl.LetPatDecl _ -> true
            | _ -> false)
    match exprDecls with
    | [] -> Number(0, s)
    | _ ->
        let rec build (ds: Ast.Decl list) : Expr =
            match ds with
            | [] -> Number(0, s)
            | [Ast.Decl.LetDecl("_", body, _)] -> body
            | [Ast.Decl.LetDecl(name, body, _)] -> Let(name, body, Var(name, s), s)
            | [Ast.Decl.LetRecDecl(bindings, _)] ->
                match bindings with
                | (name, param, body, _) :: _ -> LetRec(name, param, body, Number(0, s), s)
                | [] -> Number(0, s)
            | [Ast.Decl.LetPatDecl(pat, body, _)] -> LetPat(pat, body, Number(0, s), s)
            | Ast.Decl.LetDecl("_", body, _) :: rest ->
                Let("_", body, build rest, s)
            | Ast.Decl.LetDecl(name, body, _) :: rest ->
                Let(name, body, build rest, s)
            | Ast.Decl.LetRecDecl(bindings, _) :: rest ->
                match bindings with
                | (name, param, body, _) :: _ -> LetRec(name, param, body, build rest, s)
                | [] -> build rest
            | Ast.Decl.LetMutDecl(name, body, _) :: rest ->
                LetMut(name, body, build rest, s)
            | Ast.Decl.LetPatDecl(pat, body, _) :: rest ->
                LetPat(pat, body, build rest, s)
            | _ :: rest -> build rest
        build exprDecls
```

### prePassDecls Recursion

```fsharp
// Source: current Elaboration.fs (line ~2317) + extension for ModuleDecl
let rec private prePassDecls (decls: Ast.Decl list)   // NOTE: 'rec' added
    : Map<string, TypeInfo> * Map<string, Map<string, int>> * Map<string, int> =
    let mutable typeEnv  = Map.empty<string, TypeInfo>
    let mutable recordEnv = Map.empty<string, Map<string, int>>
    let mutable exnTags  = Map.empty<string, int>
    let exnCounter = ref 0
    for decl in decls do
        match decl with
        | Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _)) -> ...   // unchanged
        | Ast.Decl.RecordTypeDecl (Ast.RecordDecl(typeName, _, fields, _)) -> ...  // unchanged
        | Ast.Decl.ExceptionDecl(name, dataTypeOpt, _) -> ...       // unchanged
        | Ast.Decl.ModuleDecl(_, innerDecls, _) ->
            let (iT, iR, iE) = prePassDecls innerDecls
            typeEnv  <- Map.fold (fun acc k v -> Map.add k v acc) typeEnv  iT
            recordEnv <- Map.fold (fun acc k v -> Map.add k v acc) recordEnv iR
            exnTags  <- Map.fold (fun acc k v -> Map.add k v acc) exnTags  iE
        | Ast.Decl.NamespaceDecl(_, innerDecls, _) ->
            let (iT, iR, iE) = prePassDecls innerDecls
            typeEnv  <- Map.fold (fun acc k v -> Map.add k v acc) typeEnv  iT
            recordEnv <- Map.fold (fun acc k v -> Map.add k v acc) recordEnv iR
            exnTags  <- Map.fold (fun acc k v -> Map.add k v acc) exnTags  iE
        | _ -> ()
    (typeEnv, recordEnv, exnTags)
```

### Qualified Name Desugar in elaborateExpr

```fsharp
// Source: TypeCheck.fs rewriteModuleAccess pattern, adapted for Elaboration.fs
// Add BEFORE the existing | FieldAccess(recExpr, fieldName, _) -> arm

// Phase 25: MOD-05 — desugar qualified module access M.member → Var(member) or Constructor(member)
| FieldAccess(Constructor(_, None, _), memberName, span) ->
    if Map.containsKey memberName env.TypeEnv then
        // memberName is a known constructor (ADT ctor or exception ctor)
        elaborateExpr env (Constructor(memberName, None, span))
    else
        // memberName is a regular value binding
        elaborateExpr env (Var(memberName, span))
```

### E2E Test Pattern for Module System

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
module Math =
    let add x y = x + y
let result = Math.add 3 4
let _ = println (to_string result)
// --- Output:
7
0
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| Silently ignore ModuleDecl in prePassDecls | Recurse into ModuleDecl inner decls | Types/records/exceptions inside modules are now visible to elaborateExpr |
| Skip ModuleDecl in extractMainExpr | flattenDecls + include inner bindings | Module bindings are now compiled and executed |
| LetPatDecl silently dropped | LetPatDecl → LetPat in build | Module-level destructuring works |
| FieldAccess always treated as record access | Guard for Constructor-based access first | Qualified names M.x compile correctly |

**Currently broken (before this phase):**
- Any program with `module M = ...` will fail to include M's bindings at runtime
- `M.x` qualified names in expressions crash with "unknown field 'x'" in elaborateExpr
- `let (a, b) = expr` at module level is silently dropped

## Open Questions

1. **Qualified constructor APPLICATION: `M.Ctor arg` parse form**
   - What we know: `M.Ctor arg` parses as `App(FieldAccess(Constructor("M"), "Ctor"), arg)`. The FieldAccess subexpression becomes `Constructor("Ctor", None, span)` via MOD-05 guard. Then `App(Constructor("Ctor", None), arg)` is elaborated.
   - What's unclear: Does the existing `App(Constructor(name, None), arg)` arm handle this? Check `elaborateExpr` for `App(Constructor(...), arg)` pattern.
   - Recommendation: Grep `elaborateExpr` for `App.*Constructor` handling before writing tests. If missing, may need to add it (but this may already work from Phase 17).

2. **ExnCounter state in recursive prePassDecls**
   - What we know: `exnCounter` is a local `ref` in `prePassDecls`. Each recursive call creates its own counter starting at 0.
   - What's unclear: Exception tags must be globally unique across the whole program. A nested `prePassDecls` call gets its own counter starting at 0, potentially assigning the same tag to exceptions in different modules.
   - Recommendation: Pass `exnCounter` as a parameter to recursive calls, OR collect all exceptions in a flat first pass before tagging. The current code works correctly for non-module programs (single flat list). For modules, tags assigned inside a module may collide with tags at the outer level if both have exceptions. Verify with a test that has exceptions in both a module and the outer scope.

3. **Two-level qualified access: `M.Sub.x`**
   - What we know: `M.Sub.x` parses as `FieldAccess(FieldAccess(Constructor("M"), "Sub"), "x")`. TypeCheck.fs handles this (`FieldAccess(FieldAccess(Constructor(modName), subModName), fieldName)`).
   - What's unclear: After flattenDecls, is "x" in scope as a plain `Var("x")`? Only if `Sub` is also flattened. Since both `M` and `M.Sub` are recursively flattened by `flattenDecls`, yes — `x` is directly in scope.
   - Recommendation: The outer `FieldAccess(FieldAccess(Constructor("M"), "Sub"), "x")` will trigger the record `FieldAccess` handler (since `FieldAccess(Constructor("M"), "Sub")` is the receiver, not `Constructor` alone). The MOD-05 guard only matches `FieldAccess(Constructor(...), memberName)` — it does NOT match nested FieldAccess. Add an additional guard for the two-level case if tests require it:
     ```fsharp
     | FieldAccess(FieldAccess(Constructor(_, None, _), _, _), memberName, span) ->
         if Map.containsKey memberName env.TypeEnv then
             elaborateExpr env (Constructor(memberName, None, span))
         else
             elaborateExpr env (Var(memberName, span))
     ```

## Sources

### Primary (HIGH confidence)
- `src/FunLangCompiler.Compiler/Elaboration.fs` — Lines 2315-2400: prePassDecls, extractMainExpr, elaborateProgram; Line 1777: FieldAccess handler; Lines 538-600: LetPat handlers
- `deps/FunLang/src/FunLang/Eval.fs` — Lines 1148-1281: evalModuleDecls reference implementation (ModuleDecl recursion, LetPatDecl, OpenDecl handling)
- `deps/FunLang/src/FunLang/TypeCheck.fs` — Lines 512-593: rewriteModuleAccess; Lines 820-906: ModuleDecl/OpenDecl/NamespaceDecl type check handling
- `deps/FunLang/src/FunLang/Ast.fs` — Lines 311-352: Decl DU definition (ModuleDecl, OpenDecl, NamespaceDecl, LetPatDecl)
- `deps/FunLang/src/FunLang/Parser.fsy` — Lines 491-545: Module syntax grammar

### Secondary (MEDIUM confidence)
- `.planning/REQUIREMENTS.md` — MOD-01 through MOD-06 definitions

## Metadata

**Confidence breakdown:**
- prePassDecls recursion (MOD-01): HIGH — implementation is straightforward, reference in Eval.fs is clear
- extractMainExpr flattening (MOD-02): HIGH — flattenDecls pattern is clean, Eval.fs confirms semantics
- OpenDecl no-op (MOD-03): HIGH — confirmed by type checker rewriting and Eval.fs behavior
- NamespaceDecl no-op (MOD-04): HIGH — confirmed by Eval.fs `| _ -> (env, modEnv)` catch-all
- Qualified name desugar (MOD-05): HIGH (simple case), MEDIUM (nested M.Sub.x, constructor apps)
- LetPatDecl (MOD-06): HIGH — LetPat is already handled in elaborateExpr; only filter/build change needed
- ExnCounter collision: MEDIUM — needs verification test with exceptions in both module and outer scope

**Research date:** 2026-03-27
**Valid until:** Stable — this is internal compiler source, no external dependency churn
