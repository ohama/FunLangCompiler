# Phase 39: Format Strings - Research

**Researched:** 2026-03-30
**Domain:** C runtime snprintf wrappers + compile-time format string dispatch in Elaboration.fs
**Confidence:** HIGH

## Summary

Phase 39 adds `sprintf` (returns formatted `LangString*`) and `printfn` (prints formatted output with newline) to the language. Both operations require formatting integers, strings, hex values, and char codes using C-style format specifiers (`%d`, `%s`, `%x`, `%02x`, `%c`). The required specifiers and multi-arg format strings (`sprintf "%s=%d" name value`) are stated in the requirements.

The MLIR IR used by this compiler has no built-in variadic call mechanism except the special-cased `@printf` path in `Printer.fs`. Rather than extending the Printer with a general vararg mechanism, the correct approach is **typed C wrapper functions**: `lang_sprintf_1i`, `lang_sprintf_1s`, `lang_sprintf_2si`, etc., each taking a `char*` format string extracted from the `LangString` arg, plus typed arguments, and returning a `LangString*` via `snprintf`. The elaborator performs **compile-time dispatch** by pattern-matching on the format string literal, counting and typing the format specifiers, then emitting the call to the appropriate wrapper.

`printfn` is implemented by combining `sprintf` (to get the formatted string) followed by `println` (to print it with a newline), or directly by `printf` + newline. The simplest implementation: in Elaboration.fs, `printfn fmt arg` desugars to `println (sprintf fmt arg)`. For efficiency, dedicated `lang_printfn_1i` / `lang_printfn_1s` C wrappers can be added, but desugar-to-println-of-sprintf is correct and requires zero extra C code.

**Primary recommendation:** Add per-arity/per-type C wrappers (`lang_sprintf_1i`, `lang_sprintf_1s`, `lang_sprintf_2si`, `lang_sprintf_2is`, `lang_sprintf_2ii`, `lang_sprintf_2ss`) to `lang_runtime.c/h`; add compile-time format-string dispatch arms in `Elaboration.fs` for `sprintf` and `printfn`; add ExternalFuncDecl entries for each wrapper in both `externalFuncs` lists; write E2E tests.

## Standard Stack

No new external libraries. All changes are internal to `lang_runtime.c`, `lang_runtime.h`, and `Elaboration.fs`.

### Core (extended in this phase)

| Component | Location | Phase 39 Change |
|-----------|----------|-----------------|
| `lang_runtime.h` | `src/LangBackend.Compiler/` | Declare 6 sprintf wrappers (~12 LOC) |
| `lang_runtime.c` | `src/LangBackend.Compiler/` | Implement 6 sprintf wrappers via `snprintf` (~60 LOC) |
| `Elaboration.fs` | `src/LangBackend.Compiler/` | Add `sprintf` and `printfn` builtin arms with compile-time dispatch (~80 LOC delta) |

### No Changes Required

| Component | Reason |
|-----------|--------|
| `MlirIR.fs` | `LlvmCallOp`, `LlvmGEPStructOp`, `LlvmLoadOp`, `Ptr`, `I64` types all exist |
| `Printer.fs` | No new vararg call syntax needed; wrapper functions use standard `LlvmCallOp` |
| `Pipeline.fs` | `lang_runtime.c` already compiled and linked; no pass changes needed |
| `MlirBlock.Args` | No structural changes to the IR |

**Build:** `dotnet build` — no new packages.

## Architecture Patterns

### Pattern 1: Typed C Wrapper Functions for snprintf

**What:** Each combination of (arity, arg-types) gets one C function. The function extracts the `char*` data pointer from the format `LangString`, calls `snprintf` twice (first with NULL to get length, second to fill buffer), allocates a `LangString*` via `GC_malloc`, and returns it.

**When to use:** Any `sprintf` call where the format string is a compile-time literal (the only supported case).

**Wrapper naming convention:** `lang_sprintf_{N}{types}` where N is the arity (1 or 2) and types are `i` (int64_t) or `s` (char* / LangString*).

| Wrapper | C Signature | Covers format specifiers |
|---------|-------------|--------------------------|
| `lang_sprintf_1i` | `(char* fmt, int64_t a) -> LangString*` | `%d`, `%x`, `%02x`, `%c`, `%ld`, `%lx` |
| `lang_sprintf_1s` | `(char* fmt, char* a) -> LangString*` | `%s` |
| `lang_sprintf_2ii` | `(char* fmt, int64_t a, int64_t b) -> LangString*` | `%d%d`, `%x%d`, etc. |
| `lang_sprintf_2si` | `(char* fmt, char* a, int64_t b) -> LangString*` | `%s=%d`, `%s: %d`, etc. |
| `lang_sprintf_2is` | `(char* fmt, int64_t a, char* b) -> LangString*` | `%d=%s`, etc. |
| `lang_sprintf_2ss` | `(char* fmt, char* a, char* b) -> LangString*` | `%s/%s`, etc. |

