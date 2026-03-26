# Phase 16: Environment Infrastructure - Research

**Researched:** 2026-03-26
**Domain:** F# compiler data structures — ElabEnv extension, MatchCompiler DU extension, program-level declaration pre-pass
**Confidence:** HIGH

---

## Summary

Phase 16 is pure setup: no MLIR IR is emitted, no new MlirOp cases are added, and no binary layout decisions are made. The work is entirely in F# data structure extension and dispatch plumbing. Two files change: `Elaboration.fs` gains `elaborateProgram`, new `ElabEnv` fields, and a declaration pre-pass; `MatchCompiler.fs` gains two `CtorTag` variants and two `desugarPattern` arms that were previously `failwith` stubs.

The current entry point is `elaborateModule : Expr -> MlirModule` called from `Program.fs` via `Parser.start` (which parses a single expression). The new entry point must be `elaborateProgram : Ast.Module -> MlirModule`, switching to `Parser.parseModule` (which returns `Ast.Module` containing a `Decl list`). The pre-pass scans the `Decl list` for `TypeDecl`, `RecordTypeDecl`, and `ExceptionDecl` cases, registers constructor names with sequential tag indices, registers record field names with sequential indices, and builds the new `ElabEnv` maps before any expression elaboration begins.

The existing 45 E2E tests currently use single-expression inputs. They will continue passing because: (a) `Parser.parseModule` on a bare expression input produces a `Module` containing a single `LetDecl` or a wrapper form, and (b) the new `elaborateProgram` falls through the pre-pass (finding no type/record/exception declarations) and delegates to the existing `elaborateExpr` machinery unchanged.

**Primary recommendation:** Implement `elaborateProgram` as a thin wrapper that runs a pre-pass (building `TypeEnv`/`RecordEnv`/`ExnTags`), constructs `ElabEnv` with those maps, then calls the existing `elaborateExpr` on the program body expression. Add `AdtCtor`/`RecordCtor` to `CtorTag` with stub `ctorArity` cases and complete `desugarPattern` dispatch that does not emit IR (placeholder tests that return failing test nodes or `failwith` with a Phase 17 message).

---

## Standard Stack

No new external libraries. All work is pure F# within the existing project.

### Core
| Component | Current State | Phase 16 Change |
|-----------|--------------|-----------------|
| `Elaboration.fs` | `ElabEnv` has 8 fields; entry point is `elaborateModule : Expr -> MlirModule` | Add `TypeEnv`, `RecordEnv`, `ExnTags` fields; add `elaborateProgram : Ast.Module -> MlirModule` |
| `MatchCompiler.fs` | `CtorTag` has 6 variants; `desugarPattern` has `failwith` stubs at lines 121-125 | Add `AdtCtor of string * int` and `RecordCtor of string list`; implement stubs |
| `Program.fs` | Calls `Parser.start` (returns `Ast.Expr`), then `elaborateModule` | Change to `Parser.parseModule` (returns `Ast.Module`), then `elaborateProgram` |
| `LangThree.Parser` | Exports `parseModule : _ -> _ -> Ast.Module` and `start : _ -> _ -> Ast.Expr` | Unchanged; `parseModule` is already available |

### Supporting
| Component | Purpose |
|-----------|---------|
| `Ast.Decl` (LangThree) | DU with `LetDecl`, `TypeDecl of TypeDecl`, `RecordTypeDecl of RecordDecl`, `ExceptionDecl`, etc. |
| `Ast.TypeDecl` | `TypeDecl of name: string * typeParams: string list * constructors: ConstructorDecl list * Span` |
| `Ast.ConstructorDecl` | `ConstructorDecl of name: string * dataType: TypeExpr option * Span` or `GadtConstructorDecl of ...` |
| `Ast.RecordDecl` | `RecordDecl of name: string * typeParams: string list * fields: RecordFieldDecl list * Span` |
| `Ast.RecordFieldDecl` | `RecordFieldDecl of name: string * fieldType: TypeExpr * isMutable: bool * Span` |
| `Ast.Module` | `Module of decls: Decl list * Span` (and Named/Namespaced/Empty variants) |

**Installation:** No new packages required.

---

## Architecture Patterns

### Recommended Project Structure (unchanged)

No new files are created. Changes are surgical:

```
src/LangBackend.Compiler/
├── Elaboration.fs    # ElabEnv gains 3 fields; add elaborateProgram; add prePassDecls
├── MatchCompiler.fs  # CtorTag gains 2 variants; desugarPattern gains 2 arms
└── (all others)      # unchanged
src/LangBackend.Cli/
└── Program.fs        # switch parseExpr->parseModule, elaborateModule->elaborateProgram
```

### Pattern 1: ElabEnv Extension

Add three new fields to `ElabEnv`. Use F# `Map<string, ...>` — the standard map for immutable lookup tables in this codebase.

```fsharp
// TypeEnv: constructor name -> (tag index * arity)
// tag index is the 0-based position of the constructor in its TypeDecl
// arity: 0 for nullary, 1 for unary (all ConstructorDecl either have None or Some payload)
type TypeInfo = { Tag: int; Arity: int }

// RecordEnv: (record type name, field name) -> field index
// field index is 0-based position in the RecordDecl.fields list
// Alternative: Map<string, Map<string, int>> keyed by type name then field name

// ExnTags: exception constructor name -> tag index
// Uses same mechanism as ADT constructors (sequential integers, 0-based per exception "type")
// Simplest: all exception ctors share a flat namespace, each gets a unique global tag index

type ElabEnv = {
    Vars:           Map<string, MlirValue>
    Counter:        int ref
    LabelCounter:   int ref
    Blocks:         MlirBlock list ref
    KnownFuncs:     Map<string, FuncSignature>
    Funcs:          FuncOp list ref
    ClosureCounter: int ref
    Globals:        (string * string) list ref
    GlobalCounter:  int ref
    // NEW Phase 16:
    TypeEnv:        Map<string, TypeInfo>         // constructor name -> tag + arity
    RecordEnv:      Map<string, Map<string, int>> // record type name -> (field name -> index)
    ExnTags:        Map<string, int>              // exception ctor name -> tag index
}
```

`emptyEnv` gains `TypeEnv = Map.empty; RecordEnv = Map.empty; ExnTags = Map.empty`.

### Pattern 2: Declaration Pre-Pass

`elaborateProgram` takes `Ast.Module`, extracts the `Decl list`, runs a pre-pass to build the three maps, constructs `ElabEnv` with those maps, then drives expression elaboration.

```fsharp
// Simplified structure:
let elaborateProgram (ast: Ast.Module) : MlirModule =
    let decls = match ast with
                | Ast.Module(decls, _) | Ast.NamedModule(_, decls, _)
                | Ast.NamespacedModule(_, decls, _) -> decls
                | Ast.EmptyModule _ -> []

    // Pre-pass: build TypeEnv, RecordEnv, ExnTags
    let typeEnv, recordEnv, exnTags = prePassDecls decls

    let env = { emptyEnv () with TypeEnv = typeEnv; RecordEnv = recordEnv; ExnTags = exnTags }

    // Find the expression to elaborate (last LetDecl body, or wrap all LetDecls as nested Lets)
    let mainExpr = extractMainExpr decls
    // ... same elaboration as elaborateModule but using env with populated maps
    elaborateModuleFromEnv env mainExpr
```

The exact shape of `extractMainExpr` depends on how `Parser.parseModule` wraps bare expressions. See the critical finding below.

### Pattern 3: prePassDecls Implementation

```fsharp
let private prePassDecls (decls: Ast.Decl list) : Map<string,TypeInfo> * Map<string,Map<string,int>> * Map<string,int> =
    let mutable typeEnv = Map.empty
    let mutable recordEnv = Map.empty
    let mutable exnTags = Map.empty

    for decl in decls do
        match decl with
        | Ast.TypeDecl (Ast.TypeDecl(_, _, ctors, _)) ->
            ctors |> List.iteri (fun idx ctor ->
                match ctor with
                | Ast.ConstructorDecl(name, dataType, _) ->
                    let arity = match dataType with None -> 0 | Some _ -> 1
                    typeEnv <- Map.add name { Tag = idx; Arity = arity } typeEnv
                | Ast.GadtConstructorDecl(name, argTypes, _, _) ->
                    let arity = if argTypes.IsEmpty then 0 else 1  // wrap multi-arg as tuple
                    typeEnv <- Map.add name { Tag = idx; Arity = arity } typeEnv
            )
        | Ast.RecordTypeDecl (Ast.RecordDecl(typeName, _, fields, _)) ->
            let fieldMap =
                fields |> List.mapi (fun idx (Ast.RecordFieldDecl(name, _, _, _)) -> (name, idx))
                       |> Map.ofList
            recordEnv <- Map.add typeName fieldMap recordEnv
        | Ast.ExceptionDecl(name, dataType, _) ->
            let nextTag = exnTags.Count  // or use a shared counter
            exnTags <- Map.add name nextTag exnTags
        | _ -> ()  // LetDecl, LetPatDecl, etc. — no pre-pass action

    (typeEnv, recordEnv, exnTags)
```

