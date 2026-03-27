# Architecture Research: Module System + File I/O Integration

**Researched:** 2026-03-27
**Domain:** Compiler architecture — adding module system and file I/O to existing MLIR backend
**Confidence:** HIGH (all findings from direct source reading)
**Question:** How do modules and file I/O integrate with the existing compiler architecture?

---

## Summary

The existing compiler is a single-file compilation pipeline: `elaborateProgram` takes one `Ast.Module`,
runs `prePassDecls` to build `TypeEnv`/`RecordEnv`/`ExnTags`, then calls `extractMainExpr` to flatten
all `LetDecl`/`LetRecDecl`/`LetMutDecl` into a single nested `Expr` for elaboration. There are no
modules in the backend — the `ModuleDecl`, `OpenDecl`, `FileImportDecl`, and `NamespaceDecl` AST nodes
exist in `Ast.fs` but `extractMainExpr` silently discards them (the `| _ :: rest -> build rest` arm).

The interpreter (`LangThree/Eval.fs`) handles the full module system at runtime: `evalModuleDecls`
processes `ModuleDecl`, `OpenDecl`, `FileImportDecl`, and `LetRecDecl` correctly via a `Map<string,
ModuleValueEnv>` side channel. The compiler needs an analogous compile-time flattening strategy: treat
the module structure as a scoping mechanism and inline all declarations into a single elaboration pass.

File I/O is a pure C runtime addition — the same pattern used for `lang_string_concat`, `lang_range`,
and `lang_array_create`. Each file I/O builtin becomes: one C function in `lang_runtime.c`, one entry
in the `ExternalFuncs` list, and one pattern-match arm in `elaborateExpr` that emits
`LlvmCallOp`/`LlvmCallVoidOp`. All file data is `LangString*` (the existing `{i64 length, ptr data}`
struct). There are no new MLIR ops, no new `MlirType` variants, and no `MlirIR.fs` changes needed for
either feature.

**Primary recommendation:** Flatten modules compile-time in `prePassDecls`+`extractMainExpr` by
recursing into `ModuleDecl` and inlining `FileImportDecl` content; implement file I/O as pure C
runtime functions following the `lang_string_sub` pattern exactly.

---

## Architecture Patterns

### Pattern 1: How Builtins Are Added (Established Pattern)

Every builtin follows a strict four-part pattern. ALL four parts must be done together:

**Part A — C runtime function** (`lang_runtime.c` + `lang_runtime.h`)
```c
// Signature convention: LangString* for string args/returns, int64_t for int/bool/unit
LangString* lang_read_file(LangString* path) {
    // GC_malloc all allocations
    // Return LangString* for string results
    // lang_throw for error conditions (catchable by try-with)
}
```

**Part B — External function declaration** (in `elaborateModule`/`elaborateProgram` `externalFuncs` list)
```fsharp
{ ExtName = "@lang_read_file"; ExtParams = [Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

**Part C — Pattern match arm** (in `elaborateExpr`, BEFORE the general `App` case at line ~1112)
```fsharp
// Single-arg builtins: App(Var("read_file", _), pathExpr, _)
| App (Var ("read_file", _), pathExpr, _) ->
    let (pathVal, pathOps) = elaborateExpr env pathExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, pathOps @ [LlvmCallOp(result, "@lang_read_file", [pathVal])])

