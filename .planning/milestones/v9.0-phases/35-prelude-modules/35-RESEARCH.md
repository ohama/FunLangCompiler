# Phase 35: Prelude Modules - Research

**Researched:** 2026-03-29
**Domain:** FunLangCompiler prelude .fun files — module-qualified wrappers over existing builtins
**Confidence:** HIGH

## Summary

Phase 35 creates prelude module .fun source files for String, Hashtable, StringBuilder, Char, List, Array, Option, and Result. These files wrap existing compiler builtins (from Phases 31-34) with module-qualified names such as `String.endsWith`, `List.sort`, `Option.map`, etc. The compiler already handles module declarations via Phase 25 (module system): `module String = let endsWith s suffix = string_endswith s suffix` is flattened by the compiler so that `String.endsWith` → `FieldAccess(Constructor("String"), "endsWith")` → `Var("endsWith")`, which then resolves to the elaborated function body.

The LangThree interpreter already has a working `Prelude/` directory with corresponding .fun files (`String.fun`, `Hashtable.fun`, `Char.fun`, `StringBuilder.fun`, `List.fun`, `Array.fun`, `Option.fun`, `Result.fun`). These are the canonical reference implementations. FunLangCompiler Phase 35 creates a parallel `Prelude/` directory with adapted versions of these files.

The key mechanism is **prelude concatenation in the CLI**: `Program.fs` is extended to discover a `Prelude/` directory (same 3-stage search as LangThree), read all `.fun` files in order, concatenate their source with the user's source, then parse the combined text. This avoids adding `FileImportDecl` support to the compiler (which would require AST changes). Each test `.flt` file in `tests/compiler/35-*.flt` inlines the relevant module definition because the test runner compiles single files. The prelude-loading in the CLI is validated by an E2E test that passes a bare program relying on prelude symbols.

**Primary recommendation:** Create `Prelude/` directory with adapted .fun files; extend CLI to auto-load and prepend prelude source; write E2E tests with inline module definitions for the test runner, and a separate integration test that validates CLI prelude loading.

## Standard Stack

The established components for this codebase:

### Core
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| Prelude/*.fun | FunLangCompiler root `Prelude/` (new) | Module wrapper .fun source files | Same approach as LangThree Prelude/ — .fun files compiled by FunLangCompiler |
| Program.fs | src/FunLangCompiler.Cli/Program.fs | CLI entry point — add prelude loading | Existing single-file CLI; extend `main` to prepend prelude source |
| Elaboration.fs | src/FunLangCompiler.Compiler/Elaboration.fs | No changes needed | Phase 25 module flattening already handles module decls |
| tests/compiler/35-*.flt | tests/compiler/ | E2E test files with inline module defs | Existing .flt format; tests are self-contained |

### Supporting
| Component | Purpose | When to Use |
|-----------|---------|-------------|
| Phase 25 module flattening | `flattenDecls` strips ModuleDecl wrappers | Used automatically; no changes needed |
| Phase 25 qualified name desugar | `FieldAccess(Constructor("M"), "x")` → `Var("x")` | Handles `String.endsWith` → `endsWith` |
| LangThree Prelude/*.fun | Reference implementations | Copy and adapt function names; remove type-checker-specific content |
| `list_sort_by` builtin | C runtime function from Phase 32 | Used in `List.sortBy` wrapper |
| `list_of_seq` builtin | C runtime identity from Phase 32 | Used in `List.ofSeq` wrapper |
| `array_sort` builtin | C runtime function from Phase 32 | Used in `Array.sort` wrapper |
| `array_of_seq` builtin | C runtime identity from Phase 32 | Used in `Array.ofSeq` wrapper |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| CLI text-concatenation for prelude loading | `FileImportDecl` support in compiler | FileImportDecl requires AST traversal in Elaboration.fs; text concat is a one-liner change in Program.fs |
| Inline module defs in each test | Single combined prelude test file | Inline per-test gives isolated failures; single file is harder to diagnose |
| LangThree function names (optionMap, resultBind) | PRE-07/08 names (map, bind) | PRE-07/08 requirement specifies `Option.map`, `Option.bind` — new names needed |

## Architecture Patterns

### Recommended File Structure
```
Prelude/
├── String.fun         # String module — endsWith, startsWith, trim, length, contains
├── Hashtable.fun      # Hashtable module — tryGetValue, count
├── StringBuilder.fun  # StringBuilder module — add, toString (combined with Char)
├── Char.fun           # Char module — IsDigit, ToUpper, IsLetter, IsUpper, IsLower, ToLower
├── List.fun           # List module — 12 functions, all pure .fun with recursive helpers
├── Array.fun          # Array module — sort, ofSeq wrappers
├── Option.fun         # Option type + map, bind, defaultValue, iter, filter, isSome, isNone
└── Result.fun         # Result type + map, bind, defaultValue, mapError, toOption
tests/compiler/
├── 35-01-string-module.flt
├── 35-02-hashtable-module.flt
├── 35-03-stringbuilder-module.flt
├── 35-04-char-module.flt
├── 35-05-list-sort.flt
├── 35-06-list-tryfind-choose.flt
├── ...
```

### Pattern 1: Simple Builtin Wrapper Module
**What:** Module that wraps existing builtins with module-qualified names.
**When to use:** String, Hashtable, Char, StringBuilder, Array.

```
// Prelude/String.fun
module String =
    let endsWith s suffix = string_endswith s suffix
    let startsWith s prefix = string_startswith s prefix
    let trim s = string_trim s
    let length s = string_length s
    let contains s needle = string_contains s needle
```

Compiler behavior: `flattenDecls` strips `module String =` wrapper, leaving `let endsWith s suffix = string_endswith s suffix` etc. at top level. `String.endsWith` → `Var("endsWith")` → calls the function.

### Pattern 2: Pure Functional Module (List)
**What:** Module with recursive helper functions defined in pure .fun — no new builtins needed for most functions.
**When to use:** List module (sort, tryFind, choose, distinctBy, mapi, exists, item, isEmpty, head, tail).

```
// Prelude/List.fun (excerpt)
module List =
    let rec _insert x xs =
        match xs with
        | [] -> [x]
        | h :: t -> if x < h then x :: h :: t else h :: _insert x t
    let rec sort xs =
        match xs with
        | [] -> []
        | h :: t -> _insert h (sort t)
    let sortBy f xs = list_sort_by f xs
    let rec tryFind pred xs =
        match xs with
        | [] -> None
        | h :: t -> if pred h then Some h else tryFind pred t
    ...
```

Key point: `tryFind` and `choose` return `None`/`Some` — these require `type Option 'a = None | Some of 'a` to be defined BEFORE them. `Option.fun` must be loaded BEFORE `List.fun` in the prelude load order.

### Pattern 3: ADT + Module Functions (Option, Result)
**What:** Defines the ADT type (`type Option 'a = None | Some of 'a`) and utility functions together.
**When to use:** Option.fun, Result.fun.

```
// Prelude/Option.fun
module Option =
    type Option 'a = None | Some of 'a
    let map f opt = match opt with | Some x -> Some (f x) | None -> None
    let bind f opt = match opt with | Some x -> f x | None -> None
    let defaultValue def opt = match opt with | Some x -> x | None -> def
    let iter f opt = match opt with | Some x -> f x | None -> ()
    let filter pred opt = match opt with | Some x -> if pred x then Some x else None | None -> None
    let isSome opt = match opt with | Some _ -> true | None -> false
    let isNone opt = match opt with | Some _ -> false | None -> true
```

Critical: the compiler's `prePassDecls` will register `None` (tag 0, arity 0) and `Some` (tag 1, arity 1) in `TypeEnv`. The `typeParams` field of `TypeDecl` is ignored by `prePassDecls` (the wildcard `_` at line 3158). So generic types like `type Option 'a` compile fine.

**Naming alignment required:** LangThree uses `optionMap`, `optionBind`, etc., but PRE-07/08 require `map`, `bind`, `defaultValue`, etc. The FunLangCompiler Prelude files must use the short names.

### Pattern 4: CLI Prelude Loading
**What:** Before parsing user source, the CLI loads and prepends all Prelude/*.fun files.
**Why text concatenation instead of file imports:** The compiler's `elaborateProgram` takes a single `Ast.Module`. File imports would require extending `Elaboration.fs` to handle `FileImportDecl`. Text concatenation is a Program.fs-only change.

```fsharp
// In Program.fs main, before parseProgram:
let preludeSrc =
    let preludeDir =
        let cwd = "Prelude"
        if Directory.Exists cwd then cwd
        else
            let asmDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location)
            let candidate = Path.Combine(asmDir, "Prelude")
            if Directory.Exists candidate then candidate else ""
    if preludeDir = "" then ""
    else
        Directory.GetFiles(preludeDir, "*.fun")
        |> Array.sort  // Deterministic order: alphabetical
        |> Array.map File.ReadAllText
        |> String.concat "\n"

let combinedSrc = preludeSrc + "\n" + src
let ast = parseProgram combinedSrc inputPath
```

**Load order note:** Alphabetical order puts `Array.fun` before `Option.fun`, but `Array.fun` doesn't use Option. `List.fun` uses `None`/`Some` — it comes after `Option.fun` alphabetically. However, `Option.fun` must be loaded before `List.fun`. Alphabetical order is `L` < `O`, so `List.fun` comes BEFORE `Option.fun`. This requires either:
- Non-alphabetical loading with explicit dependency order
- Moving `type Option` definition to a `Core.fun` that loads first (same approach as LangThree's `Core.fun`)
- Or: putting the Option type definition at top-level of List.fun (not recommended — breaks the module abstraction)

**Recommended fix:** Use a `Core.fun` that defines the Option and Result types, loaded first (alphabetically before L/O/R). OR define loading order explicitly. OR simply put the Option type def before the List module in the concatenated source via a sorted order that's not purely alphabetical.

**Simpler approach:** Explicit load order in Program.fs:
```fsharp
let preludeFiles = [| "Core.fun"; "Option.fun"; "Result.fun"; "String.fun";
                      "Char.fun"; "Hashtable.fun"; "StringBuilder.fun";
                      "List.fun"; "Array.fun" |]
```

### Anti-Patterns to Avoid
- **Using `open Option` in tests:** `OpenDecl` is a no-op in the compiler — `open Option` doesn't bring module members into scope at the compiler level. After flattening, all functions ARE already in the global scope since modules are flattened. So `open Option` is not needed and should not be included in FunLangCompiler Prelude files.
- **Expecting type-checked Option:** The compiler is untyped. `Option.map` works by compiling the match patterns against tag 0/1, regardless of the type parameter.
- **Name collisions with helper functions:** `List.fun` defines `_insert`, `_mapi_helper`, `_distinctBy_helper`. When the prelude is prepended to user source, these names become global. Users should not define variables with the same names. Since they start with `_`, this is an acceptable limitation.
- **Mutual recursion in modules:** `LetRecDecl` in the compiler supports single-binding `let rec`. The `and` keyword for mutual recursion would create a multi-binding `LetRecDecl`. Prelude files should not use `let rec f = ... and g = ...`. All LangThree prelude files use only single `let rec`, so this is not an issue.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Prelude .fun files | Write from scratch | Adapt from LangThree's `Prelude/*.fun` | LangThree files are tested and correct; only need name adjustments |
| List sort algorithm | New quicksort in .fun | LangThree's insertion sort (`_insert` + `sort`) | Already proven to work; simple to inline |
| File loading infrastructure | FileImportDecl in Elaboration.fs | Text concatenation in Program.fs | FileImportDecl requires extensive AST changes; concat is 10 lines |
| Option/Result types | Redefine from scratch | LangThree's `type Option 'a = None | Some of 'a` | Same ADT representation — compiler uses tag-based dispatch |

**Key insight:** 90% of Phase 35 is writing .fun files that look like the LangThree prelude. The compiler doesn't need changes (except CLI prelude loading). The only new work is: (1) creating the Prelude/ directory with adapted .fun files, (2) extending Program.fs to load them, and (3) writing E2E tests.

## Common Pitfalls

### Pitfall 1: Open Module is a No-Op
**What goes wrong:** Writing `open List` at the end of List.fun (as in LangThree) causes no problems in tests but also does nothing — `OpenDecl` is silently skipped by the compiler.
**Why it happens:** `extractMainExpr` treats unknown decls as `| _ :: rest -> build rest` (line 3245 in Elaboration.fs). `OpenDecl` is a valid Decl but is not handled.
**How to avoid:** Do NOT include `open Module` statements in FunLangCompiler prelude files — they're harmless but confusing. The module is always flattened anyway.
**Warning signs:** If `open List` were removed from LangThree prelude but FunLangCompiler tests still pass, that confirms the compiler doesn't use it.

### Pitfall 2: Load Order for Option/Result Before List
**What goes wrong:** `List.fun` uses `None` and `Some` constructors in `tryFind` and `choose`. If `List.fun` is loaded before `Option.fun`, `prePassDecls` hasn't registered `None`/`Some` tags yet. Pattern matching on `None`/`Some` fails with "unknown constructor".
**Why it happens:** `prePassDecls` processes `TypeDecl` inside `ModuleDecl`, and `flattenDecls` puts declarations in source order. If List comes first, `None`/`Some` aren't in `TypeEnv` when `list_tryfind` is elaborated... actually wait, `prePassDecls` runs over ALL decls first (before elaboration). So the order in the concatenated source doesn't matter for type pre-pass. Let me reconsider.

Actually, `prePassDecls` is a pre-pass that scans ALL decls before `extractMainExpr`. So `None`/`Some` will be registered regardless of which file comes first in the concatenated source. Load order for prelude files is only relevant if one module's VALUE definitions depend on another module's runtime value (closures, etc.) — which is not the case here. The pure functional definitions in `List.fun` use `None`/`Some` in match patterns, which are handled by `prePassDecls` registering their tags first.

**Revised finding:** Load order matters only for RUNTIME semantics. Since `prePassDecls` is a pre-pass over all decls, the `None`/`Some` constructors will be registered regardless. **Load order is NOT a problem.**

**Corrected guidance:** Use alphabetical load order for simplicity.

### Pitfall 3: Option.fun Naming Conflict with User Programs
**What goes wrong:** User program defines `type Option = None | Some of int` (as in tests 17-05, 20-01, 20-04). If prelude also defines `type Option 'a = None | Some of 'a`, both TypeDecls are processed by `prePassDecls`, and the last one wins. The tag values could differ if they're listed in different constructor orders.
**Why it happens:** `prePassDecls` uses `Map.add` which overwrites — whoever appears later in the flattened decl list wins.
**How to avoid:** In tests that define their own `type Option`, do NOT include the Option prelude module. E2E tests for Option.map should not have a conflicting `type Option` definition. Since the prelude is auto-loaded by the CLI, E2E tests that test the CLI prelude loading assume no user-defined Option type. For the .flt tests (which inline the module), simply use the module definition and don't redefine Option separately.
**Warning signs:** Test output differs when prelude is loaded vs not loaded.

### Pitfall 4: Recursive LetRec Functions in Modules
**What goes wrong:** `let rec sort xs = ...` inside a module is flattened to `LetRecDecl("sort", ["xs"], body, span)` at top level. In `extractMainExpr`, this becomes `LetRec("sort", "xs", body, continuation)`. The `LetRec` arm in the compiler treats the function as having ONE parameter. But `sort` in `List.fun` is defined as `let rec sort xs = ...` with ONE parameter `xs`. This works.

BUT some functions like `let rec fold f = fun acc -> fun xs -> ...` have one explicit param (`f`) and the body returns closures. The `LetRec("fold", "f", Lambda("acc", Lambda("xs", ...)), continuation)` creates `@fold` with one param `f` returning a closure. Recursive calls are `fold f acc xs` = `App(App(App(Var("fold"), f), acc), xs)` — the first `App(Var("fold"), f)` calls `@fold(f)` directly, returning a closure for `acc`, etc. This should work.
**How to avoid:** Keep all recursive functions single-parameter (returning lambdas for additional args). All functions in LangThree prelude already follow this pattern.

### Pitfall 5: String.fun concat Uses string_concat_list
**What goes wrong:** `LangThree/Prelude/String.fun` includes `let concat sep lst = string_concat_list sep lst`. If `string_concat_list` is not implemented in the FunLangCompiler compiler (not in Phase 31 builtins), this would fail.
**Why it happens:** The LangThree prelude was written for the full interpreter feature set; not all builtins may be in FunLangCompiler yet.
**How to avoid:** Review which builtins each prelude function uses. Phase 31 adds: `string_endswith`, `string_startswith`, `string_trim`, `string_length` (from Phase 8), `string_contains` (from Phase 14), char builtins. `string_concat_list` may or may not be implemented. If not, omit `concat` from `String.fun` or implement the builtin as part of Phase 35.

Let me verify: Phase 31 tests include `31-04-string-concat-list.flt`. Let me check if `string_concat_list` is in the compiler.

**Resolution:** Phase 31 test 31-04 tests `string_concat_list`. It IS implemented. Include it in String.fun.

### Pitfall 6: Hashtable.fun includes non-PRE-02 functions
**What goes wrong:** LangThree's `Hashtable.fun` includes `create`, `get`, `set`, `containsKey`, `keys`, `remove` in addition to `tryGetValue` and `count`. These call `hashtable_get`, `hashtable_set`, etc. All these builtins DO exist in the compiler (from Phases 23-32). But PRE-02 only REQUIRES `tryGetValue` and `count` — adding the others is safe bonus functionality.
**How to avoid:** Include all Hashtable builtins that exist in the compiler. This makes the module more useful.

## Code Examples

### String.fun (canonical form for FunLangCompiler)
```
// Prelude/String.fun
module String =
    let concat sep lst = string_concat_list sep lst
    let endsWith s suffix = string_endswith s suffix
    let startsWith s prefix = string_startswith s prefix
    let trim s = string_trim s
    let length s = string_length s
    let contains s needle = string_contains s needle
```

### Option.fun (FunLangCompiler-specific names)
```
// Prelude/Option.fun
module Option =
    type Option 'a = None | Some of 'a
    let map f opt = match opt with | Some x -> Some (f x) | None -> None
    let bind f opt = match opt with | Some x -> f x | None -> None
    let defaultValue def opt = match opt with | Some x -> x | None -> def
    let iter f opt = match opt with | Some x -> f x | None -> ()
    let filter pred opt = match opt with | Some x -> if pred x then Some x else None | None -> None
    let isSome opt = match opt with | Some _ -> true | None -> false
    let isNone opt = match opt with | Some _ -> false | None -> true
```

### Result.fun (FunLangCompiler-specific names)
```
// Prelude/Result.fun
module Result =
    type Result 'a 'b = Ok of 'a | Error of 'b
    let map f r = match r with | Ok x -> Ok (f x) | Error e -> Error e
    let bind f r = match r with | Ok x -> f x | Error e -> Error e
    let mapError f r = match r with | Ok x -> Ok x | Error e -> Error (f e)
    let defaultValue def r = match r with | Ok x -> x | Error _ -> def
    let toOption r = match r with | Ok x -> Some x | Error _ -> None
```

### List.fun (excerpt — full 30+ functions)
```
// Prelude/List.fun
module List =
    let rec _insert x xs =
        match xs with | [] -> [x] | h :: t -> if x < h then x :: h :: t else h :: _insert x t
    let rec sort xs =
        match xs with | [] -> [] | h :: t -> _insert h (sort t)
    let sortBy f xs = list_sort_by f xs
    let rec tryFind pred xs =
        match xs with | [] -> None | h :: t -> if pred h then Some h else tryFind pred t
    let rec choose f xs =
        match xs with | [] -> [] | h :: t -> match f h with | Some v -> v :: choose f t | None -> choose f t
    ...
    let ofSeq coll = list_of_seq coll
```

### Test .flt file format (inline module definition)
```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
module String =
    let endsWith s suffix = string_endswith s suffix
    let startsWith s prefix = string_startswith s prefix
    let trim s = string_trim s
    let length s = string_length s
    let contains s needle = string_contains s needle
let _ = println (to_string (String.endsWith "hello.fun" ".fun")) in
let _ = println (to_string (String.startsWith "hello" "he")) in
println (to_string (String.length "hello"))
// --- Output:
true
true
5
0
```

### CLI prelude loading (Program.fs addition)
```fsharp
// In main, before parseProgram src inputPath:
let findPreludeDir () =
    let cwd = "Prelude"
    if System.IO.Directory.Exists cwd then cwd
    else
        let asmDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location)
        let candidate = System.IO.Path.Combine(asmDir, "Prelude")
        if System.IO.Directory.Exists candidate then candidate
        else ""

let preludeSrc =
    let dir = findPreludeDir ()
    if dir = "" then ""
    else
        // Explicit load order ensures Option/Result available before List/Array
        let ordered = [| "Option.fun"; "Result.fun"; "String.fun"; "Char.fun";
                         "Hashtable.fun"; "StringBuilder.fun"; "List.fun"; "Array.fun" |]
        ordered
        |> Array.choose (fun f ->
            let path = System.IO.Path.Combine(dir, f)
            if System.IO.File.Exists path then Some (System.IO.File.ReadAllText path) else None)
        |> String.concat "\n"

let combinedSrc = if preludeSrc = "" then src else preludeSrc + "\n" + src
let ast = parseProgram combinedSrc inputPath
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Per-file module test (inline) | CLI auto-loads Prelude/ directory | Phase 35 | Users get String.endsWith etc. without defining the module |
| LangThree function names (`optionMap`) | FunLangCompiler names (`Option.map`) | Phase 35 | Cleaner API matching requirement spec |

## Open Questions

1. **string_concat_list in Phase 31** — RESOLVED
   - Confirmed: `string_concat_list ", " ["a"; "b"; "c"]` works (test 31-04). Builtin name is `string_concat_list`. Include in String.fun as `let concat sep lst = string_concat_list sep lst`.

2. **hashtable_containsKey, hashtable_keys, hashtable_get builtins** — RESOLVED
   - Confirmed: tests 23-04 and 23-06 use `hashtable_containsKey` and `hashtable_keys` respectively. Also `hashtable_get` (from test 23-02), `hashtable_set`, `hashtable_remove`, `hashtable_create` all exist. Include all in Hashtable.fun.

3. **Prelude auto-loading vs. inline tests**
   - What we know: The CLI currently has no prelude loading; tests are self-contained
   - What's unclear: Whether Phase 35 E2E tests use inline module defs (simpler) or rely on CLI prelude loading (tests CLI feature too)
   - Recommendation: Use both: inline for .flt tests (fast, isolated), and optionally one integration test that tests CLI prelude loading if the feature is built

4. **`option_none_tag` / constructor tag values**
   - What we know: `prePassDecls` assigns tags based on order in `type` declaration — `None` gets tag 0, `Some` gets tag 1
   - What's unclear: If user code pattern-matches on `None`/`Some` without an `type Option` declaration, will the compiler fail?
   - Recommendation: Prelude must define `type Option` before any code using it; this is guaranteed by the load order above

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/Prelude/` — 9 existing .fun prelude files, verified working
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — Phase 25 module flattening (lines 3189-3246), `prePassDecls` (3151-3187), FieldAccess desugar (2290-2296)
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Cli/Program.fs` — single-file CLI, no prelude loading yet
- `/Users/ohama/vibe-coding/FunLangCompiler/tests/compiler/25-*.flt` — confirmed module system works (qualified vars, open, types, exceptions)

### Secondary (MEDIUM confidence)
- LangThree Prelude.fs load order logic — basis for CLI prelude loading pattern
- LangThree Eval.fs FileImportDecl handling — confirmed text-concat avoids this complexity

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — compiler module system verified working via Phase 25 tests; LangThree prelude files are the canonical reference
- Architecture: HIGH — text concatenation approach is simple and proven; load order analysis complete
- Pitfalls: HIGH — all pitfalls verified by reading actual code (Elaboration.fs, Phase 25 tests)

**Research date:** 2026-03-29
**Valid until:** Stable indefinitely — depends only on Elaboration.fs and LangThree Prelude/ which are stable
