# Stack Research: Module System + File I/O Builtins

**Researched:** 2026-03-27
**Domain:** Module system (AST flattening strategy) + File I/O C runtime additions
**Confidence:** HIGH
**Milestone:** v6.0 Modules & I/O

---

## Summary

This is a SUBSEQUENT MILESTONE research. The existing stack (F# .NET 10, MLIR text format,
LLVM 20, Boehm GC, lang_runtime.c) needs no new external dependencies for either module
system support or File I/O builtins. Both features are implemented entirely within the
existing compilation pipeline.

**Module system** in LangThree is a purely compile-time name scoping mechanism. By the time
the AST reaches the backend, module boundaries are syntactic sugar around let bindings. The
approach is to flatten modules into the same declaration list — `module M = let x = 42` and
`open M` become `let x = 42` at elaboration time. The LangThree TypeCheck already does
qualified-name rewriting (`rewriteModuleAccess`) before binding resolution, so the backend
only sees desugared `Var` and `Let` nodes.

**File I/O** builtins are straightforward additions to `lang_runtime.c` using POSIX C stdlib
functions (`fopen`, `fread`, `fwrite`, `fclose`, `popen`, `getcwd`, `getenv`, `opendir`,
`readdir`). Fourteen new builtins are defined in `TypeCheck.fs` (STD-02 through STD-15).
All return LangString or lists of LangString, and all use the existing GC_malloc heap
allocation pattern. The MLIR side only needs new `ExternalFuncDecl` entries and builtin
dispatch cases in Elaboration.fs.

**Primary recommendation:** Implement module support as AST-level flattening in Elaboration.fs
(no new MLIR ops, no new pipeline stages), and implement File I/O as C runtime functions
following the exact same pattern as existing string/array builtins.

---

## Standard Stack

No new external dependencies. All additions are in-project source changes.

### Existing Stack (confirmed unchanged)

| Component | Version | Purpose |
|-----------|---------|---------|
| F# / .NET | 10 | Compiler implementation language |
| MLIR text format | LLVM 20 | IR serialization |
| mlir-opt | LLVM 20 | Lowering passes (arith→cf→func→llvm) |
| mlir-translate | LLVM 20 | MLIR → LLVM IR |
| clang | LLVM 20 | Compile lang_runtime.c + link final binary |
| Boehm GC (libgc) | system | Heap allocation |
| lang_runtime.c | in-project | C builtins (string, list, array, hashtable, exceptions) |

### C stdlib functions needed for File I/O (already available via system libc)

| Function | Header | Purpose |
|----------|--------|---------|
| `fopen` / `fclose` | `<stdio.h>` | File open/close |
| `fread` / `fwrite` / `fputs` | `<stdio.h>` | File read/write |
| `fseek` / `ftell` | `<stdio.h>` | File size detection |
| `getcwd` | `<unistd.h>` | Current working directory |
| `getenv` | `<stdlib.h>` | Environment variable access |
| `opendir` / `readdir` / `closedir` | `<dirent.h>` | Directory listing |
| `access` (F_OK) | `<unistd.h>` | File existence check |

All these headers are already included or trivially added to `lang_runtime.c`. No new
linker flags are needed — these are all in the default C runtime linked by clang.

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| C stdlib fopen/fread | POSIX mmap | No benefit at this scale; fopen simpler |
| AST flatten in Elaboration.fs | Separate module resolver pass | Separate pass adds complexity, no benefit since TypeCheck already rewrites qualified names |
| Adding `<unistd.h>` to lang_runtime.c | Separate runtime file | Single C file is simpler; `lang_runtime.c` already uses `<stdio.h>`, `<stdlib.h>` |

---

## Architecture Patterns

### Module System: AST Flattening in Elaboration.fs

**What:** The LangThree TypeCheck already desugars qualified module access (`M.x` →
`x`) via `rewriteModuleAccess` before returning the elaborated AST. The backend
receives plain `Var`/`Let` nodes. The backend's job is to flatten `ModuleDecl` and
`OpenDecl` declarations in `extractMainExpr` and `prePassDecls`.

**How it works in practice:**

```fsharp
// AST after TypeCheck rewriting (what backend sees):
// module M = let x = 42
// open M
// let result = M.x   -->  let result = x
//
// Backend flattens ModuleDecl to its inner LetDecls:
| Ast.Decl.ModuleDecl(_, innerDecls, _) ->
    // Recursively flatten: treat inner decls as if they were at top level
    // prePassDecls recurses into innerDecls
    // extractMainExpr recurses into innerDecls
```