// Two-arg curried builtins: App(App(Var("write_file", _), pathExpr, _), contentExpr, _)
| App (App (Var ("write_file", _), pathExpr, _), contentExpr, _) ->
    let (pathVal, pathOps) = elaborateExpr env pathExpr
    let (contentVal, contentOps) = elaborateExpr env contentExpr
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, pathOps @ contentOps @ [LlvmCallVoidOp("@lang_write_file", [pathVal; contentVal]); ArithConstantOp(unitVal, 0L)])
```

**Part D — Unit return convention**: builtins returning unit produce `ArithConstantOp(unitVal, 0L)` as
the SSA result (matching the existing `print`/`println` pattern).

### Pattern 2: The Two-Arg Curried Builtin Pattern

For builtins like `write_file : string -> string -> unit`, the elaborator must match the fully-applied
two-argument form before the general `App` case. The established pattern (from `string_concat`,
`string_contains`, `array_get`) is nested `App(App(...))` matching.

Critically: single-argument partial application of these builtins (e.g., `let f = write_file "foo"`)
is NOT supported in the current compiler — it would fall through to the general `App` case and fail
with "not a known function or closure value". The interpreter handles partial application via
`BuiltinValue` chaining, but the compiler handles only fully-saturated calls. This is consistent with
the existing approach for all multi-arg builtins.

### Pattern 3: Module System — Compile-Time Flattening

The interpreter uses `Map<string, ModuleValueEnv>` as a side channel for module scoping. The compiler
does NOT need this — it operates on a flat SSA `ElabEnv`. The correct approach is to flatten module
structure at declaration-processing time:

**`prePassDecls` extension** — recurse into `ModuleDecl` inner decls:
```fsharp
| Ast.Decl.ModuleDecl(_, innerDecls, _) ->
    // Recurse: collect TypeEnv/RecordEnv/ExnTags from inner decls
    // (same as processing them at top level — module scoping is irrelevant for type/ctor declarations)
    let (innerTypeEnv, innerRecordEnv, innerExnTags) = prePassDecls innerDecls
    typeEnv <- Map.fold (fun acc k v -> Map.add k v acc) typeEnv innerTypeEnv
    recordEnv <- Map.fold (fun acc k v -> Map.add k v acc) recordEnv innerRecordEnv
    exnTags <- Map.fold (fun acc k v -> Map.add k v acc) exnTags innerExnTags
```

**`extractMainExpr` extension** — inline `ModuleDecl` bindings, skip `OpenDecl`:
```fsharp
// ModuleDecl: treat inner LetDecl/LetRecDecl as module-qualified bindings
// For now, flatten them into the outer scope (unqualified names only)
| Ast.Decl.ModuleDecl(_, innerDecls, _) :: rest ->
    // Filter inner decls to expression decls and build inner sub-expression
    let innerExpr = buildFromDecls innerDecls   // recursive helper
    Let("_", innerExpr, build rest, s)          // evaluate for side effects + bindings

// OpenDecl: at compile time, nothing to do — names are already flat
| Ast.Decl.OpenDecl _ :: rest -> build rest

// FileImportDecl: parse + flatten the imported file's declarations
| Ast.Decl.FileImportDecl(path, _) :: rest ->
    let importedDecls = loadAndParseFile path   // parse only, no eval
    build (importedDecls @ rest)                // flatten imported decls into continuation
```

**Key insight**: the compiler only needs to support the most common module usage pattern: top-level
`module Foo = ...` declarations that define named bindings, and `open Foo` that brings them into scope.
Qualified access (`Foo.bar`) is a name-resolution concern that can be handled by pre-flattening with
name mangling or by treating all names as already flat (relying on the type checker to have validated
qualified names before reaching the backend).

### Pattern 4: FileImportDecl — Parse Without Re-Evaluating

`FileImportDecl` in the AST represents `#import "path"` syntax. The interpreter resolves relative
paths and loads the file via `fileImportEvaluator` (a mutable delegate wired in `Prelude.fs`). The
compiler needs only to:
1. Resolve the path relative to the source file
2. Parse the imported file using the existing LangThree parser
3. Flatten the imported `Decl list` into the current processing pass

This is simpler than the interpreter's approach because there is no runtime module environment to
maintain — all declarations collapse into a single elaboration.

### Pattern 5: String Return from C Runtime

