# Pitfalls: Module System + File I/O Builtins

**Researched:** 2026-03-27
**Domain:** MLIR/LLVM compiler backend — adding module system and file I/O to an existing single-file compiler
**Confidence:** HIGH (grounded in actual codebase analysis of Elaboration.fs, Ast.fs, Eval.fs, lang_runtime.c)
**Milestone:** v6.0 Modules & I/O

---

## Summary

This document catalogs common mistakes when adding a module system and file I/O builtins to the LangBackend MLIR/LLVM compiler. The compiler currently uses a single-file model where `extractMainExpr` desugars all top-level `LetDecl`/`LetRecDecl`/`LetMutDecl` into a single nested `Let` expression, and `prePassDecls` performs a flat scan of a `Decl list` to populate `TypeEnv`/`RecordEnv`/`ExnTags`.

The standard approach for modules in this architecture is **compile-time flattening**: treat `ModuleDecl` nesting as a namespace prefix applied to elaborated names, recursive-`prePassDecls` into nested decl lists, and flatten all FuncOps into the single `MlirModule`. File I/O builtins follow the same pattern as existing C-runtime builtins (add to `lang_runtime.c`, add to `ExternalFuncs` list, add pattern-match arm in `elaborateExpr`), with one critical difference: FILE handles are OS resources that Boehm GC cannot collect — explicit `fclose` must be modeled in the language.

**Primary recommendation:** Flatten modules into name-mangled MLIR function symbols at elaboration time. Use `__` (double underscore) as the module separator in MLIR symbol names (e.g., `@Module__function`) since dots and colons cause toolchain friction. For file I/O, model file handles as opaque `Ptr` integers in the uniform boxed representation and always provide an explicit `file_close` builtin — never rely on GC or process-exit cleanup.

---

## Pitfall 1: Dots and Colons in MLIR Function Symbol Names

**What goes wrong:** Using `Module.function` or `Module::function` directly as MLIR `@` symbol names fails `mlir-opt` validation or produces broken `mlir-translate` output.

