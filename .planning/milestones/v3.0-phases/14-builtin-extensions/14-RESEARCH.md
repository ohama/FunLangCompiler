# Phase 14: Builtin Extensions - Research

**Researched:** 2026-03-26
**Domain:** MLIR/LLVM builtin elaboration, C runtime helpers, libc string/char operations
**Confidence:** HIGH

## Summary

Phase 14 extends the FunLangCompiler compiler with seven new builtin operations (BLT-01..07). All
builtins follow the same pattern already established in Phases 7–8: special-case App(Var("name"),
arg) patterns in `elaborateExpr` (Elaboration.fs) that compile to LlvmCallOp or LlvmCallVoidOp
targeting C runtime helpers in `lang_runtime.c`.

The codebase already has the complete infrastructure: `LlvmCallOp`, `LlvmCallVoidOp`,
`LlvmGEPStructOp`, `LlvmStoreOp`, `LlvmLoadOp`, `addStringGlobal`, `GC_malloc`, and the LangString
C struct. No new MlirOp cases are needed. The only additions are: new C helper functions in
`lang_runtime.c`, new ExternalFuncDecl entries in `elaborateModule`, and new pattern match arms in
`elaborateExpr`.

**Primary recommendation:** Add C helpers to `lang_runtime.c` for `failwith`, `string_sub`,
`string_contains`, and `string_to_int`. Implement `char_to_int`/`int_to_char` as identity ops
(char is already i64 in the MLIR type system). BLT-07 (variable string print) is already
implemented in Phase 8 — verify it works, no changes needed.

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| libc fprintf | system | failwith stderr message | Standard C stderr output |
| libc exit | system | failwith exit(1) | Standard C process exit |
| libc strstr | system | string_contains substring test | Standard C substring search |
| libc strtol | system | string_to_int safe conversion | Handles errors better than atoi |
| libc memcpy | system | string_sub buffer copy | Already used in lang_string_concat |
| Boehm GC (libgc) | system | heap alloc for new string structs | Already integrated |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| GC_malloc | Boehm GC | Allocate LangString + char buffer | Every C helper that returns a new string |
| LlvmCallOp | MlirIR | Call C runtime functions | All builtins that return a value |
| LlvmCallVoidOp | MlirIR | Call void C runtime functions | failwith (exits, returns nothing) |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| strtol for string_to_int | atoi | atoi has no error handling; strtol detects non-numeric input |
| strstr for string_contains | manual loop | strstr is libc standard, one call |
| memcpy for string_sub | strncpy | memcpy is already used in runtime, explicit length is safer |

## Architecture Patterns

### Builtin Pattern: Single-Argument App

All single-argument builtins follow this pattern in `elaborateExpr`:

```fsharp
| App (Var ("builtin_name", _), argExpr, _) ->
    let (argVal, argOps) = elaborateExpr env argExpr
    let result = { Name = freshName env; Type = <ReturnType> }
    (result, argOps @ [LlvmCallOp(result, "@lang_builtin_name", [argVal])])
```

For void/noreturn builtins like `failwith`:

```fsharp
| App (Var ("failwith", _), msgExpr, _) ->
    let (msgVal, msgOps) = elaborateExpr env msgExpr
    // Load data ptr from string struct for fprintf
    let dataPtrVal  = { Name = freshName env; Type = Ptr }
    let dataAddrVal = { Name = freshName env; Type = Ptr }
    let unitVal     = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPStructOp(dataPtrVal, msgVal, 1)
        LlvmLoadOp(dataAddrVal, dataPtrVal)
        LlvmCallVoidOp("@lang_failwith", [dataAddrVal])
        ArithConstantOp(unitVal, 0L)   // unreachable, but SSA needs a value
    ]
    (unitVal, msgOps @ ops)
```

Note: After `lang_failwith` calls `exit(1)`, the `ArithConstantOp` is dead code but MLIR SSA
requires every arm to produce a value. The function never actually returns.

### Builtin Pattern: Three-Argument App (string_sub)