All file I/O builtins that return strings must return `LangString*` — a GC-allocated struct with
layout `{int64_t length, ptr data}`. The `lang_runtime.c` already shows the exact allocation pattern
in `lang_string_concat`, `lang_to_string_int`, etc. All runtime allocations must use `GC_malloc`.
Error conditions (file not found, etc.) must use `lang_throw` with a `LangString*` message — this
makes errors catchable by existing `try-with` in user code.

### Pattern 6: `stdin_read_line` / `stdin_read_all` — Unit Argument

In the interpreter, `stdin_read_line` and `stdin_read_all` take `TupleValue []` (unit). In the
compiled representation, unit is `0L` (i64 constant). The elaborator pattern for unit-argument
builtins is:

```fsharp
// App(Var("stdin_read_line", _), unitExpr, _) — unitExpr elaborates to I64 0
| App (Var ("stdin_read_line", _), unitExpr, _) ->
    let (_unitVal, unitOps) = elaborateExpr env unitExpr   // elaborates to ArithConstantOp 0
    let result = { Name = freshName env; Type = Ptr }
    (result, unitOps @ [LlvmCallOp(result, "@lang_stdin_read_line", [])])
    // NOTE: C function takes no args — unit argument is discarded
```

The C function signature takes no arguments; the unit value from elaboration is simply dropped.

### Anti-Patterns to Avoid

- **Adding `MlirType` variants**: `Ptr` already covers all heap-allocated C types (strings, arrays,
  hashtables, file handles). File I/O operates on `LangString*` (Ptr) and C `FILE*` (opaque Ptr).
  Do not add a `FileHandle` MlirType.

- **Adding new `MlirOp` cases**: All file I/O can be expressed as `LlvmCallOp`/`LlvmCallVoidOp` to
  C runtime functions. Do not add `ReadFileOp` or similar.

- **Attempting partial application of file I/O builtins**: The pattern-match approach only handles
  fully-saturated calls. Do not try to make `write_file "path"` return a closure — this would require
  a different elaboration strategy.

- **Implementing module namespacing in ElabEnv**: The elaborator's `Vars` map is flat SSA names. Do
  not add a `ModuleStack` or qualified-name lookup table. Flatten module bindings to their local names
  at extract time.

- **Forgetting `prePassDecls` for nested modules**: `TypeDecl`, `RecordTypeDecl`, and `ExceptionDecl`
  inside `ModuleDecl` must be scanned by `prePassDecls` or constructors won't be in `TypeEnv`.

---

## File I/O Builtins: Complete Mapping

From `Eval.fs` `initialBuiltinEnv` (lines 295–435), the file I/O builtins are:

| Builtin Name | Type | C Signature | Return Type |
|---|---|---|---|
| `read_file` | `string -> string` | `LangString* lang_read_file(LangString* path)` | `Ptr` |
| `stdin_read_all` | `unit -> string` | `LangString* lang_stdin_read_all(void)` | `Ptr` |
| `stdin_read_line` | `unit -> string` | `LangString* lang_stdin_read_line(void)` | `Ptr` |
| `write_file` | `string -> string -> unit` | `void lang_write_file(LangString* path, LangString* content)` | `void` → I64 0 |
| `append_file` | `string -> string -> unit` | `void lang_append_file(LangString* path, LangString* content)` | `void` → I64 0 |
| `file_exists` | `string -> bool` | `int64_t lang_file_exists(LangString* path)` | `I64` (0 or 1) |
| `read_lines` | `string -> string list` | `LangCons* lang_read_lines(LangString* path)` | `Ptr` (cons list) |
| `write_lines` | `string -> string list -> unit` | `void lang_write_lines(LangString* path, LangCons* lines)` | `void` → I64 0 |
| `get_args` | `unit -> string list` | `LangCons* lang_get_args(void)` | `Ptr` |
| `get_env` | `string -> string` | `LangString* lang_get_env(LangString* name)` | `Ptr` |
| `get_cwd` | `unit -> string` | `LangString* lang_get_cwd(void)` | `Ptr` |
| `path_combine` | `string -> string -> string` | `LangString* lang_path_combine(LangString* dir, LangString* file)` | `Ptr` |
| `dir_files` | `string -> string list` | `LangCons* lang_dir_files(LangString* path)` | `Ptr` |
| `eprint` | `string -> unit` | `void lang_eprint(LangString* s)` | `void` → I64 0 |
| `eprintln` | `string -> unit` | `void lang_eprintln(LangString* s)` | `void` → I64 0 |