### Pattern 4: CtorTag Extension

Add two new variants to `CtorTag` in `MatchCompiler.fs`:

```fsharp
type CtorTag =
    | IntLit of int
    | BoolLit of bool
    | StringLit of string
    | ConsCtor            // h :: t
    | NilCtor             // []
    | TupleCtor of int    // tuple of arity n
    // NEW Phase 16:
    | AdtCtor of name: string * tag: int   // ADT constructor with known tag index
    | RecordCtor of fields: string list    // record with sorted field names for identity
```

`ctorArity` gains two arms:

```fsharp
| AdtCtor(_, _) -> 1    // payload treated as single field (Phase 17 extracts it)
                         // OR: 0 for nullary, 1 for unary — requires arity in the DU
| RecordCtor fields -> List.length fields
```

**Design decision for `AdtCtor`:** Include the arity in the tag so `ctorArity` is self-contained without needing `TypeEnv`:

```fsharp
| AdtCtor of name: string * tag: int * arity: int
```

`ctorArity` arm: `| AdtCtor(_, _, arity) -> arity`

### Pattern 5: desugarPattern Dispatch for ConstructorPat and RecordPat

The two `failwith` stubs at MatchCompiler.fs lines 121-125 become real dispatch arms. Phase 16 does NOT emit IR — the arms build correct `Test` structures for the decision tree. The actual IR emission happens in Phase 17 when `elaborateDecisionTree` encounters `Switch(_, AdtCtor(...), ...)`.

```fsharp
| ConstructorPat(name, argPatOpt, _) ->
    // Look up tag and arity from the scrutinee context.
    // Phase 16: MatchCompiler.compile must accept a ctorLookup function or a CtorTag map.
    // For now, use a placeholder: AdtCtor(name, tag=0, arity) where arity matches argPatOpt.
    // Phase 17 will wire the real tag from TypeEnv.
    // Simpler approach: desugarPattern receives a (string -> CtorTag) lookup parameter.
    let arity = match argPatOpt with None -> 0 | Some _ -> 1
    let subPats = match argPatOpt with None -> [] | Some p -> [p]
    ([{ Scrutinee = acc; Pattern = CtorTest(AdtCtor(name, 0, arity), subPats) }], [])

| RecordPat(fields, _) ->
    // Sort fields by name for canonical ordering (matches RecordEnv index assignment).
    let sorted = fields |> List.sortBy fst
    let fieldNames = sorted |> List.map fst
    let subPats   = sorted |> List.map snd
    ([{ Scrutinee = acc; Pattern = CtorTest(RecordCtor fieldNames, subPats) }], [])
```

**Tag lookup problem:** `desugarPattern` currently has no access to `TypeEnv`. Two approaches:

1. **Thread `TypeEnv` into `desugarPattern`** as an extra parameter. This requires changing `compile`'s signature too. Clean but more invasive.
2. **Store tag=0 as placeholder now**, fix in Phase 17 when `elaborateDecisionTree` is implemented. This is the Phase 16 approach — the decision tree structure is correct but the tag value is a placeholder.

Approach 2 is preferred for Phase 16 because the goal is dispatch-without-failwith, not correct IR generation (which is Phase 17).

### Pattern 6: Program.fs Entry Point Switch

```fsharp
// Old:
let parseExpr (src: string) (filename: string) : Ast.Expr =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    Parser.start Lexer.tokenize lexbuf

// New (add alongside, then change the call site):
let parseProgram (src: string) (filename: string) : Ast.Module =
    let lexbuf = LexBuffer<char>.FromString src
    Lexer.setInitialPos lexbuf filename
    Parser.parseModule Lexer.tokenize lexbuf
```

Then in `main`:
```fsharp
// Old:
let expr = parseExpr src inputPath
let mlirMod = Elaboration.elaborateModule expr

// New:
let ast = parseProgram src inputPath
let mlirMod = Elaboration.elaborateProgram ast
```

### Anti-Patterns to Avoid

