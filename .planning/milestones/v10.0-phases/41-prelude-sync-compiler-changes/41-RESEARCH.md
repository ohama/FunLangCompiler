# Phase 41: Prelude Sync Compiler Changes - Research

**Researched:** 2026-03-30
**Domain:** F# compiler backend — AST elaboration, MLIR name sanitization, Prelude file sync
**Confidence:** HIGH

## Summary

Phase 41 requires three compiler changes: implementing `OpenDecl` in `flattenDecls`, verifying operator MLIR name sanitization, and syncing 12 Prelude `.fun` files with FunLang.

Research confirmed that `sanitizeMlirName` in Printer.fs is already fully integrated and handles all operator characters (`^^`, `++`, `<|>`) in both `func.func` definitions and call sites. Integration testing proves operators inside modules compile to valid MLIR without errors. The `OpenDecl` implementation requires a two-pass refactor of `flattenDecls` to build a module member map first, then emit alias `LetDecl`s on open. The Prelude sync is a mechanical diff application with one exception: Hashtable.fun intentionally retains backend-specific `createStr`/`keysStr` functions absent from FunLang.

**Primary recommendation:** Implement `flattenDecls` as a two-pass function (pass 1: collect module member names; pass 2: flatten with alias emission on `OpenDecl`), update 4 Prelude files (Core, List, Option, Result) and add newlines to 3 others (HashSet, MutableList, Queue).

## Standard Stack

This phase is entirely within the existing codebase — no new libraries.

### Core
| File | Location | Purpose | Change Required |
|------|----------|---------|-----------------|
| `Elaboration.fs` | `src/FunLangCompiler.Compiler/` | AST-to-MLIR lowering | `flattenDecls` OpenDecl handling |
| `Printer.fs` | `src/FunLangCompiler.Compiler/` | MLIR serialization | `sanitizeMlirName` already integrated |
| `Prelude/*.fun` | `Prelude/` | Standard library source | Sync with FunLang |

### Supporting
| File | Location | Purpose | When to Read |
|------|----------|---------|-------------|
| `Ast.fs` (FunLang) | `../FunLang/src/FunLang/` | `OpenDecl` type definition | Understanding OpenDecl fields |
| `Eval.fs` (FunLang) | `../FunLang/src/FunLang/` | Reference OpenDecl semantics | Understanding expected behavior |
| `Parser.fsy` (FunLang) | `../FunLang/src/FunLang/` | Custom operator syntax | `let (^^) a b = ...` parsing |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Two-pass `flattenDecls` | Single-pass with state accumulation | Two-pass is cleaner; single-pass requires mutable module registry passed through |
| Alias `LetDecl`s for open | AST-level open tracking in `ElabEnv` | `LetDecl` approach stays entirely in `flattenDecls`; env approach requires threading open state |

## Architecture Patterns

### Current Structure
```
Elaboration.fs
├── flattenDecls (modName: string) (decls: Ast.Decl list) : Ast.Decl list
│   ├── ModuleDecl → recursively flatten with module name
│   ├── NamespaceDecl → recursively flatten with parent name
│   ├── LetDecl (when modName <> "") → prefix name
│   ├── LetRecDecl (when modName <> "") → prefix all bindings
│   └── _ → pass through unchanged  ← OpenDecl falls here (no-op)
└── extractMainExpr (decls: Ast.Decl list) : Expr
    └── calls flattenDecls "" decls
```

### Pattern 1: Two-Pass flattenDecls with Module Registry

**What:** First pass collects all `ModuleDecl(name, innerDecls)` into a `Map<string, string list>` (module name → list of member names). Second pass is the existing flatten logic, extended to handle `OpenDecl` by emitting alias `LetDecl`s.

**When to use:** Required because `open Core` must emit aliases for ALL members of `Core`, but `flattenDecls` processes decls sequentially and needs to know the members before it has processed them.