Curried application desugars to nested App nodes in the AST:
`string_sub s 6 5` → `App(App(App(Var("string_sub"), s), 6), 5)`

Match the outermost nesting:

```fsharp
| App (App (App (Var ("string_sub", _), strExpr, _), startExpr, _), lenExpr, _) ->
    let (strVal,   strOps)   = elaborateExpr env strExpr
    let (startVal, startOps) = elaborateExpr env startExpr
    let (lenVal,   lenOps)   = elaborateExpr env lenExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, strOps @ startOps @ lenOps @ [LlvmCallOp(result, "@lang_string_sub", [strVal; startVal; lenVal])])
```

### Builtin Pattern: Two-Argument App (string_contains)

`string_contains s sub` → `App(App(Var("string_contains"), s), sub)`

```fsharp
| App (App (Var ("string_contains", _), strExpr, _), subExpr, _) ->
    let (strVal, strOps) = elaborateExpr env strExpr
    let (subVal, subOps) = elaborateExpr env subExpr
    let result = { Name = freshName env; Type = I1 }
    (result, strOps @ subOps @ [LlvmCallOp(result, "@lang_string_contains", [strVal; subVal])])
```

`lang_string_contains` returns `int64_t` (0 or 1), stored as I1 in MLIR. Actually: return type
should be I64 (C int64_t) then truncated to I1, OR the C function can return int64_t and we store
as I64. The simplest approach: return I64 from C, treat as boolean in MLIR (0 = false, nonzero =
true). Use I64 result type and add ArithCmpIOp ne zero to get I1 if needed — but since existing
boolean operations in Elaboration.fs work with I1, we need to produce I1.

**Decision:** `lang_string_contains` returns `int64_t` (0 or 1). Elaborate to:
1. Call returning I64
2. `ArithConstantOp(zero, 0L : I64)`
3. `ArithCmpIOp(result, "ne", callResult, zero)` → I1

### Builtin Pattern: Identity (char_to_int, int_to_char)

Char literals are already elaborated as I64 (ASCII code point). `char_to_int` and `int_to_char`
are identity functions — no conversion needed:

```fsharp
| App (Var ("char_to_int", _), charExpr, _) ->
    elaborateExpr env charExpr   // char is already i64, no-op

| App (Var ("int_to_char", _), intExpr, _) ->
    elaborateExpr env intExpr    // int treated as char code point, no-op
```

However: the Char(c, _) AST node is NOT yet elaborated in Elaboration.fs (it falls through to the
catch-all `failwithf "unsupported expression"`). Phase 14 must add the `Char` elaboration case:

```fsharp
| Char (c, _) ->
    let v = { Name = freshName env; Type = I64 }
    (v, [ArithConstantOp(v, int64 (int c))])
```

### C Runtime Functions to Add

```c
// BLT-01: failwith — print to stderr and exit(1)
void lang_failwith(const char* msg) {
    fprintf(stderr, "%s\n", msg);
    exit(1);
}

// BLT-02: string_sub — extract substring [start, start+len)
LangString* lang_string_sub(LangString* s, int64_t start, int64_t len) {
    // Clamp to valid range
    if (start < 0) start = 0;
    if (start > s->length) start = s->length;
    if (len < 0) len = 0;
    if (start + len > s->length) len = s->length - start;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, s->data + start, (size_t)len);
    buf[len] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->length = len;
    r->data = buf;
    return r;
}

// BLT-03: string_contains — does s contain sub?
int64_t lang_string_contains(LangString* s, LangString* sub) {
    if (sub->length == 0) return 1;
    // strstr needs null-terminated strings — data is already null-terminated
    return strstr(s->data, sub->data) != NULL ? 1 : 0;
}

// BLT-04: string_to_int — parse string as integer
int64_t lang_string_to_int(LangString* s) {
    return (int64_t)strtol(s->data, NULL, 10);
}
```

### BLT-07: Variable String println — Already Implemented

Phase 8 already added the general `App(Var("print"|"println"), strExpr)` case to Elaboration.fs.
The case loads `data` field from the string struct and calls `@printf`. This IS the variable-string
print support. BLT-07 requires only verification via an E2E test.

