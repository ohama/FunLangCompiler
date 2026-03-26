# Phase 8: Strings - Research

**Researched:** 2026-03-26
**Domain:** MLIR/LLVM string representation, libc string operations, heap allocation via Boehm GC
**Confidence:** HIGH

## Summary

Phase 8 extends the LangBackend compiler to compile LangThree string literals into heap-allocated
two-field structs (`{i64 length, ptr data}`) and wire up the string builtins `string_length`,
`string_concat`, `to_string`, and `=`/`<>` for strings. The codebase already supports string
globals (Phase 7 print/println), GC_malloc heap allocation, and LlvmCallOp/LlvmCallVoidOp — all
the MLIR plumbing needed for strings is already in place.

The implementation approach is: (1) add a `StructType` to `MlirType` and a `LlvmGEPStructOp` to
`MlirOp` for field access, then (2) elaborate `String(s)` nodes to a static byte array global +
`GC_malloc`'d header struct, and (3) implement each builtin as one or more llvm.call ops to libc
functions (`strcmp`, `strlen`, `memcpy`, `sprintf`). The `Equal`/`NotEqual` case in Elaboration
must be made type-aware: when both operands are `Ptr`-typed values, route to `strcmp` instead of
`arith.cmpi`.

**Primary recommendation:** Implement string struct as `GC_malloc(16)` (2×8 bytes: i64 length +
ptr data), store the static byte array global pointer in field 1, store the string length in field
0, then implement all builtins as direct llvm.call to libc. No new MLIR dialects needed.

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Boehm GC (libgc) | system | Heap allocation for string header struct | Already integrated in Phase 7 |
| libc strcmp | system | String equality comparison | Standard C string compare |
| libc strlen | system | String length query | Standard C string length |
| libc memcpy | system | String content copy for concat | Standard C memory copy |
| libc sprintf | system | Integer/bool to string conversion | Standard C formatted output |
| libc malloc | system | Buffer allocation in to_string | Used indirectly through GC_malloc |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| llvm.mlir.global | MLIR 20 | Static byte array for string literal data | Every string literal |
| llvm.getelementptr | MLIR 20 | Field access in struct (GEP with struct type) | Load/store struct fields |
| GC_malloc | Boehm GC | Heap-allocate the string header | String literal elaboration |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| GC_malloc for header | llvm.alloca | alloca is stack-only, can't escape — must use GC |
| sprintf for to_string | snprintf | snprintf is safer but needs length estimate first; sprintf with stack buffer is fine for int/bool |
| strcmp for equality | hand-rolled loop | strcmp is libc standard, no reason to hand-roll |
| Static byte array global | GC_malloc'd copy | Static is simpler and safe since data is read-only |

## Architecture Patterns

### String Representation

```
String struct (GC_malloc'd, 16 bytes):
  field 0: i64 length   (byte offset 0)
  field 1: ptr data     (byte offset 8)

Data: llvm.mlir.global internal constant @__str_N("content\00") {addr_space = 0}
```

Elaborating `String("hello")`:
1. Add `@__str_0` global (already exists in `addStringGlobal`)
2. `GC_malloc(16)` → `%header`
3. `LlvmGEPStructOp(%header, 0)` → `%len_ptr` (field 0: length)
4. `ArithConstantOp(5L)` → `%len`
5. `LlvmStoreOp(%len, %len_ptr)` — store length as i64
6. `LlvmGEPStructOp(%header, 1)` → `%data_ptr` (field 1: data)
7. `LlvmAddressOfOp(@__str_0)` → `%data_addr`
8. `LlvmStoreOp(%data_addr, %data_ptr)` — store data ptr
9. Result: `%header : Ptr`

### Pattern 1: GEP for Struct Field Access

The existing `LlvmGEPLinearOp` uses i64-indexed linear GEP (for closure env arrays). For struct
fields we need typed GEP with `i32` field index. Add a new op:

```fsharp
| LlvmGEPStructOp of result: MlirValue * ptr: MlirValue * fieldIndex: int
```

Printer emits:
```
%ptr = llvm.getelementptr inbounds %base[0, N] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
```

Note the `!llvm.struct<(i64, ptr)>` type annotation is required by MLIR 20 typed GEP.

### Pattern 2: Type-Aware Equal/NotEqual

The current `Equal` case in Elaboration always emits `arith.cmpi eq`. After Phase 8, the LHS
operand may be `Ptr`-typed (a string struct). The dispatch rule:

```
Equal(lhs, rhs):
  elaborated lv type = Ptr → strcmp-based equality
  elaborated lv type = I64 → arith.cmpi eq (existing)
  elaborated lv type = I1  → arith.cmpi eq (existing)
```

strcmp-based equality pattern:
1. Load `%data_l` from `lv.field[1]`  (ptr to raw data of left string)
2. Load `%data_r` from `rv.field[1]`  (ptr to raw data of right string)
3. `LlvmCallOp(%cmp, "@strcmp", [%data_l, %data_r])` → I32
4. `ArithConstantOp(%zero, 0L : I32)`
5. `ArithCmpIOp(%result, "eq", %cmp, %zero)` — compare I32 result to 0

### Pattern 3: string_length Builtin

AST: `App(Var("string_length"), strExpr)`

```
elaborate strExpr → %str : Ptr
LlvmGEPStructOp(%len_ptr, %str, 0)
LlvmLoadOp(%len, %len_ptr)   → I64
result = %len
```

### Pattern 4: string_concat Builtin

AST: `App(App(Var("string_concat"), strA), strB)`

Full implementation uses a helper `@__string_concat` or inline ops. The inline approach:

1. Elaborate `%a : Ptr`, `%b : Ptr`
2. Load `%len_a` from `%a.field[0]` (i64)
3. Load `%len_b` from `%b.field[0]` (i64)
4. `%total_len = arith.addi %len_a, %len_b`
5. `%buf_size = arith.addi %total_len, 1` (null terminator)
6. `%buf = LlvmCallOp(@GC_malloc, [%buf_size])` — allocate data buffer
7. Load `%data_a` from `%a.field[1]`
8. `LlvmCallVoidOp(@memcpy, [%buf, %data_a, %len_a])` — copy A data
9. `%buf_offset = LlvmGEPLinearOp(%buf, %len_a)` — offset pointer by len_a
   - IMPORTANT: len_a is i64 but GEPLinearOp takes a constant int. For dynamic GEP we need a new op or use `LlvmCallOp` approach.
   - ALTERNATIVE: Emit a helper llvm.func `@__string_concat` declared as external and call it from a separate C helper file, OR use a simpler approach: `sprintf`-style via `@snprintf`.
   - RECOMMENDED SIMPLIFICATION: Use `sprintf` / `GC_malloc`:
     a. `%buf_size = addi(len_a, addi(len_b, 1))`
     b. `%buf = GC_malloc(%buf_size)` — char buffer
     c. Load data_a, data_b
     d. Use a helper function `@lang_string_concat` (declared as external, implemented in a C runtime file) that takes (ptr a_data, i64 a_len, ptr b_data, i64 b_len) and returns a new GC_malloc'd string header struct
     - OR: emit a `@__concat_helper` llvm.func inline that does the copy using `memcpy` and dynamic GEP

**DECISION: Implement `@lang_string_concat` as an external function declared in a companion C
runtime file (`lang_runtime.c`) compiled and linked with every binary.** This avoids the dynamic
GEP problem in MLIR text (MLIR typed GEP requires compile-time-known struct shape but not
necessarily constant indices — but the MLIR text for dynamic offset is more complex). A C helper is
simpler, correct, and idiomatic for a compiled language runtime.

### Pattern 5: to_string Builtin

Same C runtime approach:
- `@lang_to_string_int(i64) -> ptr` (returns string struct pointer)
- `@lang_to_string_bool(i1) -> ptr` (returns string struct pointer)