**Key insight:** The `rewriteModuleAccess` in TypeCheck converts all `FieldAccess(Constructor(modName), fieldName)` patterns to `Var(fieldName)` BEFORE the backend sees the AST. The backend never encounters qualified names — it only needs to flatten the declaration nesting.

**`NamespaceDecl`:** Treat exactly like `ModuleDecl` — namespace is just a naming prefix,
its inner declarations go to top level.

**`OpenDecl`:** No-op in the backend. TypeCheck already merged the open'd module's bindings
into the type environment. The backend never emits code for `open` directives.

**`FileImportDecl`:** For v6.0, treat as no-op or skip. The LangThree TypeCheck resolves
file imports and inlines the imported declarations into the AST before returning. The
backend receives the already-resolved flat declaration list. (Confirmed: TypeCheck's
`fileImportTypeChecker` mutable delegate is set by Prelude.fs/Program.fs, NOT by the
backend compiler.)

### File I/O: C Runtime Pattern (identical to existing builtins)

**What:** Add new C functions to `lang_runtime.c` and corresponding `ExternalFuncDecl`
entries + elaboration dispatch cases in `Elaboration.fs`.

**Established pattern** (from existing builtins):

1. Add C function to `lang_runtime.c` using `GC_malloc` for all heap allocations
2. Add function signature to `lang_runtime.h`
3. Add `ExternalFuncDecl` entry in `elaborateProgram` external function list
4. Add dispatch case in `elaborateBuiltin` (or equivalent) in `Elaboration.fs`
5. Add test in `tests/compiler/`

**String return pattern** (all file I/O returning strings follows this):

```c
// Source: lang_runtime.c existing pattern (lang_to_string_int, lang_string_sub, etc.)
LangString* lang_read_file(LangString* path) {
    FILE* f = fopen(path->data, "rb");
    if (!f) {
        // Throw catchable exception (like lang_hashtable_get pattern)
        const char* msg = "read_file: cannot open file";
        // ... build LangString* and lang_throw(msg)
    }
    fseek(f, 0, SEEK_END);
    int64_t len = (int64_t)ftell(f);
    rewind(f);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    fread(buf, 1, (size_t)len, f);
    buf[len] = '\0';
    fclose(f);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}
```

**Unit return pattern** (write_file, append_file, write_lines):

```c
// Unit = void in C; MLIR side emits LlvmCallVoidOp (returns None at MLIR level)
// The result is represented as i64 0 at the Lang level (same as print/println)
void lang_write_file(LangString* path, LangString* content) {
    FILE* f = fopen(path->data, "wb");
    if (!f) { /* lang_throw */ }
    fwrite(content->data, 1, (size_t)content->length, f);
    fclose(f);
}
```

**Bool return pattern** (file_exists):

```c
int64_t lang_file_exists(LangString* path) {
    return access(path->data, F_OK) == 0 ? 1 : 0;
}
```

**List return pattern** (read_lines, dir_files):

```c
// Build cons list using LangCons* (same as lang_range, lang_hashtable_keys)
LangCons* lang_read_lines(LangString* path) {
    // Read file, split on '\n', build cons list backwards
    // Each line is a GC_malloc'd LangString*; stored as i64 (ptrtoint)
}
```

### Recommended Changes to Each File

```
src/LangBackend.Compiler/
├── lang_runtime.h       # Add: 14 new function declarations
├── lang_runtime.c       # Add: 14 new C function implementations
│                        #   + <unistd.h>, <dirent.h> includes
├── Elaboration.fs       # Modify:
│                        #   prePassDecls: recurse into ModuleDecl/NamespaceDecl
│                        #   extractMainExpr: flatten ModuleDecl/NamespaceDecl inner decls
│                        #   elaborateBuiltin: add 14 file I/O dispatch cases
│                        #   elaborateProgram: add 14 ExternalFuncDecl entries
└── Pipeline.fs          # No changes needed (single-file compilation remains)
```

### Anti-Patterns to Avoid

- **Separate module compilation:** Do NOT attempt multi-file / separate compilation for v6.0.
  The current pipeline compiles one file to one binary. FileImportDecl is handled by LangThree's
  TypeCheck before the backend sees the AST. The backend stays single-file-in, single-binary-out.