**Example:**
```fsharp
// Source: Elaboration.fs (new implementation)

// Pass 1: collect module member names
let private collectModuleMembers (decls: Ast.Decl list) : Map<string, string list> =
    let rec collectFromDecls modName decls =
        decls |> List.collect (fun d ->
            match d with
            | Ast.Decl.ModuleDecl(name, innerDecls, _) -> collectFromDecls name innerDecls
            | Ast.Decl.LetDecl(name, _, _) when modName <> "" && name <> "_" ->
                [(modName, modName + "_" + name)]
            | Ast.Decl.LetRecDecl(bindings, _) when modName <> "" ->
                bindings |> List.map (fun (name, _, _, _) -> (modName, modName + "_" + name))
            | _ -> [])
    let pairs = collectFromDecls "" decls
    pairs |> List.groupBy fst
          |> List.map (fun (modName, xs) -> (modName, xs |> List.map snd))
          |> Map.ofList

// Pass 2: flatten with alias emission
let rec private flattenDecls (moduleMembers: Map<string, string list>)
                              (modName: string)
                              (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
            flattenDecls moduleMembers name innerDecls
        | Ast.Decl.NamespaceDecl(_, innerDecls, _) ->
            flattenDecls moduleMembers modName innerDecls
        | Ast.Decl.LetDecl(name, body, s) when modName <> "" && name <> "_" ->
            [Ast.Decl.LetDecl(modName + "_" + name, body, s)]
        | Ast.Decl.LetRecDecl(bindings, s) when modName <> "" ->
            let prefixed = bindings |> List.map (fun (name, param, body, s2) ->
                (modName + "_" + name, param, body, s2))
            [Ast.Decl.LetRecDecl(prefixed, s)]
        | Ast.Decl.OpenDecl([openedMod], s) ->
            // Emit alias LetDecl for each member: let shortName = Var(qualifiedName)
            match Map.tryFind openedMod moduleMembers with
            | Some qualifiedNames ->
                qualifiedNames |> List.map (fun qualifiedName ->
                    // Extract short name: "Core_id" → "id"
                    let shortName = qualifiedName.Substring(openedMod.Length + 1)
                    Ast.Decl.LetDecl(shortName, Ast.Var(qualifiedName, s), s))
            | None -> []  // Unknown module — ignore (no-op)
        | _ -> [d])

// extractMainExpr updated to use two-pass:
let private extractMainExpr (decls: Ast.Decl list) : Expr =
    let s = unknownSpan
    let moduleMembers = collectModuleMembers decls
    let flatDecls = flattenDecls moduleMembers "" decls
    // ... rest unchanged
```

**Critical detail:** The aliases use `Ast.Var(qualifiedName, s)` as the body. When elaborated, `Var("Core_id")` looks up `Core_id` in `env.Vars` or `env.KnownFuncs` — this works because `Core_id` was already bound in previous `LetDecl`s.

### Pattern 2: Operator MLIR Names (Already Working)

**What:** `sanitizeMlirName` in Printer.fs converts invalid characters in MLIR `@symbol` names. Applied at all use sites: `DirectCallOp`, `LlvmAddressOfOp`, `LlvmCallOp`, `LlvmCallVoidOp`, `printFuncOp`.

**When to use:** Every time an operator-containing function name appears in MLIR output.

**Mapping (already in Printer.fs):**
```fsharp
| '+' -> sb.Append("_plus_") |> ignore
| '^' -> sb.Append("_caret_") |> ignore
| '|' -> sb.Append("_pipe_") |> ignore
| '>' -> sb.Append("_gt_") |> ignore
| '<' -> sb.Append("_lt_") |> ignore
// + many more
```

**Verified working:** `List_(++)` → `@List__plus__plus_` (valid MLIR), `Core_(^^)` → `@Core__caret__caret_`.

### Pattern 3: Short-Name Alias (Already in Place)

**What:** In `elaborateExpr`, when a `Let(name, ...)` or `LetRec(name, ...)` is processed and `name` starts with uppercase letter followed by `_` (module prefix), a short-name alias is ALSO registered in `env.KnownFuncs` and `env.Vars`.

**Example:** `Let("Core_id", ...)` also registers `"id"` → same value.

**Critical interaction with OpenDecl fix:** The short-name alias mechanism means `open Module` already partially works (unqualified names work). But with multiple modules defining the same short name (e.g., `map` in both `Option` and `Result`), only the LAST definition's alias survives. The `OpenDecl` alias `LetDecl` emission fixes this by explicitly re-binding the short name AFTER all module definitions.

### Pattern 4: Prelude File Sync

**What:** Replace 4 Prelude files to match FunLang exactly. Add newline to 3 files.

**When to use:** Required to reach byte-identical state with `../FunLang/Prelude/`.