### ExternalFuncDecl Additions in elaborateModule

```fsharp
{ ExtName = "@lang_failwith";         ExtParams = [Ptr];            ExtReturn = None;     IsVarArg = false }
{ ExtName = "@lang_string_sub";       ExtParams = [Ptr; I64; I64];  ExtReturn = Some Ptr; IsVarArg = false }
{ ExtName = "@lang_string_contains";  ExtParams = [Ptr; Ptr];       ExtReturn = Some I64; IsVarArg = false }
{ ExtName = "@lang_string_to_int";    ExtParams = [Ptr];            ExtReturn = Some I64; IsVarArg = false }
```

`char_to_int` and `int_to_char` are identity ops — no C function, no ExternalFuncDecl needed.

### Ordering Constraint in elaborateExpr

Multi-argument builtins must be placed BEFORE the general `App(funcExpr, argExpr)` case to prevent
the general case from consuming the first application and failing on the partial application. The
existing codebase already follows this: `string_concat` (two-arg) is before `print` (one-arg) is
before general `App`.

Required ordering for Phase 14 additions:
1. `App(App(App(Var("string_sub"), ...), ...), ...)` — three-arg, must be first
2. `App(App(Var("string_contains", ...), ...), ...)` — two-arg
3. `App(Var("failwith", ...), ...)`
4. `App(Var("string_to_int", ...), ...)`
5. `App(Var("char_to_int", ...), ...)`
6. `App(Var("int_to_char", ...), ...)`
7. (existing) `App(App(Var("string_concat"), ...), ...)` — already before print
8. (existing) print/println/to_string/string_length cases

All new cases go BEFORE the existing `string_concat` case (or at least before general App).

### Anti-Patterns to Avoid

- **Forgetting Char elaboration:** `char_to_int` depends on `Char(c, _)` being elaborated. If
  `Char` falls through to the catch-all, the test `char_to_int 'Z'` will fail. Add `Char` case
  alongside `char_to_int`.
- **lang_failwith not calling exit:** If the C function returns without calling `exit(1)`, MLIR
  will execute dead ArithConstantOp and return 0. Must verify `exit(1)` is called.
- **string_contains returning I1 from C:** The ExternalFuncDecl must say `Some I64` (C int64_t),
  not I1. MLIR I1 is a 1-bit type; calling a C function that returns `int` into an I1 is a type
  mismatch. Use I64 → ArithCmpIOp ne 0 → I1 conversion pattern, OR return I64 and use it directly
  as a boolean (existing `Equal` result is I1, but boolean branching needs I1). Safer: use I64 and
  emit a comparison to get I1.
- **Null termination in string_sub:** `s->data` is null-terminated (guaranteed by LangString
  construction), but `s->data + start` may not be if `start > 0`. The GC_malloc'd buffer copy
  approach handles this correctly — always add explicit `\0`.
- **string_sub start/len bounds:** Do NOT silently corrupt memory. Add bounds clamping in C.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Substring extraction | Inline MLIR GEP arithmetic | C helper lang_string_sub | Dynamic pointer arithmetic is verbose in MLIR |
| String search | MLIR loop | C strstr | libc handles null termination edge cases |
| String to int | Inline MLIR digit loop | C strtol | strtol handles sign, base, error correctly |
| Stderr output | MLIR fputs/write syscalls | C fprintf(stderr, ...) | C stdlib handles buffering, portability |

**Key insight:** Same principle as Phase 8 — C helpers are dramatically simpler than equivalent
MLIR text for operations involving pointer arithmetic or libc calls with complex semantics.

## Common Pitfalls

### Pitfall 1: Char literal not elaborated
**What goes wrong:** `char_to_int 'Z'` throws "Elaboration: unsupported expression Char('Z',...)"
**Why it happens:** `Char(c, _)` has no case in `elaborateExpr` currently
**How to avoid:** Add `| Char (c, _) ->` case that emits `ArithConstantOp(int64 (int c))`
**Warning signs:** F# runtime exception during elaboration, not MLIR validation error

