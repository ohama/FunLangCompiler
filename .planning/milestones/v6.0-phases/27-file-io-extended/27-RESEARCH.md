# Phase 27: File I/O Extended - Research

**Researched:** 2026-03-27
**Domain:** C runtime extension + MLIR elaboration for extended file I/O and system builtins
**Confidence:** HIGH

## Summary

Phase 27 adds eight builtins to the FunLangCompiler compiler: `read_lines`, `write_lines`, `stdin_read_line`, `stdin_read_all`, `get_env`, `get_cwd`, `path_combine`, and `dir_files`. This is a direct continuation of Phase 26 and follows the identical three-layer pattern: C helper in `lang_runtime.c`, declaration in `lang_runtime.h`, `ExternalFuncDecl` in both `externalFuncs` lists in `Elaboration.fs`, and pattern-match arms in `elaborateExpr`. All eight builtins are already implemented in LangThree's `Eval.fs` (reference interpreter), so exact behavior is known.

The primary new complexity versus Phase 26 is list-handling. Three builtins return `string list` (`read_lines`, `dir_files`) or consume one (`write_lines`). In the MLIR ABI, a `string list` is a `LangCons*` linked list where each `cell->head` (an `int64_t`) stores a `LangString*` cast to `int64_t`. This is the same pointer-cast-to-i64 pattern used by `lang_hashtable_keys` (which stores keys as `int64_t`) — except here the head value is a pointer. The second new complexity is new POSIX headers: `<dirent.h>` for `dir_files` and `<unistd.h>` for `get_cwd` must be added to `lang_runtime.c`.

All type signatures are locked in `LangThree/TypeCheck.fs`. The reference implementations are locked in `LangThree/Eval.fs`. The externalFuncs pattern, elaboration arm patterns, and test format are directly copied from Phase 26.

**Primary recommendation:** Implement all eight builtins as C functions in `lang_runtime.c`, add their declarations to `lang_runtime.h`, register them in both `externalFuncs` lists in `Elaboration.fs`, add pattern-match arms in `elaborateExpr`, and add E2E `.flt` tests following the Phase 26 test format.

## Standard Stack

This phase uses no external libraries. All implementation is in the existing project stack.

### Core

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| lang_runtime.c | (in-repo) | C runtime helper functions | All builtins live here; GC_malloc for all allocations |
| lang_runtime.h | (in-repo) | Declarations for new C functions | Required so clang can compile the .c file |
| Elaboration.fs | (in-repo) | F# MLIR code generation | Pattern-match arms dispatch builtins to C calls |
| Boehm GC | system | Memory allocation | All heap objects use GC_malloc (never malloc/free) |
| POSIX stdio.h | system | fgets/fread for stdin | Standard C, already included |
| POSIX stdlib.h | system | getenv for get_env | Standard C, already included |
| POSIX unistd.h | system | getcwd for get_cwd | Must add include to lang_runtime.c |
| POSIX dirent.h | system | opendir/readdir/closedir for dir_files | Must add include to lang_runtime.c |

### Supporting

| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| MlirIR.fs LlvmCallOp | (in-repo) | Call C function returning value (Ptr/I64) | read_lines, stdin_read_line, stdin_read_all, get_env, get_cwd, path_combine, dir_files |
| MlirIR.fs LlvmCallVoidOp | (in-repo) | Call C function returning void, emit unit 0 | write_lines |
| lang_throw | (in-repo) | Throw catchable exception (LangString* message) | read_lines file-not-found, get_env missing var, dir_files dir-not-found |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| getcwd() for get_cwd | realpath(NULL, NULL) | getcwd is simpler and matches Directory.GetCurrentDirectory() behavior |
| opendir/readdir for dir_files | glob() or scandir() | opendir/readdir is POSIX, available on both macOS and Linux without extra flags |
| fgets loop for stdin_read_line | getline() (GNU extension) | fgets is strictly POSIX, no GNU dependency |
| dynamic buffer for stdin_read_all | fixed 64KB buffer | Dynamic realloc loop is correct; fixed buffer is wrong for large inputs — see Eval.fs which reads all of Console.In |

**Installation:** No packages needed. All components are in-repo or system libraries already linked.

## Architecture Patterns

### Recommended Project Structure

No new files needed. Changes span three existing files plus new test files:

```
src/FunLangCompiler.Compiler/
├── lang_runtime.c    # ADD: 8 new C functions + 2 new #include headers
├── lang_runtime.h    # ADD: declarations for the 8 new C functions
└── Elaboration.fs    # ADD: 8 elaborateExpr arms + ExternalFuncDecl entries in both lists
tests/compiler/
└── 27-NN-*.flt       # ADD: E2E tests for each requirement
```

### Pattern 1: String List Return (LangCons* with LangString* heads)

**What:** C function builds a forward `LangCons*` linked list where `cell->head` stores a `LangString*` pointer cast to `int64_t`. The elaboration side is identical to `hashtable_keys` (returns `Ptr`).
**When to use:** `read_lines` and `dir_files`.

The key insight: `LangCons.head` is `int64_t`. On 64-bit systems `sizeof(void*) == sizeof(int64_t)`, so storing a `LangString*` in `cell->head` via a cast is valid. The MLIR cons-list iteration code already treats `Ptr` heads this way (see how `array_to_list` and `hashtable_keys` results are consumed by list pattern matching).

```c
// Source: lang_runtime.c — pattern for read_lines
// Builds list in forward order using cursor technique (same as lang_range).
LangCons* lang_read_lines(LangString* path) {
    FILE* f = fopen(path->data, "r");
    if (f == NULL) {
        // throw catchable exception — same pattern as lang_file_read
        const char* msg_prefix = "read_lines: file not found: ";
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
    LangCons* head = NULL;
    LangCons** cursor = &head;
    char line_buf[4096];
    while (fgets(line_buf, sizeof(line_buf), f) != NULL) {
        // Strip trailing newline
        int64_t len = (int64_t)strlen(line_buf);
        if (len > 0 && line_buf[len-1] == '\n') {
            line_buf[--len] = '\0';
            // Also strip \r on Windows-style files
            if (len > 0 && line_buf[len-1] == '\r') line_buf[--len] = '\0';
        }
        // Allocate LangString for this line
        char* data = (char*)GC_malloc((size_t)(len + 1));
        memcpy(data, line_buf, (size_t)(len + 1));
        LangString* s = (LangString*)GC_malloc(sizeof(LangString));
        s->length = len;
        s->data = data;
        // Append cons cell — head stores LangString* cast to int64_t
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = (int64_t)(uintptr_t)s;
        cell->tail = NULL;
        *cursor = cell;
        cursor = &cell->tail;
    }
    fclose(f);
    return head;
}
```

Elaboration arm (identical to `hashtable_keys` and `array_to_list` — one-arg, returns Ptr):
```fsharp
// Source: Elaboration.fs — mirrors hashtable_keys pattern
| App (Var ("read_lines", _), pathExpr, _) ->
    let (pathVal, pathOps) = elaborateExpr env pathExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, pathOps @ [LlvmCallOp(result, "@lang_read_lines", [pathVal])])
```

### Pattern 2: String List Input (write_lines iterates LangCons* to write strings)

**What:** C function takes a `LangCons*` (passed as `Ptr`) and iterates it, casting each `cell->head` from `int64_t` back to `LangString*`.
**When to use:** `write_lines`.

```c
// Source: lang_runtime.c
void lang_write_lines(LangString* path, LangCons* lines) {
    FILE* f = fopen(path->data, "w");
    if (f == NULL) return; /* silently fail — matches write_file behavior */
    LangCons* cur = lines;
    while (cur != NULL) {
        LangString* s = (LangString*)(uintptr_t)cur->head;
        fwrite(s->data, 1, (size_t)s->length, f);
        fputc('\n', f);
        cur = cur->tail;
    }
    fclose(f);
}
```

Elaboration arm (two-arg void — same pattern as `write_file`):
```fsharp
// Source: Elaboration.fs — mirrors write_file two-arg void pattern
| App (App (Var ("write_lines", _), pathExpr, _), linesExpr, _) ->
    let (pathVal,  pathOps)  = elaborateExpr env pathExpr
    let (linesVal, linesOps) = elaborateExpr env linesExpr
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [LlvmCallVoidOp("@lang_write_lines", [pathVal; linesVal]); ArithConstantOp(unitVal, 0L)]
    (unitVal, pathOps @ linesOps @ ops)
```

ExternalFuncDecl for write_lines (note: Ptr for both path and lines):
```fsharp
{ ExtName = "@lang_write_lines"; ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
```

### Pattern 3: Unit-Arg Builtin Returning String (stdin_read_line, stdin_read_all, get_cwd)