**Exception:** `Hashtable.fun` intentionally differs — keeps `createStr` and `keysStr` functions that are backend-specific (not in FunLang).

### Anti-Patterns to Avoid

- **Alias at `open`-time in `extractMainExpr`:** Do not handle OpenDecl in `extractMainExpr` directly. The aliases must be emitted as `LetDecl` nodes so they participate in the `Let(name, body, continuation)` chain correctly.
- **Alias body as Lambda:** Use `Ast.Var(qualifiedName, s)` not `Ast.Lambda(...)`. The qualified name already compiles to a closure pointer; aliasing as a Var is a zero-cost indirection.
- **Mutation of short-name alias in Elaboration.fs:** Do NOT change the existing short-name alias logic in `elaborateExpr`'s `Let`/`LetRec` handlers. That mechanism is useful for intra-module references and should remain. The `OpenDecl` fix is additive, not a replacement.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| MLIR symbol validation | Custom name validator | `sanitizeMlirName` already in Printer.fs | Already fully integrated; handles all op chars |
| Module member enumeration | Parser walking | Extract directly from `Ast.Decl.ModuleDecl` inner decls | AST is already available at flatten time |
| Prelude file diffing | Manual comparison | `diff` command | Already verified — produces exact change set |

**Key insight:** The `sanitizeMlirName` function and the short-name alias mechanism already exist. Phase 41 is additive: add `collectModuleMembers` + extend `flattenDecls` to handle `OpenDecl`, then sync Prelude files.

## Common Pitfalls

### Pitfall 1: Operator Short-Name Alias

**What goes wrong:** After flattening, `List_(++)` has `(++)` as the short-name alias. But `collectModuleMembers` returns `"List_++"` as the qualified name. When emitting `LetDecl("++", Var("List_++"), s)`, the `Var` expression uses the raw operator string `"++"` — this is a valid AST node since the parser accepts `(++)` as an identifier.

**Why it happens:** Operators as function names work throughout the pipeline (parsing, elaboration, MLIR printing). The alias must use the EXACT qualified name string `"List_++"` (including the `+` chars), not a sanitized version — sanitization happens only in Printer.fs.

**How to avoid:** Use `qualifiedName` directly as the `Var` body — do NOT pre-sanitize.

**Warning signs:** `Elaboration: unbound variable '++'` error means the alias LetDecl is being emitted with wrong body, or the Var lookup is failing.

### Pitfall 2: Multiple Modules Same Member Name

**What goes wrong:** With Prelude loaded, both `Option` and `List` define short names like `map`, `filter`. The existing short-name alias gives priority to whichever module was loaded last. With `open Option` and `open List`, each `open` should override the previous aliases.

**Why it happens:** `extractMainExpr` uses `List.collect` which processes decls in order. Later `LetDecl("map", ...)` bindings correctly shadow earlier ones because `Let(name, body, inExpr)` chains give priority to the most-recently-bound name in the continuation.

**How to avoid:** The order of `LetDecl` aliases from `open` must match declaration order in the source. Since `open List` comes after `open Option` in `List.fun`, `List.map` aliases will correctly shadow `Option.map` aliases.

**Warning signs:** `let r = map (fun x -> x + 1) [1;2;3]` uses wrong module's `map`.

### Pitfall 3: collectModuleMembers at Top Level Only

**What goes wrong:** `collectModuleMembers` called with top-level decls only. But after `expandImports`, all `FileImportDecl` nodes have been expanded inline. So the decls passed to `extractMainExpr` already include all imported module declarations at the top level — `collectModuleMembers` sees the full flat list.

**Why it happens:** `Program.fs` calls `expandImports` before `elaborateProgram`, so module decls from Prelude files are inlined as top-level decls.

**How to avoid:** Pass the same `decls` list to both `collectModuleMembers` and `flattenDecls`.

**Warning signs:** `open Core` emits no aliases (module not found in registry) → `id` resolves via old short-name alias, not via explicit open alias.

### Pitfall 4: LetDecl Aliases Break Existing Short-Name Logic

**What goes wrong:** Emitting `LetDecl("id", Var("Core_id"), s)` creates a new binding that goes through the `Let(name, body, inExpr)` path in `extractMainExpr`. The `Let` handler also tries to register a short-name alias for `"id"` — but `"id"` doesn't start with uppercase, so `twoLambdaShortAlias = None`. No duplicate aliases.

