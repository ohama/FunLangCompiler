# Phase 31: String, Char & IO Builtins - Research

**Researched:** 2026-03-29
**Domain:** FunLangCompiler builtin implementation (lang_runtime.c + Elaboration.fs)
**Confidence:** HIGH

## Summary

Phase 31 adds 13 new builtins: 4 string operations (string_endswith, string_startswith, string_trim, string_concat_list), 6 char predicates/transformers (char_is_digit, char_to_upper, char_is_letter, char_is_upper, char_is_lower, char_to_lower), and 1 IO function (eprintfn). Each builtin follows the same three-layer pattern: C runtime function in lang_runtime.c, elaboration pattern match in Elaboration.fs, and external function declaration in the two `externalFuncs` lists.

The LangThree interpreter (../LangThree/src/LangThree/Eval.fs) is the reference implementation. All 13 builtins are defined there. The string builtins delegate to .NET string methods (EndsWith, StartsWith, Trim, String.Join). Char builtins delegate to System.Char static methods. `eprintfn` is a format-string function like `printfn` but writing to stderr. The backend currently supports `eprint`/`eprintln` (plain string, no format args) but not `eprintfn` (format string with %d/%s/%b specifiers).

The key implementation decision for `eprintfn` is scope: the interpreter's `eprintfn` is variadic (curried) with format specifiers. For this phase, the simplest correct approach is to implement `eprintfn` as a single-string variant (same as `eprintln` but with format string support for zero-arg case `eprintfn "message"`), or to restrict to the most common usage pattern seen in FunLexYacc: `eprintfn "%s" someString` — one `%s` specifier. The feature request file confirms the only FunLexYacc usage is `eprintfn "%s" (formatError err)` (one `%s` arg).

**Primary recommendation:** Implement all string/char builtins as C runtime calls following the `lang_string_contains` / `lang_eprint` pattern. Implement `eprintfn` as a two-arg builtin: format string (with exactly one `%s` specifier) + string value, emitting `lang_eprint(formatted)` + `lang_eprint("\n")`.

## Standard Stack

The established tools for this codebase:

### Core
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| lang_runtime.c | src/FunLangCompiler.Compiler/lang_runtime.c | C runtime library | All builtins that need C stdlib calls live here |
| lang_runtime.h | src/FunLangCompiler.Compiler/lang_runtime.h | Header declarations | Declares LangString typedef and all runtime function signatures |
| Elaboration.fs | src/FunLangCompiler.Compiler/Elaboration.fs | AST-to-MLIR translation | Pattern-matches on App(Var("builtin_name")) nodes, emits MLIR ops |

### Supporting
| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| GC_malloc | Boehm GC | Heap allocation | All LangString* allocations in runtime.c |
| LlvmCallOp | MlirIR.fs | Emit a C call that returns a value | String-returning builtins |
| LlvmCallVoidOp | MlirIR.fs | Emit a C call with void return | IO builtins (eprint, eprintln) |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| C runtime function for char builtins | Inline MLIR arithmetic | Arithmetic is feasible (char is i64), but C stdlib makes intent clear and matches existing pattern |
| C runtime for eprintfn | Direct fprintf MLIR call | fprintf is variadic; using lang_eprintln is simpler and consistent |

## Architecture Patterns

### Pattern 1: One-arg string → string builtin (string_trim)
**What:** Single LangString* in, single LangString* out.
**When to use:** string_trim, string_endswith (inner lambda), string_startswith (inner lambda)
**Example (modeled on lang_string_sub):**
```c
// In lang_runtime.c
LangString* lang_string_trim(LangString* s) {
    int64_t start = 0;
    int64_t end = s->length - 1;
    while (start <= end && (s->data[start] == ' ' || s->data[start] == '\t' ||
           s->data[start] == '\n' || s->data[start] == '\r')) start++;
    while (end >= start && (s->data[end] == ' ' || s->data[end] == '\t' ||
           s->data[end] == '\n' || s->data[end] == '\r')) end--;
    int64_t len = end - start + 1;
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, s->data + start, (size_t)len);
    buf[len] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->length = len;
    r->data = buf;
    return r;
}
```
```fsharp
// In Elaboration.fs, matches: App(Var("string_trim"), strExpr)
| App (Var ("string_trim", _), strExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, strOps @ [LlvmCallOp(result, "@lang_string_trim", [strVal])])
```

