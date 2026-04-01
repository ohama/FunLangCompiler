# Phase 26: File I/O Core - Research

**Researched:** 2026-03-27
**Domain:** C runtime extension + MLIR elaboration for file I/O builtins
**Confidence:** HIGH

## Summary

Phase 26 adds five file I/O builtins (read_file, write_file, append_file, file_exists, eprint/eprintln) to the FunLangCompiler compiler. The domain is fully understood — this is a pure codebase extension following the established builtin pattern used in phases 22 (arrays), 23 (hashtables), and 24 (array HOFs).

The pattern is: implement C helper functions in `lang_runtime.c`, declare them in `lang_runtime.h`, add `ExternalFuncDecl` entries to both `externalFuncs` lists in `Elaboration.fs`, and add pattern-match arms in `elaborateExpr`. All functions operate on `LangString*` pointers (struct `{i64 length, char* data}`), which is already the established string ABI.

Error handling for `read_file` should use `lang_throw` (catchable by try/with), matching the `lang_array_bounds_check` and `lang_hashtable_get` patterns. The `eprint`/`eprintln` builtins use `fprintf(stderr, ...)` — the same approach used in `lang_failwith` but without exiting. File handles must be opened and closed within each C function since the GC will not close them.

**Primary recommendation:** Implement all file I/O logic in `lang_runtime.c` C functions with `lang_file_*` prefix, wire them up in Elaboration.fs following the hashtable pattern, and add E2E tests with temp files for write/read round-trips.

## Standard Stack

This phase uses no external libraries. All implementation is in the existing project stack.

### Core

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| lang_runtime.c | (in-repo) | C runtime helper functions | All builtins live here; GC_malloc for string allocation |
| lang_runtime.h | (in-repo) | Declarations for C functions | Needed so clang can compile the .c file |
| Elaboration.fs | (in-repo) | F# MLIR code generation | Pattern-match arms dispatch builtins to C calls |
| Boehm GC | system | Memory allocation | All heap objects use GC_malloc |
| POSIX stdio.h | system | fopen/fread/fwrite/fclose/fprintf | Standard C file I/O |

### Supporting

| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| MlirIR.fs LlvmCallOp | (in-repo) | Call C function returning a value (Ptr/I64) | read_file, file_exists |
| MlirIR.fs LlvmCallVoidOp | (in-repo) | Call C function returning void, emit unit 0 | write_file, append_file, eprint, eprintln |
| lang_throw | (in-repo) | Throw catchable exception (LangString* message) | read_file file-not-found error |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| C runtime functions (lang_file_*) | Inline MLIR ops for fopen/fread | C functions are cleaner, already used for all complex operations; inline MLIR for fopen would require 10+ ops per builtin |
| lang_throw for read_file error | lang_failwith (exits process) | lang_throw is catchable, matches FunLang Eval.fs behavior (raises FunLangException) |
| fprintf(stderr,...) for eprint | fputs(s->data, stderr) | fprintf handles the char* extraction cleanly; fputs is equally valid |

**Installation:** No packages needed. All components are in-repo or system libraries already linked.

## Architecture Patterns

### Recommended Project Structure

No new files needed. Changes span three existing files:

```
src/FunLangCompiler.Compiler/
├── lang_runtime.c    # ADD: lang_file_read, lang_file_write, lang_file_append,
│                     #      lang_file_exists, lang_eprint, lang_eprintln
├── lang_runtime.h    # ADD: declarations for the 6 new C functions
└── Elaboration.fs    # ADD: elaborateExpr arms + ExternalFuncDecl entries
tests/compiler/
└── 26-NN-*.flt       # ADD: E2E tests for each requirement
```

### Pattern 1: C Runtime Function (Returns LangString*)

**What:** C function that allocates a new LangString* via GC_malloc and returns it as Ptr.
**When to use:** read_file — takes a path LangString*, returns content LangString*.

