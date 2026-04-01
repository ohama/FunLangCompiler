# Phase 22: Array Core - Research

**Researched:** 2026-03-27
**Domain:** Array allocation, element access, mutation, and list conversion in F# compiler backend (MLIR/LLVM)
**Confidence:** HIGH

## Summary

Phase 22 implements the six array builtins (ARR-01 through ARR-06) plus dynamic-index GEP support (ARR-07). The state machine is: arrays are one-block GC heap allocations `GC_malloc((n+1)*8)` where slot 0 holds the length as i64 and slots 1..n hold elements. The MLIR side sees only `!llvm.ptr` (consistent with the uniform representation for all heap values).

The primary design tension is between full C runtime delegation (ARCHITECTURE.md recommendation) and inline GEP emission (REQUIREMENTS.md specification). STATE.md resolves this: the project decision is one-block layout with dynamic GEP. The implementation approach is hybrid: operations requiring loops (`array_create` fill, `array_of_list`, `array_to_list`) go to C runtime; operations with fixed structure (`array_length` via constant-index GEP, and `array_get`/`array_set` via a C bounds-check helper + new `LlvmGEPDynamicOp`) are implemented inline. This satisfies ARR-07 without adding full multi-block conditional branch infrastructure to the elaborator.

ARR-07 requires adding one new DU case to `MlirIR.fs` (`LlvmGEPDynamicOp`) and one new printer case to `Printer.fs`. All other six builtins elaborate using existing MlirOp cases. The C runtime gains five new functions: `lang_array_create`, `lang_array_bounds_check`, `lang_array_of_list`, `lang_array_to_list`, and `lang_array_get_ptr` (optional helper returning a pointer to the element slot). No changes to `Pipeline.fs` or `MatchCompiler.fs`.

**Primary recommendation:** Add `LlvmGEPDynamicOp` to MlirIR + Printer first, then implement C runtime functions, then wire elaboration cases for all six builtins in dependency order.

## Standard Stack

This phase introduces no new external libraries. All components already exist.

### Core (already present, extended)

| Component | Location | Purpose | v22 Change |
|-----------|----------|---------|------------|
| `MlirIR.fs` | `src/FunLangCompiler.Compiler/` | DU for MlirOp — add `LlvmGEPDynamicOp` | Add 1 DU case |
| `Printer.fs` | `src/FunLangCompiler.Compiler/` | MlirIR → MLIR text — add printer for `LlvmGEPDynamicOp` | Add 1 match arm |
| `Elaboration.fs` | `src/FunLangCompiler.Compiler/` | AST → MlirIR: add 6 builtin cases + ExternalFuncDecl entries | Add ~50 LOC |
| `lang_runtime.c` | `src/FunLangCompiler.Compiler/` | C runtime — add array_create, bounds_check, of_list, to_list | Add ~60 LOC |
| `lang_runtime.h` | `src/FunLangCompiler.Compiler/` | C header — add array function declarations | Add ~8 LOC |

### Existing MlirOp Cases Used (no new DU beyond LlvmGEPDynamicOp)

| Op | Used For |
|----|----------|
| `LlvmCallOp(result, "@lang_array_create", [nVal; defVal])` | ARR-01: allocate and fill array |
| `LlvmGEPLinearOp(lenPtr, arrPtr, 0)` + `LlvmLoadOp` | ARR-04: array_length (constant-0 index, already works) |
| `LlvmCallVoidOp("@lang_array_bounds_check", [arrPtr; idxVal])` | ARR-02/ARR-03: bounds check before GEP |
| `ArithAddIOp(idxPlus1, idxVal, one)` + `ArithConstantOp(one, 1L)` | ARR-02/ARR-03: compute i+1 for GEP |
| `LlvmGEPDynamicOp(elemPtr, arrPtr, idxPlus1)` | ARR-02/ARR-03: element slot address (NEW) |
| `LlvmLoadOp(result, elemPtr)` | ARR-02: load element value |
| `LlvmStoreOp(newVal, elemPtr)` | ARR-03: store element value |
| `ArithConstantOp(unitVal, 0L)` | ARR-03: unit return from array_set |
| `LlvmCallOp(result, "@lang_array_of_list", [listVal])` | ARR-05: list to array |
| `LlvmCallOp(result, "@lang_array_to_list", [arrVal])` | ARR-06: array to list |