### Pattern 2: Two-arg (string, string) → bool builtin (string_endswith, string_startswith)
**What:** Two LangString* args, returns i64 (0 or 1), wrapped in bool.
**When to use:** string_endswith, string_startswith (mirror of string_contains)
**Example (modeled exactly on string_contains elaboration):**
```c
// In lang_runtime.c
int64_t lang_string_endswith(LangString* s, LangString* suffix) {
    if (suffix->length > s->length) return 0;
    int64_t offset = s->length - suffix->length;
    return memcmp(s->data + offset, suffix->data, (size_t)suffix->length) == 0 ? 1 : 0;
}

int64_t lang_string_startswith(LangString* s, LangString* prefix) {
    if (prefix->length > s->length) return 0;
    return memcmp(s->data, prefix->data, (size_t)prefix->length) == 0 ? 1 : 0;
}
```
```fsharp
// In Elaboration.fs — same structure as string_contains (lines 789-800)
| App (App (Var ("string_endswith", _), strExpr, _), suffixExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let (sufVal, sufOps) = elaborateExpr env suffixExpr
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_string_endswith", [strVal; sufVal])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, strOps @ sufOps @ ops)
```

### Pattern 3: Two-arg (string, list) → string builtin (string_concat_list)
**What:** LangString* separator + LangCons* list of LangString*, returns LangString*.
**When to use:** string_concat_list only
**Example:**
```c
// In lang_runtime.c
LangString* lang_string_concat_list(LangString* sep, LangCons* list) {
    // Two passes: first measure total length, then fill
    int64_t total = 0;
    int64_t count = 0;
    LangCons* cur = list;
    while (cur != NULL) {
        LangString* item = (LangString*)(uintptr_t)cur->head;
        total += item->length;
        count++;
        cur = cur->tail;
    }
    if (count > 1) total += sep->length * (count - 1);
    char* buf = (char*)GC_malloc((size_t)(total + 1));
    int64_t pos = 0;
    cur = list;
    int64_t i = 0;
    while (cur != NULL) {
        if (i > 0 && sep->length > 0) {
            memcpy(buf + pos, sep->data, (size_t)sep->length);
            pos += sep->length;
        }
        LangString* item = (LangString*)(uintptr_t)cur->head;
        memcpy(buf + pos, item->data, (size_t)item->length);
        pos += item->length;
        cur = cur->tail;
        i++;
    }
    buf[total] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->length = total;
    r->data = buf;
    return r;
}
```
```fsharp
// In Elaboration.fs — two-arg curried call, same as string_contains
| App (App (Var ("string_concat_list", _), sepExpr, _), listExpr, _) ->
    let (sepVal,  sepOps)  = elaborateExpr env sepExpr
    let (listVal, listOps) = elaborateExpr env listExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, sepOps @ listOps @ [LlvmCallOp(result, "@lang_string_concat_list", [sepVal; listVal])])
```

### Pattern 4: One-arg char → bool predicate (char_is_digit, char_is_letter, char_is_upper, char_is_lower)
**What:** i64 char code in, i1 bool result out. Char is i64 in the backend.
**When to use:** All four char_is_* builtins
**Example:**
```c
// In lang_runtime.c — uses <ctype.h>
int64_t lang_char_is_digit(int64_t c) {
    return isdigit((int)c) ? 1 : 0;
}
int64_t lang_char_is_letter(int64_t c) {
    return isalpha((int)c) ? 1 : 0;
}
int64_t lang_char_is_upper(int64_t c) {
    return isupper((int)c) ? 1 : 0;
}
int64_t lang_char_is_lower(int64_t c) {
    return islower((int)c) ? 1 : 0;
}
```
```fsharp
// In Elaboration.fs — mirror of the string_contains bool-wrapping pattern
| App (Var ("char_is_digit", _), charExpr, _) ->
    let (charVal, charOps) = elaborateExpr env charExpr
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_char_is_digit", [charVal])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, charOps @ ops)
```