```c
// Source: lang_runtime.c — mirrors lang_string_concat pattern
LangString* lang_file_read(LangString* path) {
    // Open file
    FILE* f = fopen(path->data, "rb");
    if (f == NULL) {
        // Throw catchable exception — mirrors lang_hashtable_get missing-key pattern
        const char* msg_prefix = "read_file: file not found: ";
        int64_t prefix_len = (int64_t)strlen(msg_prefix);
        int64_t total_len = prefix_len + path->length;
        char* buf = (char*)GC_malloc((size_t)(total_len + 1));
        memcpy(buf, msg_prefix, (size_t)prefix_len);
        memcpy(buf + prefix_len, path->data, (size_t)path->length);
        buf[total_len] = '\0';
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = total_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    // Get file size
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    // Allocate content buffer
    char* content = (char*)GC_malloc((size_t)(size + 1));
    fread(content, 1, (size_t)size, f);
    fclose(f);
    content[size] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)size;
    s->data = content;
    return s;
}
```

### Pattern 2: C Runtime Function (Void, Returns Unit)

**What:** C function with no return value. Elaboration emits LlvmCallVoidOp then ArithConstantOp(unitVal, 0L).
**When to use:** write_file, append_file, eprint, eprintln.

```c
// Source: lang_runtime.c — mirrors lang_hashtable_set void pattern
void lang_file_write(LangString* path, LangString* content) {
    FILE* f = fopen(path->data, "wb");
    if (f == NULL) return; /* silently fail, or throw — TBD */
    fwrite(content->data, 1, (size_t)content->length, f);
    fclose(f);
}

void lang_file_append(LangString* path, LangString* content) {
    FILE* f = fopen(path->data, "ab");
    if (f == NULL) return;
    fwrite(content->data, 1, (size_t)content->length, f);
    fclose(f);
}

void lang_eprint(LangString* s) {
    fwrite(s->data, 1, (size_t)s->length, stderr);
    fflush(stderr);
}

void lang_eprintln(LangString* s) {
    fwrite(s->data, 1, (size_t)s->length, stderr);
    fputc('\n', stderr);
    fflush(stderr);
}
```

### Pattern 3: C Runtime Function (Returns I64 Bool)

**What:** C function returning int64_t (0 or 1). Elaboration emits LlvmCallOp with I64 result, then ArithCmpIOp "ne" 0 to get I1.
**When to use:** file_exists — mirrors lang_hashtable_containsKey pattern.

```c
// Source: lang_runtime.c — mirrors lang_hashtable_containsKey pattern
int64_t lang_file_exists(LangString* path) {
    FILE* f = fopen(path->data, "r");
    if (f != NULL) { fclose(f); return 1; }
    return 0;
}
```

Note: `fopen` to check existence is portable and consistent with POSIX. `access()` is available on Linux/macOS but requires unistd.h and is not strictly needed.

### Pattern 4: ExternalFuncDecl Entries

**What:** Both `externalFuncs` lists in Elaboration.fs (around lines 2294 and 2465) must be updated identically.
**When to use:** Every new C function requires an entry in BOTH lists.

```fsharp
// Source: Elaboration.fs — append after @lang_array_init entry
{ ExtName = "@lang_file_read";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_file_write";   ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_file_append";  ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_file_exists";  ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_eprint";       ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_eprintln";     ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
```

### Pattern 5: Elaboration Arms

**What:** Pattern-match arms in `elaborateExpr` that dispatch builtins to C calls.
**When to use:** One arm per builtin, ordered from most-specific (multi-arg) to least-specific (single-arg).