**Why it happens:** The short-name extraction logic checks `System.Char.IsUpper(name.[0])`, which is false for lowercase short names like `id`, `map`, `not`.

**How to avoid:** No action needed — the interaction is safe by design.

### Pitfall 5: Option/Result Function Renaming Breaks Existing Tests

**What goes wrong:** Tests 35-05 and 35-06 define their OWN `Option`/`Result` modules inline (not via Prelude) and use the OLD names `map`, `bind`, `defaultValue`. Changing Prelude files does NOT affect these tests.

**Why it happens:** Each test that defines `module Option = let map...` overrides the Prelude's `Option` module at elaboration time because flattening processes decls in order: Prelude first, then the test file's decls overwrite the short-name aliases.

**How to avoid:** No special handling needed. Tests 35-05/35-06 are self-contained.

**Warning signs:** Test 35-05 or 35-06 fails after Prelude sync → inspect whether they define their own module or rely on Prelude.

### Pitfall 6: `(<|>)` and `(++)` MLIR function definition name

**What goes wrong:** When `let (<|>) a b = ...` inside `module Option` is flattened to `let Option_<|> a b = ...`, the elaborator creates a FuncOp with `Name = "@Option_<|>"`. The `printFuncOp` function calls `sanitizeMlirName "@Option_<|>"` → `"@Option__lt__pipe__gt_"`. This is a valid MLIR identifier.

**Why it happens:** `sanitizeMlirName` checks all 4 call sites in Printer.fs and is applied consistently. Research confirmed it's already applied to `printFuncOp` at line 209.

**How to avoid:** Integration tests are the primary verification. Run the full E2E test suite after Prelude sync.

## Code Examples

### Complete flattenDecls Rewrite

```fsharp
// Source: Elaboration.fs (Phase 41 implementation)

/// Phase 41: Collect module member names for OpenDecl alias emission.
/// Returns Map<moduleName, [qualifiedName, ...]> for top-level ModuleDecl only.
/// Called once on the full decl list before flattenDecls.
let private collectModuleMembers (decls: Ast.Decl list) : Map<string, string list> =
    let rec collect modName decls =
        decls |> List.collect (fun d ->
            match d with
            | Ast.Decl.ModuleDecl(name, innerDecls, _) -> collect name innerDecls
            | Ast.Decl.LetDecl(name, _, _) when modName <> "" && name <> "_" ->
                [(modName, modName + "_" + name)]
            | Ast.Decl.LetRecDecl(bindings, _) when modName <> "" ->
                bindings |> List.map (fun (name, _, _, _) -> (modName, modName + "_" + name))
            | _ -> [])
    let pairs = collect "" decls
    pairs
    |> List.groupBy fst
    |> List.map (fun (modName, xs) -> (modName, xs |> List.map snd))
    |> Map.ofList

/// Phase 41: flattenDecls now takes a moduleMembers registry.
/// OpenDecl([modName], _) emits alias LetDecl for each member.
let rec private flattenDecls (moduleMembers: Map<string, string list>)
                              (modName: string)
                              (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
            flattenDecls moduleMembers name innerDecls
        | Ast.Decl.NamespaceDecl(_, innerDecls, _) ->
            flattenDecls moduleMembers modName innerDecls
        | Ast.Decl.LetDecl(name, body, s) when modName <> "" && name <> "_" ->
            [Ast.Decl.LetDecl(modName + "_" + name, body, s)]
        | Ast.Decl.LetRecDecl(bindings, s) when modName <> "" ->
            let prefixed = bindings |> List.map (fun (name, param, body, s2) ->
                (modName + "_" + name, param, body, s2))
            [Ast.Decl.LetRecDecl(prefixed, s)]
        | Ast.Decl.OpenDecl([openedMod], s) ->
            match Map.tryFind openedMod moduleMembers with
            | Some qualifiedNames ->
                qualifiedNames |> List.map (fun qualifiedName ->
                    let shortName = qualifiedName.Substring(openedMod.Length + 1)
                    Ast.Decl.LetDecl(shortName, Ast.Var(qualifiedName, s), s))
            | None -> []
        | _ -> [d])

/// Updated extractMainExpr calls two-pass flatten.
let private extractMainExpr (decls: Ast.Decl list) : Expr =
    let s = unknownSpan
    let moduleMembers = collectModuleMembers decls
    let flatDecls = flattenDecls moduleMembers "" decls
    // ... rest of extractMainExpr unchanged
```

