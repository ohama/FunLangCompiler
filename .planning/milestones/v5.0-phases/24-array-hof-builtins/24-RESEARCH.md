# Phase 24: Array HOF Builtins - Research

**Researched:** 2026-03-27
**Domain:** Higher-order array builtins (iter, map, fold, init) in F# compiler backend (MLIR/LLVM) — closure ABI, C runtime callback design
**Confidence:** HIGH

## Summary

Phase 24 implements four higher-order array builtins: `array_iter`, `array_map`, `array_fold`, and `array_init`. These functions take a closure argument (function passed as a value) and apply it across array elements in a loop. The fundamental design question is how to call a user-supplied closure from inside a loop iteration.

The closure ABI in this compiler is already well-defined: every closure is a heap-allocated block where slot 0 is an `!llvm.ptr` function pointer, and slots 1..n are captures. Calls go through `IndirectCallOp` which emits `llvm.call %fnPtr(%envPtr, %arg) : !llvm.ptr, (!llvm.ptr, i64) -> i64`. This ABI is shared between MLIR-generated code and the C runtime. C can call a closure using a matching function-pointer typedef — `int64_t (*fn)(void* env, int64_t arg)` — which is exactly what the inner llvm.func body expects.

The right approach is **C runtime functions that accept a closure pointer and use the existing closure ABI**. The elaborator has no loop construct (no `ForOp`/`WhileOp`/`scf.for` infrastructure). Implementing inline MLIR loops would require adding multi-block basic-block control flow with loop counters and phi-style block arguments — a substantial elaborator change not justified by these four builtins. C runtime callbacks are clean, require zero new MlirOp cases, zero new Printer cases, and directly reuse the established closure layout.

**Primary recommendation:** Implement `lang_array_iter`, `lang_array_map`, `lang_array_fold`, and `lang_array_init` as C runtime functions that accept a closure `void*` and call back into MLIR-generated code via the existing closure ABI (`int64_t (*fn)(void* env, int64_t arg)`). Elaborate each HOF builtin as a matched App pattern that emits `LlvmCallOp`/`LlvmCallVoidOp` to the corresponding C function, passing the closure ptr and array ptr as arguments.

## Standard Stack

This phase introduces no new external libraries. All components already exist.

### Core (already present, extended)

| Component | Location | Purpose | v24 Change |
|-----------|----------|---------|------------|
| `lang_runtime.c` | `src/FunLangCompiler.Compiler/` | C runtime — add 4 HOF functions | Add ~80 LOC |
| `lang_runtime.h` | `src/FunLangCompiler.Compiler/` | C header — declare 4 HOF functions | Add ~6 LOC |
| `Elaboration.fs` | `src/FunLangCompiler.Compiler/` | AST → MlirIR: add 4 HOF builtin cases + ExternalFuncDecl entries | Add ~60 LOC |

### Existing MlirOp Cases Used (NO new DU cases needed)

| Op | Used For |
|----|----------|
| `LlvmCallVoidOp("@lang_array_iter", [closurePtr; arrPtr])` | ARR-08: array_iter |
| `LlvmCallOp(result, "@lang_array_map", [closurePtr; arrPtr])` | ARR-09: array_map |
| `LlvmCallOp(result, "@lang_array_fold", [closurePtr; initVal; arrPtr])` | ARR-10: array_fold |
| `LlvmCallOp(result, "@lang_array_init", [nVal; closurePtr])` | ARR-11: array_init |
| `LlvmPtrToIntOp` / `LlvmIntToPtrOp` | Coerce closure between Ptr/I64 as needed |

**Installation:** No new packages. Build with existing `dotnet build`.

## Architecture Patterns

### Recommended Implementation Order