```fsharp
// Source: Elaboration.fs — modeled after hashtable_set (void two-arg) and hashtable_containsKey (bool)

// write_file — two-arg, void return
| App (App (Var ("write_file", _), pathExpr, _), contentExpr, _) ->
    let (pathVal,    pathOps)    = elaborateExpr env pathExpr
    let (contentVal, contentOps) = elaborateExpr env contentExpr
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [LlvmCallVoidOp("@lang_file_write", [pathVal; contentVal]); ArithConstantOp(unitVal, 0L)]
    (unitVal, pathOps @ contentOps @ ops)

// append_file — two-arg, void return (identical shape to write_file)
| App (App (Var ("append_file", _), pathExpr, _), contentExpr, _) ->
    let (pathVal,    pathOps)    = elaborateExpr env pathExpr
    let (contentVal, contentOps) = elaborateExpr env contentExpr
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [LlvmCallVoidOp("@lang_file_append", [pathVal; contentVal]); ArithConstantOp(unitVal, 0L)]
    (unitVal, pathOps @ contentOps @ ops)

// read_file — one-arg, returns Ptr (LangString*)
| App (Var ("read_file", _), pathExpr, _) ->
    let (pathVal, pathOps) = elaborateExpr env pathExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, pathOps @ [LlvmCallOp(result, "@lang_file_read", [pathVal])])

// file_exists — one-arg, returns I1 (bool via I64 comparison)
| App (Var ("file_exists", _), pathExpr, _) ->
    let (pathVal, pathOps) = elaborateExpr env pathExpr
    let rawVal  = { Name = freshName env; Type = I64 }
    let zeroVal = { Name = freshName env; Type = I64 }
    let boolVal = { Name = freshName env; Type = I1  }
    let ops = [
        LlvmCallOp(rawVal, "@lang_file_exists", [pathVal])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)
    ]
    (boolVal, pathOps @ ops)

// eprint — one-arg, void return
| App (Var ("eprint", _), strExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [LlvmCallVoidOp("@lang_eprint", [strVal]); ArithConstantOp(unitVal, 0L)]
    (unitVal, strOps @ ops)

// eprintln — one-arg, void return
| App (Var ("eprintln", _), strExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [LlvmCallVoidOp("@lang_eprintln", [strVal]); ArithConstantOp(unitVal, 0L)]
    (unitVal, strOps @ ops)
```

### Anti-Patterns to Avoid

- **Forgetting to update BOTH externalFuncs lists:** Elaboration.fs has two nearly identical `externalFuncs` lists (around line 2294 for module elaboration and line 2465 for top-level). Missing either causes MLIR "undefined symbol" errors.
- **Using fclose after lang_throw:** lang_throw calls _longjmp and never returns, so fclose before throw is required. Failure to close before throw causes file descriptor leak (though process typically exits on unhandled exceptions anyway).
- **Using `access(path, F_OK)` for file_exists:** fopen is simpler, equally portable, and consistent with the runtime's dependencies. `access()` requires `<unistd.h>` which is not currently included.
- **Inline MLIR for file operations:** All complex operations (anything requiring fopen/fread/fclose sequences) belong in C, not as inline MLIR ops. The MLIR elaboration layer only orchestrates calls to C.
- **Omitting fflush for eprint/eprintln:** Without fflush, stderr output may not appear if the process exits normally through GC_malloc-allocated code. Always flush after writing to stderr.
- **Not null-terminating GC_malloc'd content buffer:** `fread` does not null-terminate. The `content[size] = '\0'` is required so `lang_string_contains` and other functions that call `strstr` work correctly.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| File existence check | Custom syscall or stat() wrapper | fopen("r") + fclose | fopen is already used, no extra includes needed |
| Read entire file | Incremental read loop in MLIR | lang_file_read C function | MLIR cannot express loops over unknown file sizes |
| String error message construction | Multi-op MLIR sequence | C function with GC_malloc + memcpy | Already established pattern (see lang_hashtable_get) |
| stderr output | @fprintf MLIR call with format string | lang_eprint/lang_eprintln C functions | Avoids IsVarArg complexity; cleaner |