**Note on `read_lines`/`write_lines`/`dir_files`/`get_args`**: These return or accept cons lists.
The cons cell layout is already established in `lang_runtime.c` (Phase 10):
`{int64_t head @ offset 0, LangCons* tail @ offset 8}`. For string lists, `head` must be cast to a
`LangString*`. This is a new cons-cell layout concern — existing cons cells store `int64_t` heads, but
string lists need `ptr` heads. The cleanest approach: cast `LangString*` to `int64_t` via pointer
casting, identical to how the hashtable stores values. The existing `LangCons` layout already works
this way since `int64_t` and `ptr` are the same size on 64-bit.

---

## Module System: What the Compiler Needs to Handle

From `Ast.fs` (`Decl` type, lines 311–354), the module-level declarations are:

| AST Node | Interpreter Behavior | Compiler Strategy |
|---|---|---|
| `ModuleDecl(name, innerDecls, _)` | Creates `ModuleValueEnv` in `modEnv` map | Flatten `innerDecls` into current scope |
| `OpenDecl(path, _)` | Merges module values into current `env` | No-op (names already flat) |
| `NamespaceDecl(path, innerDecls, _)` | Not in `evalModuleDecls` arm list | Treat as `ModuleDecl` (same inner decls) |
| `FileImportDecl(path, _)` | Parses + evaluates file via `fileImportEvaluator` | Parse file, flatten its `Decl list` |
| `TypeAliasDecl` | No-op at runtime | No-op (type-level only) |
| `LetPatDecl(pat, body, _)` | `matchPattern` at module level | Add to `extractMainExpr` filter list |

**`elaborateProgram`** extracts decls from `Ast.Module` variants:
```fsharp
let decls =
    match ast with
    | Ast.Module(decls, _) | Ast.NamedModule(_, decls, _) | Ast.NamespacedModule(_, decls, _) -> decls
    | Ast.EmptyModule _ -> []
```
This is already correct — the top-level module wrapper is handled. The missing pieces are:
1. `prePassDecls` doesn't recurse into `ModuleDecl` inner decls
2. `extractMainExpr` silently drops `ModuleDecl`/`OpenDecl`/`FileImportDecl`

---

## Integration Points: Where Changes Land

### `Elaboration.fs` changes (all changes)

1. **`prePassDecls`** (line 2317): Add arm for `Ast.Decl.ModuleDecl` that recurses into inner decls.
   Add arm for `Ast.Decl.NamespaceDecl` that does the same.

2. **`extractMainExpr`** (line 2355):
   - Add `Ast.Decl.ModuleDecl` to the filter list — recurse into inner decls
   - Add `Ast.Decl.OpenDecl` to the filter list as a no-op
   - Add `Ast.Decl.FileImportDecl` handling — parse imported file, inline decls
   - Add `Ast.Decl.LetPatDecl` handling — it currently exists in the filter but needs build arms

3. **Builtin pattern arms** (before line 1112, the general `App` case): Add one arm per file I/O
   builtin. Order matters: two-arg builtins (`write_file`, `append_file`, `path_combine`) must come
   before one-arg builtins (`read_file`, `file_exists`, etc.).

4. **`externalFuncs` list** (line 2279): Add one `ExternalFuncDecl` per new C runtime function.

### `lang_runtime.c` and `lang_runtime.h` changes