**Example (lang_sprintf_1i):**
```c
// Source: lang_runtime.c — Phase 39
LangString* lang_sprintf_1i(char* fmt, int64_t a) {
    int len = snprintf(NULL, 0, fmt, (long)a);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, (long)a);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}
```

**Example (lang_sprintf_1s):**
```c
// Source: lang_runtime.c — Phase 39
LangString* lang_sprintf_1s(char* fmt, char* a) {
    int len = snprintf(NULL, 0, fmt, a);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}
```

**Example (lang_sprintf_2si):**
```c
// Source: lang_runtime.c — Phase 39
LangString* lang_sprintf_2si(char* fmt, char* a, int64_t b) {
    int len = snprintf(NULL, 0, fmt, a, (long)b);
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a, (long)b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}
```

### Pattern 2: Compile-Time Format String Dispatch in elaborateExpr

**What:** The `sprintf` builtin arm pattern-matches on the literal format string, counts specifiers, infers their types (`%d`/`%x`/`%02x`/`%c` → int, `%s` → string/ptr), then emits:
1. GEP + Load to extract `char*` data from the format `LangString` arg
2. For string args: GEP + Load to extract `char*` data from each `LangString` arg
3. `LlvmCallOp` to the appropriate `lang_sprintf_Nt` wrapper

**When to use:** Every `sprintf fmt arg1 [arg2 ...]` call where `fmt` is a string literal at compile time.

**Example (1-arg integer format):**
```fsharp
// Source: Elaboration.fs — elaborateExpr, Phase 39
// sprintf "%d" n  OR  sprintf "%x" n  OR  sprintf "%02x" n  etc.
| App (App (Var ("sprintf", _), String (fmt, _), _), argExpr, _)
    when countFormatSpecifiers fmt = 1 && firstSpecifierIsInt fmt ->
    let fmtGlobal  = addStringGlobal env fmt          // GC-rooted constant
    let fmtPtrVal  = { Name = freshName env; Type = Ptr }
    let (argVal, argOps) = elaborateExpr env argExpr
    // Coerce I1 → I64 if needed
    let (i64ArgVal, coerceOps) = coerceToI64 env argVal
    let result = { Name = freshName env; Type = Ptr }
    let ops = [
        LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
        LlvmCallOp(result, "@lang_sprintf_1i", [fmtPtrVal; i64ArgVal])
    ]
    (result, argOps @ coerceOps @ ops)
```

**Key detail:** For integer format specifiers, the format `LangString` arg is a compile-time literal — it is added as a global string with `addStringGlobal env fmt` and accessed via `LlvmAddressOfOp`. This avoids GEP+Load from a runtime `LangString` for the format string. The wrappers receive `char*` directly.

**Example (1-arg string format):**
```fsharp
// sprintf "%s" s
| App (App (Var ("sprintf", _), String ("%s", _), _), argExpr, _) ->
    let fmtGlobal  = addStringGlobal env "%s"
    let fmtPtrVal  = { Name = freshName env; Type = Ptr }
    let (argVal, argOps) = elaborateExpr env argExpr
    let (argPtr, coerceOps) = coerceToPtrArg env argVal
    // Extract char* data from LangString
    let dataPtrVal  = { Name = freshName env; Type = Ptr }
    let dataAddrVal = { Name = freshName env; Type = Ptr }
    let result      = { Name = freshName env; Type = Ptr }
    let ops = [
        LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
        LlvmGEPStructOp(dataPtrVal, argPtr, 1)
        LlvmLoadOp(dataAddrVal, dataPtrVal)
        LlvmCallOp(result, "@lang_sprintf_1s", [fmtPtrVal; dataAddrVal])
    ]
    (result, argOps @ coerceOps @ ops)
```