**Key insight:** The entire file I/O domain belongs in C runtime functions, not inline MLIR. The MLIR elaboration layer is a thin dispatch layer that calls C. Attempting to inline file I/O as MLIR ops would require dozens of operations and lose the clarity of the established pattern.

## Common Pitfalls

### Pitfall 1: Two externalFuncs Lists

**What goes wrong:** Adding entries to only one of the two `externalFuncs` lists in Elaboration.fs. The MLIR emit succeeds for one codepath but crashes or produces undefined symbol errors for the other.
**Why it happens:** Elaboration.fs has two separate module-building functions (one for module-level programs around line 2293, one for top-level programs around line 2464). Both independently build the externalFuncs list.
**How to avoid:** Search for `@lang_array_init` (the last existing entry before the new ones) and update BOTH occurrences identically.
**Warning signs:** E2E tests pass for some programs but not others; MLIR errors about undeclared symbols.

### Pitfall 2: File Not Closed Before lang_throw

**What goes wrong:** `lang_file_read` opens a file, then calls `lang_throw` on error without closing. This leaks the file descriptor.
**Why it happens:** `lang_throw` calls `_longjmp` and never returns to the call site.
**How to avoid:** Call `fclose(f)` before calling `lang_throw`, or never open the file before checking existence first.
**Warning signs:** File descriptor exhaustion in long-running programs with many read errors.

### Pitfall 3: Binary Mode for Text Files

**What goes wrong:** Opening files with `"r"` (text mode) on Windows would mangle `\r\n` to `\n`. On Linux/macOS, text and binary mode are identical, but `"rb"` is more correct for byte-accurate reads.
**Why it happens:** Platform differences in text mode.
**How to avoid:** Use `"rb"` for read_file and `"wb"`/`"ab"` for write/append. The FunLang Eval.fs uses `File.ReadAllText` which is text-mode, but for the C backend, binary mode ensures no byte mangling.
**Warning signs:** String length mismatches between FunLang interpreted and compiled modes on Windows (not currently tested).

### Pitfall 4: eprint/eprintln ARM Placement in elaborateExpr

**What goes wrong:** If the eprint/eprintln arms are placed after the general `App (Var (name, _), ...)` catch-all arm, they are never reached.
**Why it happens:** F# pattern matching is first-match. The general App arm at line ~1119 handles all unrecognized function applications.
**How to avoid:** Place all new builtin arms BEFORE the general `App (funcExpr, argExpr, _)` arm. Follow the established convention: add after the last existing builtin arm (around the array HOF arms, line ~1040).
**Warning signs:** Compilation succeeds but the builtin is treated as a user-defined function call, causing "unknown function" MLIR errors or wrong behavior.

### Pitfall 5: Curried Two-Arg Builtins

**What goes wrong:** `write_file path content` is syntactically `App(App(Var "write_file", path), content)` — a nested application. Pattern matching only on `App(Var "write_file", ...)` catches partial application, not the full call.
**Why it happens:** The language uses curried application. Two-arg builtins need a nested App pattern.
**How to avoid:** Follow the `hashtable_set` three-arg and `hashtable_get` two-arg patterns exactly. `write_file` and `append_file` need `App (App (Var ("write_file", _), pathExpr, _), contentExpr, _)`.
**Warning signs:** Partial application is elaborated as a closure instead of a direct C call; runtime failure or wrong type.

## Code Examples

### E2E Test for write_file + read_file round-trip

```
// Source: tests/compiler/ — follows established .flt format
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let path = "/tmp/lang_test_write.txt" in
write_file path "hello";
let content = read_file path in
println content
// --- Output:
hello
0
```

### E2E Test for file_exists

```
// --- Input:
let exists = file_exists "/nonexistent_xyz_abc.txt" in
if exists then print "yes" else print "no"
// --- Output:
no
0
```

### E2E Test for eprint/eprintln (stderr not captured by test harness)

```
// --- Input:
eprint "err: ";
eprintln "done"
// --- Output:
0
```