Add all file I/O C functions. All string arguments/results use the existing `LangString*` typedef
(already defined within `lang_runtime.c` as a local `typedef struct`). For `lang_runtime.h`, add
forward declarations for all new functions.

**Important**: `LangString` is currently defined as a local `typedef` inside `lang_runtime.c` (not
in the header). The header does not expose it. When writing C runtime functions, use the local
`LangString` typedef. The MLIR side sees it as an opaque `!llvm.ptr`.

### No changes needed

- `MlirIR.fs` — no new types or ops
- `Printer.fs` — no new printing logic
- `Pipeline.fs` — no new build steps
- `LangThree/Ast.fs` — AST already has all nodes
- `LangThree/Eval.fs` — interpreter already works

---

## Common Pitfalls

### Pitfall 1: `prePassDecls` Missing ModuleDecl Recursion

**What goes wrong:** Type constructors and record types declared inside `module Foo = ...` are
invisible to the elaborator. Pattern matching on them fails at runtime with "unknown constructor".
**Why it happens:** `prePassDecls` has `| _ -> ()` as a catch-all — `ModuleDecl` silently falls
through.
**How to avoid:** Add `| Ast.Decl.ModuleDecl(_, innerDecls, _) ->` arm that calls `prePassDecls`
recursively and merges the results.
**Warning signs:** ADT patterns that work in non-module programs fail when the type is declared inside
a module.

### Pitfall 2: `extractMainExpr` Dropping Module Bindings

**What goes wrong:** A `let` declaration inside `module Foo = let bar = 42` is silently dropped.
Referencing `bar` fails.
**Why it happens:** `extractMainExpr` filters to `LetDecl | LetRecDecl | LetMutDecl` and discards
`ModuleDecl`.
**How to avoid:** Add `ModuleDecl` handling that recurses into inner decls and builds the inner
expression as a nested continuation.
**Warning signs:** Programs using module-level `let` bindings inside `module` blocks produce
"unknown variable" errors.

### Pitfall 3: Ordering of Builtin Pattern Arms

**What goes wrong:** `App(App(Var("write_file"), path), content)` is matched by the general `App`
arm as a closure call and fails.
**Why it happens:** The general `App` case is at line ~1112. Two-arg builtin patterns must be placed
BEFORE this line, and in the correct order (two-arg before one-arg for the same builtin name).
**How to avoid:** Add all file I/O builtin arms in the region between `int_to_char` (line ~1050) and
the general `App` case (line ~1112).
**Warning signs:** `"Elaboration: unsupported App — 'write_file' is not a known function or closure value"` error.

### Pitfall 4: String List Cons Cell Head Type

**What goes wrong:** `read_lines` returns a cons list of strings. The existing cons cell layout uses
`int64_t head` but strings are `LangString*` pointers. Treating the head directly as I64 causes a
type confusion.
**Why it happens:** The original cons cell was designed for integer lists. Pointer values happen to
fit in `int64_t` on 64-bit platforms, but the MLIR elaboration must load the head as `Ptr` not `I64`.
**How to avoid:** When elaborating a string list from a C runtime function, load cons cell heads with
`LlvmLoadOp` producing a `Ptr`-typed result, or use `LlvmIntToPtrOp` after an I64 load.
**Warning signs:** Pattern matching on the result of `read_lines` produces garbled string values.

### Pitfall 5: FileImportDecl Path Resolution

**What goes wrong:** A relative path in `#import "utils.lt"` fails to resolve because the compiler
doesn't know the source file's directory.
**Why it happens:** `elaborateProgram` receives an `Ast.Module` with no filename information. The
span's `FileName` field exists but may be `<unknown>` if the lexer didn't track it.
**How to avoid:** Pass the source file path down from the CLI entry point to `elaborateProgram`, or
use `TypeCheck.resolveImportPath` (which already exists in `LangThree`) after ensuring the span
filename is populated correctly.
**Warning signs:** `FileNotFoundException` or "file not found" for valid relative paths.