**Installation:** No new packages. Build with existing `dotnet build`.

## Architecture Patterns

### Recommended Implementation Order

```
1. MlirIR.fs: Add LlvmGEPDynamicOp DU case            (unblocks all inline GEP)
2. Printer.fs: Add printer arm for LlvmGEPDynamicOp    (needed before MLIR codegen tests)
3. lang_runtime.c/h: lang_array_create                 (ARR-01; validates layout)
4. lang_runtime.c/h: lang_array_bounds_check           (ARR-02/ARR-03 dep)
5. lang_runtime.c/h: lang_array_of_list                (ARR-05)
6. lang_runtime.c/h: lang_array_to_list                (ARR-06)
7. Elaboration.fs: ExternalFuncDecl registrations      (must be before builtin cases)
8. Elaboration.fs: array_create builtin                (ARR-01)
9. Elaboration.fs: array_length builtin                (ARR-04; uses existing GEP)
10. Elaboration.fs: array_get builtin                  (ARR-02; needs LlvmGEPDynamicOp)
11. Elaboration.fs: array_set builtin                  (ARR-03; needs LlvmGEPDynamicOp)
12. Elaboration.fs: array_of_list builtin              (ARR-05)
13. Elaboration.fs: array_to_list builtin              (ARR-06)
14. E2E tests: 22-01 through 22-07
```

### Pattern 1: New MlirOp — LlvmGEPDynamicOp

**What:** A GEP with a runtime-computed SSA index (i64). Emits `llvm.getelementptr %ptr[%idx] : (!llvm.ptr, i64) -> !llvm.ptr, i64`.

**When to use:** Any array element access where the index is an SSA value (not a compile-time constant). Specifically for array_get and array_set.

```fsharp
// MlirIR.fs addition — after LlvmGEPLinearOp:
| LlvmGEPDynamicOp of result: MlirValue * ptr: MlirValue * index: MlirValue
// result.Type must be Ptr; index.Type must be I64
// emits: %result = llvm.getelementptr %ptr[%index] : (!llvm.ptr, i64) -> !llvm.ptr, i64
```

```fsharp
// Printer.fs addition — after LlvmGEPLinearOp arm:
| LlvmGEPDynamicOp(result, ptr, index) ->
    sprintf "%s%s = llvm.getelementptr %s[%s] : (!llvm.ptr, i64) -> !llvm.ptr, i64"
        indent result.Name ptr.Name index.Name
```

**MLIR syntax reference:** `%0 = llvm.getelementptr %base[%ssa_idx] : (!llvm.ptr, i64) -> !llvm.ptr, i64`
- The `i64` type annotation at the end is the element type (uniform i64 elements)
- For arrays of opaque `ptr` elements, use `!llvm.ptr` as element type instead

**Important:** For the one-block array layout, all elements are i64 (uniform representation — pointer-typed values are stored as ptrtoint i64 words). Use `i64` as the element type in the GEP type annotation.

### Pattern 2: One-Block Array Memory Layout

**What:** `GC_malloc((n+1)*8)` allocates one contiguous block. Slot 0 = length (i64). Slots 1..n = element values (i64 each, uniform representation).

```
GC_malloc((n+1)*8) → ptr
  offset 0:   length     [i64]    ← LlvmGEPLinearOp(result, arr, 0) + LlvmLoadOp
  offset 8:   element[0] [i64]    ← LlvmGEPDynamicOp(slot, arr, 1) + LlvmLoadOp
  offset 16:  element[1] [i64]
  ...
  offset 8n:  element[n-1]        ← LlvmGEPDynamicOp(slot, arr, n) + LlvmLoadOp
```

**Why one-block:** Simpler than two-block (LangArray header + separate data buffer). Fewer GC allocations. The GC sees the entire array as one scannable object. Consistent with the project decision in STATE.md.

**C runtime struct is NOT needed for this layout** — the layout is computed purely by arithmetic. C runtime helpers operate on raw `int64_t*` pointers.

### Pattern 3: C Runtime Array Functions

**What:** Three functions handle cases that require iteration or multi-allocation:

```c
// lang_runtime.c additions

typedef int64_t* LangArray;  // opaque ptr; slot 0 = length, slots 1..n = elements

/* array_create: allocate (n+1)*8 bytes, fill slots 1..n with default_val */
LangArray lang_array_create(int64_t n, int64_t default_val) {
    if (n < 0) {
        // call lang_throw with appropriate exception
        // For simplicity: call lang_failwith
        extern void lang_failwith(const char* msg);
        lang_failwith("Array.create: negative size");
        return NULL; // unreachable
    }
    int64_t* arr = (int64_t*)GC_malloc((n + 1) * 8);
    arr[0] = n;
    for (int64_t i = 1; i <= n; i++) arr[i] = default_val;
    return arr;
}

/* bounds check: calls lang_throw (via lang_failwith) if index OOB */
void lang_array_bounds_check(int64_t* arr, int64_t i) {
    int64_t len = arr[0];
    if (i < 0 || i >= len) {
        // Raise exception consistent with LangThree evaluator
        // The evaluator raises StringValue "Array.get: index N out of bounds (length M)"
        // lang_failwith calls lang_throw internally (see existing pattern)
        char msg[128];
        snprintf(msg, sizeof(msg),
                 "Array index %ld out of bounds (length %ld)",
                 (long)i, (long)len);
        extern void lang_failwith(const char*);
        lang_failwith(msg);
    }
}

/* array_of_list: traverse cons list, build one-block array */
LangArray lang_array_of_list(LangCons* list) {
    // Count length first
    int64_t n = 0;
    LangCons* cur = list;
    while (cur != NULL) { n++; cur = cur->tail; }
    int64_t* arr = (int64_t*)GC_malloc((n + 1) * 8);
    arr[0] = n;
    cur = list;
    for (int64_t i = 1; i <= n; i++) {
        arr[i] = cur->head;
        cur = cur->tail;
    }
    return arr;
}

/* array_to_list: build cons list from array elements */
LangCons* lang_array_to_list(int64_t* arr) {
    int64_t n = arr[0];
    LangCons* head = NULL;
    // Build in reverse to produce forward-order list
    for (int64_t i = n; i >= 1; i--) {
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = arr[i];
        cell->tail = head;
        head = cell;
    }
    return head;
}
```

**Note on `lang_array_bounds_check`:** The LangThree evaluator raises `LangThreeException(StringValue(...))`. To match this, `lang_array_bounds_check` should call `lang_throw` with a proper exception value. However, the simplest approach for Phase 22 is to call `lang_failwith` (which already calls `lang_throw`). This exits correctly but with a simpler message format. Match the exact evaluator message format if test 22-05 checks the exception payload.

### Pattern 4: array_length Elaboration (Inline, ARR-04)

**What:** `array_length arr` → `GEP(arr, 0)` + `LlvmLoadOp`. Slot 0 of the one-block array IS the length. Uses existing `LlvmGEPLinearOp` with constant index 0.

```fsharp
// array_length: App(Var("array_length"), arrExpr)
| App (Var ("array_length", _), arrExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let lenPtrVal = { Name = freshName env; Type = Ptr }
    let lenVal    = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmGEPLinearOp(lenPtrVal, arrVal, 0)
        LlvmLoadOp(lenVal, lenPtrVal)
    ]
    (lenVal, arrOps @ ops)
```

**Why inline:** Identical pattern to `string_length` which already uses `LlvmGEPStructOp`. No new ops needed. One op per call vs. one C runtime call.

### Pattern 5: array_get Elaboration (Hybrid, ARR-02)

**What:** Bounds check via C runtime, then inline GEP + load.

```fsharp
// array_get: App(App(Var("array_get"), arrExpr), idxExpr)
| App (App (Var ("array_get", _), arrExpr, _), idxExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let (idxVal, idxOps) = elaborateExpr env idxExpr
    let oneVal     = { Name = freshName env; Type = I64 }
    let idxPlus1   = { Name = freshName env; Type = I64 }
    let elemPtr    = { Name = freshName env; Type = Ptr }
    let result     = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmCallVoidOp("@lang_array_bounds_check", [arrVal; idxVal])  // raises on OOB
        ArithConstantOp(oneVal, 1L)
        ArithAddIOp(idxPlus1, idxVal, oneVal)                         // i + 1
        LlvmGEPDynamicOp(elemPtr, arrVal, idxPlus1)                   // &arr[i+1]
        LlvmLoadOp(result, elemPtr)                                    // load element
    ]
    (result, arrOps @ idxOps @ ops)
```