**Why it happens:** The MLIR language reference defines `suffix-id` as `(letter|id-punct)(letter|id-punct|digit)*` where `id-punct = [$._-]`. Dots *are* allowed in suffix-id for symbols (only type aliases ban dots). However, the alias note "names reserved for dialect types" causes confusion, and more critically, LLVM tools like `llvm-nm`, linker symbol tables, and debuggers on macOS/ARM64 treat dots as special (OCaml learned this the hard way: PR #11430 introduced dots in symbol names and broke LLDB on macOS, requiring a platform-specific override in PR #12933 and PR #14143). Colons are never allowed in suffix-id and require string-literal quoting (`@"Module::fn"`), which `mlir-translate` may not handle in all LLVM versions.

**How to avoid:** Use `__` (double underscore) as the module separator. Name-mangle `Module.function` as `@Module__function`. This is safe for MLIR text format, survives `mlir-opt`, `mlir-translate`, linker, and debuggers on both Linux x86-64 and macOS arm64.

**Warning signs:**
- `mlir-opt` error: `error: expected '@' in function declaration` or parse error on function name
- `mlir-translate` produces LLVM IR with quoted symbol names like `@"Module.fn"` that the linker treats differently than expected
- `nm` on the output binary shows `Module.fn` which breaks `perf` annotation on Linux

**Code pattern:**
```fsharp
// Correct: flatten Module.function into a mangled MLIR name
let mlirFuncName (modulePath: string list) (name: string) : string =
    let prefix = if modulePath.IsEmpty then "" else (String.concat "__" modulePath) + "__"
    "@" + prefix + name

// Wrong: do NOT use dots or colons
// "@Module.function"  <- works in MLIR text but breaks downstream tools
// "@Module::function" <- parse error in mlir-opt
```

---

## Pitfall 2: prePassDecls Does Not Recurse Into ModuleDecl

**What goes wrong:** `prePassDecls` currently iterates a flat `Decl list` and has a catch-all `| _ -> ()` for unknown decls. Adding `ModuleDecl` without updating `prePassDecls` means constructors, record types, and exception declarations inside modules are invisible to the elaborator's `TypeEnv`/`RecordEnv`/`ExnTags`. The elaborator then fails at runtime with "constructor not found" or "record type not found" when processing code that uses types defined inside a module.

**Why it happens:** The flat `for decl in decls do match decl with ...` loop simply skips `ModuleDecl`. Since `ModuleDecl` carries an inner `Decl list`, it needs a recursive call.

**How to avoid:** Add a recursive case to `prePassDecls`:

```fsharp
| Ast.Decl.ModuleDecl(moduleName, innerDecls, _) ->
    // Recurse — constructors/records/exceptions inside modules must be visible
    let (innerTypeEnv, innerRecordEnv, innerExnTags) = prePassDecls innerDecls
    // Merge with optional name-prefixing strategy
    for kv in innerTypeEnv do typeEnv <- Map.add kv.Key kv.Value typeEnv
    for kv in innerRecordEnv do recordEnv <- Map.add kv.Key kv.Value recordEnv
    for kv in innerExnTags do exnTags <- Map.add kv.Key kv.Value exnTags
```

**Warning signs:**
- Elaboration error "TypeEnv lookup failed for constructor X" on any code inside a `module` block
- Works in LangThree interpreter (Eval.fs handles `ModuleDecl` recursively) but fails in backend

---

## Pitfall 3: extractMainExpr Silently Drops ModuleDecl Bodies

**What goes wrong:** `extractMainExpr` filters to only `LetDecl | LetRecDecl | LetMutDecl` and skips everything else with `| _ -> ()`. A `ModuleDecl` wrapping a `let _ = ...` side-effect expression gets silently dropped. The program compiles but produces no output because the entry-point expression was inside a module that never got elaborated.

**Why it happens:** The current desugaring was designed for flat top-level `let` declarations. Modules introduce a new container level that the filter doesn't penetrate.

**How to avoid:** Before filtering, flatten the decl list: recursively extract all executable decls from inside `ModuleDecl` nesting. Alternatively, elaborate `ModuleDecl` bodies as a sequenced block before the top-level continuation:

```fsharp
// Flatten approach: extract let decls from module bodies first
let rec flattenDecls (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(_, innerDecls, _) -> flattenDecls innerDecls
        | other -> [other])

let private extractMainExpr (decls: Ast.Decl list) : Expr =
    let flatDecls = flattenDecls decls
    // ... existing logic on flatDecls
```

**Warning signs:**
- Program compiles with exit code 0 but produces no stdout output
- Tests with `module M = let _ = println "hello"` pass in interpreter, silent in backend

---

## Pitfall 4: OpenDecl Creates Name Ambiguity in Elaboration Env

**What goes wrong:** `open Module` in LangThree brings all of `Module`'s bindings into scope. If the elaboration environment (`ElabEnv`) is just a `Map<string, MlirValue>`, then opening two modules that both define `"helper"` silently shadows the first. In the interpreter this is harmless (last-writer wins in `Map.add`). In the backend it can cause wrong FuncOp to be called because the SSA value for the wrong `@Module__helper` got bound under the key `"helper"`.

**Why it happens:** Module flattening at elaboration time means that once functions are elaborated into `FuncOp`s, the `Var` lookup in the env needs to resolve `"helper"` to `@Module1__helper` vs `@Module2__helper`. If `OpenDecl` just re-exports all names without qualification, two opens collide.

**How to avoid:** Track module opens as a stack of module namespaces in `ElabEnv`. When resolving `Var("helper")`, search the open-module stack in LIFO order. If ambiguous, fail with a diagnostic rather than silently picking one. For v6.0 scope: only support `open` for modules whose functions have been pre-elaborated into the env with qualified names already.

**Warning signs:**
- Wrong function body called at runtime (silent corruption, not a crash)
- Only manifests when two opened modules share a function name

---

## Pitfall 5: File Handles Are Not GC-Collectible — Boehm GC Will Not Close Them

**What goes wrong:** File handles opened with `fopen()` are OS resources (file descriptors). Boehm GC manages *heap memory* only. Even if a `LangFile*` struct is GC-allocated and becomes unreachable, GC will reclaim its memory bytes but will NOT call `fclose()`. The file descriptor leaks. On Linux the default per-process limit is 1024 (or 65536 with `ulimit -n`) open file descriptors. Programs that open many files in a loop will exhaust them, causing `fopen()` to return `NULL`, which then causes a null pointer dereference in the runtime.

**Why it happens:** Conservative GC has no concept of finalizers that run arbitrary C code (Boehm does have a finalizer API — `GC_register_finalizer` — but it is complex, unreliable with conservative collection, and not currently used in this project). The pattern used for all other heap objects (strings, lists, closures) is "allocate with GC_malloc, never free" — that pattern is wrong for file handles.

**How to avoid:**
1. Model file handles as opaque `i64` integers (the raw `FILE*` pointer cast to `int64_t` via `ptrtoint`). Do NOT wrap them in a `GC_malloc`'d struct — wrapping adds no benefit and masks the lifetime issue.
2. Expose `file_close : int -> unit` as a required builtin. Document that user code MUST call it.
3. In `lang_runtime.c`, implement `lang_file_close(int64_t handle)` that calls `fclose((FILE*)handle)`.
4. For the high-level builtins (`read_file`, `write_file`) that open and immediately close, call `fclose` inside the C function before returning — no handle escapes to user code.

```c
// In lang_runtime.c — safe pattern: open, use, close internally
LangString* lang_read_file(LangString* path) {
    FILE* f = fopen(path->data, "r");
    if (!f) { /* raise exception */ }
    // ... read content ...
    fclose(f);  // MUST close before return — GC will not do this
    return result;
}

// For handle-based API, expose explicit close
void lang_file_close(int64_t handle) {
    FILE* f = (FILE*)(uintptr_t)handle;
    if (f) fclose(f);
}
```

**Warning signs:**
- `fopen()` returning NULL after many file operations in a test
- `errno == EMFILE` ("Too many open files") in runtime errors
- Programs that work in interpreter (no C runtime, .NET closes on GC) fail in native binary

---

## Pitfall 6: Null Return From fopen Not Checked → Null Pointer Dereference in MLIR

**What goes wrong:** `fopen()` returns `NULL` on error (file not found, permission denied). If the C runtime function passes this `NULL` back as a `Ptr` to MLIR-generated code, any subsequent GEP or load on the null pointer causes a segfault rather than a catchable LangThree exception.

**Why it happens:** The existing pattern for runtime errors is to either call `lang_failwith` (which exits) or `lang_throw` (which triggers the setjmp/longjmp exception mechanism). File open failure is a common recoverable error that should be a catchable exception, not a process exit.

**How to avoid:** In every `lang_file_*` C function, check the return value of `fopen` and call `lang_throw` with a string exception value if it is NULL:

```c
int64_t lang_file_open(LangString* path, LangString* mode) {
    FILE* f = fopen(path->data, mode->data);
    if (!f) {
        // Construct a LangString for the error message
        char buf[512];
        snprintf(buf, sizeof(buf), "file_open: cannot open '%s'", path->data);
        // Allocate on GC heap so lang_throw can carry it
        LangString* err = lang_string_from_cstr(buf);
        lang_throw((void*)err);
        return 0;  // unreachable, but satisfies the type checker
    }
    return (int64_t)(uintptr_t)f;
}
```

**Warning signs:**
- Segfault (signal 11) when opening a nonexistent file rather than a catchable exception
- Tests pass when the file exists, silently crash when it does not

---

## Pitfall 7: ExternalFuncs List Is Duplicated in Two Places

**What goes wrong:** `Elaboration.fs` contains the `ExternalFuncs` list in **two** separate locations: one in the old `elaborateModule` entry point (~line 2279) and one in the new `elaborateProgram` entry point (~line 2426). Adding a new `@lang_file_*` external declaration to only one list causes link failures (`undefined reference to lang_file_open`) in programs that use one entry point but not the other.

**Why it happens:** The two entry points evolved independently. Both emit a hardcoded `externalFuncs` list literal.

**How to avoid:** Extract `externalFuncs` into a single `let private standardExternalFuncs = [...]` value at module level, shared by both entry points. When adding any new C runtime function, update this single list:

```fsharp
let private standardExternalFuncs : ExternalFuncDecl list = [
    { ExtName = "@GC_init"; ... }
    // ... existing entries ...
    // Add new file I/O entries here ONCE
    { ExtName = "@lang_file_open";  ExtParams = [Ptr; Ptr]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
    { ExtName = "@lang_file_close"; ExtParams = [I64];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
    { ExtName = "@lang_read_file";  ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
    // ...
]
```

**Warning signs:**
- `clang: error: undefined symbol: _lang_file_open` at link time
- Works in one compilation path but not the other
- Diff shows the two `externalFuncs` lists have diverged

---

## Pitfall 8: LetPatDecl at Module Level Is Not Handled by extractMainExpr

**What goes wrong:** `Ast.Decl.LetPatDecl` (module-level pattern binding like `let (a, b) = expr`) is not in the filter list in `extractMainExpr`. It falls through to `| _ -> ()` and is silently dropped. If a module exports values via a tuple destructuring at the top level, those names are never bound.

**Why it happens:** `LetPatDecl` was added for Phase 28 (N-tuple module-level binding) and was simply not added to the filter/build logic. The interpreter's `evalModuleDecls` handles it explicitly, but `extractMainExpr` doesn't.

**How to avoid:** Add `Ast.Decl.LetPatDecl` to both the filter predicate and the build match arms:

```fsharp
| Ast.Decl.LetPatDecl _ -> true  // add to filter

// In build:
| Ast.Decl.LetPatDecl(pat, body, _) :: rest ->
    LetPat(pat, body, build rest, s)
```

**Warning signs:**
- Module-level `let (x, y) = (1, 2)` compiles without error but `x` and `y` are undefined when used below

---

## Pitfall 9: stdin_read_line Returns Empty String at EOF — Not An Exception

**What goes wrong:** LangThree's `stdin_read_line` returns `StringValue ""` when `ReadLine()` returns null (EOF). The native backend implementation via `fgets` or `fgetc` also returns an empty string at EOF. Programs that loop on `stdin_read_line` expecting EOF to be signaled differently (e.g., by an exception) loop forever or produce incorrect output.

**Why it happens:** This is a semantic decision baked into Eval.fs (line 317: `if line = null then StringValue "" else StringValue line`). The backend must match this exact behavior to preserve interpreter compatibility.

**How to avoid:** Implement `lang_stdin_read_line` in C to return an empty `LangString` (length=0, data="") at EOF, exactly matching the interpreter:

```c
LangString* lang_stdin_read_line(void) {
    char buf[4096];
    if (fgets(buf, sizeof(buf), stdin) == NULL) {
        // EOF or error — return empty string (matches interpreter behavior)
        return lang_string_from_cstr("");
    }
    // Strip trailing newline
    size_t len = strlen(buf);
    if (len > 0 && buf[len-1] == '\n') buf[--len] = '\0';
    return lang_string_from_cstr_len(buf, len);
}
```

**Warning signs:**
- Program loops forever reading stdin in the backend but terminates correctly in interpreter
- Difference between `stdin_read_line` returning `""` vs raising an exception at EOF

---

## Pitfall 10: Module-Level Let Rec With Multiple Bindings Loses All But First

**What goes wrong:** `extractMainExpr` handles `LetRecDecl` with a pattern that only extracts the FIRST binding:

```fsharp
| Ast.Decl.LetRecDecl(bindings, _) :: rest ->
    match bindings with
    | (name, param, body, _) :: _ -> LetRec(name, param, body, build rest, s)
    | [] -> build rest
```

For `let rec f x = ... and g x = ...` (mutual recursion, `bindings` has two entries), only `f` is elaborated. `g` is unreachable. Calls to `g` will fail with "variable not in scope."

**Why it happens:** The comment in the code says "Phase 17 will need real support" — this was a known temporary limitation. Mutual recursion support was deferred.

**How to avoid:** For v6.0, if modules contain mutually recursive functions, the full `bindings` list must be elaborated. The MLIR backend already supports multiple `FuncOp`s; the fix is to extend `extractMainExpr` or handle mutual recursion at the elaboration level by emitting all bindings as peer `FuncOp`s before emitting the continuation.

**Warning signs:**
- `let rec even n = ... and odd n = ...` compiles silently but `odd` is unbound at runtime
- Only the first function in a mutual-rec group works; the second is missing

---

## Pitfall 11: File I/O Builtins Must Pass Strings as LangString*, Not char*

**What goes wrong:** Using `char*` directly in `lang_read_file(char* path)` breaks the calling convention. All string values in this compiler are represented as `LangString*` — a GC-malloc'd struct with `{i64 length, ptr data}`. The elaboration code passes a `Ptr` (the `LangString*`) to C functions. If the C function signature expects `char*`, the function receives a pointer to the struct, not the char data, and reads garbage or crashes.

**Why it happens:** It's tempting to use `char*` since C standard library functions (fopen, etc.) take `char*`. Every existing lang_runtime.c function takes `LangString*` and internally accesses `->data`.

**How to avoid:** Always use `LangString*` in C function signatures. Access `path->data` to pass to OS functions:

```c
// CORRECT
LangString* lang_read_file(LangString* path) {
    FILE* f = fopen(path->data, "r");  // use ->data for OS call
    ...
}

// WRONG — receives LangString* but treats it as char*
LangString* lang_read_file(char* path) {
    FILE* f = fopen(path, "r");  // path is actually a LangString* — reads struct header as chars
    ...
}
```

**Warning signs:**
- `fopen` returns NULL unexpectedly for paths that exist
- Garbled path strings in error messages
- Works for short strings (if length field happens to be a valid ASCII char sequence) but fails for longer paths

---

## Pitfall 12: write_file Overwrites; Callers Expect Interpreter Semantics

**What goes wrong:** LangThree's `write_file` uses `File.WriteAllText` which truncates-then-writes. `append_file` uses `File.AppendAllText`. In the C backend, using `fopen(path, "w")` for `lang_write_file` matches this. Using `fopen(path, "a")` for `lang_append_file` matches that. If these are swapped (or if `"r+"` is used instead), programs produce wrong output that is hard to debug since it's a behavioral difference, not a crash.

**How to avoid:** Map backend builtins to C fopen modes that match the interpreter exactly:

| LangThree builtin | Interpreter behavior | C fopen mode |
|-------------------|---------------------|--------------|
| `write_file`      | Truncate and write  | `"w"`        |
| `append_file`     | Append to end       | `"a"`        |
| `read_file`       | Read entire file    | `"r"`        |

**Warning signs:**
- `append_file` tests fail because content is overwritten instead of appended
- File content after write is truncated unexpectedly

---

## Pitfall 13: read_lines Must Return Cons List, Not Array

**What goes wrong:** `lang_read_lines` in C must return a cons list (`LangCons*`) — the same linked-list representation used for all list values. If it returns an array pointer or any other layout, the elaboration code that treats the result as a cons list (null-check for empty, GEP for head/tail) will read garbage.

**Why it happens:** `File.ReadAllLines` in .NET returns a `string[]`. It's natural in C to also return an array. But the backend's list representation is `LangCons*` (null-terminated linked list of cons cells), not an array.

**How to avoid:** Build the cons list in reverse order using `lang_cons` cells:

```c
LangCons* lang_read_lines(LangString* path) {
    // ... read all lines into a buffer ...
    // Build cons list: fold right (or build forward then reverse)
    LangCons* result = NULL;  // nil
    for (int i = line_count - 1; i >= 0; i--) {
        LangString* s = lang_string_from_cstr(lines[i]);
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = (int64_t)(uintptr_t)s;
        cell->tail = result;
        result = cell;
    }
    return result;
}
```

**Warning signs:**
- Pattern matching on `read_lines` result crashes (null pointer dereference on first element access)
- `List.length (read_lines path)` returns wrong count or segfaults

---

## Pitfall 14: Qualified Name Lookup Order Matters for prePassDecls Merging

**What goes wrong:** When `prePassDecls` recursively processes nested `ModuleDecl`s and merges inner `TypeEnv` into outer, if two modules define a type with the same constructor name (e.g., both define `type option = None | Some of int`), the inner merge silently overwrites the outer. The tag index for `None` from the first module is overwritten by the second module's tag.

**Why it happens:** `Map.add` on collision takes the new value. Without module-qualified keys, all constructors share a flat namespace.

**How to avoid:** For v6.0, either:
1. Require globally unique constructor names (enforced by the LangThree type checker anyway for unqualified names in scope)
2. Or prefix TypeEnv keys with the module path: `"Module__None"` instead of `"None"`

Since LangThree already enforces unique constructor names within scope via its type checker, option 1 is safe for v6.0.

**Warning signs:**
- Pattern matching dispatches to the wrong constructor's tag
- `Some 42` constructs an ADT with the wrong tag integer, causing match to fail

---

## Open Questions

1. **FileImportDecl handling**: The AST has `FileImportDecl of path: string * Span` for file-based imports. LangThree's Eval.fs uses a `fileImportEvaluator` delegate to load and evaluate imported files. For the backend, multi-file compilation is listed as out-of-scope ("incremental/separate compilation — 모듈 시스템 선행 필요"). The question is: should `FileImportDecl` be silently ignored (safest for v6.0) or should it trigger a compile-time error? Recommendation: emit a clear compile-time error message "FileImportDecl not yet supported in native backend" rather than silent ignoring.

2. **stdin_read_all blocking behavior**: `lang_stdin_read_all` must read until EOF. In a terminal (interactive stdin), this blocks until Ctrl-D. This matches interpreter behavior but may surprise users. No mitigation needed — document it.

3. **write_lines newline convention**: LangThree's `File.WriteAllLines` appends `\n` after each line (on Unix) or `\r\n` (on Windows). The C backend running on Linux should use `\n`. On macOS arm64 (also the target), `\n` is correct. Verify the C implementation uses `\n` explicitly, not system-default `fprintf` newline.

---

## Sources

### Primary (HIGH confidence — direct codebase analysis)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — `prePassDecls`, `extractMainExpr`, `elaborateProgram`, `ExternalFuncs` list (lines 2314–2460)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.c` — existing C runtime patterns for `LangString`, file I/O helpers
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.h` — `LangCons`, `LangHashtable`, `LangClosureFn` ABI
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — `Decl` DU cases: `ModuleDecl`, `OpenDecl`, `FileImportDecl`, `LetPatDecl`, `LetRecDecl`
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` — interpreter behavior for `stdin_read_line` (null→""), `read_file`, `write_file`, `append_file`, `read_lines`, `write_lines`, `evalModuleDecls`

### Secondary (HIGH confidence — official documentation)
- [MLIR Language Reference](https://mlir.llvm.org/docs/LangRef/) — `suffix-id` character rules: `[$._-]` allowed in symbol names; dots permitted for `@` sigil, banned for type alias `!`
- [MLIR Symbols and Symbol Tables](https://mlir.llvm.org/docs/SymbolsAndSymbolTables/) — symbol reference rules

### Secondary (MEDIUM confidence — verified via web search)
- [OCaml PR #11430 / #12933 / #14143](https://github.com/ocaml/ocaml/pull/14143) — OCaml used dots in LLVM symbol names, broke LLDB on macOS arm64, had to add platform-specific override; validates `__` separator recommendation
- [Boehm GC documentation](https://www.hboehm.info/gc/) — GC manages heap memory, does not call fclose; file descriptors are OS resources outside GC scope

### Tertiary (LOW confidence — general knowledge)
- POSIX `EMFILE` limit (~1024 default, configurable): standard OS constraint on open file descriptors
- `fopen` mode strings `"r"`, `"w"`, `"a"` — standard C semantics matching .NET `File.ReadAllText`/`WriteAllText`/`AppendAllText`

---

## Metadata

**Confidence breakdown:**
- Module system pitfalls (1–4, 10, 14): HIGH — grounded in actual code paths in Elaboration.fs and Ast.fs
- File I/O pitfalls (5–9, 11–13): HIGH — grounded in lang_runtime.c patterns and Eval.fs semantics
- MLIR symbol naming (1): HIGH — confirmed via official MLIR LangRef and OCaml toolchain history

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable domain — MLIR 20 / LLVM 20 conventions, Boehm GC API)