These are simpler to implement in C than inline MLIR.

### Pattern 6: C Runtime File

Create `src/LangBackend.Compiler/lang_runtime.c` with:
- `lang_string_struct` type alias = `{int64_t length; char* data;}`
- `lang_string_concat(struct*, struct*) -> struct*`
- `lang_to_string_int(int64_t) -> struct*`
- `lang_to_string_bool(int64_t) -> struct*`

Link this C file when compiling every binary (via Pipeline.fs clang args).

### Anti-Patterns to Avoid

- **Dynamic index GEP in MLIR text:** MLIR typed GEP with dynamic (non-constant) byte offsets requires careful syntax. Use C runtime functions for operations that need dynamic pointer arithmetic.
- **Stack-allocating string header:** Never use llvm.alloca for string header — strings must be GC-tracked heap objects so they can escape function scope.
- **Passing I32 to I64 ops:** strcmp returns i32 — must compare it to `arith.constant 0 : i32`, not 0 : i64.
- **forgetting null terminator in buffer:** The GC_malloc'd concat buffer needs len+1 bytes for `\0`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| String concatenation | Inline MLIR GEP loop | C runtime helper | Dynamic byte offset GEP in MLIR is verbose; C is 5 lines |
| to_string | Inline MLIR sprintf | C runtime helper | sprintf format string in MLIR global is workable but C is clearer |
| strcmp | Custom char-by-char loop | @strcmp external decl | Standard libc, already pattern matches existing @GC_init/@printf |

**Key insight:** For operations requiring dynamic pointer arithmetic or libc calls with complex
argument patterns, a thin C runtime file is dramatically simpler than equivalent MLIR text. This is
exactly how real compilers handle runtime builtins (LLVM has compiler-rt, GHC has the RTS).

## Common Pitfalls

### Pitfall 1: Wrong GEP syntax for struct fields
**What goes wrong:** Using `LlvmGEPLinearOp` (linear byte GEP) for struct field access
**Why it happens:** Existing GEPLinearOp was designed for opaque-pointer byte arrays
**How to avoid:** Add `LlvmGEPStructOp` that emits `llvm.getelementptr inbounds %ptr[0, N] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>`
**Warning signs:** MLIR validation error about GEP type mismatch

### Pitfall 2: strcmp returns i32, not i1
**What goes wrong:** Trying to use strcmp result directly in `cf.cond_br` (which needs i1)
**Why it happens:** strcmp is defined as `int strcmp(const char*, const char*)` in C
**How to avoid:** Add `ArithCmpIOp("eq", strcmp_result : I32, zero : I32)` step after strcmp call
**Warning signs:** MLIR type error on cond_br operand

### Pitfall 3: Type inference for Equal case
**What goes wrong:** `Equal(String(...), String(...))` hits the arith.cmpi branch (comparing Ptr values)
**Why it happens:** Current Equal case doesn't check operand types
**How to avoid:** Elaborate both sides first, check if `lv.Type = Ptr`, dispatch to strcmp path

### Pitfall 4: C runtime struct layout must match MLIR struct layout
**What goes wrong:** C uses `{int64_t len; char* data}` but MLIR struct has padding/alignment issues
**Why it happens:** Struct layout is ABI-dependent
**How to avoid:** Use `__attribute__((packed))` in C or ensure consistent 8-byte alignment. Since
`int64_t` is 8 bytes and `char*` is 8 bytes (x86-64), the natural layout IS packed at offsets
0 and 8 — no padding needed.

### Pitfall 5: GC_malloc argument type mismatch
**What goes wrong:** Passing i64 to GC_malloc which expects `size_t` (i64 on x86-64 Linux, but
this can cause confusion with I32)
**Why it happens:** GC_malloc declared as `@GC_malloc(!llvm.ptr i64) -> ptr` — already correct
**How to avoid:** String header is always 16 bytes; emit `ArithConstantOp(16L)` of type I64