### Pattern 6: array_set Elaboration (Hybrid, ARR-03)

**What:** Bounds check via C runtime, then inline GEP + store. Returns unit.

```fsharp
// array_set: App(App(App(Var("array_set"), arrExpr), idxExpr), valExpr)
| App (App (App (Var ("array_set", _), arrExpr, _), idxExpr, _), valExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let (idxVal, idxOps) = elaborateExpr env idxExpr
    let (newVal, valOps) = elaborateExpr env valExpr
    let oneVal   = { Name = freshName env; Type = I64 }
    let idxPlus1 = { Name = freshName env; Type = I64 }
    let elemPtr  = { Name = freshName env; Type = Ptr }
    let unitVal  = { Name = freshName env; Type = I64 }
    let ops = [
        LlvmCallVoidOp("@lang_array_bounds_check", [arrVal; idxVal])
        ArithConstantOp(oneVal, 1L)
        ArithAddIOp(idxPlus1, idxVal, oneVal)
        LlvmGEPDynamicOp(elemPtr, arrVal, idxPlus1)
        LlvmStoreOp(newVal, elemPtr)
        ArithConstantOp(unitVal, 0L)
    ]
    (unitVal, arrOps @ idxOps @ valOps @ ops)
```

### Pattern 7: ExternalFuncDecl Registrations

Add to both `externalFuncs` lists in `Elaboration.fs` (lines ~2036 and ~2169):

```fsharp
{ ExtName = "@lang_array_create";       ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_bounds_check"; ExtParams = [Ptr; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_of_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_to_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

**Note:** No entry for `array_get` or `array_set` — those are implemented inline with `LlvmGEPDynamicOp`. No entry for `array_length` — also inline.

### Pattern 8: array_create and Conversion Elaboration

```fsharp
// array_create: App(App(Var("array_create"), nExpr), defExpr)
| App (App (Var ("array_create", _), nExpr, _), defExpr, _) ->
    let (nVal, nOps)    = elaborateExpr env nExpr
    let (defVal, defOps) = elaborateExpr env defExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, nOps @ defOps @ [LlvmCallOp(result, "@lang_array_create", [nVal; defVal])])

// array_of_list: App(Var("array_of_list"), listExpr)
| App (Var ("array_of_list", _), listExpr, _) ->
    let (listVal, listOps) = elaborateExpr env listExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, listOps @ [LlvmCallOp(result, "@lang_array_of_list", [listVal])])