### Pitfall 6: `lang_throw` for File Errors

**What goes wrong:** A C runtime file I/O error calls `exit(1)` directly, bypassing user `try-with`
handlers.
**Why it happens:** Copying the `lang_failwith` exit pattern instead of the `lang_throw` pattern.
**How to avoid:** Use `lang_throw((void*)msg)` where `msg` is a GC-allocated `LangString*` for all
catchable errors (file not found, permission denied, etc.). Only use `exit()` for truly unrecoverable
errors like OOM.
**Warning signs:** `try-with` doesn't catch file I/O errors in tests.

---

## Code Examples

### Existing Single-Arg Builtin (reference: `read_file` follows this)

```fsharp
// Source: Elaboration.fs line ~771 (string_length pattern)
| App (Var ("string_length", _), strExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let lenPtrVal = { Name = freshName env; Type = Ptr }
    let lenVal    = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPStructOp(lenPtrVal, strVal, 0)   // &header.length
        LlvmLoadOp(lenVal, lenPtrVal)             // load i64 length
    ]
    (lenVal, strOps @ ops)
```

### Existing Two-Arg Builtin (reference: `write_file` follows this)

```fsharp
// Source: Elaboration.fs line ~750 (string_concat pattern)
| App (App (Var ("string_concat", _), aExpr, _), bExpr, _) ->
    let (aVal, aOps) = elaborateExpr env aExpr
    let (bVal, bOps) = elaborateExpr env bExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, aOps @ bOps @ [LlvmCallOp(result, "@lang_string_concat", [aVal; bVal])])
```

### Unit-Returning Builtin Pattern

```fsharp
// Source: Elaboration.fs line ~1054 (print pattern, literal fast path)
| App (Var ("print", _), strExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let dataPtrVal  = { Name = freshName env; Type = Ptr }
    let dataAddrVal = { Name = freshName env; Type = Ptr }
    let fmtRes  = { Name = freshName env; Type = I32 }
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPStructOp(dataPtrVal, strVal, 1)
        LlvmLoadOp(dataAddrVal, dataPtrVal)
        LlvmCallOp(fmtRes, "@printf", [dataAddrVal])
        ArithConstantOp(unitVal, 0L)
    ]
    (unitVal, strOps @ ops)
// Pattern for lang_runtime void functions: use LlvmCallVoidOp + ArithConstantOp(unitVal, 0L)
```

### ExternalFuncDecl Pattern (existing array functions for reference)

```fsharp
// Source: Elaboration.fs line ~2298 (externalFuncs list)
{ ExtName = "@lang_array_create"; ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_bounds_check"; ExtParams = [Ptr; I64]; ExtReturn = None; IsVarArg = false; Attrs = [] }
// void return -> ExtReturn = None, elaboration uses LlvmCallVoidOp
// Ptr return  -> ExtReturn = Some Ptr, elaboration uses LlvmCallOp
```

### C Runtime String Function Pattern (from existing `lang_string_concat`)

```c
// Source: lang_runtime.c line 14
LangString* lang_string_concat(LangString* a, LangString* b) {
    int64_t total = a->length + b->length;
    char* buf = (char*)GC_malloc(total + 1);
    memcpy(buf, a->data, (size_t)a->length);
    memcpy(buf + a->length, b->data, (size_t)b->length);
    buf[total] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = total;
    s->data = buf;
    return s;
}
// Key invariants:
// - All allocations via GC_malloc (no malloc/calloc)
// - Null-terminate data buffers for C string compat
// - Return newly allocated LangString*, not pointer into input
```

### Module Flattening in extractMainExpr