- **Qualified name handling in Elaboration.fs:** Do NOT try to parse `M.x` qualified names in
  the backend. TypeCheck's `rewriteModuleAccess` already converts them to plain `Var` nodes before
  the backend runs.
- **Separate C file for I/O:** Do NOT create a second C runtime file. Add all I/O functions to
  the existing `lang_runtime.c`. The compiler links exactly one runtime object file.
- **Unit-as-tuple allocation:** When a builtin returns unit (write_file etc.), do NOT allocate
  a heap tuple. Follow the existing print/println pattern: emit `LlvmCallVoidOp` and then
  produce an `ArithConstantOp` of zero as the "unit" value.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| File read-entire | Custom buffered reader | `fseek`/`ftell`/`fread` in C | Already works, GC handles buffer |
| Qualified name resolution | Name mangling in MLIR | TypeCheck's `rewriteModuleAccess` | Already done before backend |
| Cons list from string split | Custom linked list | `LangCons*` pattern from lang_runtime.c | Consistent with existing list ABI |
| Directory listing | Custom syscall | `opendir`/`readdir`/`closedir` | POSIX, available on macOS + Linux |
| File existence check | `stat()` call | `access(path, F_OK)` | Simpler, correct for all platforms |
| Error propagation for I/O | Return code checking | `lang_throw(LangString*)` | Makes errors catchable in try/with |

**Key insight:** Every "new" problem in v6.0 has an exact structural analog in the existing
codebase. Module flattening mirrors how `NamespaceDecl` is already handled (inner decls processed
in current scope). I/O builtins mirror how `lang_hashtable_keys` returns a list and
`lang_failwith` throws a string error.

---

## Common Pitfalls

### Pitfall 1: Assuming Backend Handles Qualified Names

**What goes wrong:** Developer adds qualified-name dispatch to Elaboration.fs,
e.g. trying to match `FieldAccess(Constructor("M", ...), "x", ...)` as a variable lookup.
**Why it happens:** The module tests use `M.x` syntax, so it seems like the backend must handle it.
**How to avoid:** Check that LangThree TypeCheck's `rewriteModuleAccess` has already run before
the backend sees the AST. In the backend CLI (`Program.fs`), parsing is done but TypeCheck is
NOT called — the backend compiles directly from the raw parsed AST. This means qualified names
ARE present in the AST the backend receives.
**Correction:** The backend must either (a) call TypeCheck rewriting, or (b) implement its own
module-access flattening pass. Option (b) is simpler since TypeCheck requires prelude setup.
**Warning signs:** `match` arm for `FieldAccess` patterns in Elaboration.fs without corresponding
module-lookup logic.

### Pitfall 2: FileImportDecl Requiring Multi-File Compilation

**What goes wrong:** Treating `open "lib.fun"` as a directive to read and compile another file
at backend elaboration time.
**Why it happens:** The LangThree interpreter's `Program.fs` sets up `fileImportTypeChecker`
to recursively load files. The backend has no equivalent.
**How to avoid:** For v6.0, `FileImportDecl` should be skipped at the backend level. The
LangThree tests for file import (`file-import-basic.flt`) are interpreter tests, not compiler
tests. The backend test suite (`tests/compiler/`) has no file import tests.
**Warning signs:** Any code in Elaboration.fs that tries to call `System.IO.File.ReadAllText`
on a module import path.

### Pitfall 3: Module Inner Types Not Reaching prePassDecls

**What goes wrong:** A `ModuleDecl` contains `TypeDecl` or `RecordTypeDecl` inner declarations.
If `prePassDecls` doesn't recurse into `ModuleDecl`, the constructor tags won't be registered
and pattern matching on module-scoped ADTs will fail.
**Why it happens:** The current `prePassDecls` only does a flat scan of the top-level decl list.
**How to avoid:** Make `prePassDecls` recurse into `ModuleDecl`/`NamespaceDecl` inner decls
(the test `module-adt.flt` exercises this: module containing a type T with constructors).
**Warning signs:** `match` failure at runtime on constructors defined inside modules.

### Pitfall 4: Module Let Bindings Not Appearing in extractMainExpr