**Example (2-arg %s=%d format):**
```fsharp
// sprintf "%s=%d" name value  — App(App(App(Var "sprintf", String), nameExpr), valExpr)
// Pattern: 3-deep App nesting, format string has 1 %s specifier then 1 int specifier
| App (App (App (Var ("sprintf", _), String (fmt, _), _), arg1Expr, _), arg2Expr, _)
    when countFormatSpecifiers fmt = 2 && specifierTypes fmt = [StringSpec; IntSpec] ->
    let fmtGlobal   = addStringGlobal env fmt
    let fmtPtrVal   = { Name = freshName env; Type = Ptr }
    let (arg1Val, arg1Ops) = elaborateExpr env arg1Expr
    let (arg2Val, arg2Ops) = elaborateExpr env arg2Expr
    let (arg1Ptr, coerce1) = coerceToPtrArg env arg1Val
    let (arg2I64, coerce2) = coerceToI64 env arg2Val
    let data1PtrVal  = { Name = freshName env; Type = Ptr }
    let data1AddrVal = { Name = freshName env; Type = Ptr }
    let result       = { Name = freshName env; Type = Ptr }
    let ops = [
        LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
        LlvmGEPStructOp(data1PtrVal, arg1Ptr, 1)
        LlvmLoadOp(data1AddrVal, data1PtrVal)
        LlvmCallOp(result, "@lang_sprintf_2si", [fmtPtrVal; data1AddrVal; arg2I64])
    ]
    (result, arg1Ops @ arg2Ops @ coerce1 @ coerce2 @ ops)
```

### Pattern 3: printfn as Desugar to println(sprintf)

**What:** `printfn fmt arg` desugars at elaboration time to `println (sprintf fmt arg)`. This reuses all the `sprintf` machinery with zero new C code.

**When to use:** Always. No dedicated `lang_printfn_*` C functions needed.

**Example:**
```fsharp
// Source: Elaboration.fs — printfn one-arg desugar (literal only, no format arg)
| App (Var ("printfn", _), String (fmt, _), _) ->
    let s = Ast.unknownSpan
    elaborateExpr env (App(Var("println", s), String(fmt, s), s))

// printfn "%d" n  →  println (sprintf "%d" n)
| App (App (Var ("printfn", _), String (fmt, _), _), argExpr, _) ->
    let s = Ast.unknownSpan
    let sprintfExpr = App(App(Var("sprintf", s), String(fmt, s), s), argExpr, s)
    elaborateExpr env (App(Var("println", s), sprintfExpr, s))

// printfn "%s=%d" name value  →  println (sprintf "%s=%d" name value)
| App (App (App (Var ("printfn", _), String (fmt, _), _), arg1Expr, _), arg2Expr, _) ->
    let s = Ast.unknownSpan
    let sprintfExpr = App(App(App(Var("sprintf", s), String(fmt, s), s), arg1Expr, s), arg2Expr, s)
    elaborateExpr env (App(Var("println", s), sprintfExpr, s))
```

### Pattern 4: Format Specifier Analysis Helpers

**What:** Helper functions in F# (in Elaboration.fs) that analyze a format string literal to determine:
- How many specifiers it has (`countFormatSpecifiers`)
- What type each specifier maps to (int64 or ptr/char*)

**When to use:** Guard conditions on the `sprintf` and `printfn` pattern match arms.

**Implementation:**
```fsharp
// Source: Elaboration.fs — Phase 39 helpers (add near top of file, before elaborateExpr)
type FmtSpec = IntSpec | StrSpec

/// Count the number of % format specifiers in a format string.
/// Handles %% (escaped percent) by not counting it.
let private countFmtSpecs (fmt: string) : int =
    let mutable count = 0
    let mutable i = 0
    while i < fmt.Length do
        if fmt.[i] = '%' && i + 1 < fmt.Length then
            if fmt.[i+1] <> '%' then count <- count + 1
            i <- i + 2
        else
            i <- i + 1
    count

/// Return the ordered list of specifier types in a format string.
/// %d, %x, %02x, %c, %ld, %lx → IntSpec
/// %s → StrSpec
/// %% → ignored
let private fmtSpecTypes (fmt: string) : FmtSpec list =
    let specs = System.Collections.Generic.List<FmtSpec>()
    let mutable i = 0
    while i < fmt.Length do
        if fmt.[i] = '%' && i + 1 < fmt.Length then
            if fmt.[i+1] = '%' then
                i <- i + 2
            else
                // Scan past flags, width, precision, length modifier to the conversion char
                let mutable j = i + 1
                // Skip flags: -, +, space, 0, #
                while j < fmt.Length && "-+ 0#".Contains(fmt.[j]) do j <- j + 1
                // Skip width digits
                while j < fmt.Length && fmt.[j] >= '0' && fmt.[j] <= '9' do j <- j + 1
                // Skip precision
                if j < fmt.Length && fmt.[j] = '.' then
                    j <- j + 1
                    while j < fmt.Length && fmt.[j] >= '0' && fmt.[j] <= '9' do j <- j + 1
                // Skip length modifiers: l, ll, h, hh, z, t
                while j < fmt.Length && "lhzt".Contains(fmt.[j]) do j <- j + 1
                // Conversion character
                if j < fmt.Length then
                    match fmt.[j] with
                    | 's' -> specs.Add(StrSpec)
                    | _   -> specs.Add(IntSpec)  // d, i, o, u, x, X, c, p all map to i64
                    j <- j + 1
                i <- j
        else
            i <- i + 1
    specs |> Seq.toList
```

