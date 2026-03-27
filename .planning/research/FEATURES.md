# Features Research: Module System + File I/O Builtins

**Researched:** 2026-03-27
**Domain:** LangThree AST — module system nodes and file I/O builtins
**Confidence:** HIGH (all findings from direct source reading of LangThree frontend)

---

## Summary

This document catalogues every AST node, declaration form, and builtin function that the v6.0
Modules & I/O milestone must add to the LangBackend compiler. The research reads `Ast.fs`,
`Eval.fs`, `TypeCheck.fs`, `Parser.fsy`, and integration tests directly from the LangThree
frontend source at `../LangThree/src/LangThree/`.

The module system introduces four new `Decl` variants and four `Module` top-level variants.
The file I/O phase introduces 14 new builtin functions (Phase 32 / STD-02 through STD-15). The
backend currently handles neither set; both must be elaborated to native code.

**Primary recommendation:** Implement `ModuleDecl` and `OpenDecl` as elaboration-time flattening
into the existing `Env` (no new MLIR IR nodes needed). Implement file I/O builtins as C runtime
calls using the established `@lang_*` pattern from `lang_runtime.c`.

---

## Part 1: Module System

### 1.1 Module-level AST Nodes (Ast.fs — `Decl` discriminated union)

All six module-related `Decl` variants exist in `Ast.Decl`:

| Decl Variant | Signature | Notes |
|---|---|---|
| `ModuleDecl` | `name: string * decls: Decl list * Span` | Nested sub-module with indented body |
| `OpenDecl` | `path: string list * Span` | Open directive — merges a named module into scope |
| `NamespaceDecl` | `path: string list * decls: Decl list * Span` | Namespace-scoped decl container (exists in AST but NOT parsed by `parseModule`) |
| `FileImportDecl` | `path: string * Span` | `open "lib.fun"` — import external file at runtime |
| `TypeAliasDecl` | `name: string * typeParams: string list * body: TypeExpr * Span` | Type alias (existing, already skipped by backend) |
| `LetRecDecl` | `bindings: (string * string * Expr * Span) list * Span` | Mutual recursion (existing, already handled) |

### 1.2 Top-level Module Variants (Ast.fs — `Module` discriminated union)

The `Ast.Module` type has four variants, of which the backend already handles all four via
pattern matching in `elaborateProgram`:

```fsharp
type Module =
    | Module of decls: Decl list * Span            // anonymous module (no header)
    | NamedModule of name: string list * decls: Decl list * Span   // module MyModule
    | NamespacedModule of name: string list * decls: Decl list * Span  // namespace MyApp.Utils
    | EmptyModule of Span
```

The backend already unwraps all four variants correctly in `elaborateProgram` (line 2397):
```fsharp
| Ast.Module(decls, _) | Ast.NamedModule(_, decls, _) | Ast.NamespacedModule(_, decls, _) -> decls
| Ast.EmptyModule _ -> []
```

**The module name in `NamedModule`/`NamespacedModule` is ignored at runtime — no elaboration work needed for the top-level variants.**

### 1.3 Parser Syntax (Parser.fsy)

#### Top-level module/namespace header

```
module MyModule
namespace MyApp.Utils
```

Parsed into `NamedModule` / `NamespacedModule`. No indentation required after the header.

#### Nested module definition

```
module Inner =
    let x = 42
    let y = 100
```

Parsed into `ModuleDecl("Inner", [LetDecl("x",...); LetDecl("y",...)], span)`.
Requires `INDENT`/`DEDENT` around the body (indentation-based).

#### Nested modules (deep)

```
module Outer =
    module Inner =
        let value = 42
```

Parsed as `ModuleDecl("Outer", [ModuleDecl("Inner", [LetDecl("value",...)], _)], _)`.
`Outer.Inner.value` is qualified access via chained `FieldAccess` nodes.

#### Open directive

```
open ModuleName
open A.B.C
```

Parsed into `OpenDecl(["ModuleName"], span)` or `OpenDecl(["A";"B";"C"], span)`.
Multi-segment paths (`A.B.C`) are parsed but only single-segment is fully supported at runtime.

#### File import directive

```
open "lib.fun"
open "/absolute/path/to/utils.fun"
```