// array_to_list: App(Var("array_to_list"), arrExpr)
| App (Var ("array_to_list", _), arrExpr, _) ->
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, arrOps @ [LlvmCallOp(result, "@lang_array_to_list", [arrVal])])
```

### Pattern 9: Placement in elaborateExpr Match

Array builtins must be placed BEFORE the general `App` case, following the existing precedence pattern. Order within array cases: three-arg first (`array_set`), then two-arg (`array_get`, `array_create`), then one-arg (`array_length`, `array_of_list`, `array_to_list`). This mirrors the existing `string_sub` (three-arg) → `string_concat` (two-arg) → `string_length` (one-arg) ordering.

### Anti-Patterns to Avoid

- **Using `LlvmGEPStructOp` for element access:** `LlvmGEPStructOp` has a hardcoded struct type `!llvm.struct<(i64, ptr)>` in its printer output — wrong for the flat i64-element array. Use `LlvmGEPLinearOp` for constant-index slot 0 (length), and `LlvmGEPDynamicOp` for SSA-index element slots.
- **Skipping bounds check before GEP:** The GEP would silently access adjacent heap memory. The C runtime `lang_array_bounds_check` must be called first; it raises the appropriate exception.
- **Two-block layout (LangArray header + separate data ptr):** The project decision is one-block. Do not create a `{length, data*}` struct — that was ARCHITECTURE.md's original proposal but was superseded by STATE.md's decision.
- **Implementing array_of_list / array_to_list inline:** These require loop iteration over linked lists, which the current elaborator cannot express without `LetRec`. Always call C runtime for these.
- **Forgetting to update both ExternalFuncDecl lists:** There are two identical lists in `Elaboration.fs` at lines ~2036 and ~2169 (for two compilation paths). Both must be updated.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Array initialization loop | Inline MLIR loop | `lang_array_create` C function | Elaborator has no loop construct; C handles fill simply |
| List→array conversion | Inline MLIR traversal | `lang_array_of_list` C function | Requires list length count + element copy loop; needs C |
| Array→list conversion | Inline MLIR traversal | `lang_array_to_list` C function | Reverse-build cons list loop; needs C |
| Bounds check conditional branch | Multi-block if/else in MLIR | `lang_array_bounds_check` C call | Avoids adding block-level control flow to elaborator; C call terminates on OOB |
| Dynamic GEP index | LlvmGEPLinearOp (int) | New LlvmGEPDynamicOp (MlirValue) | LlvmGEPLinearOp only accepts compile-time `int`; cannot use SSA value |

**Key insight:** The hybrid approach (C runtime for loops/initialization, inline GEP for element access after bounds check) is the minimal change that satisfies all requirements without adding loop constructs or multi-block if/else to the elaborator.

## Common Pitfalls

### Pitfall 1: Forgetting That Slot 0 Is Length, Slots 1..n Are Elements (One-Based Indexing in Memory)

**What goes wrong:** Using `GEP(arr, i)` to access element at logical index `i`. This reads the wrong slot — element 0 is at physical slot 1, element 1 is at physical slot 2, etc.

**Why it happens:** Natural instinct is zero-based physical indexing. The one-block layout adds one extra slot at the front.

**How to avoid:** Always emit `ArithConstantOp(1L)` + `ArithAddIOp(idxPlus1, idx, 1)` before `LlvmGEPDynamicOp`. For `array_length`, use `GEP(arr, 0)` — the length is at slot 0.

**Warning signs:** `array_get arr 0` returns what should be `array_get arr 1`; `array_length` returns a garbage value (actually the first element).

### Pitfall 2: Bounds Check Uses Wrong Comparand (Off-By-One on Length)

**What goes wrong:** Valid indices are `0 <= i < length`. An OOB check that allows `i == length` silently reads one slot past the end (which is `GC_malloc`'d but belongs to another allocation or is uninitialized).

**Why it happens:** "Less than or equal" vs "strictly less than" confusion.

**How to avoid:** In `lang_array_bounds_check`: `if (i < 0 || i >= len)`. NOT `i > len`. Test the exact boundary: index `length - 1` must succeed, index `length` must throw.

**Warning signs:** `array_get arr (array_length arr - 1)` works; `array_get arr (array_length arr)` should throw but doesn't.

### Pitfall 3: Wrong Element Type in LlvmGEPDynamicOp Printer Output

**What goes wrong:** The MLIR `llvm.getelementptr` type annotation must specify the element type. For the one-block layout, elements are `i64`. If the printer emits `!llvm.ptr` as the element type, MLIR will compute pointer-sized (8-byte) strides — which is correct on 64-bit, but the MLIR verifier may reject `!llvm.ptr` as an element type for GEP on an opaque pointer.

**How to avoid:** The correct printer output is:
```
%result = llvm.getelementptr %ptr[%idx] : (!llvm.ptr, i64) -> !llvm.ptr, i64
```
The trailing `i64` is the element type. This tells MLIR each element is 8 bytes — exactly right for `int64_t` slots.

**Warning signs:** `mlir-opt` fails with "getelementptr index type mismatch" or "element type mismatch".

### Pitfall 4: defVal Type Mismatch in array_create

**What goes wrong:** `array_create n default` where `default` is a pointer-typed value (e.g., a string). The elaborated `defVal` is `Ptr`-typed. But `lang_array_create` takes `int64_t default_val`. Passing `Ptr` to an `I64` parameter.

**Why it happens:** The uniform representation means pointer values are GC heap addresses. At the MLIR level, `Ptr` and `I64` are different types.

**How to avoid:** Use `LlvmPtrToIntOp` to coerce `Ptr`-typed `defVal` to `I64` before passing to `lang_array_create`. Or, change the C signature to take `void* default_val` (i.e., `ExtParams = [I64; Ptr]`) and accept any value as opaque bits. The simplest approach: store elements as `i64` uniformly. If `defVal.Type = Ptr`, emit `LlvmPtrToIntOp` before the call.

**Warning signs:** MLIR verifier error: "type mismatch in call argument"; string-initialized arrays produce wrong values.

### Pitfall 5: Missing ExternalFuncDecl for lang_array_bounds_check

**What goes wrong:** The elaborator emits `LlvmCallVoidOp("@lang_array_bounds_check", ...)` but no `ExternalFuncDecl` declares the function signature. `mlir-opt` fails with "undefined external function @lang_array_bounds_check".

**Why it happens:** There are TWO `externalFuncs` lists in `Elaboration.fs` (for two compilation code paths). Updating only one produces intermittent failures depending on which path is taken.

**How to avoid:** Search for `@lang_range` or `@lang_throw` and add the new declarations at BOTH occurrences.

**Warning signs:** `mlir-opt` error "use of undefined value '@lang_array_bounds_check'"; tests fail only in one compilation mode.

### Pitfall 6: Omitting lang_array_bounds_check from lang_runtime.h

**What goes wrong:** The C function is implemented in `lang_runtime.c` but not declared in `lang_runtime.h`. Clang compiles `lang_runtime.c` without complaint (the definition is there), but if any other C file includes `lang_runtime.h`, the declaration is missing. More practically, `lang_failwith` inside `lang_array_bounds_check` needs `extern void lang_failwith(const char*)` — include the header.

**How to avoid:** Declare all new functions in `lang_runtime.h`. Include `lang_runtime.h` at the top of `lang_runtime.c` (already done).

### Pitfall 7: GC_malloc vs GC_malloc_atomic for Array Allocation

**What goes wrong:** The one-block array stores elements as `i64` words. If elements contain pointer values (e.g., `string array`), using `GC_malloc_atomic` prevents the GC from tracing those pointers, leading to premature collection of live objects.

**How to avoid:** Use `GC_malloc` (not `GC_malloc_atomic`) in `lang_array_create`. Phase 22 uses a polymorphic layout — the GC must conservatively scan all slots. `GC_malloc_atomic` should only be used for pure-integer buffers where no slot is a live GC pointer.

**Warning signs:** Strings stored in arrays are randomly collected; programs using `string array` crash non-deterministically.

## Code Examples

### Full Codegen for array_get arr 2

```mlir
; Source: Pattern 5 above
; let arr = ... (Ptr)
; array_get arr 2