**What:** C function takes no args (unit is discarded in elaboration). Elaboration uses `hashtable_create` pattern: elaborate unit arg for side-effects, discard result, call C function with no args.
**When to use:** `stdin_read_line`, `stdin_read_all`, `get_cwd`.

```c
// Source: lang_runtime.c
LangString* lang_stdin_read_line(void) {
    // Dynamic buffer to avoid fixed-size limitations
    int64_t cap = 256;
    char* buf = (char*)GC_malloc((size_t)cap);
    int64_t len = 0;
    int c;
    while ((c = fgetc(stdin)) != EOF && c != '\n') {
        if (len + 1 >= cap) {
            cap *= 2;
            char* newbuf = (char*)GC_malloc((size_t)cap);
            memcpy(newbuf, buf, (size_t)len);
            buf = newbuf;
        }
        buf[len++] = (char)c;
    }
    buf[len] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

LangString* lang_stdin_read_all(void) {
    int64_t cap = 1024;
    char* buf = (char*)GC_malloc((size_t)cap);
    int64_t len = 0;
    int c;
    while ((c = fgetc(stdin)) != EOF) {
        if (len + 1 >= cap) {
            cap *= 2;
            char* newbuf = (char*)GC_malloc((size_t)cap);
            memcpy(newbuf, buf, (size_t)len);
            buf = newbuf;
        }
        buf[len++] = (char)c;
    }
    buf[len] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

LangString* lang_get_cwd(void) {
    char tmp[4096];
    if (getcwd(tmp, sizeof(tmp)) == NULL) {
        // getcwd failed — throw catchable exception
        const char* msg_str = "get_cwd: failed";
        int64_t msg_len = (int64_t)strlen(msg_str);
        char* buf = (char*)GC_malloc((size_t)(msg_len + 1));
        memcpy(buf, msg_str, (size_t)(msg_len + 1));
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = msg_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    int64_t len = (int64_t)strlen(tmp);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, tmp, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}
```

Elaboration arms (all three use the `hashtable_create` unit-arg pattern):
```fsharp
| App (Var ("stdin_read_line", _), unitExpr, _) ->
    let (_uVal, uOps) = elaborateExpr env unitExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, uOps @ [LlvmCallOp(result, "@lang_stdin_read_line", [])])

| App (Var ("stdin_read_all", _), unitExpr, _) ->
    let (_uVal, uOps) = elaborateExpr env unitExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, uOps @ [LlvmCallOp(result, "@lang_stdin_read_all", [])])

| App (Var ("get_cwd", _), unitExpr, _) ->
    let (_uVal, uOps) = elaborateExpr env unitExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, uOps @ [LlvmCallOp(result, "@lang_get_cwd", [])])
```

### Pattern 4: One-Arg Returning String with Error (get_env)

**What:** C function takes a `LangString*` path/name and returns `LangString*`. Throws on error. Same shape as `read_file`.
**When to use:** `get_env`.

```c
// Source: lang_runtime.c — mirrors lang_file_read single-arg string→string pattern
LangString* lang_get_env(LangString* varName) {
    const char* val = getenv(varName->data);
    if (val == NULL) {
        // Throw catchable exception — same as lang_file_read
        const char* msg_prefix = "get_env: variable '";
        const char* msg_suffix = "' not set";
        int64_t prefix_len = (int64_t)strlen(msg_prefix);
        int64_t suffix_len = (int64_t)strlen(msg_suffix);
        int64_t total_len = prefix_len + varName->length + suffix_len;
        char* buf = (char*)GC_malloc((size_t)(total_len + 1));
        memcpy(buf, msg_prefix, (size_t)prefix_len);
        memcpy(buf + prefix_len, varName->data, (size_t)varName->length);
        memcpy(buf + prefix_len + varName->length, msg_suffix, (size_t)(suffix_len + 1));
        LangString* msg = (LangString*)GC_malloc(sizeof(LangString));
        msg->length = total_len;
        msg->data = buf;
        lang_throw((void*)msg);
        return NULL; /* unreachable */
    }
    int64_t len = (int64_t)strlen(val);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, val, (size_t)(len + 1));
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}
```

Elaboration arm (identical shape to `read_file`):
```fsharp
| App (Var ("get_env", _), nameExpr, _) ->
    let (nameVal, nameOps) = elaborateExpr env nameExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, nameOps @ [LlvmCallOp(result, "@lang_get_env", [nameVal])])
```