**What goes wrong:** `let result = M.add 3 4` works but the `let add x y = x + y` inside the
module is never emitted as MLIR, so there's no function to call.
**Why it happens:** `extractMainExpr` filters to only `LetDecl`/`LetRecDecl`/`LetMutDecl` at
the top level. `ModuleDecl` is not matched, so inner lets are dropped.
**How to avoid:** In `extractMainExpr`, when encountering `ModuleDecl(_, innerDecls, _)`, prepend
the inner decls to the continuation. The inner `let` bindings need to become part of the main
expression chain before the outer let bindings that reference them.
**Warning signs:** "variable not found" errors for names defined inside a module.

### Pitfall 5: write_file Unit Return Represented as Tuple

**What goes wrong:** `let _ = write_file "/tmp/x" "y"` causes a crash because the "unit"
value is allocated as a tuple struct pointer, but the continuation treats it as i64.
**Why it happens:** Overly literal mapping of the type system's `unit = TTuple []`.
**How to avoid:** Follow the exact pattern of `print`/`println`: emit `LlvmCallVoidOp`, then
produce `ArithConstantOp(result, 0L)` as the i64 unit sentinel. This is the established convention.
**Warning signs:** GEP instructions on the result of a void builtin call.

### Pitfall 6: LangString Path Null Termination Assumption

**What goes wrong:** `fopen(path->data, "rb")` returns NULL because `path->data` is not
null-terminated.
**Why it happens:** LangString stores `{int64_t length, char* data}`. The compile-time string
literals allocated by `elaborateStringLiteral` DO null-terminate (the global `\00` suffix and
the GC_malloc+1 pattern). But concatenated strings from `lang_string_concat` also null-terminate
(see `buf[total] = '\0'`). So this is safe in practice.
**How to avoid:** Confirm that all LangString* passed to file functions come from string literals
or `lang_string_concat`, both of which guarantee null termination. Document this assumption in
comments for new I/O functions.
**Warning signs:** None in practice, but add a defensive `path->data[path->length] = '\0'` in
C functions that pass the pointer to OS calls if uncertain.

---

## Code Examples

### Pattern: Flatten ModuleDecl in prePassDecls

```fsharp
// Elaboration.fs — prePassDecls modification
let private prePassDecls (decls: Ast.Decl list)
    : Map<string, TypeInfo> * Map<string, Map<string, int>> * Map<string, int> =
    // ... existing mutable setup ...
    let rec processDecls (ds: Ast.Decl list) =
        for decl in ds do
            match decl with
            | Ast.Decl.TypeDecl _ -> (* existing ADT handling *)
            | Ast.Decl.RecordTypeDecl _ -> (* existing record handling *)
            | Ast.Decl.ExceptionDecl _ -> (* existing exception handling *)
            | Ast.Decl.ModuleDecl(_, innerDecls, _) ->
                processDecls innerDecls  // Recurse into module
            | Ast.Decl.NamespaceDecl(_, innerDecls, _) ->
                processDecls innerDecls  // Namespace is transparent
            | _ -> ()
    processDecls decls
    (typeEnv, recordEnv, exnTags)
```

### Pattern: Flatten ModuleDecl in extractMainExpr

```fsharp
// Elaboration.fs — extractMainExpr modification
// Current filter:
// | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _ | Ast.Decl.LetMutDecl _ -> true

// Add a pre-step: flatten ModuleDecl/NamespaceDecl to their inner decls recursively
let rec flattenDecls (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(_, innerDecls, _) -> flattenDecls innerDecls
        | Ast.Decl.NamespaceDecl(_, innerDecls, _) -> flattenDecls innerDecls
        | other -> [other])
// Then pass flattenDecls result through the existing filter + build pipeline
```

### Pattern: Qualified Name Handling (Module.member in AST)

The backend CLI does NOT call TypeCheck, so the raw parsed AST still has `FieldAccess`
nodes for qualified names. The elaboration must handle them:

```fsharp
// In elaborateExpr, add a case BEFORE the default FieldAccess handler:
// Module.member where the "module" appears as Constructor(modName, None, _)
// Since TypeCheck isn't run, we need to desugar this ourselves.
// The simplest approach: treat FieldAccess(Constructor(name, None, _), field, _)
// as Var(field, _) — the inner decl was already flattened to top scope.
| FieldAccess(Constructor(_, None, _), fieldName, span) ->
    // Qualified module access — field name was flattened into scope
    elaborateExpr env (Var(fieldName, span))
```