Parsed into `FileImportDecl("lib.fun", span)` or `FileImportDecl("/absolute/path.fun", span)`.
Relative paths are resolved relative to the importing file's directory.

### 1.4 Qualified Access in Expressions

`Module.member` syntax is parsed as a **chained `FieldAccess` expression**, NOT as a qualified name AST node:

```
Math.add 3 4
```
Parsed as: `App(FieldAccess(Constructor("Math", None, _), "add", _), Number(3,_), _)`

The type checker rewrites `FieldAccess(Constructor(modName, None), fieldName)` into a direct
variable reference via `rewriteModuleAccess` in `TypeCheck.fs`. The backend sees the ALREADY
REWRITTEN expression — a plain `Var` or `App` after elaboration.

**Key insight:** Qualified access `Module.member` becomes `Var("member", ...)` after the
TypeCheck rewrite. The backend does NOT need to handle `FieldAccess` as module access during
elaboration if we rewrite at the same stage.

However, the TypeCheck rewrite happens during LangThree's own type checker. In the backend,
we receive the ORIGINAL unrewritten AST from the parser. The backend must perform its own
`ModuleDecl`/`OpenDecl` analysis to resolve `FieldAccess(Constructor(modName), fieldName)`.

### 1.5 How the Evaluator Handles Modules (Eval.fs)

The interpreter's approach (which the backend should mirror):

1. **`ModuleDecl`**: Evaluates all inner decls into a sub-environment, stores it as a
   `ModuleValueEnv` in `modEnv` keyed by module name. Does NOT inject bindings into the parent env.

2. **`OpenDecl`**: Looks up the module in `modEnv`, merges its `Values` and `CtorEnv` into the
   current `env`. Makes module bindings available unqualified.

3. **`FieldAccess(Constructor(modName, None), fieldName)`**: At elaboration time, this is the
   qualified access pattern. The evaluator resolves this by looking up `modName` in `modEnv`,
   then `fieldName` in `Values` or `CtorEnv`.

4. **`FileImportDecl`**: Loads and evaluates an external file, merging its env into the current
   scope. This is handled via a registered `fileImportEvaluator` delegate.

### 1.6 Module Value Environment Types (Eval.fs)

```fsharp
type ModuleValueEnv = {
    Values: Env                              // name -> Value (let bindings)
    CtorEnv: Map<string, Value>             // constructor name -> DataValue/FunctionValue
    RecEnv: RecordEnv                       // record type info
    SubModules: Map<string, ModuleValueEnv> // nested modules
}
```

For the backend, the equivalent concept is: at elaboration time, a `ModuleDecl` defines a
**namespace** that maps `moduleName.fieldName` to an MLIR `Value` (SSA variable). `OpenDecl`
injects those bindings into the current elaboration environment.

### 1.7 What the Backend Must Implement for Modules

The backend elaboration must handle:

| Decl | Backend Action |
|------|---------------|
| `ModuleDecl(name, innerDecls, _)` | Elaborate `innerDecls` in a nested env. Store result bindings in a `Map<string, ElabEnv>` keyed by `name`. |
| `OpenDecl([name], _)` | Merge named module's bindings into current elaboration env. |
| `OpenDecl([a;b;c], _)` | Multi-segment: traverse sub-module hierarchy. |
| `FieldAccess(Constructor(mod,None), field)` | Look up `mod` in module env, retrieve binding for `field`. Return its SSA `Value`. |
| `FileImportDecl(path, _)` | Parse + elaborate external file; merge bindings. COMPLEX — see below. |

### 1.8 LangThree Module Tests (Expected Behaviors)

From `tests/flt/file/module/` and `ModuleTests.fs`:

| Test | Input | Expected Output |
|------|-------|----------------|
| `module-basic.flt` | `module M = let x = 42` then `M.x` | `42` |
| `module-nested.flt` | `module Outer = module Inner = let x = 42` then `Outer.Inner.x` | `42` |
| `module-open.flt` | `module M = let x = 42` then `open M` then `x` | `42` |
| `module-qualified.flt` | `module Math = let add x y = x + y` then `Math.add 3 4` | `7` |
| `module-adt.flt` | Module with ADT type + open + match | `2` |
| `module-record.flt` | Module with record type + open + field access | `7` |
| `module-exception.flt` | Module with exception decl + open + raise/try-with | `"oops"` |
| `namespace-basic.flt` | `namespace MyApp.Utils` header | normal behavior |
| `module-nested-access.flt` | `Outer.Inner.value` | `42` |