```fsharp
// Source: Elaboration.fs line 2355 — extend build function
let rec build (ds: Ast.Decl list) : Expr =
    match ds with
    | [] -> Number(0, s)
    // ... existing arms ...
    | Ast.Decl.ModuleDecl(_, innerDecls, _) :: rest ->
        // Flatten module: treat inner decls as if they were at outer scope
        // This means module-scoped names become flat names (no qualification needed at backend)
        // The type checker has already validated qualified access before we get here
        let exprInnerDecls =
            innerDecls |> List.filter (fun d ->
                match d with
                | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _ | Ast.Decl.LetMutDecl _ -> true
                | Ast.Decl.ModuleDecl _ -> true   // recurse
                | _ -> false)
        build (exprInnerDecls @ rest)
    | Ast.Decl.OpenDecl _ :: rest -> build rest   // no-op: names already flat
    | Ast.Decl.NamespaceDecl(_, innerDecls, _) :: rest ->
        build (innerDecls @ rest)   // same as ModuleDecl
```

---

## Open Questions

1. **Qualified name access at backend level**
   - What we know: the type checker validates `Foo.bar` qualified names before compilation
   - What's unclear: does the AST use `Var("Foo.bar", _)` or `Var("bar", _)` for qualified refs?
   - Recommendation: Check how LangThree's type checker resolves qualified names in the elaborated AST
     before implementing module flattening. If it resolves to `Var("bar")` (unqualified), flattening
     is trivially correct. If it resolves to `Var("Foo.bar")`, need name mangling.

2. **`FileImportDecl` access to parser from Elaboration.fs**
   - What we know: `LangThree` is a project reference; the parser is accessible
   - What's unclear: whether `Elaboration.fs` should parse imported files directly or whether
     `elaborateProgram`'s call site (likely `Program.fs` or CLI) should pre-flatten imports
   - Recommendation: Pre-flatten `FileImportDecl` at the CLI level (before calling
     `elaborateProgram`), passing a fully-inlined `Ast.Module` to the elaborator. This keeps
     `Elaboration.fs` free of parser dependencies.

3. **String list cons cell head type**
   - What we know: existing cons cells store `int64_t head`; string lists need `LangString*`
   - What's unclear: whether to add a new C type alias or reuse `int64_t` with casts
   - Recommendation: Reuse `int64_t` in the C ABI (cast `LangString*` to `int64_t` in C runtime
     functions, cast back when accessed). On the MLIR side, load heads as `I64` then use
     `LlvmIntToPtrOp` to get a `Ptr`. This avoids any new cons cell structure.

---

## Sources

### Primary (HIGH confidence — direct source reading)

- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — ElabEnv,
  prePassDecls, extractMainExpr, elaborateProgram, all builtin patterns, externalFuncs list
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.c` — existing C runtime
  functions, LangString/LangCons layout, GC_malloc pattern, lang_throw convention
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.h` — struct definitions,
  function declarations
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — all Decl variants, Module variants,
  ModuleDecl/OpenDecl/FileImportDecl/NamespaceDecl
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` — evalModuleDecls (all module handling),
  initialBuiltinEnv (all file I/O builtins with types), ModuleValueEnv struct
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MlirIR.fs` — all MlirOp/MlirType
  variants, ExternalFuncDecl struct
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Pipeline.fs` — compilation pipeline,
  runtime.c link step

### Secondary (MEDIUM confidence — planning documents)

- `.planning/PROJECT.md` — confirmed v6.0 goal, constraints, key decisions
- `.planning/MILESTONES.md` — confirmed what's shipped, what's out of scope

---

## Metadata

**Confidence breakdown:**
- File I/O integration pattern: HIGH — exact same pattern as 15+ existing builtins, fully documented
- Module flattening strategy: HIGH — prePassDecls/extractMainExpr structure is clear from source
- Qualified name resolution: MEDIUM — depends on how LangThree type checker represents resolved names
  in the AST (needs verification before implementing FileImportDecl)
- String list cons cell head: MEDIUM — technically workable with cast approach, but needs careful MLIR
  load-type handling

**Research date:** 2026-03-27
**Valid until:** Stable (no external dependencies — all findings from project source)