Note: This works because module flattening puts all inner bindings at the same scope level.
`M.add` where `add` was defined inside `module M = let add x y = ...` becomes just `add`.

### Pattern: read_file builtin in lang_runtime.c

```c
// Source: adapted from lang_string_sub pattern + lang_hashtable_get error pattern
LangString* lang_read_file(LangString* path) {
    FILE* f = fopen(path->data, "rb");
    if (!f) {
        const char* msg_str = "read_file: cannot open file";
        int64_t msg_len = (int64_t)strlen(msg_str);
        char* buf = (char*)GC_malloc((size_t)(msg_len + 1));
        memcpy(buf, msg_str, (size_t)(msg_len + 1));
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = msg_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    fseek(f, 0, SEEK_END);
    int64_t len = (int64_t)ftell(f);
    rewind(f);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    fread(buf, 1, (size_t)len, f);
    buf[len] = '\0';
    fclose(f);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}
```

### Pattern: write_file builtin (void return → unit at MLIR level)

```c
void lang_write_file(LangString* path, LangString* content) {
    FILE* f = fopen(path->data, "wb");
    if (!f) { /* lang_throw pattern */ }
    fwrite(content->data, 1, (size_t)content->length, f);
    fclose(f);
}
```

```fsharp
// Elaboration.fs dispatch — matches print/println pattern
| "write_file" ->
    // Two curried args: path then content
    // Emit as two-argument C call (needs curried closure wrapping or direct call pattern)
    // Use the established two-arg builtin pattern (same as string_sub: 3-arg)
```

### Pattern: read_lines returning cons list

```c
// Source: follows lang_hashtable_keys cons list construction pattern
LangCons* lang_read_lines(LangString* path) {
    FILE* f = fopen(path->data, "rb");
    if (!f) { /* lang_throw */ return NULL; }
    // Read content, split on '\n', build list forward
    // Each element is LangString* cast to i64 via ptrtoint convention
    // ...
}
```

---

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| No module support in backend | Flatten ModuleDecl in Elaboration.fs | No new MLIR ops needed |
| No file I/O | C stdlib wrappers in lang_runtime.c | Follows existing builtin pattern exactly |
| Single-file compilation only | Remains single-file (FileImportDecl = no-op) | No pipeline changes needed |

**What IS changing:**
- `lang_runtime.c`: +14 new functions, +2 new includes (`<unistd.h>`, `<dirent.h>`)
- `lang_runtime.h`: +14 new declarations
- `Elaboration.fs`: `prePassDecls` recursion, `extractMainExpr` flattening,
  qualified-name desugar in `elaborateExpr`, +14 builtin dispatch cases,
  +14 `ExternalFuncDecl` entries in `elaborateProgram`
- `Pipeline.fs`: No changes

---

## Full Builtin Inventory for v6.0

All 14 builtins from `TypeCheck.fs` (STD-02 through STD-15):

| Builtin | C function | Signature | Return |
|---------|------------|-----------|--------|
| `read_file` | `lang_read_file` | `(Ptr) -> Ptr` | LangString* |
| `stdin_read_all` | `lang_stdin_read_all` | `() -> Ptr` | LangString* |
| `stdin_read_line` | `lang_stdin_read_line` | `() -> Ptr` | LangString* |
| `write_file` | `lang_write_file` | `(Ptr, Ptr) -> void` | unit (i64 0) |
| `append_file` | `lang_append_file` | `(Ptr, Ptr) -> void` | unit (i64 0) |
| `file_exists` | `lang_file_exists` | `(Ptr) -> I64` | bool (1/0) |
| `read_lines` | `lang_read_lines` | `(Ptr) -> Ptr` | LangCons* |
| `write_lines` | `lang_write_lines` | `(Ptr, Ptr) -> void` | unit (i64 0) |
| `get_args` | `lang_get_args` | `() -> Ptr` | LangCons* |
| `get_env` | `lang_get_env` | `(Ptr) -> Ptr` | LangString* (throws if unset) |
| `get_cwd` | `lang_get_cwd` | `() -> Ptr` | LangString* |
| `path_combine` | `lang_path_combine` | `(Ptr, Ptr) -> Ptr` | LangString* |
| `dir_files` | `lang_dir_files` | `(Ptr) -> Ptr` | LangCons* |
| `eprint` / `eprintln` | `lang_eprint` / `lang_eprintln` | `(Ptr) -> void` | unit |