---

## Part 2: File I/O Builtins

### 2.1 Complete Builtin List (Phase 32 / STD-02 through STD-15)

All 14 builtins exist in `Eval.fs` (`initialBuiltinEnv`) and `TypeCheck.fs` (`initialTypeEnv`):

| Name | Type Signature | Behavior | Error |
|------|----------------|----------|-------|
| `read_file` | `string -> string` | Read entire file as string | Raises `LangThreeException` if file not found |
| `stdin_read_all` | `unit -> string` | Read all stdin | No error |
| `stdin_read_line` | `unit -> string` | Read one line from stdin; returns `""` on EOF | No error |
| `write_file` | `string -> string -> unit` | Write string to file (overwrite) | OS error propagates |
| `append_file` | `string -> string -> unit` | Append string to file | OS error propagates |
| `file_exists` | `string -> bool` | Test if file exists | No error |
| `read_lines` | `string -> string list` | Read file as list of lines | Raises `LangThreeException` if file not found |
| `write_lines` | `string -> string list -> unit` | Write list of strings as lines | OS error propagates |
| `get_args` | `unit -> string list` | Get command-line arguments passed to script | No error |
| `get_env` | `string -> string` | Get environment variable | Raises `LangThreeException` if not set |
| `get_cwd` | `unit -> string` | Get current working directory | No error |
| `path_combine` | `string -> string -> string` | Combine path components | No error |
| `dir_files` | `string -> string list` | List files in directory | Raises `LangThreeException` if dir not found |
| `eprint` | `string -> unit` | Write to stderr (no newline) | No error |
| `eprintln` | `string -> unit` | Write to stderr (with newline) | No error |

### 2.2 Detailed Signatures (from TypeCheck.fs initialTypeEnv)

```fsharp
// STD-02
"read_file",      Scheme([], TArrow(TString, TString))
// STD-03
"stdin_read_all", Scheme([], TArrow(TTuple [], TString))
// STD-04
"stdin_read_line", Scheme([], TArrow(TTuple [], TString))
// STD-05
"write_file",     Scheme([], TArrow(TString, TArrow(TString, TTuple [])))
// STD-06
"append_file",    Scheme([], TArrow(TString, TArrow(TString, TTuple [])))
// STD-07
"file_exists",    Scheme([], TArrow(TString, TBool))
// STD-08
"read_lines",     Scheme([], TArrow(TString, TList TString))
// STD-09
"write_lines",    Scheme([], TArrow(TString, TArrow(TList TString, TTuple [])))
// STD-10
"get_args",       Scheme([], TArrow(TTuple [], TList TString))
// STD-11
"get_env",        Scheme([], TArrow(TString, TString))
// STD-12
"get_cwd",        Scheme([], TArrow(TTuple [], TString))
// STD-13
"path_combine",   Scheme([], TArrow(TString, TArrow(TString, TString)))
// STD-14
"dir_files",      Scheme([], TArrow(TString, TList TString))
// STD-15a
"eprint",         Scheme([], TArrow(TString, TTuple []))
// STD-15b
"eprintln",       Scheme([], TArrow(TString, TTuple []))
```

### 2.3 LangThree File I/O Test Expected Behaviors

From `tests/flt/file/fileio/`:

| Test | Operations | Expected Output |
|------|-----------|----------------|
| `fileio-write-read.flt` | `write_file "/tmp/..." "hello world"` then `read_file "/tmp/..."` | `"hello world"` |
| `fileio-append.flt` | `write_file "..." "hello"` then `append_file "..." " world"` then `read_file` | `"hello world"` |
| `fileio-file-exists.flt` | `write_file`, check `file_exists`, check missing | `true`, `false` |
| `fileio-write-read-lines.flt` | `write_lines "..." ["line1";"line2";"line3"]` then `read_lines` | list of 3 strings |
| `fileio-get-cwd.flt` | `get_cwd ()` | non-empty string |
| `fileio-path-combine.flt` | `path_combine "/tmp" "test.txt"` | `"/tmp/test.txt"` |
| `fileio-dir-files.flt` | `write_file "/tmp/flt-dirfiles-test.txt" "data"` then `dir_files "/tmp"` | list contains that file |
| `fileio-get-env-missing.flt` | `get_env "FLT_TEST_DEFINITELY_UNSET_XYZZY"` | raises exception |