### Pattern 5: One-arg char → char transformer (char_to_upper, char_to_lower)
**What:** i64 char code in, i64 char code out.
**When to use:** char_to_upper, char_to_lower
**Example:**
```c
// In lang_runtime.c
int64_t lang_char_to_upper(int64_t c) {
    return (int64_t)toupper((int)c);
}
int64_t lang_char_to_lower(int64_t c) {
    return (int64_t)tolower((int)c);
}
```
```fsharp
// In Elaboration.fs — same as char_to_int (pure pass-through with renaming)
| App (Var ("char_to_upper", _), charExpr, _) ->
    let (charVal, charOps) = elaborateExpr env charExpr
    let result = { Name = freshName env; Type = I64 }
    (result, charOps @ [LlvmCallOp(result, "@lang_char_to_upper", [charVal])])
```

### Pattern 6: eprintfn as two-arg (format string, arg) → unit
**What:** eprintfn takes a static format string (e.g., `"%s"`) and one argument, calls lang_eprintln.
**When to use:** IO-01 (eprintfn)
**Scope decision:** FunLexYacc only uses `eprintfn "%s" str`. Implement as `eprintfn "%s" str` = `eprintln str` (desugar in elaboration, zero new runtime code).
**Example:**
```fsharp
// In Elaboration.fs — desugar eprintfn "%s" str as eprintln str
| App (App (Var ("eprintfn", _), String (fmt, _), _), argExpr, _)
    when fmt = "%s" ->
    let (argVal, argOps) = elaborateExpr env argExpr
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [LlvmCallVoidOp("@lang_eprintln", [argVal]); ArithConstantOp(unitVal, 0L)]
    (unitVal, argOps @ ops)

// Zero-arg case: eprintfn "literal message" ()
| App (Var ("eprintfn", _), String (fmt, _), _) ->
    // Emit lang_eprintln with a new string literal (fmt + no trailing newline needed — lang_eprintln adds \n)
    elaborateExpr env (App(Var("eprintln", Ast.unknownSpan), String(fmt, Ast.unknownSpan), Ast.unknownSpan))
```

### Anti-Patterns to Avoid
- **Using `fprintf(stderr, ...)` directly from MLIR:** Variadic C calls from MLIR require IsVarArg=true and are fragile. Use `lang_eprintln` which already handles fwrite + fputc('\n') + fflush.
- **Allocating char builtins as LangString:** Char is i64 throughout the backend. char_to_upper/char_to_lower take i64 and return i64 — no struct allocation needed.
- **Forgetting the bool-wrapping for _is_ predicates:** The `string_contains` pattern emits `LlvmCallOp(rawResult, ...)` → `ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)`. All four char_is_* and two string_ends/starts must do the same to get I1 type.

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Whitespace trimming | Custom loop in elaboration | lang_string_trim in C | Already have ctype.h in runtime.c; consistent allocation via GC_malloc |
| Char case test | Inline MLIR comparison ops | lang_char_is_*/to_* calling ctype.h | Locale-aware ctype handles edge cases; consistent with existing pattern |
| String suffix/prefix check | Manual string_sub + compare | lang_string_endswith / lang_string_startswith | memcmp is correct and off-by-one safe |

**Key insight:** Every builtin that touches memory must go through GC_malloc (not malloc/calloc) so the Boehm GC can track it.

## Common Pitfalls

### Pitfall 1: External function declarations duplicated
**What goes wrong:** The `externalFuncs` list appears TWICE in Elaboration.fs — once for expression-only programs (around line 2677) and once for full declaration programs (around line 2867). Adding a new C function to only one copy causes "undefined external function" in one mode.
**Why it happens:** The two `elaborate` function overloads (for Expr vs Decl list) each build their own module.
**How to avoid:** After adding to line ~2727 block, also add to line ~2918 block. Search for `@lang_eprintln` to find both insertion points.
**Warning signs:** E2E test passes for expression-only tests but fails for tests with `let` declarations at top level.