### Pattern 5: Two-Arg Returning String (path_combine)

**What:** C function takes two `LangString*` arguments and returns a new `LangString*`. No error case. Same shape as `write_file` but returns Ptr instead of void.
**When to use:** `path_combine`.

```c
// Source: lang_runtime.c
LangString* lang_path_combine(LangString* dir, LangString* file) {
    // Use POSIX path.separator = '/'
    // If dir ends with '/' already, don't add another
    int add_sep = (dir->length > 0 && dir->data[dir->length-1] != '/') ? 1 : 0;
    int64_t total = dir->length + add_sep + file->length;
    char* buf = (char*)GC_malloc((size_t)(total + 1));
    memcpy(buf, dir->data, (size_t)dir->length);
    if (add_sep) buf[dir->length] = '/';
    memcpy(buf + dir->length + add_sep, file->data, (size_t)file->length);
    buf[total] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = total;
    s->data = buf;
    return s;
}
```

Elaboration arm (two-arg returning Ptr):
```fsharp
| App (App (Var ("path_combine", _), dirExpr, _), fileExpr, _) ->
    let (dirVal,  dirOps)  = elaborateExpr env dirExpr
    let (fileVal, fileOps) = elaborateExpr env fileExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, dirOps @ fileOps @ [LlvmCallOp(result, "@lang_path_combine", [dirVal; fileVal])])
```

### Pattern 6: dir_files (unit-to-string-list via opendir/readdir)

**What:** C function takes a `LangString*` directory path, opens it with `opendir`, iterates entries with `readdir`, skips `.` and `..`, builds a `LangCons*` list of `LangString*` filenames (not full paths, matching `Directory.GetFiles` on the last component).

**Important:** LangThree's `Eval.fs` uses `Directory.GetFiles(path)` which returns **full absolute paths**. The C implementation should match this — combine `path` + `/` + `d_name` to produce full paths (not bare filenames). Verify against `Eval.fs` line 415: `let files = System.IO.Directory.GetFiles(path)`.

```c
// Source: lang_runtime.c
#include <dirent.h>  // ADD to includes

LangCons* lang_dir_files(LangString* path) {
    DIR* dir = opendir(path->data);
    if (dir == NULL) {
        const char* msg_prefix = "dir_files: directory not found: ";
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
    LangCons* head = NULL;
    LangCons** cursor = &head;
    struct dirent* entry;
    while ((entry = readdir(dir)) != NULL) {
        // Skip . and ..
        if (entry->d_name[0] == '.' &&
            (entry->d_name[1] == '\0' || (entry->d_name[1] == '.' && entry->d_name[2] == '\0')))
            continue;
        // Skip non-regular files to match Directory.GetFiles behavior
        if (entry->d_type != DT_REG && entry->d_type != DT_UNKNOWN) continue;
        // Build full path: dir + "/" + d_name
        int64_t name_len = (int64_t)strlen(entry->d_name);
        int add_sep = (path->length > 0 && path->data[path->length-1] != '/') ? 1 : 0;
        int64_t full_len = path->length + add_sep + name_len;
        char* full = (char*)GC_malloc((size_t)(full_len + 1));
        memcpy(full, path->data, (size_t)path->length);
        if (add_sep) full[path->length] = '/';
        memcpy(full + path->length + add_sep, entry->d_name, (size_t)(name_len + 1));
        LangString* s = (LangString*)GC_malloc(sizeof(LangString));
        s->length = full_len;
        s->data = full;
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = (int64_t)(uintptr_t)s;
        cell->tail = NULL;
        *cursor = cell;
        cursor = &cell->tail;
    }
    closedir(dir);
    return head;
}
```

Elaboration arm (identical to `read_lines` — one-arg, returns Ptr):
```fsharp
| App (Var ("dir_files", _), pathExpr, _) ->
    let (pathVal, pathOps) = elaborateExpr env pathExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, pathOps @ [LlvmCallOp(result, "@lang_dir_files", [pathVal])])
```

### Pattern 7: ExternalFuncDecl Entries (Both Lists)

Both `externalFuncs` lists in `Elaboration.fs` (around lines 2376 and 2553) must be updated identically. Add after the existing `@lang_eprintln` entry:

```fsharp
{ ExtName = "@lang_read_lines";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_write_lines";      ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_stdin_read_line";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_stdin_read_all";   ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_get_env";          ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_get_cwd";          ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_path_combine";     ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_dir_files";        ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Anti-Patterns to Avoid

- **Fixed-size line buffer in read_lines:** Using a stack buffer like `char line_buf[4096]` truncates lines longer than 4095 bytes. Since these are GC-managed programs, consider this an acceptable tradeoff but document the limit. For Phase 27 this is acceptable given LangThree's `File.ReadAllLines` also has practical limits.
- **Using `realloc` instead of `GC_malloc`:** The GC owns all memory. Never call `malloc`/`realloc`/`free`. When growing a buffer, allocate a new `GC_malloc` block and `memcpy`.
- **Forgetting `d_type` check for dir_files:** `readdir` returns all directory entries including subdirectories and symlinks. `Directory.GetFiles` on .NET returns only files. Use `d_type == DT_REG` (regular file) check, with `DT_UNKNOWN` fallback for filesystems that don't set d_type.
- **Using empty dir path for get_cwd:** `getcwd` requires a buffer. The 4096-byte static buffer is sufficient for PATH_MAX (typically 4096 on Linux/macOS).
- **path_combine not matching System.IO.Path.Combine behavior:** `Path.Combine("a", "/b")` in .NET returns `/b` (absolute second path wins). For Phase 27, simple concatenation with separator is sufficient — the Eval.fs uses `Path.Combine` but the common case in tests is relative + relative. Document this deviation.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| String list cons cell building | Custom MLIR ops | C function returning LangCons* with Ptr heads | Matches lang_range and lang_hashtable_keys patterns exactly |
| Growing stdin read buffer | Fixed malloc + realloc | GC_malloc new block + memcpy | GC owns all memory; realloc is not GC-safe |
| Directory listing | stat() + opendir loop in MLIR | lang_dir_files C function | MLIR cannot express loops; dirent.h is POSIX |
| Environment variable lookup | getenv inline MLIR | lang_get_env C function | Error handling requires lang_throw which needs C frame |
| Path separator detection | Platform-specific MLIR | lang_path_combine C function (always '/') | macOS and Linux both use '/' |

**Key insight:** All eight builtins require either dynamic memory (reading unknown-length data), list construction, POSIX syscalls (getcwd, opendir), or error handling via lang_throw. All of these belong in C, not inline MLIR.

## Common Pitfalls

### Pitfall 1: Two externalFuncs Lists (same as Phase 26)

**What goes wrong:** Adding entries to only one of the two `externalFuncs` lists. Programs elaborated through one code path (module vs. bare expression) will get MLIR "undefined symbol" errors.
**Why it happens:** Elaboration.fs has two module-building functions with separate `externalFuncs` lists — around lines 2376 and 2553.
**How to avoid:** Search for `@lang_eprintln` (last Phase 26 entry) and update BOTH occurrences identically.
**Warning signs:** Some E2E tests pass, others fail with MLIR errors about undeclared symbols.

### Pitfall 2: string list head is Ptr cast to i64, not raw int64

**What goes wrong:** Writing `cell->head = (int64_t)s` (directly) without `(uintptr_t)` intermediate cast triggers compiler warnings or, on some compilers, incorrect behavior due to pointer-to-integer truncation warnings.
**Why it happens:** Direct pointer-to-integer casts are implementation-defined without the intermediate `uintptr_t` cast.
**How to avoid:** Always cast via `uintptr_t`: `cell->head = (int64_t)(uintptr_t)s`. This matches C standard rules for pointer-to-integer conversions.
**Warning signs:** Clang warnings about casting pointer to integer of different size.

### Pitfall 3: write_lines fwrite vs fputs

**What goes wrong:** Using `fputs(s->data, f)` for write_lines — this works for null-terminated strings but stops at embedded nulls. LangString may have embedded nulls (though unlikely in practice).
**Why it happens:** fputs uses null termination; fwrite uses explicit length.
**How to avoid:** Use `fwrite(s->data, 1, (size_t)s->length, f)` followed by `fputc('\n', f)` — same pattern as `lang_file_write`.
**Warning signs:** Truncated output for strings with embedded null bytes.

### Pitfall 4: stdin_read_all must read until EOF, not until newline

**What goes wrong:** Confusing `stdin_read_line` (read one line) with `stdin_read_all` (read all of stdin until EOF).
**Why it happens:** Both use fgetc loop; they differ in termination condition.
**How to avoid:** `stdin_read_line` terminates on `\n` OR `EOF`. `stdin_read_all` terminates only on `EOF`. Match LangThree's `Console.In.ReadToEnd()` vs `Console.In.ReadLine()`.
**Warning signs:** `stdin_read_all` tests end early; pipe tests with multi-line input only produce first line.

### Pitfall 5: dir_files order not guaranteed

**What goes wrong:** E2E tests that check exact ordering of `dir_files` output will be flaky — `readdir` does not return entries in alphabetical order.
**Why it happens:** Filesystem-dependent order from readdir. Linux ext4 returns hash order; macOS HFS+ returns creation order.
**How to avoid:** E2E tests for `dir_files` should only test length of result or presence of specific filenames, not order. Or sort in the test program: `let files = dir_files path in ...`.
**Warning signs:** E2E tests pass on one machine, fail on another.

### Pitfall 6: read_lines fgets 4096-byte limit

**What goes wrong:** Lines longer than 4095 bytes are truncated silently.
**Why it happens:** `fgets(buf, sizeof(buf), f)` reads at most sizeof(buf)-1 bytes per call.
**How to avoid:** Accept this limitation for Phase 27. The reference implementation (`File.ReadAllLines`) handles arbitrary line lengths, but for the compiled backend 4096 bytes per line is practical. Document the limit.
**Warning signs:** E2E tests with long lines produce truncated output.

### Pitfall 7: Elaboration arm ordering for unit-arg builtins

**What goes wrong:** If `stdin_read_line`, `stdin_read_all`, or `get_cwd` arms are placed after the general `App (funcExpr, argExpr, _)` catch-all, they are never reached.
**Why it happens:** F# pattern matching is first-match. The general App arm handles all unrecognized function applications.
**How to avoid:** Place all new arms BEFORE the general `App (funcExpr, argExpr, _)` arm — after the last Phase 26 builtin arm (eprintln).
**Warning signs:** "Unknown function" MLIR errors or wrong behavior at runtime.

## Code Examples

### E2E Test Format (from Phase 26)

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; RC=$?; rm -f /tmp/lang_test_27_NN.txt $OUTBIN; echo $RC'
// --- Input:
let path = "/tmp/lang_test_27_NN.txt" in
write_lines path ["hello"; "world"];
let lines = read_lines path in
match lines with
| [] -> println "empty"
| h :: _ -> println h
// --- Output:
hello
0
```