### Prelude File Changes — Core.fun

```fsharp
// Target: match FunLang/Prelude/Core.fun exactly
module Core =
    let id x = x
    let const x = fun y -> x
    let compose f = fun g -> fun x -> f (g x)
    let flip f = fun x -> fun y -> f y x
    let apply f = fun x -> f x
    let (^^) a b = string_concat a b
    let not x = if x then false else true
    let min a = fun b -> if a < b then a else b
    let max a = fun b -> if a > b then a else b
    let abs x = if x < 0 then 0 - x else x
    let fst p = match p with | (a, _) -> a
    let snd p = match p with | (_, b) -> b
    let ignore x = ()

open Core
```

### Prelude File Changes — List.fun (additions only)

Add after `append` (line 6), before `hd`:
```fsharp
    let rec zip xs = fun ys -> match xs with | [] -> [] | x :: xt -> match ys with | [] -> [] | y :: yt -> (x, y) :: zip xt yt
    let rec take n = fun xs -> if n = 0 then [] else match xs with | [] -> [] | h :: t -> h :: take (n - 1) t
    let rec drop n = fun xs -> if n = 0 then xs else match xs with | [] -> [] | _ :: t -> drop (n - 1) t
```

Add after `nth` (before `head`):
```fsharp
    let (++) xs ys = append xs ys
```

Add at end (after `ofSeq`):
```fsharp

open List
```

### Prelude File Changes — Option.fun

```fsharp
// Target: match FunLang/Prelude/Option.fun exactly
module Option =
    type Option 'a = None | Some of 'a
    let optionMap f = fun opt -> match opt with | Some x -> Some (f x) | None -> None
    let optionBind f = fun opt -> match opt with | Some x -> f x | None -> None
    let optionDefault def = fun opt -> match opt with | Some x -> x | None -> def
    let isSome opt = match opt with | Some _ -> true | None -> false
    let isNone opt = match opt with | Some _ -> false | None -> true
    let (<|>) a b = match a with | Some x -> Some x | None -> b
    let optionIter f = fun opt -> match opt with | Some x -> f x | None -> ()
    let optionFilter pred = fun opt -> match opt with | Some x -> if pred x then Some x else None | None -> None
    let optionDefaultValue def = fun opt -> match opt with | Some x -> x | None -> def
    let optionIsSome opt = match opt with | Some _ -> true | None -> false
    let optionIsNone opt = match opt with | Some _ -> false | None -> true

open Option
```

### Prelude File Changes — Result.fun

```fsharp
// Target: match FunLang/Prelude/Result.fun exactly
module Result =
    type Result 'a 'b = Ok of 'a | Error of 'b
    let resultMap f = fun r -> match r with | Ok x -> Ok (f x) | Error e -> Error e
    let resultBind f = fun r -> match r with | Ok x -> f x | Error e -> Error e
    let resultMapError f = fun r -> match r with | Ok x -> Ok x | Error e -> Error (f e)
    let resultDefault def = fun r -> match r with | Ok x -> x | Error _ -> def
    let isOk r = match r with | Ok _ -> true | Error _ -> false
    let isError r = match r with | Ok _ -> false | Error _ -> true
    let resultIter f = fun r -> match r with | Ok x -> f x | Error _ -> ()
    let resultToOption r = match r with | Ok x -> Some x | Error _ -> None
    let resultDefaultValue def = fun r -> match r with | Ok x -> x | Error _ -> def

open Result
```

### Newline Fix (HashSet.fun, MutableList.fun, Queue.fun)

These files are byte-identical to FunLang EXCEPT for missing trailing newline. Add `\n` at end of each file.

### Verifying sanitizeMlirName coverage