### Pitfall 2: Multi-arg builtin caught by partial application
**What goes wrong:** `string_sub s 6 5` hits general `App` case with `App(App(Var("string_sub"),s),6)` as funcExpr, which is not in KnownFuncs → failwith "unsupported App"
**Why it happens:** General App case tries to look up `string_sub` as a func name, but funcExpr is `App(App(...))`, not `Var`
**How to avoid:** Place three-arg pattern `App(App(App(Var("string_sub"), ...), ...), ...)` BEFORE general App
**Warning signs:** "Elaboration: unsupported App" error at runtime

### Pitfall 3: lang_failwith ExternalFuncDecl return type
**What goes wrong:** MLIR emits a call to `@lang_failwith` expecting a result value (if ExtReturn = Some I64)
**Why it happens:** failwith never returns — must use `LlvmCallVoidOp` and ExtReturn = None
**How to avoid:** ExtReturn = None, use `LlvmCallVoidOp("@lang_failwith", [dataAddrVal])`, add dead `ArithConstantOp` after
**Warning signs:** MLIR error about unused result or type mismatch on call op

### Pitfall 4: string_contains boolean representation
**What goes wrong:** Using I1 as ExtReturn type for `@lang_string_contains` (C int → I1 mismatch)
**Why it happens:** MLIR I1 is 1-bit; C `int` is 32-bit; they don't match in MLIR's type system
**How to avoid:** ExtReturn = Some I64, then emit ArithCmpIOp ne zero to produce I1
**Warning signs:** MLIR validation error about type mismatch on func.call result

### Pitfall 5: freeVars catch-all for Char
**What goes wrong:** freeVars does not handle Char — hits conservative `| _ -> Set.empty` (acceptable, no variables in Char)
**How to avoid:** Char has no free variables, the catch-all is correct. No change needed here.

## Code Examples

### failwith Elaboration (MLIR output)

```mlir
// failwith "error message" where %msg is a LangString* (Ptr)
%t0 = llvm.getelementptr inbounds %msg[0, 1] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
%t1 = llvm.load %t0 : !llvm.ptr -> !llvm.ptr    // raw char* data
llvm.call @lang_failwith(%t1) : (!llvm.ptr) -> ()
%t2 = arith.constant 0 : i64    // dead code — process already exited
```

### string_sub Elaboration (MLIR output)

```mlir
// string_sub s 6 5 where %s : Ptr, %start : I64, %len : I64
%result = llvm.call @lang_string_sub(%s, %start, %len) : (!llvm.ptr, i64, i64) -> !llvm.ptr
// %result is LangString* for the substring
```

### string_contains Elaboration (MLIR output)

```mlir
// string_contains "hello world" "world" where %s, %sub : Ptr
%raw = llvm.call @lang_string_contains(%s, %sub) : (!llvm.ptr, !llvm.ptr) -> i64
%zero = arith.constant 0 : i64
%result = arith.cmpi ne, %raw, %zero : i64
// %result : i1  (true if sub found)
```

### char_to_int Elaboration

```fsharp
// Char 'Z' elaborates to:
| Char (c, _) ->
    let v = { Name = freshName env; Type = I64 }
    (v, [ArithConstantOp(v, int64 (int c))])   // 'Z' = 90

// char_to_int charExpr is identity:
| App (Var ("char_to_int", _), charExpr, _) ->
    elaborateExpr env charExpr
```

```mlir
// char_to_int 'Z'
%t0 = arith.constant 90 : i64
// return %t0 directly (identity — char_to_int is a no-op in MLIR)
```

### C Runtime Additions