### Pitfall 6: Missing external declarations for new C runtime functions
**What goes wrong:** Linking succeeds but MLIR validation fails because callee not declared
**Why it happens:** MLIR requires all called functions to be forward-declared
**How to avoid:** Add `@lang_string_concat`, `@lang_to_string_int`, `@lang_to_string_bool` to ExternalFuncs unconditionally (alongside GC_init, GC_malloc, printf)

## Code Examples

### String Literal Elaboration (MLIR output)

```mlir
// let s = "hello" in ...
// Globals section:
llvm.mlir.global internal constant @__str_0("hello\00") {addr_space = 0 : i32}

// In @main body:
%t0 = arith.constant 16 : i64                              // header size
%t1 = llvm.call @GC_malloc(%t0) : (i64) -> !llvm.ptr      // alloc header
%t2 = llvm.getelementptr inbounds %t1[0, 0] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
%t3 = arith.constant 5 : i64
llvm.store %t3, %t2 : i64, !llvm.ptr                      // store length
%t4 = llvm.getelementptr inbounds %t1[0, 1] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
%t5 = llvm.mlir.addressof @__str_0 : !llvm.ptr
llvm.store %t5, %t4 : !llvm.ptr, !llvm.ptr                // store data ptr
// %t1 is now the string struct ptr
```

### string_length Elaboration (MLIR output)

```mlir
// string_length s  where %s = string struct ptr
%t6 = llvm.getelementptr inbounds %s[0, 0] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
%t7 = llvm.load %t6 : !llvm.ptr -> i64
// %t7 is the length (i64)
```

### String Equality Elaboration (MLIR output)

```mlir
// "abc" = "abc"
// ... elaborate both sides to %sl, %sr : Ptr
%t8  = llvm.getelementptr inbounds %sl[0, 1] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
%t9  = llvm.load %t8 : !llvm.ptr -> !llvm.ptr
%t10 = llvm.getelementptr inbounds %sr[0, 1] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
%t11 = llvm.load %t10 : !llvm.ptr -> !llvm.ptr
%t12 = llvm.call @strcmp(%t9, %t11) : (!llvm.ptr, !llvm.ptr) -> i32
%t13 = arith.constant 0 : i32
%t14 = arith.cmpi eq, %t12, %t13 : i32
// %t14 : i1 is true if equal
```

### C Runtime (lang_runtime.c)

```c
#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <gc.h>

typedef struct { int64_t length; char* data; } LangString;

LangString* lang_string_concat(LangString* a, LangString* b) {
    int64_t total = a->length + b->length;
    char* buf = (char*)GC_malloc(total + 1);
    memcpy(buf, a->data, a->length);
    memcpy(buf + a->length, b->data, b->length);
    buf[total] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = total;
    s->data = buf;
    return s;
}

LangString* lang_to_string_int(int64_t n) {
    char tmp[32];
    int len = snprintf(tmp, sizeof(tmp), "%ld", n);
    char* buf = (char*)GC_malloc(len + 1);
    memcpy(buf, tmp, len + 1);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}

LangString* lang_to_string_bool(int64_t b) {
    const char* str = b ? "true" : "false";
    int64_t len = strlen(str);
    char* buf = (char*)GC_malloc(len + 1);
    memcpy(buf, str, len + 1);
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = len;
    s->data = buf;
    return s;
}
```

### Pipeline Integration

Pipeline.fs must compile `lang_runtime.c` to an object file and link it:

```fsharp
// In compile function:
let runtimeSrc = Path.Combine(runtimeDir, "lang_runtime.c")
let runtimeObj = Path.Combine(tempDir, "lang_runtime.o")
// Step 1: compile runtime
runProcess "clang" (sprintf "-c %s -I/opt/homebrew/opt/bdw-gc/include -o %s" runtimeSrc runtimeObj)
// Step 2: link with object
let clangArgs = sprintf "-Wno-override-module %s %s %s -o %s" llFile runtimeObj gcLinkFlags outputPath
```