### 2.4 Runtime Implementation Pattern

File I/O builtins follow the same pattern as existing builtins in `lang_runtime.c`:
- C function receives/returns `int64_t` (pointer-sized values)
- String arguments arrive as `ptr` (pointing to LangString struct `{i64 length, ptr data}`)
- String return values are GC_malloc'd `LangString` structs
- Lists return `LangCons*` (the standard cons list structure)
- Unit return = return `0` (i64 sentinel)

New C runtime functions needed (suggested names):

```c
// STD-02
ptr lang_read_file(ptr path_str);        // returns LangString*

// STD-03
ptr lang_stdin_read_all(void);           // returns LangString*

// STD-04
ptr lang_stdin_read_line(void);          // returns LangString*

// STD-05
void lang_write_file(ptr path, ptr content);

// STD-06
void lang_append_file(ptr path, ptr content);

// STD-07
int64_t lang_file_exists(ptr path);      // returns 0/1

// STD-08
ptr lang_read_lines(ptr path);           // returns LangCons*

// STD-09
void lang_write_lines(ptr path, ptr lines_list);

// STD-10
ptr lang_get_args(void);                 // returns LangCons* of LangString*

// STD-11
ptr lang_get_env(ptr varname);           // returns LangString* or throws

// STD-12
ptr lang_get_cwd(void);                  // returns LangString*

// STD-13
ptr lang_path_combine(ptr dir, ptr file); // returns LangString*

// STD-14
ptr lang_dir_files(ptr path);            // returns LangCons*

// STD-15a
void lang_eprint(ptr msg);

// STD-15b
void lang_eprintln(ptr msg);
```

### 2.5 Error-Throwing Builtins

Three builtins throw `LangThreeException` on error (matching existing `lang_throw` infrastructure):
- `read_file` — file not found
- `read_lines` — file not found
- `get_env` — variable not set
- `dir_files` — directory not found

These use `lang_throw` with a string-boxed error message, just like `lang_failwith`.

---

## Part 3: File Import (`FileImportDecl`)

### 3.1 Syntax

```
open "lib.fun"
open "/absolute/path/utils.fun"
```

Parsed as `FileImportDecl(path: string, Span)`.

### 3.2 Semantics

A file import loads and evaluates a separate `.fun` source file, merging its top-level bindings
into the current scope. From the evaluator (`Eval.fs`):

```fsharp
| FileImportDecl(path, _span) ->
    let resolvedPath = TypeCheck.resolveImportPath path currentEvalFile
    fileImportEvaluator resolvedPath recEnv modEnv env
```

The imported file's bindings become available in the current scope unqualified (like `open`).
If the imported file contains `module M = ...`, then `M.func` is also accessible.

### 3.3 Path Resolution (`TypeCheck.resolveImportPath`)

```fsharp
let resolveImportPath (importPath: string) (importingFile: string) : string =
    if Path.IsPathRooted importPath then
        importPath  // absolute path: use as-is
    else
        // relative: resolve relative to importing file's directory
        let dir = Path.GetDirectoryName importingFile
        Path.GetFullPath(Path.Combine(dir, importPath))
```

### 3.4 Backend Complexity Assessment