### Pitfall 2: string_concat_list list elements are LangString* stored as i64
**What goes wrong:** LangCons.head is `int64_t`. A LangString* pointer is stored as `(int64_t)(uintptr_t)ptr`. When iterating the list in `lang_string_concat_list`, failing to cast back to `LangString*` yields garbage data.
**Why it happens:** LangCons uses `int64_t head` for all values. Pointer types are stored as integers and cast on use. See `lang_read_lines` (line 550) and `lang_write_lines` (line 564) for the established pattern.
**How to avoid:** Always cast: `LangString* item = (LangString*)(uintptr_t)cur->head;`
**Warning signs:** Segfault or garbage output when string_concat_list is called on a non-empty list.

### Pitfall 3: Bool return type mismatch for predicate builtins
**What goes wrong:** Char predicates and string predicates return `int64_t` from C (0 or 1) but the elaboration must produce `I1` (MLIR bool). If the elaboration arm returns the raw `rawResult` with `Type = I64` instead of the `boolResult` with `Type = I1`, downstream `if`/`match` expressions break.
**Why it happens:** `ArithCmpIOp` is needed to convert i64 0/1 to i1.
**How to avoid:** Follow `string_contains` exactly: emit `LlvmCallOp(rawResult, ...)` then `ArithConstantOp(zeroVal, 0L)` then `ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)` and return `boolResult`.
**Warning signs:** Compilation error in later MLIR lowering phases; or predicates always evaluate as false/true.

### Pitfall 4: eprintfn test must redirect stderr
**What goes wrong:** E2E tests compare stdout only. If an `eprintfn` test does not redirect stderr with `2>/dev/null`, the stderr output leaks into the test runner's output comparison.
**Why it happens:** The test command captures stdout only. See `26-06-eprint.flt` and `26-07-eprintln.flt` — both use `$OUTBIN 2>/dev/null`.
**How to avoid:** All `eprintfn` E2E tests must use `$OUTBIN 2>/dev/null` in the command line.
**Warning signs:** Test expected output doesn't match actual output even though logic is correct.

### Pitfall 5: ctype.h not included in lang_runtime.c
**What goes wrong:** `isdigit`, `isalpha`, `isupper`, `islower`, `toupper`, `tolower` are from `<ctype.h>`. If not included, compiles with warnings and may silently misbehave.
**Why it happens:** lang_runtime.c currently includes `<stdint.h>`, `<string.h>`, `<stdio.h>`, `<stdlib.h>`, `<unistd.h>`, `<dirent.h>`. It does NOT include `<ctype.h>`.
**How to avoid:** Add `#include <ctype.h>` to lang_runtime.c at the top.
**Warning signs:** Compiler warning about implicit declaration of `isdigit`; incorrect results for non-ASCII chars.

## Code Examples

### lang_string_contains (existing reference — use as template)
```c
// Source: lang_runtime.c line 74
int64_t lang_string_contains(LangString* s, LangString* sub) {
    if (sub->length == 0) return 1;
    return strstr(s->data, sub->data) != NULL ? 1 : 0;
}
```
```fsharp
// Source: Elaboration.fs line 789
| App (App (Var ("string_contains", _), strExpr, _), subExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let (subVal, subOps) = elaborateExpr env subExpr
    let rawResult  = { Name = freshName env; Type = I64 }
    let zeroVal    = { Name = freshName env; Type = I64 }
    let boolResult = { Name = freshName env; Type = I1 }
    let ops = [
        LlvmCallOp(rawResult, "@lang_string_contains", [strVal; subVal])
        ArithConstantOp(zeroVal, 0L)
        ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
    ]
    (boolResult, strOps @ subOps @ ops)
```

### eprint/eprintln (existing reference — use for eprintfn)
```fsharp
// Source: Elaboration.fs line 1160
| App (Var ("eprint", _), strExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = [LlvmCallVoidOp("@lang_eprint", [strVal]); ArithConstantOp(unitVal, 0L)]
    (unitVal, strOps @ ops)
```