```fsharp
// All 4 call sites in Printer.fs (already integrated):
// 1. printFuncOp (line 209): func.func/llvm.func definition name
sprintf "  %s %s%s%s {\n%s\n  }" keyword (sanitizeMlirName func.Name) ...

// 2. DirectCallOp (line 104): func.call callee
sprintf "... func.call %s(...) ..." (sanitizeMlirName callee) ...

// 3. LlvmAddressOfOp (line 120): llvm.mlir.addressof
sprintf "... llvm.mlir.addressof %s ..." (sanitizeMlirName fnName) ...

// 4. LlvmCallOp / LlvmCallVoidOp (lines 145-159): llvm.call callee
let sc = sanitizeMlirName callee
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `OpenDecl` is a no-op | `OpenDecl` emits alias LetDecls | Phase 41 | Unqualified names work correctly after `open` |
| Prelude Option uses `map`, `bind`, etc. | Prelude Option uses `optionMap`, `optionBind`, etc. | Phase 41 | FunLang parity; avoids `map` name collision with List.map |
| Prelude List has no `zip`/`take`/`drop`/(++) | Added | Phase 41 | Enables new list operations in compiled programs |

**Deprecated/outdated:**
- `OpenDecl` no-op behavior: The wildcard `| _ -> [d]` in `flattenDecls` currently passes `OpenDecl` through. After Phase 41, the wildcard must NOT match `OpenDecl` — add an explicit case before the wildcard.

## Open Questions

1. **Multi-segment open paths (e.g., `open A.B`)**
   - What we know: `OpenDecl` type is `OpenDecl of path: string list * Span`, so `open A.B` = `OpenDecl(["A"; "B"], _)`. FunLang's evaluator only handles the `[name]` single-segment case and ignores multi-segment.
   - What's unclear: Prelude files only use single-segment `open Core`, `open List`, etc. Multi-segment is not needed.
   - Recommendation: Match only `OpenDecl([openedMod], s)` (single-segment) and emit `[]` for multi-segment paths. This matches FunLang evaluator behavior.

2. **`open` inside a module body**
   - What we know: FunLang allows `open` at top-level only for Prelude files. No Prelude file uses nested open.
   - What's unclear: If `open List` appears inside a `module Foo = ... open List ...`, `flattenDecls` is processing `innerDecls` with `modName = "Foo"`. The `OpenDecl` handler would look up `List` in `moduleMembers` and emit aliases — but those aliases would be namespaced as `LetDecl("Foo_map", ...)` if `modName <> ""`.
   - Recommendation: Emit aliases WITHOUT the outer `modName` prefix regardless of current `modName`. Add `when modName = ""` guard to the `OpenDecl` case, OR emit unqualified aliases always. Research finding: Prelude only uses top-level open, so this edge case doesn't block Phase 41.

3. **backward compatibility of Option/Result API rename**
   - What we know: Tests 35-05 (`option-module`) and 35-06 (`result-module`) define their own inline `Option`/`Result` modules with OLD names (`map`, `bind`, etc.). These will continue to work.
   - What's unclear: Are there any test files using `Option.map` or `Result.map` via the Prelude (not inline)?
   - Finding: `grep` confirms only tests 35-05/35-06 use `Option.map`/`Result.map`, and both define their own modules.
   - Recommendation: No compatibility shims needed; proceed with the rename.

## Sources

### Primary (HIGH confidence)
- Direct code inspection of `Elaboration.fs` lines 3678–3694 (flattenDecls), 662–674 (short-name alias), 688–695 (Let short-name alias), 967–994 (LetRec short-name alias) — codebase source of truth
- Direct code inspection of `Printer.fs` lines 7–33 (`sanitizeMlirName`), 104, 120, 145–159, 209 (call sites) — confirmed full integration
- `diff` of all 12 Prelude files — exact change set identified
- Integration tests (`/tmp/test_op*.fun`) — verified operator compilation works end-to-end

### Secondary (MEDIUM confidence)
- `../FunLang/src/FunLang/Eval.fs` lines 1620–1631 — reference implementation of `open` semantics
- `../FunLang/src/FunLang/Parser.fsy` lines 363–367, 782–786 — custom operator `let (op)` syntax

## Metadata

**Confidence breakdown:**
- OpenDecl implementation pattern: HIGH — code structure is clear, two-pass approach is correct
- sanitizeMlirName coverage: HIGH — verified all 4 call sites, integration tested with operators
- Prelude file diffs: HIGH — exact diff output inspected, changes are mechanical
- Backward compatibility: HIGH — existing tests confirmed self-contained, no Prelude dependency for old names

**Research date:** 2026-03-30
**Valid until:** 2026-04-30 (stable codebase, no external dependencies)