`FileImportDecl` requires the backend to:
1. Parse the external `.fun` file (invoke the full LangThree parser)
2. Type-check it (optional — may rely on LangThree's type checker having already done this)
3. Elaborate it (recursive `elaborateProgram` call)
4. Merge its MLIR-level bindings into the current elaboration env

This is significantly more complex than `ModuleDecl`/`OpenDecl` because it requires recursive
compilation. Options:
- **Option A (Simple):** Reject `FileImportDecl` with an error for now; scope to v7.0.
- **Option B (Medium):** Pre-process all imports before backend elaboration using the LangThree
  type checker's already-resolved environments; inline imported decls into a flat `Decl list`.
- **Option C (Full):** Recursive elaboration with file loading at backend level.

**Recommendation:** Start with `ModuleDecl`/`OpenDecl` (no file loading). Treat `FileImportDecl`
as a stretch goal or separate phase. The LangThree test suite has only 3 file-import test cases
vs. 11 module test cases.

---

## Part 4: What Exists vs. What Needs Work

### 4.1 Already Working in Backend

| Feature | Status |
|---------|--------|
| `NamedModule` / `NamespacedModule` header unwrapping | Done (elaborateProgram line 2397) |
| `EmptyModule` | Done |
| `TypeDecl`, `RecordTypeDecl`, `ExceptionDecl` in prePassDecls | Done |
| `LetDecl`, `LetRecDecl`, `LetMutDecl` in extractMainExpr | Done |

### 4.2 Gaps — Module System

| Gap | Priority |
|-----|----------|
| `ModuleDecl` in `prePassDecls` (collect types from inner decls) | HIGH |
| `ModuleDecl` in `extractMainExpr` (inject inner `let` bindings into scope) | HIGH |
| `ModuleDecl` as a named scope for qualified access | HIGH |
| `OpenDecl` in `extractMainExpr` (merge module bindings into env) | HIGH |
| `FieldAccess(Constructor(modName), fieldName)` elaboration | HIGH |
| `FileImportDecl` | LOW (stretch) |
| Multi-segment `OpenDecl` (`open A.B.C`) | MEDIUM |
| `NamespaceDecl` (body-less — not produced by parser) | VERY LOW (not parsed) |

### 4.3 Gaps — File I/O Builtins

All 14 builtins (STD-02 through STD-15) are missing from the backend. They exist in the
interpreter but are not wired up in `Elaboration.fs` or `lang_runtime.c`.

Current backend has NO `@lang_read_file`, `@lang_write_file`, etc. declarations.

### 4.4 Module + Type Interaction

When a `ModuleDecl` contains `TypeDecl`/`RecordTypeDecl`/`ExceptionDecl`, these must be
processed in `prePassDecls` for the module's inner decls. Current `prePassDecls` only scans
the top-level `Decl list` and does NOT recurse into `ModuleDecl` inner decls.

This means if a module-contained ADT constructor is used qualified (`Module.Ctor value`),
the backend will fail to find it in `TypeEnv` at elaboration time.

**Fix required:** `prePassDecls` must recurse into `ModuleDecl.decls` to collect type info.

---

## Part 5: Architecture for Backend Implementation

### 5.1 Module Flattening Strategy

The recommended approach for the backend (matching the Eval.fs pattern):

1. **Pre-pass extended:** `prePassDecls` recurses into `ModuleDecl` inner decls, collecting
   all type/record/exception info from any nesting depth.

2. **Module env:** Add a `ModuleEnv : Map<string, Map<string, Value>>` to the elaboration
   context — maps module name to (field name → MLIR SSA value).

3. **ModuleDecl elaboration:** Elaborate all inner `LetDecl`/`LetRecDecl`/`LetMutDecl` inside
   the module, collecting the resulting SSA values into a sub-map. Store as `ModuleEnv[name]`.
   The SSA values are valid in the enclosing scope (MLIR values are globally visible within a
   function block).

4. **OpenDecl elaboration:** Copy all entries from `ModuleEnv[name]` into the current `Env`.

5. **FieldAccess(Constructor(modName), field) elaboration:** Look up `ModuleEnv[modName][field]`.

### 5.2 Alternative: Rewrite Pass Before Elaboration

Another approach: add a pre-elaboration rewrite pass (similar to TypeCheck's `rewriteModuleAccess`)
that transforms `ModuleDecl`/`OpenDecl` into flat sequences and rewrites qualified access to
direct variable references. The elaborator then sees only plain `Let`/`Var` nodes.

This is cleaner architecturally but requires a new AST transformation layer.

### 5.3 File I/O as C Runtime Calls

File I/O builtins follow the established pattern:
1. Add C functions to `lang_runtime.c` and declare in `lang_runtime.h`
2. Add `ExtFuncDecl` entries in `elaborateProgram`'s `externalFuncs` list
3. In `Elaboration.fs`, handle `Var("read_file", _)` etc. as known builtins that return
   partially-applied MLIR closures (curried application pattern)

The curried pattern for `write_file` (two string args → unit) follows the existing
array/hashtable multi-arg builtin patterns.

---

## Part 6: Exact AST Nodes Requiring Elaboration

### Complete List of New AST Nodes

**`Decl` variants (new for backend):**
1. `ModuleDecl of name: string * decls: Decl list * Span`
2. `OpenDecl of path: string list * Span`
3. `FileImportDecl of path: string * Span` (stretch)
4. `NamespaceDecl of path: string list * decls: Decl list * Span` (exists in AST but not parsed — skip)

**`Expr` variants (existing but unhandled in module context):**
- `FieldAccess(Constructor(modName, None, _), fieldName, span)` when `modName` is a module —
  this is the qualified access pattern; the backend already handles `FieldAccess` for records
  but must distinguish module vs. record access.

**`Module` top-level variants (already handled):**
- `NamedModule`, `NamespacedModule` — already handled in `elaborateProgram`

### Not New — Already Exists (No Backend Work Needed)

- `TypeAliasDecl` — already skipped by backend
- All `Expr` nodes used inside modules — same elaboration as top-level
- `LetRecDecl` inside module — same mutual recursion handling

---

## Part 7: Open Questions

1. **`FieldAccess` disambiguation:** The backend currently handles `FieldAccess` only for
   records. When `expr` is `Constructor(name, None)` and `name` is a known module, it must be
   treated as qualified module access, not a record field access. The disambiguation must occur
   at elaboration time using the module environment.

   - What we know: TypeCheck uses `Map.containsKey modName modules` check.
   - What's unclear: How does the backend know at elaboration time whether a `Constructor` name
     refers to a module vs. an ADT nullary constructor?
   - Recommendation: Maintain a `ModuleNames : Set<string>` in the elaboration environment.
     If `Constructor(name, None)` appears as the `expr` of `FieldAccess` AND `name` is in
     `ModuleNames`, treat as module access.

2. **Module-internal types crossing module boundary:** When code does `open Color` then uses
   `match shape with | Color.Red -> ...`, the constructor `Red` must be available in `TypeEnv`.
   The `prePassDecls` fix (recursing into `ModuleDecl`) handles this for type-tag lookup.

3. **`FileImportDecl` scope for v6.0:** Given 3 import tests vs 11 module tests, file import
   may be deferred. The 3 tests are: `file-import-basic`, `file-import-qualified`,
   `file-import-module-qualified`. All require the full parse/elaborate cycle on an external file.

4. **Module SSA lifetime:** MLIR SSA values defined inside a module's elaboration must be
   visible outside (in the enclosing scope). Since all elaboration happens within a single MLIR
   `@main` function, SSA values are block-scoped and accessible throughout. No special handling
   needed for value lifetime.

---

## Sources

### Primary (HIGH confidence — direct source reading)

- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — complete AST definition
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` — evaluator implementation
  lines 295-435 (file I/O builtins), 574-600 (module env types), 1147-1281 (evalModuleDecls)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/TypeCheck.fs` — type signatures
  lines 65-148 (all builtin type schemes), 150-241 (module exports), 617-634 (resolveImportPath)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Parser.fsy` — syntax rules
  lines 494-545 (module grammar), 724-728 (QualifiedIdent)
- `/Users/ohama/vibe-coding/LangThree/tests/LangThree.Tests/ModuleTests.fs` — test cases
- `/Users/ohama/vibe-coding/LangThree/tests/flt/file/module/` — 11 E2E module tests
- `/Users/ohama/vibe-coding/LangThree/tests/flt/file/fileio/` — 8 E2E file I/O tests
- `/Users/ohama/vibe-coding/LangThree/tests/flt/file/import/` — 3 E2E file import tests
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs`
  lines 2315-2430 (prePassDecls, extractMainExpr, elaborateProgram)

---

## Metadata

**Confidence breakdown:**
- Module AST nodes: HIGH — read directly from Ast.fs
- File I/O builtins: HIGH — read directly from Eval.fs + TypeCheck.fs
- Backend implementation approach: MEDIUM — inferred from Eval.fs pattern + existing backend code
- FileImportDecl complexity: HIGH — confirmed by reading TypeCheck.fs file import delegates

**Research date:** 2026-03-27
**Valid until:** Stable — these are implementation details of existing LangThree frontend code