; idxVal = arith.constant 2
%t0 = arith.constant 2 : i64

; bounds check (raises if 2 >= length)
llvm.call @lang_array_bounds_check(%arr, %t0) : (!llvm.ptr, i64) -> ()

; compute slot index: 2 + 1 = 3
%t1 = arith.constant 1 : i64
%t2 = arith.addi %t0, %t1 : i64

; GEP to slot 3 (element at index 2)
%t3 = llvm.getelementptr %arr[%t2] : (!llvm.ptr, i64) -> !llvm.ptr, i64

; load the element
%t4 = llvm.load %t3 : !llvm.ptr -> i64
```

### Full Codegen for array_length arr

```mlir
; Source: Pattern 4 above — slot 0 holds length

; GEP to slot 0
%t0 = llvm.getelementptr %arr[0] : (!llvm.ptr) -> !llvm.ptr, i64

; load length
%t1 = llvm.load %t0 : !llvm.ptr -> i64
```

### C lang_array_create (Correct One-Block Layout)

```c
// Source: Pattern 3 above
// Slot 0 = n (length), slots 1..n = default_val
int64_t* lang_array_create(int64_t n, int64_t default_val) {
    if (n < 0) { lang_failwith("Array.create: negative size"); return NULL; }
    int64_t* arr = (int64_t*)GC_malloc((n + 1) * sizeof(int64_t));
    arr[0] = n;
    for (int64_t i = 1; i <= n; i++) arr[i] = default_val;
    return arr;
}
```

### lang_array_to_list Correct Order

```c
// Elements stored arr[1], arr[2], ..., arr[n]
// cons list: arr[1] :: arr[2] :: ... :: arr[n] :: []
// Build by iterating backwards to produce forward list:
LangCons* lang_array_to_list(int64_t* arr) {
    int64_t n = arr[0];
    LangCons* head = NULL;
    for (int64_t i = n; i >= 1; i--) {  // reverse iteration
        LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
        cell->head = arr[i];
        cell->tail = head;
        head = cell;
    }
    return head;
}
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|-----------------|--------|
| No array support (v4.0) | One-block GC_malloc layout + C runtime + inline GEP | Arrays fully supported |
| LlvmGEPLinearOp (constant index only) | LlvmGEPDynamicOp (SSA value index) added | Runtime array element addressing |
| All complex ops in C runtime | Hybrid: C for init/loops, inline GEP for element access | Cleaner codegen, fewer C function calls for get/set |