### Pattern 5: ExternalFuncDecl for Wrapper Functions

**What:** Add `@lang_sprintf_*` entries to both `externalFuncs` lists in `Elaboration.fs` (around lines 3462 and 3721).

**Example:**
```fsharp
// Source: Elaboration.fs — both externalFuncs lists, Phase 39 additions
// Phase 39: Format string wrappers
{ ExtName = "@lang_sprintf_1i";  ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_sprintf_1s";  ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_sprintf_2ii"; ExtParams = [Ptr; I64; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_sprintf_2si"; ExtParams = [Ptr; Ptr; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_sprintf_2is"; ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_sprintf_2ss"; ExtParams = [Ptr; Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Pattern 6: coerceToI64 Helper

**What:** A helper function parallel to existing `coerceToPtrArg` that coerces `I1` (bool) to `I64` via `ArithExtuIOp`. `Ptr` types should NOT be passed to int-taking sprintf wrappers — if someone does `sprintf "%d" (char_to_int 'A')`, the arg is already `I64`.

```fsharp
// Source: Elaboration.fs — Phase 39 helper (add near coerceToPtrArg)
let private coerceToI64Arg (env: ElabEnv) (v: MlirValue) : MlirValue * MlirOp list =
    match v.Type with
    | I1  ->
        let ext = { Name = freshName env; Type = I64 }
        (ext, [ArithExtuIOp(ext, v)])
    | I64 -> (v, [])
    | Ptr ->
        // ptrtoint: convert pointer to int (rare, but handles char-as-ptr edge cases)
        let i = { Name = freshName env; Type = I64 }
        (i, [LlvmPtrToIntOp(i, v)])
    | I32 ->
        // Should not arise; treat as I64
        (v, [])