Note: `write_lines` receives a `Ptr` (LangCons* list of LangString*). The MLIR side passes the
cons list pointer directly.

`get_args`: For the backend, `argc`/`argv` are not passed to `@main` in the current MLIR
generation. For v6.0, `lang_get_args` can be implemented using `getenv`-style storage populated
by a `lang_set_args(argc, argv)` call from a C shim, OR `@main` signature can be extended to
accept `(i32, ptr)`. The simpler approach: return an empty list (or implement via a global).

---

## Open Questions

1. **Qualified name handling: TypeCheck or backend rewriting?**
   - What we know: The backend CLI (`Program.fs`) does NOT call TypeCheck. It calls `parseProgram`
     then `elaborateProgram` directly. The raw AST still has `FieldAccess(Constructor("M"), "x")`.
   - What's unclear: Whether the plan is to add TypeCheck rewriting to the backend CLI, or to
     handle it in `elaborateExpr` directly.
   - Recommendation: Handle in `elaborateExpr` with a simple pattern: `FieldAccess(Constructor(_, None, _), fieldName, span)` → `Var(fieldName, span)`. This is O(1) per node and correct after declaration flattening.

2. **get_args implementation**
   - What we know: The current `@main` in MLIR is `func.func @main() -> i64`. It has no argc/argv.
   - What's unclear: Whether v6.0 requires a working `get_args` or can return empty list.
   - Recommendation: Implement `lang_get_args` as returning empty list (NULL cons) for v6.0.
     If argv is needed, it requires changing `@main` signature to `(i32, ptr) -> i64` and passing
     through to a global. Defer this complexity unless tests require it.

3. **write_lines list ABI**
   - What we know: `write_lines` takes a string list. In the backend, lists are `LangCons*` where
     each `head` is a `i64` that is a `ptrtoint(LangString*)`. So the C function must cast heads
     back to `LangString*`.
   - Recommendation: Follow the `lang_array_iter` pattern: cast `(LangString*)(uintptr_t)(cell->head)`.

4. **FileImportDecl at backend level**
   - What we know: The LangThree interpreter resolves file imports recursively. The backend does not.
   - What's unclear: If any planned v6.0 backend tests use `open "file.fun"` syntax.
   - Recommendation: Skip `FileImportDecl` in `prePassDecls` and `extractMainExpr` (treat as no-op).
     No compiler test currently exercises file imports.

---

## Sources

### Primary (HIGH confidence)

- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — Confirmed full
  structure of `prePassDecls`, `extractMainExpr`, `elaborateProgram`, and builtin dispatch pattern
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.c` — Confirmed all
  existing C runtime patterns (string, cons list, error throwing, GC_malloc)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.h` — Confirmed ABI
  for all existing functions
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Pipeline.fs` — Confirmed no changes
  needed (single C file compilation, no new linker flags needed)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/TypeCheck.fs` — Confirmed all 14 file I/O
  builtin type signatures, module handling strategy, `rewriteModuleAccess` behavior
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — Confirmed all `Decl` variants
  (ModuleDecl, OpenDecl, NamespaceDecl, FileImportDecl, LetDecl, LetRecDecl, LetMutDecl)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Cli/Program.fs` — Confirmed backend CLI
  does NOT call TypeCheck (only `parseProgram` + `elaborateProgram`)

### Secondary (MEDIUM confidence)

- LangThree test files in `tests/flt/file/module/` and `tests/flt/file/fileio/` — Confirmed
  expected behaviors: module-basic, module-open, module-qualified, module-nested, all fileio tests
- LangThree test files in `tests/flt/file/import/` — Confirmed file imports are interpreter-only tests

### Tertiary (LOW confidence)

- None — all findings verified from source code

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new dependencies, confirmed via Pipeline.fs and lang_runtime.c
- Module flattening architecture: HIGH — confirmed via Ast.fs Decl variants and TypeCheck.fs rewriting strategy
- File I/O C patterns: HIGH — exact pattern established by lang_hashtable_get, lang_range, lang_string_sub
- Qualified name handling without TypeCheck: MEDIUM — the behavior is confirmed (backend CLI skips TypeCheck), the exact implementation approach is a design decision for the planner
- get_args implementation: LOW — argc/argv not currently threaded through MLIR main

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable codebase, no external dependencies to track)