### E2E Test for stdin_read_line (pipe stdin)

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && echo "hello" | $OUTBIN; RC=$?; rm -f $OUTBIN; echo $RC'
// --- Input:
let line = stdin_read_line () in println line
// --- Output:
hello
0
```

### E2E Test for get_env

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && HOME=/testval $OUTBIN; RC=$?; rm -f $OUTBIN; echo $RC'
// --- Input:
let v = get_env "HOME" in println v
// --- Output:
/testval
0
```

### E2E Test for get_cwd

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && (cd /tmp && $OUTBIN); RC=$?; rm -f $OUTBIN; echo $RC'
// --- Input:
let cwd = get_cwd () in println cwd
// --- Output:
/tmp
0
```

### lang_runtime.h declaration additions

```c
// Add to lang_runtime.h before #endif
LangCons* lang_read_lines(LangString* path);
void      lang_write_lines(LangString* path, LangCons* lines);
LangString* lang_stdin_read_line(void);
LangString* lang_stdin_read_all(void);
LangString* lang_get_env(LangString* varName);
LangString* lang_get_cwd(void);
LangString* lang_path_combine(LangString* dir, LangString* file);
LangCons* lang_dir_files(LangString* path);
```

### New includes for lang_runtime.c

```c
// Add after existing #include <stdlib.h>
#include <unistd.h>   // getcwd
#include <dirent.h>   // opendir, readdir, closedir, struct dirent
```

## State of the Art

This is an internal compiler project with no external library evolution.

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No list-returning string builtins | LangCons* with Ptr-cast heads | Phase 27 | New pattern; follow lang_hashtable_keys precedent |
| No stdin/env/cwd/dir access | 8 new C builtins | Phase 27 | Adds POSIX includes to lang_runtime.c |
| No path manipulation | lang_path_combine | Phase 27 | Simple '/' concatenation, not full Path.Combine semantics |

**Deprecated/outdated:**
- None relevant to this phase.

## Open Questions

1. **dir_files: full paths vs. bare filenames**
   - What we know: LangThree `Eval.fs` line 415 uses `Directory.GetFiles(path)` which returns full absolute paths (e.g., `/tmp/dir/file.txt`, not just `file.txt`).
   - What's unclear: Do tests in Phase 27 rely on full paths or just filenames? The requirement (FIO-13) says "lists files in directory" without specifying path format.
   - Recommendation: Match LangThree behavior — return full paths (dir + "/" + d_name). If `dir` is relative, the result will be relative (e.g., `./dir/file.txt`). Tests should be written to extract the last component if needed.

2. **read_lines: trailing newline on last line**
   - What we know: `fgets` includes the `\n` in the buffer if present. `File.ReadAllLines` in .NET strips the `\n`. The C implementation strips `\n` (and `\r\n`) in the loop.
   - What's unclear: If the file ends without a trailing newline, `fgets` returns the last line without `\n` — this is handled correctly. If the file ends with a trailing newline, `File.ReadAllLines` does NOT add an empty string, but `fgets` would stop at EOF after consuming the newline.
   - Recommendation: The fgets loop naturally handles this correctly — the trailing newline is consumed in the last `fgets` call, and no extra empty string is added.

3. **path_combine: absolute second argument**
   - What we know: `System.IO.Path.Combine("a", "/b")` returns `/b` in .NET. The C implementation does simple concatenation.
   - What's unclear: Whether any test programs rely on the absolute-path-wins behavior.
   - Recommendation: Implement simple concatenation with `/` separator for Phase 27. If a second argument starts with `/`, the result will be `dir//absolute` which is valid POSIX (double slash is equivalent to single). Accept this divergence for now.