```
1. lang_runtime.h: Declare LangClosure typedef + 4 function prototypes
2. lang_runtime.c: Implement lang_array_iter      (ARR-08; validates callback loop)
3. lang_runtime.c: Implement lang_array_map       (ARR-09; allocates output array)
4. lang_runtime.c: Implement lang_array_fold      (ARR-10; two-arg callback)
5. lang_runtime.c: Implement lang_array_init      (ARR-11; produces array from index fn)
6. Elaboration.fs: ExternalFuncDecl entries (BOTH lists at lines ~2205 and ~2348)
7. Elaboration.fs: array_iter builtin case        (4-arg App chain: ARR-08)
8. Elaboration.fs: array_map builtin case         (3-arg App chain: ARR-09)
9. Elaboration.fs: array_fold builtin case        (4-arg App chain: ARR-10)
10. Elaboration.fs: array_init builtin case       (3-arg App chain: ARR-11)
11. E2E tests: 24-01 through 24-04 (one per HOF)
```

### Pattern 1: Closure ABI for C Callback

**What:** Every closure block (allocated by the closure-maker func.func) has layout:
```
slot 0: fn_ptr (!llvm.ptr) — pointer to the inner llvm.func
slot 1: capture[0] (i64)
slot 2: capture[1] (i64)
...
```
The inner `llvm.func` signature is always `(%arg0: !llvm.ptr, %arg1: i64) -> i64`. The C runtime can call this with:
```c
typedef int64_t (*LangClosureFn)(void* env, int64_t arg);
```
This matches the MLIR ABI because `!llvm.ptr` lowers to `void*` in C and `i64` lowers to `int64_t`.

**When to use:** Whenever C runtime code needs to invoke a user-supplied closure.

**How to call:**
```c
// Source: Direct code analysis of Printer.fs IndirectCallOp pattern
// and Elaboration.fs closure-maker/inner-func structure

typedef int64_t (*LangClosureFn)(void* env, int64_t arg);

static inline int64_t lang_call_closure(void* closure, int64_t arg) {
    LangClosureFn fn = *(LangClosureFn*)closure;  // load fn_ptr from slot 0
    return fn(closure, arg);                        // call with (env=closure, arg)
}
```

### Pattern 2: lang_array_iter (ARR-08)

**Type:** `(a -> unit) -> a array -> unit`