### External function declaration template
```fsharp
// Source: Elaboration.fs line ~2718
{ ExtName = "@lang_eprint";       ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_eprintln";     ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
// New entries to add:
{ ExtName = "@lang_string_endswith";   ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_string_startswith"; ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_string_trim";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_string_concat_list";ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_char_is_digit";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_char_to_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_char_is_letter";    ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_char_is_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_char_is_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_char_to_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
```

### E2E test template for bool-returning builtins
```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
if string_endswith "hello.fun" ".fun" then 1 else 0
// --- Output:
1
0
```

### E2E test template for eprintfn (stderr redirect required)
```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN 2>/dev/null; echo $?; rm -f $OUTBIN'
// --- Input:
let _ = eprintfn "%s" "error msg" in println "ok"
// --- Output:
ok
0
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No char predicates in backend | All 6 char builtins via C stdlib (ctype.h) | Phase 31 | Enables char classification in compiled programs |
| No string_endswith/startswith | lang_string_endswith/startswith via memcmp | Phase 31 | Enables suffix/prefix checks without string_sub workaround |
| No string_trim | lang_string_trim stripping whitespace | Phase 31 | Needed for GrammarParser.fun and YaccEmit.fun |
| No string_concat_list | lang_string_concat_list over LangCons* | Phase 31 | Enables DfaMin.fun String.concat usage |
| No eprintfn | eprintfn desugared to lang_eprintln in Elaboration.fs | Phase 31 | Enables Diagnostics.fun compilation |

## Open Questions

1. **eprintfn scope: single `%s` only, or full format mini-language?**
   - What we know: Only FunLexYacc usage is `eprintfn "%s" (formatError err)` — one `%s` arg. The feature request marks it MEDIUM effort, P1.
   - What's unclear: Whether any E2E test should exercise `eprintfn "%d" 42` (int arg).
   - Recommendation: Implement the `%s` case (desugar to `eprintln`) plus a static zero-arg case (`eprintfn "literal"` = `eprintln "literal"`). Mark other specifiers as future work. This is enough to pass any test grounded in FunLexYacc usage.

2. **string_concat_list: list elements are i64 (pointer-as-int)?**
   - What we know: LangCons.head is `int64_t`. When the list contains strings, each head is a pointer cast to int64, as confirmed by `lang_read_lines`/`lang_write_lines` pattern.
   - What's unclear: Whether Elaboration.fs needs to emit a ptrtoint coercion before passing the list to the C function, or if the Ptr value already flows as I64 through cons cells.
   - Recommendation: The C function receives a `LangCons*` (passed as Ptr from elaboration). The individual items are retrieved by casting `cur->head` to `LangString*`. No special handling in Elaboration.fs needed.

## Sources

### Primary (HIGH confidence)
- `src/FunLangCompiler.Compiler/lang_runtime.c` — all existing C runtime patterns examined
- `src/FunLangCompiler.Compiler/lang_runtime.h` — LangString typedef, all declared functions
- `src/FunLangCompiler.Compiler/Elaboration.fs` — string_contains (line 789), char_to_int (line 1225), eprint (line 1160), eprintln (line 1167), external declarations (lines 2677-2729 and 2867-2919)
- `../LangThree/src/LangThree/Eval.fs` — reference implementations of all 13 new builtins (lines 237-388)
- `tests/compiler/26-06-eprint.flt`, `26-07-eprintln.flt` — E2E test pattern for stderr builtins
- `tests/compiler/14-03-string-contains.flt` — E2E test pattern for bool-returning two-arg string builtin

### Secondary (MEDIUM confidence)
- `langbackend-feature-requests.md` — scope and priority of eprintfn (Feature 29, line 848)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — examined all relevant source files directly
- Architecture: HIGH — patterns derived from working existing builtins in the same codebase
- Pitfalls: HIGH — identified from code inspection (duplicate externalFuncs, ctype.h missing, LangCons head casting)

**Research date:** 2026-03-29
**Valid until:** 2026-04-29 (stable codebase; patterns don't change between phases)