```

### Anti-Patterns to Avoid

- **Trying to emit a vararg `@snprintf` call directly from MLIR:** The `Printer.fs` only has the vararg call syntax for `@printf`. Adding a second vararg declaration/call path requires significant Printer changes. Use typed C wrappers instead.
- **Passing `LangString*` directly as the fmt arg to C wrappers:** The wrappers need `char*` (the `.data` field). Extract with `LlvmGEPStructOp(ptr, 1)` + `LlvmLoadOp`. Exception: when the format string is a compile-time literal, use `addStringGlobal env fmt` + `LlvmAddressOfOp` — this is simpler and avoids GEP+Load for the fmt arg.
- **Supporting non-literal format strings:** `sprintf s arg` where `s` is a variable is not supported. This is consistent with eprintfn, which also only handles literal format strings. Non-literal fmt → compile error or fall-through to general App arm (which will fail with a type error).
- **Adding printfn as a separate C function:** Desugar `printfn fmt arg` to `println (sprintf fmt arg)` at elaboration time. Zero new C code needed.
- **Only adding wrappers to one externalFuncs list:** There are two identical lists in `Elaboration.fs`. Add to both. The pattern was documented in Phase 37/38 research.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Buffer sizing for snprintf | Custom pre-computation | `snprintf(NULL, 0, fmt, ...)` returns required length | POSIX-standard: first call with NULL/0 returns length, second call fills buffer |
| Variadic MLIR calls to snprintf | Extend Printer.fs with new vararg syntax | Typed C wrapper functions | 6 typed wrappers cover all required specifier combinations; no Printer changes needed |
| printfn C function | New lang_printfn_* C functions | Desugar to `println (sprintf ...)` in elaborateExpr | Zero C code; reuses all sprintf logic |

**Key insight:** The two-pass `snprintf` idiom (pass NULL/0 to get length, then allocate and fill) is the standard C approach for dynamically-sized formatted strings. This is already used internally in `lang_runtime.c` via `snprintf` for error messages. The GC allocator (`GC_malloc`) is safe to use for the result buffer.

## Common Pitfalls

### Pitfall 1: Pattern Match Ordering for sprintf Arms
**What goes wrong:** The 2-arg `sprintf` arm (`App(App(App(Var "sprintf", fmt), arg1), arg2)`) must be placed BEFORE the 1-arg arm (`App(App(Var "sprintf", fmt), arg)`) in `elaborateExpr`. If the 1-arg arm comes first, the pattern `App(App(Var "sprintf", fmt), arg1)` will partially match the outer `App` of a 2-arg call, treating `arg2` application as something unrelated.
**Why it happens:** `App(App(App(Var "sprintf", fmt), arg1), arg2)` is `App( App(App(...), arg1), arg2 )`. The outermost `App` has as its function `App(App(Var "sprintf", fmt), arg1)`. If `Elaboration.fs` processes the 1-arg arm first, it sees `App(App(Var "sprintf", fmt), arg1)` as a complete 1-arg call, returns a result, and then the outer `App` tries to apply that result as a function to `arg2`.
**How to avoid:** Place 2-arg arms BEFORE 1-arg arms. This mirrors the existing `eprintfn` pattern where the two-arg case is commented "MUST come before one-arg case".
**Warning signs:** `sprintf "%s=%d" name value` elaborates without error but produces wrong output (e.g., returns a function pointer instead of a string).

### Pitfall 2: Format String as Global vs Runtime LangString
**What goes wrong:** The format string literal (e.g., `"%d"`) is available at elaboration time. Rather than creating a `LangString*` for it at runtime and then GEP+Load extracting the `char*`, use `addStringGlobal env fmt` to add it as an MLIR string constant, then `LlvmAddressOfOp` to get the `char*` directly. If you elaborate the format string as a `String(fmt, _)` expression via `elaborateExpr`, you get a `LangString*` (Ptr) and then need GEP+Load. The `addStringGlobal env fmt` path is 2 ops shorter.
**Why it happens:** The `String` literal elaboration path (around line 85 of Elaboration.fs) wraps the string in a `LangString` struct. For sprintf wrappers that expect `char*`, the struct's `.data` pointer must be extracted.
**How to avoid:** In the `sprintf` pattern match arms, when the format string is a `String(fmt, _)` literal, call `addStringGlobal env fmt` directly (not `elaborateExpr env (String(fmt, _))`). This gives a global name you can use with `LlvmAddressOfOp` to get `char*` directly.
**Warning signs:** Generated MLIR has 2 extra GEP+Load ops per sprintf call for the format string extraction.

### Pitfall 3: %c Specifier Needs char Code as int, Not char Value
**What goes wrong:** `sprintf "%c" 65` should produce `"A"`. The language represents chars as `int64_t` (char code point), which is correct for `%c` — `printf("%c", (long)65)` prints `A`. No special handling needed. However, if a char value comes from `char_to_int` or similar, it is already `I64`. The `I1` case (bool) should map to `IntSpec` and will be coerced to `I64`. The `Ptr` case (string) passing to `%c` is a user error — no runtime defense is needed for this phase.
**How to avoid:** Treat all non-string specifiers as `IntSpec` and coerce to `I64` using the `coerceToI64Arg` helper.

### Pitfall 4: ExternalFuncDecl Not Updated in Both Lists
**What goes wrong:** `Elaboration.fs` has two identical `externalFuncs` lists (one for module-path elaboration ~line 3357, one for program-path ~line 3608). Adding `@lang_sprintf_*` to only one causes MLIR validation failures in the other path.
**How to avoid:** Search for `@lang_init_args` (Phase 38 anchor) and add Phase 39 entries immediately after it in both lists.
**Warning signs:** Simple E2E tests pass, module-using programs crash with MLIR validation error.

### Pitfall 5: snprintf with %ld vs %d for int64_t
**What goes wrong:** The language uses `int64_t` for integers. C's `%d` formats an `int` (32-bit). On 64-bit platforms, passing `int64_t` to `%d` is undefined behavior (though it usually works when the value fits in 32 bits). The correct specifier for `int64_t` in C is `%ld` (on Linux/macOS with 64-bit long) or `PRId64`.
**Why it matters:** The user writes `sprintf "%d" 42` — the format string IS `%d`. The C wrapper receives `fmt = "%d"` literally and passes it to `snprintf` with a `(long)` cast. Since `long` is 64-bit on all modern 64-bit platforms (LP64 model on macOS/Linux), `snprintf(buf, len, "%d", (long)n)` is safe. `%d` with `long` arg is implementation-defined but works on all targets this compiler supports.
**How to avoid:** Cast all int args to `(long)` in the C wrappers (not `(int)`) to ensure 64-bit value is passed. The `%d` format specifier accepts `long` on LP64 platforms. This matches what `lang_to_string_int` already does: `snprintf(tmp, sizeof(tmp), "%ld", (long)n)`.
**Warning signs:** Large integers (>2^31) are truncated to 32 bits. Test with `sprintf "%d" 4000000000`.

### Pitfall 6: Char Specifier `%c` Needs i64 Cast to int
**What goes wrong:** `snprintf(buf, n, "%c", (long)97)` where 97 is the char code. `%c` expects an `int`, not `long`. On most platforms this is fine because `long` args are passed in registers and `%c` takes the low byte. But strictly, pass `(int)(long)a` for `%c` format strings, or just use `(long)` uniformly — the `%c` format picks the low byte regardless of the integer width.
**How to avoid:** For all int wrappers, use `(long)a` cast. The wrappers receive the format string as-is, so `snprintf(NULL, 0, "%c", (long)65)` will correctly return 1 (length of "A").

## Code Examples

### C Runtime: lang_sprintf_1i and lang_sprintf_1s

```c
// Source: lang_runtime.c — Phase 39
// 1-arg int wrapper: handles %d, %x, %02x, %c, etc.
LangString* lang_sprintf_1i(char* fmt, int64_t a) {
    int len = snprintf(NULL, 0, fmt, (long)a);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, (long)a);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