Note: The test harness captures stdout only. Stderr output from eprint/eprintln does not appear in the `// --- Output:` section. The test verifies the program exits 0 and produces no stdout.

### lang_runtime.h declaration additions

```c
// Source: lang_runtime.h — append before #endif
LangString* lang_file_read(LangString* path);
void        lang_file_write(LangString* path, LangString* content);
void        lang_file_append(LangString* path, LangString* content);
int64_t     lang_file_exists(LangString* path);
void        lang_eprint(LangString* s);
void        lang_eprintln(LangString* s);
```

## State of the Art

This is an internal compiler project with no external library evolution. All patterns are stable.

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| lang_try_enter (setjmp in C) | lang_try_push + inline _setjmp (Phase 19) | Phase 19 | ARM64 PAC compatibility — file I/O does not use setjmp, no impact |
| No builtins beyond print | Established C-runtime builtin pattern | Phase 14+ | Phase 26 follows this fully-established pattern |

**Deprecated/outdated:**
- None relevant to this phase.

## Open Questions

1. **write_file error handling on open failure**
   - What we know: FunLang Eval.fs does NOT throw on write failure — it just calls `File.WriteAllText` which throws .NET IOException if it fails. The requirement (FIO-02) says "creates or overwrites" but doesn't specify error behavior.
   - What's unclear: Should `lang_file_write` silently ignore fopen failure, or throw via `lang_throw`?
   - Recommendation: Silently ignore (return without writing) for now, matching the simplest safe behavior. The FunLang interpreter would throw a .NET exception, but since the requirement doesn't specify, silent failure is less surprising than a crash. This can be revisited if FIO-02 gets an error-handling requirement.

2. **Test file cleanup for write/append tests**
   - What we know: Tests that write to `/tmp/` files leave artifacts if the test command doesn't clean up.
   - What's unclear: Should the test command include `rm -f` for the temp file?
   - Recommendation: Use a fixed path like `/tmp/lang_test_26_NN.txt` and add cleanup to the test command: `OUTBIN=$(mktemp ...) && ... && $OUTBIN; RC=$?; rm -f /tmp/lang_test_26_NN.txt $OUTBIN; echo $RC`. Alternatively, use `mktemp` to generate the path at test time — but .flt tests are static. Use a fixed, known-safe path and clean up in the command line.

## Sources

### Primary (HIGH confidence)

- Direct code reading: `lang_runtime.c` — LangString struct layout, GC_malloc pattern, lang_throw usage, lang_hashtable_get error throwing
- Direct code reading: `Elaboration.fs` — externalFuncs list structure (both occurrences), elaborateExpr arm patterns for hashtable_containsKey (bool return), hashtable_set (void two-arg), hashtable_create (one-arg), print/println (void + stdio), failwith (void + noreturn)
- Direct code reading: `MlirIR.fs` — LlvmCallOp, LlvmCallVoidOp, ExternalFuncDecl type definitions
- Direct code reading: `FunLang/Eval.fs` — read_file error behavior (throws on missing file), write_file/append_file behavior, eprint/eprintln stderr behavior
- Direct code reading: `FunLang/TypeCheck.fs` — type signatures for all 6 builtins

### Secondary (MEDIUM confidence)

- POSIX stdio.h `fopen`/`fread`/`fwrite`/`fclose`/`fseek`/`ftell` — standard C, stable since C89
- `fwrite` for eprint vs `fprintf` — fwrite is more direct for binary strings with known length

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — entire stack is in-repo code I read directly
- Architecture: HIGH — patterns copied directly from working examples in the same codebase
- Pitfalls: HIGH — identified from direct code inspection of the two externalFuncs lists, pattern ordering, and C file handle semantics
- Open questions: MEDIUM — write_file error semantics are genuinely unspecified in requirements

**Research date:** 2026-03-27
**Valid until:** Stable — this is an internal codebase. Valid until Elaboration.fs architecture changes.