The return value of the callback is discarded (it's `unit` = 0). The function returns `void`.

```c
// Source: Direct analysis of array layout and closure ABI
// Array: slot 0 = length, slots 1..n = elements (i64)

void lang_array_iter(void* closure, int64_t* arr) {
    int64_t n = arr[0];
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 1; i <= n; i++) {
        fn(closure, arr[i]);   // discard return value (unit)
    }
}
```

**Elaboration pattern** (`array_iter f arr` — two-arg after currying resolves):
```fsharp
// App(App(Var("array_iter"), fExpr), arrExpr)
| App (App (Var ("array_iter", _), fExpr, _), arrExpr, _) ->
    let (fVal,   fOps)   = elaborateExpr env fExpr
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    // f may be Ptr (closure) or I64 (passed through uniform ABI)
    let (closurePtr, coerceOps) = coerceToPtr env fVal
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = coerceOps @ [
        LlvmCallVoidOp("@lang_array_iter", [closurePtr; arrVal])
        ArithConstantOp(unitVal, 0L)
    ]
    (unitVal, fOps @ arrOps @ ops)
```

### Pattern 3: lang_array_map (ARR-09)

**Type:** `(a -> b) -> a array -> b array`

Allocates a new output array of the same length, fills it by calling the closure on each element.

```c
int64_t* lang_array_map(void* closure, int64_t* arr) {
    int64_t n = arr[0];
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 1; i <= n; i++) {
        out[i] = fn(closure, arr[i]);
    }
    return out;
}
```

**Elaboration pattern** (`array_map f arr` — two-arg):
```fsharp
| App (App (Var ("array_map", _), fExpr, _), arrExpr, _) ->
    let (fVal,   fOps)   = elaborateExpr env fExpr
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let (closurePtr, coerceOps) = coerceToPtr env fVal
    let result = { Name = freshName env; Type = Ptr }
    let ops = coerceOps @ [ LlvmCallOp(result, "@lang_array_map", [closurePtr; arrVal]) ]
    (result, fOps @ arrOps @ ops)
```

### Pattern 4: lang_array_fold (ARR-10)

**Type:** `(acc -> a -> acc) -> acc -> a array -> acc`

The fold function is a two-argument closure. The inner llvm.func ABI only takes one argument. Therefore, calling `f acc x` requires two applications: first `f acc` returns a partial closure, then `(f acc) x` applies that closure to `x`.

This means `lang_array_fold` must call the closure twice per iteration — first to partially apply `acc`, then to apply `x`:

```c
int64_t lang_array_fold(void* closure, int64_t init, int64_t* arr) {
    int64_t n = arr[0];
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t acc = init;
    for (int64_t i = 1; i <= n; i++) {
        // First application: f acc -> partial closure (returned as i64 via ptrtoint)
        int64_t partial = fn(closure, acc);
        // partial is an i64 that is actually a ptrtoint'd closure pointer
        void* partial_ptr = (void*)partial;
        LangClosureFn fn2 = *(LangClosureFn*)partial_ptr;
        // Second application: (f acc) x -> new acc
        acc = fn2(partial_ptr, arr[i]);
    }
    return acc;
}
```

**Key insight:** The fold function `(fun acc x -> acc + x)` is curried in this compiler. It compiles to a closure-maker that takes `acc` and returns another closure that takes `x`. The first call to `fn(closure, acc)` returns a new closure (as a ptrtoint i64, following the uniform representation). The second call applies that closure to the array element.

**Elaboration pattern** (`array_fold f init arr` — three-arg):
```fsharp
| App (App (App (Var ("array_fold", _), fExpr, _), initExpr, _), arrExpr, _) ->
    let (fVal,    fOps)    = elaborateExpr env fExpr
    let (initVal, initOps) = elaborateExpr env initExpr
    let (arrVal,  arrOps)  = elaborateExpr env arrExpr
    let (closurePtr, coerceOps) = coerceToPtr env fVal
    let (initI64, initCoerceOps) = coerceToI64 env initVal
    let result = { Name = freshName env; Type = I64 }
    let ops = coerceOps @ initCoerceOps @ [
        LlvmCallOp(result, "@lang_array_fold", [closurePtr; initI64; arrVal])
    ]
    (result, fOps @ initOps @ arrOps @ ops)
```

### Pattern 5: lang_array_init (ARR-11)

**Type:** `int -> (int -> a) -> a array`

Creates an array of length `n` where each element is `f(i)` for `i` in `0..n-1`.

```c
int64_t* lang_array_init(int64_t n, void* closure) {
    if (n < 0) {
        lang_failwith("array_init: negative size");
        return NULL; /* unreachable */
    }
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < n; i++) {
        out[i + 1] = fn(closure, i);  // logical index i, physical slot i+1
    }
    return out;
}
```

**Note:** The closure argument is passed as `i` (zero-based logical index), and the result is stored at physical slot `i+1`.

**Elaboration pattern** (`array_init n f` — two-arg):
```fsharp
| App (App (Var ("array_init", _), nExpr, _), fExpr, _) ->
    let (nVal, nOps) = elaborateExpr env nExpr
    let (fVal, fOps) = elaborateExpr env fExpr
    let (nI64, nCoerceOps) = coerceToI64 env nVal
    let (closurePtr, fCoerceOps) = coerceToPtr env fVal
    let result = { Name = freshName env; Type = Ptr }
    let ops = nCoerceOps @ fCoerceOps @ [
        LlvmCallOp(result, "@lang_array_init", [nI64; closurePtr])
    ]
    (result, nOps @ fOps @ ops)
```

### Pattern 6: coerceToPtr Helper

The `fVal` (closure argument) may be `Ptr` or `I64` depending on how it was passed (e.g., via a let-binding vs. through the uniform ABI). Use this inline helper:

```fsharp
// Inline helper — not a named function, just inline code
let (closurePtr, coerceOps) =
    if fVal.Type = Ptr then
        (fVal, [])
    else
        // I64 passed through uniform ABI — inttoptr
        let ptrVal = { Name = freshName env; Type = Ptr }
        (ptrVal, [LlvmIntToPtrOp(ptrVal, fVal)])
```

Similarly for `coerceToI64` (for `initVal` in fold):
```fsharp
let (initI64, initCoerceOps) =
    if initVal.Type = I64 then (initVal, [])
    elif initVal.Type = I1  then
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithExtuIOp(v, initVal)])
    else (* Ptr *)
        let v = { Name = freshName env; Type = I64 }
        (v, [LlvmPtrToIntOp(v, initVal)])
```

### Pattern 7: ExternalFuncDecl Registrations

Add to **BOTH** `externalFuncs` lists in `Elaboration.fs` (at lines ~2205 and ~2348):

```fsharp
{ ExtName = "@lang_array_iter"; ExtParams = [Ptr; Ptr]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_map";  ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_fold"; ExtParams = [Ptr; I64; Ptr]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_init"; ExtParams = [I64; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Pattern 8: Placement in elaborateExpr Match

HOF builtins must be placed **before the general `App` case** and **after the existing Phase 22/23 array/hashtable cases**. Order: three-arg first (`array_fold`), then two-arg (`array_iter`, `array_map`, `array_init`).

Insert before line ~970 (after the `hashtable_create` case, before `char_to_int`):
```fsharp
// Phase 24: Array HOF builtins

// array_fold — three-arg (must appear before two-arg)
| App (App (App (Var ("array_fold", _), fExpr, _), initExpr, _), arrExpr, _) -> ...

// array_iter — two-arg
| App (App (Var ("array_iter", _), fExpr, _), arrExpr, _) -> ...

// array_map — two-arg
| App (App (Var ("array_map", _), fExpr, _), arrExpr, _) -> ...

// array_init — two-arg
| App (App (Var ("array_init", _), nExpr, _), fExpr, _) -> ...
```

### Anti-Patterns to Avoid

- **Inline MLIR loop emission:** The elaborator has no `ForOp`, `WhileOp`, or `scf.for`. Adding multi-block loops would require block-argument phi infrastructure — a much larger change. Use C runtime instead.
- **C runtime with raw function pointer (not closure-aware):** Passing only a function pointer without the env ptr breaks closures that capture variables. Always pass the full closure block (slot 0 = fn_ptr, slots 1+ = captures).
- **Calling the closure fn_ptr with `(arg, env)` order:** The MLIR convention is `fn(env, arg)` — env first, arg second. Confirm from `IndirectCallOp` printer: `llvm.call %fnPtr(%envPtr, %arg)`.
- **Forgetting that `array_fold`'s closure is curried:** `(fun acc x -> ...)` compiles as two nested closures. The outer closure takes `acc` and returns an inner closure. C runtime must do two closure calls per iteration.
- **Returning I64 from lang_array_iter:** `array_iter` is `unit`-returning. The C function should be `void`. The elaboration adds `ArithConstantOp(unitVal, 0L)` as the MLIR-level return.
- **Forgetting to update both ExternalFuncDecl lists:** There are two identical `externalFuncs` lists (~line 2205 and ~line 2348). Both must be updated.
- **Using `GC_malloc_atomic` in `lang_array_map`/`lang_array_init`:** Output arrays may contain pointer-typed values (e.g., `string array`). Use `GC_malloc` so the GC can trace all slots conservatively.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Loop over array elements | Inline MLIR multi-block CFG | C runtime function | Elaborator has no loop ops; adding them is major work |
| Curried fold callback | Single-call callback | Two-call sequence (outer then inner closure) | All binary functions are curried — two calls required |
| Closure invocation | Manual GEP+load+call pattern in C | `*(LangClosureFn*)closure)(closure, arg)` idiom | Consistent with compiler's closure ABI |

**Key insight:** All four HOFs delegate iteration entirely to C. The elaborator only needs to pass the closure block pointer and array pointer to the C function. Zero new MlirOp variants are needed.

## Common Pitfalls

### Pitfall 1: array_fold Needs Two Closure Calls Per Iteration

**What goes wrong:** Writing `lang_array_fold` as a single call `fn(closure, elem)` produces wrong results or a crash because the fold function is `(acc -> a -> acc)`, which is a curried two-argument function compiled as two nested closures.

**Why it happens:** In LangThree's evaluator, `callValue (callValue fVal acc) x` makes this explicit. In C, the first call returns an intermediate closure (as `int64_t` via ptrtoint), which must then be called with the element.

**How to avoid:** In `lang_array_fold`:
1. `int64_t partial = fn(closure, acc)` — first call returns inner closure as i64
2. Cast: `void* partial_ptr = (void*)partial`
3. `acc = (*(LangClosureFn*)partial_ptr)(partial_ptr, arr[i])` — second call

**Warning signs:** `array_fold (fun acc x -> acc + x) 0 [|1;2;3|]` returns `3` instead of `6` (only last element counted); or segfault when C tries to use `partial` as a function pointer directly.

### Pitfall 2: Closure Argument May Be I64 (Needs inttoptr Coercion)

**What goes wrong:** When a closure is passed through the uniform ABI (e.g., returned from another function, or passed as a `let f = fun x -> ...`), its value in MLIR is `I64` (a ptrtoint'd pointer). Emitting `LlvmCallVoidOp("@lang_array_iter", [fVal; arrVal])` where `fVal.Type = I64` and the C signature expects `!llvm.ptr` will cause an MLIR type mismatch.

**Why it happens:** The uniform representation passes closures as i64. The elaborator's general App case handles this with `LlvmIntToPtrOp`, but the builtin cases must do it explicitly.

**How to avoid:** Always check `fVal.Type` and emit `LlvmIntToPtrOp` when it is `I64`. See Pattern 6 (coerceToPtr helper).

**Warning signs:** `mlir-opt` error "type mismatch: expected '!llvm.ptr', got 'i64'" on the HOF call.

### Pitfall 3: array_iter Must Return Unit (i64 0), Not Void

**What goes wrong:** Making the elaboration of `array_iter` return nothing (or a `void`-typed value). The `elaborateExpr` function always returns a `(MlirValue * MlirOp list)` — the result must be a valid SSA value. `array_iter`'s MLIR call is void, but the surrounding expression still needs a value.

**How to avoid:** After `LlvmCallVoidOp`, emit `ArithConstantOp(unitVal, 0L)` and return `unitVal`. This matches the `unit = 0` convention used by all other void-returning builtins (e.g., `array_set`, `hashtable_set`).

**Warning signs:** F# compile error in Elaboration.fs "type mismatch" on the HOF match arm; or `mlir-opt` complaining about a missing result value.

### Pitfall 4: array_init Index Is Zero-Based, Physical Slot Is i+1

**What goes wrong:** In `lang_array_init`, calling `fn(closure, i)` where `i` starts at `1` rather than `0`. This produces the sequence `f(1), f(2), ...` instead of `f(0), f(1), ...`.

**Why it happens:** Confusion between logical indices (0-based user-visible) and physical slots (1-based array layout).

**How to avoid:** Loop `i` from `0` to `n-1` (for the function argument), store at `out[i+1]` (physical slot). The `array_init 5 (fun i -> i * i)` test checks for `[0; 1; 4; 9; 16]` — if `f(0)=0` is wrong, the index starts at the wrong value.

**Warning signs:** `array_init 5 (fun i -> i * i)` produces `[1; 4; 9; 16; 25]` (shifted by one).

### Pitfall 5: fold's init Argument May Be Ptr-Typed (Needs ptrtoint)

**What goes wrong:** The accumulator argument to `lang_array_fold` is declared as `int64_t init` in C, but if the initial value is a pointer (e.g., folding strings or other heap values), the MLIR `initVal` will have type `Ptr`. Passing `Ptr` where `I64` is expected causes an MLIR type error.

**How to avoid:** Use the `coerceToI64` pattern (Pattern 6) on `initVal` before emitting the call. This matches the uniform representation: all values round-trip through i64 across the ABI boundary.

**Warning signs:** MLIR verifier error "type mismatch in call argument" for `lang_array_fold`.

### Pitfall 6: Missing Declaration of LangClosureFn in lang_runtime.c

**What goes wrong:** `lang_runtime.c` refers to `*(LangClosureFn*)closure` but `LangClosureFn` is not defined. The C compiler produces "undeclared identifier" or "incomplete type" errors.

**How to avoid:** Add the typedef at the top of `lang_runtime.c` (or in `lang_runtime.h`):
```c
typedef int64_t (*LangClosureFn)(void* env, int64_t arg);
```

**Warning signs:** C compilation error in `lang_runtime.c` during `dotnet build`.

## Code Examples

### Complete lang_array_iter

```c
/* Source: closure ABI from Printer.fs IndirectCallOp + array layout from lang_runtime.c */
typedef int64_t (*LangClosureFn)(void* env, int64_t arg);