**No deprecated patterns.** This phase extends the existing architecture without replacing anything.

## Open Questions

1. **defVal pointer coercion in array_create**
   - What we know: `lang_array_create` takes `(int64_t n, int64_t default_val)`. If `defVal` is `Ptr`-typed (e.g., string), MLIR will reject passing `!llvm.ptr` to an `i64` parameter.
   - What's unclear: Do any Phase 22 tests use non-integer default values?
   - Recommendation: Change `lang_array_create` signature to `(int64_t n, void* default_val)` i.e. `ExtParams = [I64; Ptr]` — this accepts both scalars (ptrtoint coerced) and pointers uniformly. Alternatively, handle type-dependent coercion in the elaborator with `LlvmPtrToIntOp`. The safest approach: use `Ptr` as the second param type and `LlvmIntToPtrOp`/`LlvmPtrToIntOp` as needed.

2. **Exception payload format for OOB**
   - What we know: The evaluator raises `StringValue(sprintf "Array.get: index %d out of bounds (length %d)" i arr.Length)`.
   - What's unclear: Do the Phase 22 E2E tests check the OOB exception message exactly?
   - Recommendation: Use `lang_failwith` in `lang_array_bounds_check` for simplicity. If a test checks the exact message, switch to constructing a proper exception value and calling `lang_throw` directly.

3. **LlvmGEPDynamicOp element type for Ptr-element arrays**
   - What we know: In the one-block layout, elements are stored as `i64` (uniform representation). Pointer values are stored via ptrtoint coercion.
   - What's unclear: When loading a `Ptr`-typed element (e.g., from `string array`), the load type annotation must match the expected MLIR type.
   - Recommendation: Always emit `LlvmLoadOp` with `result.Type = I64` for array elements, then use `LlvmIntToPtrOp` if the consumer needs a `Ptr`. This is consistent with how cons cell elements work.

## Sources

### Primary (HIGH confidence)
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/MlirIR.fs` — confirmed `LlvmGEPLinearOp` takes `int` (compile-time constant); no dynamic variant exists
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Printer.fs` — confirmed GEP printer output format; `LlvmGEPLinearOp` → `llvm.getelementptr %ptr[N]`
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — confirmed string_length inline GEP pattern; ExternalFuncDecl list locations (lines ~2036, ~2169); two-list requirement
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` — confirmed LangCons struct layout; GC_malloc usage patterns; lang_range loop pattern
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/STATE.md` — authoritative project decision: one-block GC_malloc((n+1)*8) layout
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/REQUIREMENTS.md` — authoritative requirements: ARR-01 through ARR-07 specifications
- Direct code analysis of `../LangThree/src/LangThree/Eval.fs` — confirmed array builtin semantics: bounds check + OOB exception; array_of_list/array_to_list round-trip behavior

### Secondary (MEDIUM confidence)
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/research/ARCHITECTURE.md` — array section; note: C-runtime-only recommendation superseded by REQUIREMENTS.md inline GEP spec, but C function signatures remain accurate
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/research/PITFALLS.md` — M-17 (bounds check), M-18 (GC_malloc variant) confirmed
- WebSearch: MLIR `llvm.getelementptr` with SSA-value index syntax confirmed: `%r = llvm.getelementptr %ptr[%idx] : (!llvm.ptr, i64) -> !llvm.ptr, T` (https://mlir.llvm.org/docs/Dialects/LLVM/)

### Tertiary (LOW confidence)
- `/Users/ohama/vibe-coding/FunLangCompiler/.planning/research/FEATURES.md` — original two-vs-one-block analysis; superseded by STATE.md decision

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new external deps; all required ops identified by direct code analysis
- Architecture: HIGH — one-block layout confirmed in STATE.md (authoritative decision record); LlvmGEPDynamicOp syntax confirmed by WebSearch MLIR docs
- Pitfalls: HIGH — off-by-one (slot 0 = length), bounds comparand, GC_malloc vs atomic, double ExternalFuncDecl — all derived from direct code + existing pitfall research

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable domain — only changes if MLIR dialect GEP syntax changes or project layout decision is revised)