## New MlirIR and Printer Additions Required

### MlirType additions

```fsharp
| StructType of MlirType list  // e.g. StructType [I64; Ptr] → !llvm.struct<(i64, ptr)>
```

### MlirOp additions

```fsharp
| LlvmGEPStructOp of result: MlirValue * ptr: MlirValue * fieldIndex: int
// Printer: %result = llvm.getelementptr inbounds %ptr[0, N] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>
```

The struct type in GEP output must always be the string struct type `!llvm.struct<(i64, ptr)>`.
This is hardcoded for Phase 8 since only string structs use this op; a generic approach can wait
for Phase 9 tuples.

### Printer case for LlvmGEPStructOp

```fsharp
| LlvmGEPStructOp(result, ptr, fieldIndex) ->
    sprintf "%s%s = llvm.getelementptr inbounds %s[0, %d] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>"
        indent result.Name ptr.Name fieldIndex
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| string as raw i8* pointer | string as {i64 len, ptr data} header struct | Phase 8 | Enables O(1) string_length |
| no string builtins | strcmp/concat/to_string via C runtime | Phase 8 | All STR requirements met |
| print/println via static globals only | string structs for runtime-computed strings | Phase 8 | Dynamic string operations |

**Deprecated/outdated:**
- `App(Var("print"|"println"), String(s))` pattern in Elaboration: After Phase 8, `print` should accept a string struct (Ptr type). But for backward compat in this phase, keep the special-case pattern AND add a general `App(Var("print"), strExpr)` case that loads `data` from the struct and calls printf.

## Open Questions

1. **print/println with string variables**
   - What we know: Current Phase 7 print/println only handles `App(Var("print"), String(s))` — i.e., static string literals
   - What's unclear: Should Phase 8 upgrade print to accept any string struct (Ptr)?
   - Recommendation: Yes — add `App(Var("print"|"println"), strExpr)` case that loads `%data` from struct and calls `@printf`. Keep the static-literal fast path for backward compat.

2. **to_string dispatch on Bool vs Int**
   - What we know: `to_string true` and `to_string 42` both use `App(Var("to_string"), arg)` where arg type must be inferred
   - What's unclear: The current Elaboration doesn't carry type info from the AST
   - Recommendation: Elaborate both sides first: if result type is I1, call `@lang_to_string_bool`; if I64, call `@lang_to_string_int`. Type is visible from the MlirValue result type after elaboration.

3. **string_concat with curried application**
   - What we know: `string_concat "foo" "bar"` in AST = `App(App(Var("string_concat"), String("foo")), String("bar"))`
   - What's unclear: Current App elaboration doesn't handle builtin multi-arg applications
   - Recommendation: Special-case `App(App(Var("string_concat"), strA), strB)` in Elaboration before the general App case, matching how print/println is special-cased.

## Sources

### Primary (HIGH confidence)

- Direct code inspection of MlirIR.fs, Elaboration.fs, Printer.fs, Pipeline.fs — exact types and patterns
- Direct code inspection of LangThree/src/LangThree/Ast.fs — String(string * span) literal node confirmed
- MLIR 20 LLVM dialect docs (pattern from existing codebase) — llvm.getelementptr inbounds syntax

### Secondary (MEDIUM confidence)

- C ABI knowledge — struct layout `{int64_t, char*}` at offsets 0 and 8 on x86-64 (natural alignment, no padding)
- libc function signatures — strcmp (i32), strlen (i64), memcpy (void), sprintf (i32)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all libraries already in use or trivially available
- Architecture: HIGH — based on direct code inspection, patterns follow Phase 7 exactly
- Pitfalls: HIGH — identified from code analysis, not speculation
- C runtime approach: HIGH — simpler than inline MLIR for dynamic pointer arithmetic

**Research date:** 2026-03-26
**Valid until:** Stable (internal codebase, no external dependencies changing)