- **Emitting any IR in the pre-pass:** The pre-pass only builds `Map` structures. No `freshName`, no `MlirOp` construction. IR emission starts in Phase 17.
- **Using `Parser.start` with module inputs:** A file with `type Color = Red | Green | Blue` followed by expression code is a module, not a single expression. `Parser.start` will fail on it. Switch to `Parser.parseModule`.
- **Assuming `Parser.parseModule` on bare expression input yields the same structure:** Verify empirically (see Open Questions). The existing tests use bare expression inputs.
- **Putting ExnTags in TypeEnv directly:** Exception constructors share the name → tag mapping need with ADT constructors, but they come from different AST nodes. Keeping them in a separate `ExnTags` map preserves the distinction for Phase 19 (exception handlers need to know something is an exception vs. ADT). Alternatively, merge them into `TypeEnv` if Phase 17/19 treats them identically.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Tag index assignment | Custom hash or string-based tags | `List.iteri` with index as tag | Sequential integer tags are what the `i64 tag @field0` layout requires |
| Map building | Mutable dictionary then convert | F# `Map.ofList` / fold into `Map.add` | Already the idiom throughout the codebase |
| Field sorting | Custom compare | `List.sortBy fst` | RecordPat fields arrive in arbitrary order; canonical sort matches RecordEnv index order |
| Constructor lookup in `desugarPattern` | Global mutable state | Extra parameter or placeholder | Global mutable breaks thread safety and test isolation |

---

## Common Pitfalls

### Pitfall 1: `Parser.parseModule` on Bare Expression Inputs

**What goes wrong:** Existing E2E test inputs are bare expressions (e.g., `let rec sum lst = ... in sum [1..5]`). `Parser.parseModule` may wrap these differently than expected — for example, as a `Module([LetDecl(...)], ...)` or as a `Module([LetRecDecl(...)], ...)` rather than as a single expression.

**Why it happens:** The LangThree parser has two entry points: `start` (expression grammar) and `parseModule` (declaration grammar). The module grammar may or may not accept bare expressions or may desugar them differently.

**How to avoid:** Before implementing `elaborateProgram`, empirically test `Parser.parseModule` on the simplest existing test input (e.g., `42`) and a `let` expression. Inspect the `Ast.Module` structure. Then design `extractMainExpr` to match what `parseModule` actually produces.

**Warning signs:** All 45 E2E tests fail after the Program.fs switch — this indicates `extractMainExpr` is not handling the module structure correctly.

### Pitfall 2: Constructor Name Collision Across Types

**What goes wrong:** If two `TypeDecl`s define a constructor with the same name (e.g., `type A = Some of int` and `type B = Some of string`), the last one wins in the `TypeEnv` map.

**Why it happens:** `TypeEnv` uses constructor name as the key with no namespace prefix.