void lang_array_iter(void* closure, int64_t* arr) {
    int64_t n = arr[0];
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 1; i <= n; i++) {
        fn(closure, arr[i]);   /* discard unit return value */
    }
}
```

### Complete lang_array_map

```c
/* Source: same closure ABI; GC_malloc pattern from lang_array_create */
int64_t* lang_array_map(void* closure, int64_t* arr) {
    int64_t n = arr[0];
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 1; i <= n; i++) {
        out[i] = fn(closure, arr[i]);
    }
    return out;
}
```

### Complete lang_array_fold (Two-Call Pattern for Curried Binary Function)

```c
/* Source: LangThree Eval.fs array_fold uses callValue(callValue fVal acc) x — two applications */
int64_t lang_array_fold(void* closure, int64_t init, int64_t* arr) {
    int64_t n = arr[0];
    LangClosureFn fn = *(LangClosureFn*)closure;
    int64_t acc = init;
    for (int64_t i = 1; i <= n; i++) {
        /* First application: outer closure(acc) -> inner closure (returned as ptrtoint i64) */
        int64_t partial = fn(closure, acc);
        /* Second application: inner closure(arr[i]) -> new acc */
        void* partial_ptr = (void*)partial;
        LangClosureFn fn2 = *(LangClosureFn*)partial_ptr;
        acc = fn2(partial_ptr, arr[i]);
    }
    return acc;
}
```

### Complete lang_array_init

```c
/* Source: LangThree Eval.fs array_init uses Array.init n (fun i -> callValue fVal (IntValue i)) */
int64_t* lang_array_init(int64_t n, void* closure) {
    if (n < 0) {
        lang_failwith("array_init: negative size");
        return NULL; /* unreachable */
    }
    int64_t* out = (int64_t*)GC_malloc((size_t)((n + 1) * 8));
    out[0] = n;
    LangClosureFn fn = *(LangClosureFn*)closure;
    for (int64_t i = 0; i < n; i++) {
        out[i + 1] = fn(closure, i);  /* logical index 0..n-1, physical slot 1..n */
    }
    return out;
}
```

### ExternalFuncDecl Entries (both lists)

```fsharp
// Source: Elaboration.fs lines ~2205 and ~2348 — add after lang_array_to_list entries
{ ExtName = "@lang_array_iter"; ExtParams = [Ptr; Ptr];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_map";  ExtParams = [Ptr; Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_fold"; ExtParams = [Ptr; I64; Ptr]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_array_init"; ExtParams = [I64; Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
```

### Elaboration of array_iter (Full)

```fsharp
// Source: pattern from hashtable_set (three-arg) and array_get (two-arg) in Elaboration.fs
| App (App (Var ("array_iter", _), fExpr, _), arrExpr, _) ->
    let (fVal,   fOps)   = elaborateExpr env fExpr
    let (arrVal, arrOps) = elaborateExpr env arrExpr
    let (closurePtr, coerceOps) =
        if fVal.Type = Ptr then (fVal, [])
        else
            let p = { Name = freshName env; Type = Ptr }
            (p, [LlvmIntToPtrOp(p, fVal)])
    let unitVal = { Name = freshName env; Type = I64 }
    let ops = coerceOps @ [
        LlvmCallVoidOp("@lang_array_iter", [closurePtr; arrVal])
        ArithConstantOp(unitVal, 0L)
    ]
    (unitVal, fOps @ arrOps @ ops)
```

### E2E Test Format (following existing .flt conventions)

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let arr = array_of_list [1; 2; 3] in
let sum = array_fold (fun acc x -> acc + x) 0 arr in
sum
// --- Output:
6
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|-----------------|--------|
| No HOF array support (v4.0) | C runtime HOF functions using closure ABI | iter/map/fold/init all work with arbitrary closures |
| Inline MLIR loops (N/A — never existed) | C runtime loop with closure callback | No new MlirOp variants; elaborator stays simple |
| Single-call fold callback | Two-call sequence for curried binary | Correct semantics for curried `(fun acc x -> ...)` |

**No deprecated patterns in this phase.** This phase extends the existing architecture without replacing anything.

## Open Questions

1. **array_fold return type when accumulator is a pointer**
   - What we know: `lang_array_fold` returns `int64_t`. If the fold accumulates pointer-typed values (e.g., folding a string array into a string), the result is a ptrtoint'd pointer stored as i64. The elaboration returns `I64`.
   - What's unclear: Whether any Phase 24 tests fold over non-integer accumulators.
   - Recommendation: The uniform representation handles this correctly — returning i64 from C is fine; the caller in MLIR casts back via `LlvmIntToPtrOp` if needed. No special handling required for Phase 24.

2. **array_iter with print — does print's side effects work inside C callback?**
   - What we know: `array_iter print arr` calls `print` on each element. `print` is a builtin that in the compiled code calls `printf`. The closure for `print` would be an MLIR-generated closure around the printf call.
   - What's unclear: Whether `print` compiles to a closure value or is a known-function direct call. Known functions that are not closures cannot be passed as first-class values directly.
   - Recommendation: The test `array_iter print arr` should work if `print` is passed via `fun x -> print x`. Pure `array_iter print arr` may fail if `print` is not a closure value. Plan the test to use `array_iter (fun x -> print x) arr` to be safe, unless the elaborator handles first-class builtin lifting.

3. **array_fold with non-zero initial value of Ptr type**
   - What we know: The `coerceToI64` pattern handles Ptr→I64 via `LlvmPtrToIntOp`.
   - What's unclear: Whether this is actually needed for Phase 24 tests (all four success criteria use integer values).
   - Recommendation: Implement the coercion defensively; it costs one extra match arm.

## Sources

### Primary (HIGH confidence)
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Printer.fs` lines 108-110 — confirmed `IndirectCallOp` ABI: `llvm.call %fnPtr(%envPtr, %arg) : !llvm.ptr, (!llvm.ptr, argType) -> resultType` — env ptr comes before arg
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/MlirIR.fs` lines 40-79 — confirmed no loop ops (no ForOp, WhileOp, scf.for); confirmed `IndirectCallOp` signature
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` lines 446-451 — confirmed inner llvm.func signature `InputTypes = [Ptr; I64]; ReturnType = Some I64` (env first, arg second)
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` lines 1544-1548 — confirmed lambda body: `%arg0: Ptr = env`, `%arg1: I64 = arg`
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` — confirmed array layout: `arr[0]=length, arr[1..n]=elements`; `GC_malloc` pattern; `lang_failwith` usage
- Direct code analysis of `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.h` — confirmed no HOF declarations exist; `LangClosureFn` typedef not yet present
- Direct code analysis of `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` lines 488-522 — confirmed LangThree HOF semantics: `array_fold` uses `callValue (callValue fVal acc) x` (two calls), `array_init` uses `Array.init n (fun i -> callValue fVal (IntValue i))` (zero-based index)
- Direct code analysis of `Elaboration.fs` lines 2205 and 2348 — confirmed two `externalFuncs` lists exist; both must be updated

### Secondary (MEDIUM confidence)
- Phase 22 RESEARCH.md (`/Users/ohama/vibe-coding/FunLangCompiler/.planning/phases/22-array-core/22-RESEARCH.md`) — confirmed one-block array layout, GEP patterns, coercion requirements (Pitfall 4 re: Ptr-typed defVal); directly applicable

### Tertiary (LOW confidence)
- None required — all findings sourced from direct code analysis

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new external deps; confirmed by direct analysis of MlirIR.fs (no loop ops), existing C runtime patterns, ExternalFuncDecl mechanism
- Architecture: HIGH — closure ABI confirmed from Printer.fs + Elaboration.fs inner func structure; C callback pattern confirmed by cross-referencing Eval.fs fold semantics
- Pitfalls: HIGH — two-call fold pitfall confirmed from Eval.fs source; index zero-based pitfall confirmed from Eval.fs `Array.init n (fun i -> ...)` pattern; both ExternalFuncDecl lists confirmed at lines 2205 and 2348

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable domain — only changes if closure ABI or array layout changes)