// 1-arg string wrapper: handles %s
LangString* lang_sprintf_1s(char* fmt, char* a) {
    int len = snprintf(NULL, 0, fmt, a);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}
```

### C Runtime: 2-arg wrappers

```c
// Source: lang_runtime.c — Phase 39
LangString* lang_sprintf_2ii(char* fmt, int64_t a, int64_t b) {
    int len = snprintf(NULL, 0, fmt, (long)a, (long)b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, (long)a, (long)b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_2si(char* fmt, char* a, int64_t b) {
    int len = snprintf(NULL, 0, fmt, a, (long)b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a, (long)b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_2is(char* fmt, int64_t a, char* b) {
    int len = snprintf(NULL, 0, fmt, (long)a, b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, (long)a, b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}

LangString* lang_sprintf_2ss(char* fmt, char* a, char* b) {
    int len = snprintf(NULL, 0, fmt, a, b);
    if (len < 0) len = 0;
    char* buf = (char*)GC_malloc((size_t)(len + 1));
    snprintf(buf, (size_t)(len + 1), fmt, a, b);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = (int64_t)len;
    s->data = buf;
    return s;
}
```

### lang_runtime.h additions

```c
// Source: lang_runtime.h — Phase 39
/* Phase 39: Format string wrappers (snprintf delegation) */
LangString* lang_sprintf_1i(char* fmt, int64_t a);
LangString* lang_sprintf_1s(char* fmt, char* a);
LangString* lang_sprintf_2ii(char* fmt, int64_t a, int64_t b);
LangString* lang_sprintf_2si(char* fmt, char* a, int64_t b);
LangString* lang_sprintf_2is(char* fmt, int64_t a, char* b);
LangString* lang_sprintf_2ss(char* fmt, char* a, char* b);
```

### Elaboration.fs: sprintf 1-arg integer arm

```fsharp
// Source: Elaboration.fs — elaborateExpr, Phase 39
// MUST come before general App arm.
// sprintf with 1 int-type specifier: "%d", "%x", "%02x", "%c", etc.
| App (App (Var ("sprintf", _), String (fmt, _), _), argExpr, _)
    when (let specs = fmtSpecTypes fmt in specs.Length = 1 && specs.[0] = IntSpec) ->
    let fmtGlobal = addStringGlobal env fmt
    let fmtPtrVal = { Name = freshName env; Type = Ptr }
    let (argVal, argOps) = elaborateExpr env argExpr
    let (i64Val, coerceOps) = coerceToI64Arg env argVal
    let result = { Name = freshName env; Type = Ptr }
    let ops = [
        LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
        LlvmCallOp(result, "@lang_sprintf_1i", [fmtPtrVal; i64Val])
    ]
    (result, argOps @ coerceOps @ ops)

// sprintf with 1 string specifier: "%s"
| App (App (Var ("sprintf", _), String (fmt, _), _), argExpr, _)
    when (let specs = fmtSpecTypes fmt in specs.Length = 1 && specs.[0] = StrSpec) ->
    let fmtGlobal    = addStringGlobal env fmt
    let fmtPtrVal    = { Name = freshName env; Type = Ptr }
    let (argVal, argOps) = elaborateExpr env argExpr
    let (argPtr, coerce) = coerceToPtrArg env argVal
    let dataPtrVal   = { Name = freshName env; Type = Ptr }
    let dataAddrVal  = { Name = freshName env; Type = Ptr }
    let result       = { Name = freshName env; Type = Ptr }
    let ops = [
        LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
        LlvmGEPStructOp(dataPtrVal, argPtr, 1)
        LlvmLoadOp(dataAddrVal, dataPtrVal)
        LlvmCallOp(result, "@lang_sprintf_1s", [fmtPtrVal; dataAddrVal])
    ]
    (result, argOps @ coerce @ ops)
```

### Elaboration.fs: sprintf 2-arg arm (must be BEFORE 1-arg arms)

```fsharp
// Source: Elaboration.fs — elaborateExpr, Phase 39
// MUST come BEFORE 1-arg sprintf arms (outer App matches first)
| App (App (App (Var ("sprintf", _), String (fmt, _), _), arg1Expr, _), arg2Expr, _)
    when (let specs = fmtSpecTypes fmt in specs.Length = 2) ->
    let specs = fmtSpecTypes fmt
    let fmtGlobal  = addStringGlobal env fmt
    let fmtPtrVal  = { Name = freshName env; Type = Ptr }
    let (arg1Val, arg1Ops) = elaborateExpr env arg1Expr
    let (arg2Val, arg2Ops) = elaborateExpr env arg2Expr
    let result = { Name = freshName env; Type = Ptr }
    match specs with
    | [IntSpec; IntSpec] ->
        let (a1, c1) = coerceToI64Arg env arg1Val
        let (a2, c2) = coerceToI64Arg env arg2Val
        let ops = [LlvmAddressOfOp(fmtPtrVal, fmtGlobal); LlvmCallOp(result, "@lang_sprintf_2ii", [fmtPtrVal; a1; a2])]
        (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
    | [StrSpec; IntSpec] ->
        let (a1Ptr, c1) = coerceToPtrArg env arg1Val
        let dp1 = { Name = freshName env; Type = Ptr }
        let da1 = { Name = freshName env; Type = Ptr }
        let (a2, c2) = coerceToI64Arg env arg2Val
        let ops = [
            LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
            LlvmGEPStructOp(dp1, a1Ptr, 1); LlvmLoadOp(da1, dp1)
            LlvmCallOp(result, "@lang_sprintf_2si", [fmtPtrVal; da1; a2])
        ]
        (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
    | [IntSpec; StrSpec] ->
        let (a1, c1) = coerceToI64Arg env arg1Val
        let (a2Ptr, c2) = coerceToPtrArg env arg2Val
        let dp2 = { Name = freshName env; Type = Ptr }
        let da2 = { Name = freshName env; Type = Ptr }
        let ops = [
            LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
            LlvmGEPStructOp(dp2, a2Ptr, 1); LlvmLoadOp(da2, dp2)
            LlvmCallOp(result, "@lang_sprintf_2is", [fmtPtrVal; a1; da2])
        ]
        (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
    | [StrSpec; StrSpec] ->
        let (a1Ptr, c1) = coerceToPtrArg env arg1Val
        let (a2Ptr, c2) = coerceToPtrArg env arg2Val
        let dp1 = { Name = freshName env; Type = Ptr }; let da1 = { Name = freshName env; Type = Ptr }
        let dp2 = { Name = freshName env; Type = Ptr }; let da2 = { Name = freshName env; Type = Ptr }
        let ops = [
            LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
            LlvmGEPStructOp(dp1, a1Ptr, 1); LlvmLoadOp(da1, dp1)
            LlvmGEPStructOp(dp2, a2Ptr, 1); LlvmLoadOp(da2, dp2)
            LlvmCallOp(result, "@lang_sprintf_2ss", [fmtPtrVal; da1; da2])
        ]
        (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
    | _ -> failwith (sprintf "sprintf: unsupported 2-arg format specifier combo in '%s'" fmt)
```

### E2E Test Pattern (.flt)

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let s1 = sprintf "%d" 42
let s2 = sprintf "%s=%d" "key" 99
let s3 = sprintf "%02x" 255
let _ = println s1
let _ = println s2
let _ = println s3
// --- Output:
42
key=99
ff
0
```

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let n = 5
let _ = printfn "%d states" n
// --- Output:
5 states
0
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No sprintf/printfn | `sprintf "%d" n` returns formatted string | Phase 39 | FunLexYacc can format integers, hex values, strings |
| `eprintfn "%s"` hardcoded to "%s" only | Full format-string dispatch via fmtSpecTypes helper | Phase 39 | Arbitrary format strings supported for all required specifiers |

**Deprecated/outdated:**
- Nothing deprecated. Existing `print`, `println`, `eprintln`, `eprintfn` builtins are unaffected.

## Recommended Plan Structure

Phase 39 has a single clean boundary: all changes are in `lang_runtime.c/h` and `Elaboration.fs`. One plan is sufficient:

- **39-01-PLAN.md:** Add C wrappers (lang_sprintf_*) + F# format string helpers (fmtSpecTypes, coerceToI64Arg) + Elaboration.fs sprintf/printfn arms + ExternalFuncDecl entries in both lists + 3 E2E tests:
  - `39-01-sprintf-int.flt` — tests `%d`, `%x`, `%02x`, `%c`
  - `39-02-sprintf-multi.flt` — tests `sprintf "%s=%d" name value`
  - `39-03-printfn.flt` — tests `printfn "%d states" n`

## Open Questions

1. **Non-literal format strings (`sprintf s arg` where `s` is a variable)**
   - What we know: `eprintfn` only handles literal format strings. The `sprintf` pattern arms all require `String(fmt, _)` literal match.
   - What's unclear: Whether FunLexYacc ever uses `sprintf` with a non-literal format string.
   - Recommendation: Only support literal format strings (same restriction as `eprintfn`). If a non-literal format string is passed, the general `App` arm will catch it and fail. No action needed for this phase.

2. **`%ld` vs `%d` in user format strings**
   - What we know: C's `%d` is for `int`, `%ld` is for `long`. The runtime casts `int64_t` args to `(long)` before passing to snprintf. On LP64 systems (macOS, Linux x86-64, ARM64), `long` is 64-bit.
   - What's unclear: Whether FunLexYacc uses `%ld` in format strings.
   - Recommendation: The wrappers pass `(long)` regardless of the specifier. Both `%d` and `%ld` work correctly for 64-bit values on LP64 platforms. No special handling needed.

3. **`%x` vs `%02x` — format string classification**
   - What we know: Both `%x` and `%02x` are integer specifiers (IntSpec). The `fmtSpecTypes` helper scans past flags and width digits before reading the conversion character.
   - What's unclear: Whether the `fmtSpecTypes` scanner correctly handles `%02x` (flag `0`, width `2`, conversion `x`).
   - Recommendation: The scanner in the architecture patterns section handles this correctly: skip flags (`-+0 #`), skip width digits, skip precision, skip length modifiers, then read conversion char. Verify with unit test in the plan.

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` — Complete elaborateExpr function; `eprintfn` pattern arms (lines 1649-1659) as model for sprintf dispatch; both externalFuncs lists; `addStringGlobal`, `coerceToPtrArg`, `freshName` utilities; `LlvmAddressOfOp`, `LlvmGEPStructOp`, `LlvmLoadOp` usage patterns
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.c` — `lang_to_string_int` (lines 29-38) as model for snprintf+GC_malloc pattern; `LangString` struct layout (lines 12-15); existing `snprintf` usage for length measurement (lines 31, 269)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/lang_runtime.h` — All existing declarations; LangString forward declaration; Phase 38 pattern for declaration additions
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Printer.fs` — Confirmed vararg call syntax is only for `@printf` (lines 115-117); standard `LlvmCallOp` syntax for non-vararg calls (lines 118-120)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MlirIR.fs` — `LlvmCallOp`, `LlvmGEPStructOp`, `LlvmLoadOp`, `LlvmAddressOfOp` all exist as MlirOp cases
- `/Users/ohama/vibe-coding/LangBackend/.planning/phases/38-cli-arguments/38-RESEARCH.md` — Confirmed two-externalFuncs-list pitfall; %arg0/%arg1 naming; LangString/GC_malloc patterns

### Secondary (MEDIUM confidence)
- POSIX `snprintf` with NULL/0 returning required length: standard behavior, confirmed present in glibc and macOS libc
- LP64 platform guarantees: `long` is 64-bit on macOS/Linux x86-64 and ARM64 — standard knowledge for the platforms this compiler targets

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all changes are extensions of patterns already present in the codebase; no new libraries
- Architecture: HIGH — eprintfn pattern, LangString GEP+Load pattern, and both externalFuncs lists all confirmed by direct code inspection
- Pitfalls: HIGH — 2-arg before 1-arg ordering is a confirmed risk from eprintfn precedent; two-lists pitfall confirmed from Phase 37/38; snprintf LP64 behavior is standard

**Research date:** 2026-03-30
**Valid until:** 2026-04-30 (stable codebase, low churn)