**How to avoid:** For Phase 16, accept this limitation (it's the same behavior as the LangThree interpreter). Document it. Phase 17+ can address it if needed.

**Warning signs:** Tests that use constructors from multiple types with overlapping names produce wrong tag values.

### Pitfall 3: `ExnTags.Count` for Next Tag Index Is Brittle

**What goes wrong:** Using `exnTags.Count` (or `Map.count exnTags`) as the next tag index works only if declarations are processed once and sequentially. If `prePassDecls` is called multiple times or if the map is pre-seeded, tags shift.

**How to avoid:** Use a `ref int` counter instead of `Map.count`. Increment it after each `ExceptionDecl`.

### Pitfall 4: `CtorTag` Equality in splitClauses

**What goes wrong:** `splitClauses` in `MatchCompiler.fs` uses `tTag = selTag` (structural equality). With placeholder `tag=0` in `AdtCtor(name, 0, arity)`, two constructors from different ADTs but both assigned placeholder tag 0 compare as equal.

**Why it happens:** Placeholder tags are all 0 in Phase 16. The comparison `AdtCtor("Some", 0, 1) = AdtCtor("None", 0, 0)` is false (name differs), but `AdtCtor("Some", 0, 1) = AdtCtor("Some", 0, 1)` is true. Since the name is included in the DU, equality is actually safe — equality is by constructor name, not tag number. The tag value is irrelevant for Phase 16 decision tree structure.

**Resolution:** `AdtCtor` comparison uses the full DU structural equality which includes the `name` field. Names are unique per constructor (within a type). Phase 16 is safe.

### Pitfall 5: `Ast.Decl.TypeDecl` Shadows `Ast.TypeDecl`

**What goes wrong:** In `Ast.fs`, the module-level declaration `Decl.TypeDecl` wraps `Ast.TypeDecl`. Pattern matching in `prePassDecls` requires double-level matching:

```fsharp
| Ast.TypeDecl (Ast.TypeDecl(name, typeParams, ctors, span)) -> ...
```

The outer `Ast.TypeDecl` is the `Decl` case; the inner `Ast.TypeDecl(...)` is the nested DU.

**Warning signs:** Pattern match incomplete warning or runtime `MatchFailureException` on type declarations.

---

## Code Examples

Verified patterns from direct code inspection:

### Existing ElabEnv Construction (Elaboration.fs lines 32-35)

```fsharp
// Source: Elaboration.fs line 32
let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
      KnownFuncs = Map.empty; Funcs = ref []; ClosureCounter = ref 0
      Globals = ref []; GlobalCounter = ref 0 }
```

Phase 16 extends this with:
```fsharp
      TypeEnv = Map.empty; RecordEnv = Map.empty; ExnTags = Map.empty
```

### Existing CtorTag and ctorArity (MatchCompiler.fs lines 29-76)

```fsharp
// Source: MatchCompiler.fs lines 29-36
type CtorTag =
    | IntLit of int
    | BoolLit of bool
    | StringLit of string
    | ConsCtor
    | NilCtor
    | TupleCtor of int

// Source: MatchCompiler.fs lines 69-76
let private ctorArity (tag: CtorTag) : int =
    match tag with
    | IntLit _    -> 0
    | BoolLit _   -> 0
    | StringLit _ -> 0
    | NilCtor     -> 0
    | ConsCtor    -> 2
    | TupleCtor n -> n
```

Phase 16 adds:
```fsharp
    | AdtCtor(_, _, arity) -> arity
    | RecordCtor fields    -> List.length fields
```

### Existing desugarPattern Stubs (MatchCompiler.fs lines 120-125)

```fsharp
// Source: MatchCompiler.fs lines 120-125
| ConstructorPat _ ->
    failwith "MatchCompiler: ConstructorPat not yet supported in backend"
| RecordPat _ ->
    failwith "MatchCompiler: RecordPat not yet supported in backend"
| OrPat _ ->
    failwith "MatchCompiler: OrPat not yet supported in backend"
```

Phase 16 replaces `ConstructorPat` and `RecordPat` arms; `OrPat` stays as `failwith` (out of scope).

### Ast.Decl Structure (Ast.fs lines 312-330)

```fsharp
// Source: Ast.fs lines 312-330
type Decl =
    | LetDecl of name: string * body: Expr * Span
    | LetPatDecl of pat: Pattern * body: Expr * Span
    | TypeDecl of TypeDecl                // wraps Ast.TypeDecl
    | RecordTypeDecl of RecordDecl        // wraps Ast.RecordDecl
    | ExceptionDecl of name: string * dataType: TypeExpr option * Span
    | ModuleDecl of name: string * decls: Decl list * Span
    | OpenDecl of path: string list * Span
    | NamespaceDecl of path: string list * decls: Decl list * Span
    | TypeAliasDecl of name: string * typeParams: string list * body: TypeExpr * Span
    | LetRecDecl of bindings: (string * string * Expr * Span) list * Span
    | FileImportDecl of path: string * Span
    | LetMutDecl of name: string * body: Expr * Span
```

### Ast.Module Variants (Ast.fs lines 334-338)

```fsharp
// Source: Ast.fs lines 334-338
type Module =
    | Module of decls: Decl list * Span
    | NamedModule of name: string list * decls: Decl list * Span
    | NamespacedModule of name: string list * decls: Decl list * Span
    | EmptyModule of Span
```

### Existing TuplePat desugarPattern for reference (MatchCompiler.fs lines 103-119)

```fsharp
// Source: MatchCompiler.fs lines 103-119
| TuplePat (pats, _) ->
    let n = List.length pats
    ([{ Scrutinee = acc; Pattern = CtorTest(TupleCtor n, []) }], [])
    |> ignore
    let subResults =
        pats |> List.mapi (fun i subPat ->
            desugarPattern (Field(acc, i)) subPat
        )
    let subTests = subResults |> List.collect fst
    let subBinds = subResults |> List.collect snd
    (subTests, subBinds)
```

Note: The `TuplePat` implementation has dead code (the first expression with `|> ignore`). The real implementation starts with `let subResults = ...`. Phase 16's `ConstructorPat`/`RecordPat` should follow the same sub-pattern expansion pattern.

---

## State of the Art

| Old Approach | Current Approach | Phase 16 Change |
|--------------|------------------|-----------------|
| `elaborateModule : Expr -> MlirModule` | Unchanged | Add `elaborateProgram : Ast.Module -> MlirModule` |
| `Parser.start` (expression grammar) | Unchanged | Switch CLI to `Parser.parseModule` (module grammar) |
| `CtorTag` has 6 variants | Unchanged | Gains `AdtCtor` and `RecordCtor` (8 total) |
| `desugarPattern` `failwith` at ConstructorPat/RecordPat | Unchanged | Real dispatch returning correct `Test` lists |
| `ElabEnv` has 8 fields | Unchanged | Gains `TypeEnv`, `RecordEnv`, `ExnTags` (11 total) |

**Not changing in Phase 16:**
- `MlirIR.fs` — no new ops, no new types
- `Printer.fs` — no new print cases
- `Pipeline.fs` — unchanged
- `lang_runtime.c` — unchanged
- Any existing `elaborateExpr` arms — unchanged

---

## Open Questions

1. **`Parser.parseModule` behavior on bare expression inputs**
   - What we know: `Parser.start` parses a single expression; `Parser.parseModule` parses a `Module` of `Decl list`
   - What's unclear: Does `parseModule` accept `42` or `let x = 5 in x + 1` as valid module input? If so, does it produce `Module([LetDecl("_", ...)], ...)` or something else?
   - Recommendation: Before writing `extractMainExpr`, add a small F# REPL/test that calls `Parser.parseModule` on the simplest existing test input and inspects the result. This directly determines how `elaborateProgram` drives the existing `elaborateExpr`.

2. **How to thread `TypeEnv` into `desugarPattern` for real tag lookup**
   - What we know: Phase 16 uses placeholder tag=0; Phase 17 needs real tags for correct IR
   - What's unclear: Best approach — extra parameter to `compile` and `desugarPattern`, or closure capture
   - Recommendation: For Phase 16, use placeholder tag=0. In Phase 17, add `ctorLookup: string -> TypeInfo` parameter to `compile` and `desugarPattern`. Both phases are self-contained.

3. **`ExnTags` vs. merging into `TypeEnv`**
   - What we know: ExceptionDecl names follow the same name→tag pattern as constructor names; Phase 19 (TryWith) will need to look up exception constructors
   - What's unclear: Whether Phase 19 needs to distinguish "this is an exception ctor" from "this is an ADT ctor" at lookup time
   - Recommendation: Keep `ExnTags` separate for Phase 16. If Phase 19 finds it cleaner to merge them into `TypeEnv`, that's a Phase 19 refactor decision.

---

## Sources

### Primary (HIGH confidence)

- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` (lines 20-35, 1226-1268) — `ElabEnv` definition, `emptyEnv`, `elaborateModule` — direct inspection
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MatchCompiler.fs` (lines 29-126) — `CtorTag` DU, `ctorArity`, `desugarPattern` with `failwith` stubs — direct inspection
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Cli/Program.fs` — current `parseExpr`/`elaborateModule` call site — direct inspection
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` (lines 161-338) — `TypeDecl`, `ConstructorDecl`, `RecordDecl`, `RecordFieldDecl`, `Decl`, `Module` — direct inspection
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Parser.fs` (lines 3764-3767) — `parseModule` and `start` signatures — direct inspection
- `.planning/research/SUMMARY.md` — project-level research confirming architecture approach and ElabEnv extension design

### Secondary (MEDIUM confidence)

- `.planning/REQUIREMENTS.md` — ADT-01 through REC-01 requirement text — authoritative for Phase 16 scope
- `.planning/ROADMAP.md` — Phase 16 success criteria — authoritative for what must be true

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new dependencies; all changes are additive F# map/DU extensions
- Architecture: HIGH — pre-pass pattern is identical to LangThree's `Elaborate.fs` pre-pass; CtorTag extension is a textbook DU additive extension
- Pitfalls: HIGH — all pitfalls identified from direct code inspection (shadow names, `Parser.parseModule` behavior, tag placeholder)

**Research date:** 2026-03-26
**Valid until:** Stable — these are internal F# data structure decisions, not external API dependencies