4. **dir_files: d_type not set (DT_UNKNOWN) on some filesystems**
   - What we know: Some filesystems (NFS, some ext2 variants) return `DT_UNKNOWN` for all entries, meaning you cannot skip directories by d_type alone.
   - What's unclear: Whether E2E tests rely on accurate DT_REG filtering.
   - Recommendation: For Phase 27, include `DT_UNKNOWN` entries in the result (don't skip them) to ensure correctness on all filesystems. This means `dir_files` may include subdirectory names. Tests should not rely on subdirectory exclusion.

## Sources

### Primary (HIGH confidence)

- Direct code reading: `LangThree/Eval.fs` lines 350-417 — exact implementations of all 8 builtins; error messages, edge cases (stdin EOF returns "", get_env null check, dir_files missing dir)
- Direct code reading: `LangThree/TypeCheck.fs` lines 84-104 — exact type signatures for all 8 builtins
- Direct code reading: `lang_runtime.c` — LangString struct, LangCons struct, existing patterns for lang_file_read (string return), lang_file_write (void two-arg), lang_hashtable_keys (LangCons* return), lang_range (cursor-based cons building)
- Direct code reading: `Elaboration.fs` lines 875-969 — exact elaboration patterns for array_to_list (one-arg Ptr return), hashtable_keys (one-arg Ptr return), write_file (two-arg void), hashtable_create (unit-arg)
- Direct code reading: `Elaboration.fs` lines 2376-2381 and 2553-2558 — both externalFuncs list locations for Phase 26 entries
- Direct code reading: Phase 26 `.flt` tests — test command format, temp file pattern, stdin pipe pattern for future reference

### Secondary (MEDIUM confidence)

- POSIX `dirent.h` `opendir`/`readdir`/`closedir` — standard POSIX, available on macOS and Linux without extra compiler flags
- POSIX `unistd.h` `getcwd` — standard POSIX, available on macOS and Linux without extra compiler flags
- `d_type` field in `struct dirent` — POSIX extension available on macOS and Linux; may be `DT_UNKNOWN` on some filesystems

### Tertiary (LOW confidence)

- `dir_files` ordering behavior — readdir order is filesystem-dependent; test design must account for this

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — entire stack is in-repo code read directly
- Architecture: HIGH — all patterns copied directly from working Phase 26 examples in the same codebase
- C function signatures: HIGH — derived from LangThree Eval.fs implementations
- Type signatures: HIGH — directly from TypeCheck.fs
- Pitfalls: HIGH — identified from code inspection (two externalFuncs lists, pointer casting, buffer sizing)
- Open questions: MEDIUM — dir_files path format and d_type behavior are genuinely ambiguous in requirements

**Research date:** 2026-03-27
**Valid until:** Stable — internal codebase. Valid until Elaboration.fs architecture changes.