```c
void lang_failwith(const char* msg) {
    fprintf(stderr, "%s\n", msg);
    exit(1);
}

LangString* lang_string_sub(LangString* s, int64_t start, int64_t len) {
    if (start < 0) start = 0;
    if (start > s->length) start = s->length;
    if (len < 0) len = 0;
    if (start + len > s->length) len = s->length - start;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    memcpy(buf, s->data + start, (size_t)len);
    buf[len] = '\0';
    LangString* r = (LangString*)GC_malloc(sizeof(LangString));
    r->length = len;
    r->data = buf;
    return r;
}

int64_t lang_string_contains(LangString* s, LangString* sub) {
    if (sub->length == 0) return 1;
    return strstr(s->data, sub->data) != NULL ? 1 : 0;
}

int64_t lang_string_to_int(LangString* s) {
    return (int64_t)strtol(s->data, NULL, 10);
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No failwith | lang_failwith via C helper | Phase 14 | Enables error handling |
| No substring | lang_string_sub via C helper | Phase 14 | Enables string manipulation |
| No string search | lang_string_contains via C helper | Phase 14 | Enables pattern search |
| No string parsing | lang_string_to_int via strtol | Phase 14 | Enables string→int conversion |
| No char ops | Char → i64 identity, char_to_int/int_to_char no-op | Phase 14 | Enables char arithmetic |
| print literal-only | print/println variable strings | Phase 8 (already done) | BLT-07 already satisfied |

**Deprecated/outdated:**
- None. All Phase 14 additions are additive. Existing behavior is preserved.

## BLT-07 Status

BLT-07 ("print/println variable string support") was already implemented in Phase 8 as the general
`App(Var("print"|"println"), strExpr)` case that loads `data` field from LangString and calls
`@printf`. Phase 14 only needs to verify this works end-to-end with a new test. No code changes
required for BLT-07.

## Open Questions

1. **failwith message format on stderr**
   - What we know: REQUIREMENTS say "stderr에 메시지 출력 후 exit(1)"
   - What's unclear: Should message include a newline? A "Fatal: " prefix?
   - Recommendation: Emit `fprintf(stderr, "%s\n", msg)` — matches `lang_match_failure` style (which adds its own message). Simple newline, no prefix.

2. **string_to_int error behavior**
   - What we know: strtol returns 0 on parse failure with errno set
   - What's unclear: Should lang_string_to_int call failwith on invalid input?
   - Recommendation: For Phase 14, return 0 on failure (simple, matches F# int.TryParse behavior). The test case only exercises valid input ("42").

3. **Char literal in ConstPat patterns**
   - What we know: Phase 13 (PAT-08) handles ConstPat(CharConst) separately
   - What's unclear: Does Phase 14's `Char(c, _)` elaboration enable char pattern matching?
   - Recommendation: Phase 14 only adds the Char expression elaboration. ConstPat(CharConst) in match is Phase 13's responsibility (PAT-08). These are separate.

## Sources

### Primary (HIGH confidence)

- Direct code inspection of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — confirmed existing builtin patterns (print, println, string_length, string_concat, to_string), confirmed Char has no elaboration case, confirmed externalFuncs list structure
- Direct code inspection of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` — confirmed LangString struct, existing helper functions (lang_string_concat, lang_to_string_int, lang_to_string_bool, lang_match_failure)
- Direct code inspection of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/MlirIR.fs` — confirmed available MlirOp cases (LlvmCallOp, LlvmCallVoidOp, LlvmGEPStructOp, ArithConstantOp, ArithCmpIOp)
- Direct code inspection of `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — confirmed `Char of char * span: Span` AST node, `App of func: Expr * arg: Expr * span: Span` structure
- Direct code inspection of `.planning/REQUIREMENTS.md` — confirmed BLT-01..07 scope

### Secondary (MEDIUM confidence)

- C ABI knowledge — `int64_t` return from C function maps to `i64` in MLIR's type system; `void` maps to ExtReturn = None
- libc function signatures — strtol, strstr, fprintf, exit all standard POSIX

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all libraries already in use or trivially available
- Architecture: HIGH — based on direct code inspection, all patterns are established in Phase 7–8
- Pitfalls: HIGH — identified from code analysis and pattern matching behavior of F# DU cases
- C runtime approach: HIGH — same proven pattern as Phase 8

**Research date:** 2026-03-26
**Valid until:** Stable (internal codebase, no external dependencies changing)
