# Phase 5: Closures via Elaboration - Research

**Researched:** 2026-03-26
**Domain:** MLIR llvm dialect (llvm.func, llvm.alloca, llvm.getelementptr, llvm.store, llvm.load, llvm.call indirect), closure representation, free variable analysis, elaboration extension
**Confidence:** HIGH

---

## Summary

Phase 5 extends the compiler with first-class lambda expressions that capture free variables. The MLIR encoding uses a flat closure struct `{ fn_ptr: ptr, captured_var_0: i64, captured_var_1: i64, ... }` stack-allocated in the **caller's** frame. Lambda body functions are emitted as `llvm.func` (not `func.func`) so that `llvm.mlir.addressof` can take their address. Indirect dispatch uses `llvm.call %fn_ptr(%env, %arg) : !llvm.ptr, (!llvm.ptr, i64) -> i64`.

All MLIR patterns have been verified end-to-end on this system (LLVM 20.1.4): zero-capture closures, one-capture closures (the `add_n` test case), two-capture closures, indirect call through an intermediate frame, and the full pipeline to binary. The critical design decision is **caller-allocates** (alloca lives in the frame that creates the closure, not the frame of the function returning the closure). Callee-allocates causes stack-use-after-return UB when any other function is called between closure creation and use.

The elaboration extension requires: (1) free variable analysis for Lambda expressions, (2) special-casing `Let(name, Lambda(...), body)` to compile the lambda and add to `KnownFuncs`, (3) new `MlirType | Ptr`, (4) new LLVM-level `MlirOp` cases for alloca/GEP/load/store/addressof/indirect-call/llvm-return, (5) `FuncOp.IsLlvmFunc: bool` to emit `llvm.func` bodies, and (6) `App` dispatch: if var is in `Vars` with `Type=Ptr` → closure call, if in `KnownFuncs` → check whether it's a closure-making func or a direct-call func.

**Primary recommendation:** Use caller-allocates flat closure structs, all-linear-GEP for field access (no struct type annotation needed in the fill/load code), and `llvm.func` + `llvm.mlir.addressof` for lambda body functions. Emit one `LlvmAllocaOp` at the call site before each closure-making call.

---

## Standard Stack

### Core

| Tool / Module | Version | Purpose | Why Standard |
|---------------|---------|---------|--------------|
| `llvm` MLIR dialect | LLVM 20.1.4 | `llvm.func`, `llvm.alloca`, `llvm.getelementptr`, `llvm.store`, `llvm.load`, `llvm.call` (indirect), `llvm.mlir.addressof` | Only dialect that supports function pointers + heap-style struct layout |
| `func` MLIR dialect | LLVM 20.1.4 | `func.func` for closure-making wrapper functions (e.g. `@add_n`), `func.call` for calling them | Already used in Phases 1-4; `func.func` with `!llvm.ptr` params/return is valid |
| `arith` MLIR dialect | LLVM 20.1.4 | Still used for arithmetic inside lambda bodies (arith ops inside `llvm.func`) | Present in existing pipeline |
| `MlirIR.fs` | Phase 5 additions | Add `Ptr` to `MlirType`; add LLVM-level ops to `MlirOp` DU; add `IsLlvmFunc` to `FuncOp` | Follows extensible DU pattern from Phases 1-4 |
| `Elaboration.fs` | Phase 5 extensions | Add free-var analysis; handle `Lambda` in `Let` binding; extend `App` dispatch | Extended in-place; no new files needed |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `FsLit (fslit)` | — | E2E test runner | Phase 5 test files for closure feature |
| `mlir-opt` LLVM 20 | 20.1.4 | Lowering pipeline: same pass order as Phases 1-4 | No pipeline changes needed |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `llvm.func` for lambda body | `func.func` with `!llvm.ptr` param | `func.func` cannot be used with `llvm.mlir.addressof`; `llvm.mlir.addressof` requires `llvm.func` or `llvm.mlir.global` |
| Caller-allocates closure | Callee-allocates (alloca inside the closure-making function) | Callee-allocates causes **stack-use-after-return UB** when any other function call occurs between closure creation and use (verified to segfault with an intermediate frame) |
| All-linear-GEP for field access | Typed struct GEP `%ptr[0, N] : !llvm.struct<...>` | Linear GEP needs no struct type annotation in fill/load code; only alloca needs the struct type; simpler Printer logic |
| Separate `ClosureFuncs` map in `ElabEnv` | Extend `FuncSignature` with `ClosureInfo option` | Single `KnownFuncs` map handles both direct-call and closure-making functions; clean discriminant via `ClosureInfo` |

---

## Architecture Patterns

### Recommended Project Structure

```
src/FunLangCompiler.Compiler/
├── MlirIR.fs        # Add Ptr to MlirType; add 7 new MlirOp cases; add IsLlvmFunc to FuncOp
├── Printer.fs       # Add printType Ptr; add 7 new printOp cases; update printFuncOp for IsLlvmFunc
├── Elaboration.fs   # Add freeVars; handle Lambda in Let; extend App dispatch; extend FuncSignature
└── Pipeline.fs      # Unchanged
tests/compiler/
├── 05-01-closure-basic.flt   # add_n + add5 test (exit 8)
└── 05-02-lambda-no-capture.flt  # fun x -> x + 1 test
```

### Pattern 1: MlirType Addition

**What:** Add `Ptr` to represent `!llvm.ptr` (opaque pointer, LLVM 20 convention).

```fsharp
// MlirIR.fs
type MlirType =
    | I64
    | I32
    | I1
    | Ptr   // NEW: represents !llvm.ptr
```

Printer: `| Ptr -> "!llvm.ptr"`

### Pattern 2: New MlirOp Cases

**What:** Seven new ops to express LLVM-level closure mechanics. Each maps to one MLIR statement.

```fsharp
// MlirIR.fs — new cases for MlirOp DU
| LlvmAllocaOp     of result: MlirValue * count: MlirValue * numCaptures: int
  // Prints: %result = llvm.alloca %count x !llvm.struct<(ptr{, i64}^numCaptures)> : (i64) -> !llvm.ptr
  // result.Type = Ptr; count.Type = I64

| LlvmStoreOp      of value: MlirValue * ptr: MlirValue
  // Prints: llvm.store %value, %ptr : {valueType}, !llvm.ptr
  // VOID: no result. Printer handles this like ReturnOp (no LHS assignment)

| LlvmLoadOp       of result: MlirValue * ptr: MlirValue
  // Prints: %result = llvm.load %ptr : !llvm.ptr -> {result.Type}
  // result.Type determines loaded type (Ptr or I64)

| LlvmAddressOfOp  of result: MlirValue * fnName: string
  // Prints: %result = llvm.mlir.addressof @fnName : !llvm.ptr
  // result.Type = Ptr

| LlvmGEPLinearOp  of result: MlirValue * ptr: MlirValue * index: int
  // Prints: %result = llvm.getelementptr %ptr[N] : (!llvm.ptr) -> !llvm.ptr, i64
  // Linear GEP: skip N * sizeof(i64) bytes from ptr
  // Used to access capture slots: index = captureIdx + 1 (skip fn_ptr at [0])

| LlvmReturnOp     of operands: MlirValue list
  // Prints: llvm.return [%val : type] or llvm.return (void)
  // Used in llvm.func bodies (NOT func.func bodies)

| IndirectCallOp   of result: MlirValue * fnPtr: MlirValue * envPtr: MlirValue * arg: MlirValue
  // Prints: %result = llvm.call %fnPtr(%envPtr, %arg) : !llvm.ptr, (!llvm.ptr, i64) -> i64
  // Indirect dispatch: all closures have uniform (env, arg) -> i64 calling convention
```

### Pattern 3: FuncOp Extension for llvm.func

**What:** Add `IsLlvmFunc: bool` to `FuncOp`. Lambda body functions use `IsLlvmFunc = true`.

```fsharp
// MlirIR.fs
type FuncOp = {
    Name:        string
    InputTypes:  MlirType list
    ReturnType:  MlirType option
    Body:        MlirRegion
    IsLlvmFunc:  bool          // NEW: true -> emit "llvm.func"; false -> emit "func.func"
}
```

Printer `printFuncOp` change:
- `if func.IsLlvmFunc then "llvm.func" else "func.func"` for the keyword
- Both use same `%argN` parameter naming (MLIR auto-renames anyway)
- `IsLlvmFunc` bodies use `LlvmReturnOp`, regular bodies use `ReturnOp`

**Backward compatibility:** All existing `FuncOp` record literals must add `IsLlvmFunc = false`. Search for all `{ Name = ...; InputTypes = ...; ReturnType = ...; Body = ... }` record construction sites and add the new field.

### Pattern 4: Free Variable Analysis

**What:** A pure helper function that computes the set of free variables in a lambda body.

```fsharp
// Elaboration.fs — new helper
let rec freeVars (boundVars: Set<string>) (expr: Expr) : Set<string> =
    match expr with
    | Number _ | Bool _ -> Set.empty
    | Var (name, _) ->
        if Set.contains name boundVars then Set.empty else Set.singleton name
    | Add (l, r, _) | Subtract (l, r, _) | Multiply (l, r, _) | Divide (l, r, _)
    | Equal (l, r, _) | NotEqual (l, r, _) | LessThan (l, r, _) | GreaterThan (l, r, _)
    | LessEqual (l, r, _) | GreaterEqual (l, r, _) | And (l, r, _) | Or (l, r, _) ->
        Set.union (freeVars boundVars l) (freeVars boundVars r)
    | Negate (e, _) -> freeVars boundVars e
    | If (c, t, e, _) ->
        Set.unionMany [ freeVars boundVars c; freeVars boundVars t; freeVars boundVars e ]
    | Let (name, e1, e2, _) ->
        Set.union (freeVars boundVars e1) (freeVars (Set.add name boundVars) e2)
    | Lambda (param, body, _) ->
        freeVars (Set.add param boundVars) body
    | App (f, a, _) ->
        Set.union (freeVars boundVars f) (freeVars boundVars a)
    | LetRec (name, param, body, inExpr, _) ->
        let innerBound = Set.add name (Set.add param boundVars)
        Set.union (freeVars innerBound body) (freeVars (Set.add name boundVars) inExpr)
    | _ -> Set.empty  // conservative: other exprs (String, Char, etc.) have no free vars
```

### Pattern 5: FuncSignature Extension for Closures

**What:** Extend `FuncSignature` with optional `ClosureInfo` to distinguish closure-making functions from direct-call functions.

```fsharp
// Elaboration.fs
type ClosureInfo = {
    InnerLambdaFn:  string    // MLIR name of the llvm.func body, e.g. "@closure_fn_0"
    NumCaptures:    int       // number of captured variables (struct has ptr + N i64 fields)
}

type FuncSignature = {
    MlirName:    string
    ParamTypes:  MlirType list
    ReturnType:  MlirType
    ClosureInfo: ClosureInfo option  // NEW: None = direct-call func; Some = closure-maker
}
```

When `ClosureInfo = Some`, calling this function requires a pre-allocated env_ptr argument.

### Pattern 6: Lambda Compilation (Let handler)

**What:** When elaborating `Let(name, Lambda(outerParam, lambdaBody), inExpr)`, special-case it to compile the lambda.

**Algorithm:**
1. The `lambdaBody` must itself be a `Lambda(innerParam, innerBody)` (one level of closure). Phase 5 scope: one closure level.
2. Compute `captures = freeVars {outerParam} lambdaBody |> Set.toList |> List.sort` (sorted for determinism).
3. Generate a fresh name for the inner llvm.func body: `@closure_fn_{counter}`.
4. Compile inner lambda body with a fresh env:
   - `%arg0` = env ptr (type Ptr)
   - `%arg1` = `innerParam` (type I64)
   - For each capture at index i: emit `LlvmGEPLinearOp(gep_result, %arg0, i+1)` + `LlvmLoadOp(capture_val, gep_result)`, bind capture name to capture_val
   - Elaborate `innerBody` with these bindings
   - Emit `LlvmReturnOp [bodyVal]`
5. Assemble the inner `FuncOp` with `IsLlvmFunc = true`, `InputTypes = [Ptr; I64]`, `ReturnType = Some I64`.
6. Generate `@outerParam_func` (the closure-maker func.func):
   - `%arg0` = `outerParam: I64`
   - `%arg1` = `env_ptr: Ptr` (caller-allocated, passed in)
   - Emit `LlvmAddressOfOp(fn_ptr_val, "@closure_fn_N")`
   - Emit `LlvmStoreOp(fn_ptr_val, %arg1)` (store fn_ptr at env[0])
   - For each capture at index i: emit `LlvmGEPLinearOp(slot, %arg1, i+1)` + `LlvmStoreOp(captureVal, slot)`
   - Emit `ReturnOp [%arg1]` (return the env ptr)
   - `InputTypes = [I64; Ptr]`, `ReturnType = Some Ptr`, `IsLlvmFunc = false`
7. Add both FuncOps to `env.Funcs`.
8. Add to `env.KnownFuncs`: `name -> { MlirName = "@add_n"; ParamTypes = [I64]; ReturnType = Ptr; ClosureInfo = Some { InnerLambdaFn = "@closure_fn_N"; NumCaptures = N } }`
9. Elaborate `inExpr` with updated env.

### Pattern 7: App Dispatch (extended for closures)

**What:** `App(funcExpr, argExpr)` now has three possible cases:

```fsharp
| App (funcExpr, argExpr, _) ->
    match funcExpr with
    | Var (name, _) ->
        match Map.tryFind name env.KnownFuncs with
        | Some sig_ when sig_.ClosureInfo.IsNone ->
            // DIRECT CALL (Phase 4 behavior) — known non-closure function
            let (argVal, argOps) = elaborateExpr env argExpr
            let result = { Name = freshName env; Type = sig_.ReturnType }
            (result, argOps @ [DirectCallOp(result, sig_.MlirName, [argVal])])
        | Some sig_ ->
            // CLOSURE-MAKING CALL — allocate env in current frame, then call
            let ci = sig_.ClosureInfo.Value
            let (argVal, argOps) = elaborateExpr env argExpr
            let countVal = { Name = freshName env; Type = I64 }
            let envPtrVal = { Name = freshName env; Type = Ptr }
            let resultVal = { Name = freshName env; Type = Ptr }
            let setupOps = [
                ArithConstantOp(countVal, 1L)
                LlvmAllocaOp(envPtrVal, countVal, ci.NumCaptures)
            ]
            let callOp = DirectCallOp(resultVal, sig_.MlirName, [argVal; envPtrVal])
            (resultVal, argOps @ setupOps @ [callOp])
        | None ->
            // Check Vars for closure value (type Ptr)
            match Map.tryFind name env.Vars with
            | Some closureVal when closureVal.Type = Ptr ->
                // INDIRECT/CLOSURE CALL
                let (argVal, argOps) = elaborateExpr env argExpr
                let fnPtrVal = { Name = freshName env; Type = Ptr }
                let result = { Name = freshName env; Type = I64 }
                let loadOp = LlvmLoadOp(fnPtrVal, closureVal)
                let callOp = IndirectCallOp(result, fnPtrVal, closureVal, argVal)
                (result, argOps @ [loadOp; callOp])
            | _ ->
                failwithf "Elaboration: unsupported App — '%s' is not a known function or closure value" name
    | _ ->
        failwithf "Elaboration: unsupported App (only named function application supported in Phase 5)"
```

### Pattern 8: Printer Cases for New Ops

**What:** Each new op maps to exactly one MLIR statement. Struct type for LlvmAllocaOp is generated from `numCaptures`.

```fsharp
// Printer.fs additions to printOp

| LlvmAllocaOp(result, count, numCaptures) ->
    let fields =
        if numCaptures = 0 then "ptr"
        else "ptr" + String.concat "" (List.replicate numCaptures ", i64")
    sprintf "%s%s = llvm.alloca %s x !llvm.struct<(%s)> : (i64) -> !llvm.ptr"
        indent result.Name count.Name fields

| LlvmStoreOp(value, ptr) ->
    sprintf "%sllvm.store %s, %s : %s, !llvm.ptr"
        indent value.Name ptr.Name (printType value.Type)

| LlvmLoadOp(result, ptr) ->
    sprintf "%s%s = llvm.load %s : !llvm.ptr -> %s"
        indent result.Name ptr.Name (printType result.Type)

| LlvmAddressOfOp(result, fnName) ->
    sprintf "%s%s = llvm.mlir.addressof %s : !llvm.ptr"
        indent result.Name fnName

| LlvmGEPLinearOp(result, ptr, index) ->
    sprintf "%s%s = llvm.getelementptr %s[%d] : (!llvm.ptr) -> !llvm.ptr, i64"
        indent result.Name ptr.Name index

| LlvmReturnOp [] ->
    sprintf "%sllvm.return" indent
| LlvmReturnOp operands ->
    let vals =
        operands
        |> List.map (fun v -> sprintf "%s : %s" v.Name (printType v.Type))
        |> String.concat ", "
    sprintf "%sllvm.return %s" indent vals

| IndirectCallOp(result, fnPtr, envPtr, arg) ->
    sprintf "%s%s = llvm.call %s(%s, %s) : !llvm.ptr, (!llvm.ptr, %s) -> %s"
        indent result.Name fnPtr.Name envPtr.Name arg.Name (printType arg.Type) (printType result.Type)
```

Printer for `printFuncOp` — add `IsLlvmFunc` handling:
```fsharp
let private printFuncOp (func: FuncOp) : string =
    let keyword = if func.IsLlvmFunc then "llvm.func" else "func.func"
    // ... rest same as before, just replace "func.func" with keyword
```

### Pattern 9: Verified MLIR Text for Test Case

**What:** The full verified MLIR for `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3`.

```mlir
module {
  llvm.func @closure_fn_0(%arg0: !llvm.ptr, %arg1: i64) -> i64 {
    %t0 = llvm.getelementptr %arg0[1] : (!llvm.ptr) -> !llvm.ptr, i64
    %t1 = llvm.load %t0 : !llvm.ptr -> i64
    %t2 = arith.addi %arg1, %t1 : i64
    llvm.return %t2 : i64
  }
  func.func @add_n(%arg0: i64, %arg1: !llvm.ptr) -> !llvm.ptr {
    %t3 = llvm.mlir.addressof @closure_fn_0 : !llvm.ptr
    llvm.store %t3, %arg1 : !llvm.ptr, !llvm.ptr
    %t4 = llvm.getelementptr %arg1[1] : (!llvm.ptr) -> !llvm.ptr, i64
    llvm.store %arg0, %t4 : i64, !llvm.ptr
    return %arg1 : !llvm.ptr
  }
  func.func @main() -> i64 {
    %t5 = arith.constant 1 : i64
    %t6 = llvm.alloca %t5 x !llvm.struct<(ptr, i64)> : (i64) -> !llvm.ptr
    %t7 = arith.constant 5 : i64
    %t8 = func.call @add_n(%t7, %t6) : (i64, !llvm.ptr) -> !llvm.ptr
    %t9 = llvm.load %t8 : !llvm.ptr -> !llvm.ptr
    %t10 = arith.constant 3 : i64
    %t11 = llvm.call %t9(%t8, %t10) : !llvm.ptr, (!llvm.ptr, i64) -> i64
    return %t11 : i64
  }
}
// Compiles, runs, exits with 8 (= 5 + 3)
```

### Anti-Patterns to Avoid

- **Callee-allocates closure (alloca inside the closure-making function):** Causes stack-use-after-return UB verified to segfault (exit 139) with any intermediate function call. Always alloca in the caller's frame.
- **Using `llvm.mlir.addressof` on a `func.func`:** This op only works on `llvm.func` / `llvm.mlir.global`. Lambda body functions MUST be `llvm.func`.
- **Using `llvm.mlir.null` for initialization:** This op does not exist in LLVM 20 MLIR dialect. Use actual value stores.
- **Struct GEP for field access in the lambda body:** Requires knowing the struct type (num captures), which the lambda body doesn't know (opaque env). Use linear GEP `%env[N]` with i64 stride instead.
- **Adding `Lambda` values to `Vars` (as MlirValue):** Lambda is not a value — it's a function. Handle `Let(name, Lambda(...), body)` by adding to `KnownFuncs`, not `Vars`.
- **Forgetting `IsLlvmFunc = false` on existing FuncOp record literals:** The new field is required; all existing record constructions in `Elaboration.fs` must be updated.
- **Using `return` in llvm.func bodies:** `llvm.func` requires `llvm.return`. Use `LlvmReturnOp` in lambda body elaboration and `ReturnOp` in func.func bodies.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Closure memory management | Custom GC or reference counting | Stack allocation in caller's frame | Phase 5 limitation is acceptable; caller-alloca is safe for non-escaping closures used within the same function |
| Function pointer type safety | Type-indexed closure structs | Uniform `!llvm.ptr` env + uniform `(!llvm.ptr, i64) -> i64` signature | Uniform convention avoids type parameterization at the MLIR level; type safety is the elaboration's job |
| Struct type calculation | Compute byte offsets manually | `llvm.alloca %N x !llvm.struct<(ptr, i64, ...)>` with MLIR struct type | MLIR handles alignment and field sizing; linear GEP handles field offsets |
| Curried multi-param closures | Complex nested closure chaining | Scope Phase 5 to one level of Lambda nesting | Full currying is a future phase; `add_n` test case needs exactly one nesting level |

**Key insight:** `llvm.func` and `func.func` can coexist in the same MLIR module and are both lowered by the same pass pipeline (`--convert-func-to-llvm`, `--reconcile-unrealized-casts`). No pipeline changes are needed.

---

## Common Pitfalls

### Pitfall 1: Callee-Allocates Segfault

**What goes wrong:** `@add_n` allocates the closure env with `llvm.alloca` in its own frame, returns the pointer. When `@main` then calls `@apply` (or any other function) before using the closure, `@apply`'s stack frame overwrites `@add_n`'s stack frame. Exit code 139 (SIGSEGV).

**Why it happens:** `llvm.alloca` creates a stack allocation valid only for the lifetime of the function containing it. Stack frames are reused after function return.

**How to avoid:** Always emit `LlvmAllocaOp` in the elaboration of `App(closureMakingFunc, arg)` — i.e., in the caller's frame. The closure-making func.func takes an extra `%arg1: !llvm.ptr` (the pre-allocated env).

**Warning signs:** Works for the direct test case but fails when any function call is inserted between closure creation and use.

### Pitfall 2: llvm.mlir.addressof on func.func

**What goes wrong:** `llvm.mlir.addressof @lambda_body` fails with `'llvm.mlir.addressof' op must reference a global defined by 'llvm.mlir.global', 'llvm.mlir.alias' or 'llvm.func'`.

**Why it happens:** `func.func` functions are in the `func` dialect and not visible to LLVM's `mlir.addressof`.

**How to avoid:** Lambda body functions (the ones called through fn_ptr) MUST be `llvm.func`. Set `IsLlvmFunc = true` for all lambda-generated FuncOps.

**Warning signs:** mlir-opt error mentioning `'llvm.mlir.addressof' op must reference`.

### Pitfall 3: ReturnOp vs LlvmReturnOp in Wrong Context

**What goes wrong:** Using `return %val : i64` inside an `llvm.func` body produces MLIR parse error. Using `llvm.return %val : i64` inside a `func.func` body similarly fails.

**Why it happens:** The two dialects have separate return operations with different syntax.

**How to avoid:** Lambda body elaboration must accumulate `LlvmReturnOp` for the function terminator. Regular `LetRec`/closure-maker function bodies use `ReturnOp`. The discriminant is `FuncOp.IsLlvmFunc`.

**Warning signs:** mlir-opt error about wrong terminator in a function body.

### Pitfall 4: Free Variable Set Ordering Non-Determinism

**What goes wrong:** `Set.toList` ordering in F# is deterministic (sorted by value), but if capture indices vary between elaboration runs, the struct layout may not match what the lambda body expects.

**Why it happens:** If the capture list order in `@add_n`'s fill code doesn't match the `getelementptr %env[i+1]` indices in the lambda body, the wrong captured value is loaded.

**How to avoid:** Use `Set.toList` (which gives sorted order) and use the same sorted order when (a) generating the fill instructions in `@add_n` and (b) generating the load instructions in the lambda body. Document the convention: captures are filled and loaded in alphabetically sorted order.

**Warning signs:** Binary runs without crashing but returns wrong value (e.g. `add5 3` exits 8 for `n=5, x=3` → captured n is correct; but a 2-capture test returns wrong sum).

### Pitfall 5: Missing IsLlvmFunc = false on Existing FuncOps

**What goes wrong:** F# record syntax error or runtime mismatch when adding `IsLlvmFunc` field to `FuncOp` if any existing record construction site doesn't include it.

**Why it happens:** F# records require all fields to be specified. Any existing `{ Name = ...; InputTypes = ...; ReturnType = ...; Body = ... }` will fail to compile.

**How to avoid:** Grep for all FuncOp record construction sites in `Elaboration.fs` and add `IsLlvmFunc = false` to each.

**Warning signs:** F# compile error `the field 'IsLlvmFunc' was not found`.

### Pitfall 6: llvm.func Parameter Auto-Renaming

**What goes wrong:** The MLIR printer renames `llvm.func` parameters to `%arg0`, `%arg1`, etc. regardless of what names you use in the source. If the lambda body code uses the original param names (e.g. `%env`, `%x`) but the Printer emits `%arg0`, `%arg1`, the emitted MLIR will be valid.

**Why it happens:** `llvm.func` doesn't preserve argument names in its canonical form (unlike `func.func` which preserves `%argN` names).

**How to avoid:** The Printer for `llvm.func` bodies should use `%arg0` for the env parameter and `%arg1` for the actual argument, consistent with `func.func` convention. This matches what mlir-opt outputs after renaming.

---

## Code Examples

Verified on LLVM 20.1.4 with full pipeline to binary (all exit codes confirmed):

### 1-capture closure (the Phase 5 success criteria test)

```mlir
module {
  llvm.func @closure_fn_0(%arg0: !llvm.ptr, %arg1: i64) -> i64 {
    %t0 = llvm.getelementptr %arg0[1] : (!llvm.ptr) -> !llvm.ptr, i64
    %t1 = llvm.load %t0 : !llvm.ptr -> i64
    %t2 = arith.addi %arg1, %t1 : i64
    llvm.return %t2 : i64
  }
  func.func @add_n(%arg0: i64, %arg1: !llvm.ptr) -> !llvm.ptr {
    %t3 = llvm.mlir.addressof @closure_fn_0 : !llvm.ptr
    llvm.store %t3, %arg1 : !llvm.ptr, !llvm.ptr
    %t4 = llvm.getelementptr %arg1[1] : (!llvm.ptr) -> !llvm.ptr, i64
    llvm.store %arg0, %t4 : i64, !llvm.ptr
    return %arg1 : !llvm.ptr
  }
  func.func @main() -> i64 {
    %t5 = arith.constant 1 : i64
    %t6 = llvm.alloca %t5 x !llvm.struct<(ptr, i64)> : (i64) -> !llvm.ptr
    %t7 = arith.constant 5 : i64
    %t8 = func.call @add_n(%t7, %t6) : (i64, !llvm.ptr) -> !llvm.ptr
    %t9 = llvm.load %t8 : !llvm.ptr -> !llvm.ptr
    %t10 = arith.constant 3 : i64
    %t11 = llvm.call %t9(%t8, %t10) : !llvm.ptr, (!llvm.ptr, i64) -> i64
    return %t11 : i64
  }
}
// Exit: 8 (= 5 + 3). Verified.
```

### 0-capture closure

```mlir
module {
  llvm.func @lambda_zero(%arg0: !llvm.ptr, %arg1: i64) -> i64 {
    %c1 = llvm.mlir.constant(1 : i64) : i64
    %r = llvm.add %arg1, %c1 : i64
    llvm.return %r : i64
  }
  func.func @main() -> i64 {
    %one = arith.constant 1 : i64
    %env = llvm.alloca %one x !llvm.struct<(ptr)> : (i64) -> !llvm.ptr
    %fn = llvm.mlir.addressof @lambda_zero : !llvm.ptr
    llvm.store %fn, %env : !llvm.ptr, !llvm.ptr
    %fn_loaded = llvm.load %env : !llvm.ptr -> !llvm.ptr
    %four = arith.constant 4 : i64
    %result = llvm.call %fn_loaded(%env, %four) : !llvm.ptr, (!llvm.ptr, i64) -> i64
    return %result : i64
  }
}
// Exit: 5 (= 4 + 1). Verified.
```

### 2-capture closure

```mlir
module {
  llvm.func @lambda2(%arg0: !llvm.ptr, %arg1: i64) -> i64 {
    %a_ptr = llvm.getelementptr %arg0[1] : (!llvm.ptr) -> !llvm.ptr, i64
    %a = llvm.load %a_ptr : !llvm.ptr -> i64
    %b_ptr = llvm.getelementptr %arg0[2] : (!llvm.ptr) -> !llvm.ptr, i64
    %b = llvm.load %b_ptr : !llvm.ptr -> i64
    %xa = llvm.add %arg1, %a : i64
    %result = llvm.add %xa, %b : i64
    llvm.return %result : i64
  }
  func.func @fill(%arg0: i64, %arg1: i64, %arg2: !llvm.ptr) -> !llvm.ptr {
    %fn = llvm.mlir.addressof @lambda2 : !llvm.ptr
    llvm.store %fn, %arg2 : !llvm.ptr, !llvm.ptr
    %s1 = llvm.getelementptr %arg2[1] : (!llvm.ptr) -> !llvm.ptr, i64
    llvm.store %arg0, %s1 : i64, !llvm.ptr
    %s2 = llvm.getelementptr %arg2[2] : (!llvm.ptr) -> !llvm.ptr, i64
    llvm.store %arg1, %s2 : i64, !llvm.ptr
    return %arg2 : !llvm.ptr
  }
  func.func @main() -> i64 {
    %one = arith.constant 1 : i64
    %env = llvm.alloca %one x !llvm.struct<(ptr, i64, i64)> : (i64) -> !llvm.ptr
    %a = arith.constant 3 : i64
    %b = arith.constant 4 : i64
    %cls = func.call @fill(%a, %b, %env) : (i64, i64, !llvm.ptr) -> !llvm.ptr
    %fn = llvm.load %cls : !llvm.ptr -> !llvm.ptr
    %x = arith.constant 1 : i64
    %r = llvm.call %fn(%cls, %x) : !llvm.ptr, (!llvm.ptr, i64) -> i64
    return %r : i64
  }
}
// Exit: 8 (= 1 + 3 + 4). Verified.
```

### Indirect call syntax (verified correct form)

```mlir
// Non-vararg indirect call — the "2 trailing types" form:
%result = llvm.call %fn_ptr(%env, %arg) : !llvm.ptr, (!llvm.ptr, i64) -> i64
//                                        ^ callee ptr type  ^ func signature
```

### FsLit test file format (Phase 5)

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3
// --- Output:
8
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `MlirType = I64 \| I32 \| I1` | Add `\| Ptr` | Phase 5 | Closure env pointers have a proper type in the IR |
| `MlirOp` has arith + cf + func.call + return | Add 7 LLVM-level ops | Phase 5 | Closure allocation and dispatch expressible in MlirOp |
| All `FuncOp`s emit `func.func` | `IsLlvmFunc = true` emits `llvm.func` | Phase 5 | Lambda body functions can have their address taken |
| `App` only dispatches to `KnownFuncs` (direct call) | `App` dispatches to KnownFuncs direct / KnownFuncs closure-maker / Vars Ptr (closure call) | Phase 5 | Higher-order functions work |
| `Let(name, expr, body)` — expr is always a value | Special-case `Let(name, Lambda(...), body)` to compile lambda | Phase 5 | `let f x = fun y -> ...` works |
| `FuncSignature { MlirName; ParamTypes; ReturnType }` | Add `ClosureInfo option` | Phase 5 | Planner knows whether calling a KnownFunc requires env_ptr allocation |

---

## Open Questions

1. **Multi-level lambda nesting (currying beyond one level)**
   - What we know: `let f a b = a + b` in LangThree desugars to `Let("f", Lambda("a", Lambda("b", Add(a,b))), ...)`. The inner Lambda("b", Add(a,b)) captures `a`.
   - Phase 5 scope: one closure level (Lambda wrapping non-Lambda body). `fun x -> x + n` is the target.
   - What's unclear: Whether Phase 5 test suite requires `f a b = a + b` style (curried two-arg function).
   - Recommendation: Start with one closure level. If multi-level is needed, the recursive structure is the same: each Lambda layer adds one level of closure wrapping.

2. **LetRec with Lambda body (recursive higher-order function)**
   - What we know: `let rec map f lst = ...` would combine LetRec + Lambda.
   - Phase 5 scope: the success criteria doesn't require recursive closure. `let add_n n = fun x -> x + n` is non-recursive.
   - Recommendation: Keep LetRec handling identical to Phase 4 (fails if body is a Lambda). Document as future scope.

3. **Category test files (TEST-02 requirement)**
   - What we know: The requirement says "FsLit test files for all feature categories (arithmetic, comparison, if-else, let, let-rec, lambda) pass together".
   - What's unclear: Whether new category-specific test files are needed (e.g. `05-lambda.flt`) or if the existing category tests (02-*, 03-*, 04-*) cover it.
   - Recommendation: Add a `05-01-closure-basic.flt` for the success criteria test. The existing 01-04 tests cover prior categories and should pass unchanged.

4. **Closure struct alignment for Ptr field**
   - What we know: LLVM's struct layout places a `ptr` field (8 bytes on 64-bit) at offset 0, followed by `i64` fields. The linear GEP with `i64` stride works correctly because `sizeof(ptr) == sizeof(i64) == 8` on 64-bit systems.
   - What's unclear: Whether this assumption breaks on 32-bit targets.
   - Recommendation: Phase 5 is 64-bit only (Homebrew LLVM on macOS arm64/x86-64). Document limitation.

---

## Sources

### Primary (HIGH confidence — verified e2e on this system, 2026-03-26)

All patterns verified with `mlir-opt 20.1.4` + `mlir-translate` + `clang`, full pipeline to binary:

- Zero-capture closure: alloca `struct<(ptr)>`, addressof llvm.func, indirect call → exit 5. **Confirmed.**
- One-capture closure (add_n): caller-allocates env in `@main`, callee fills it → exit 8. **Confirmed.**
- Two-capture closure: `struct<(ptr, i64, i64)>`, linear GEP indices 1 and 2 → exit 8. **Confirmed.**
- Callee-allocates UB: closure made in `@make_closure`, called through `@apply` (intermediate frame) → **exit 139 (SIGSEGV). Confirmed.**
- Caller-allocates safety: same `@make_closure` + `@apply` pattern with env allocated in `@main` → exit 8. **Confirmed.**
- `llvm.mlir.addressof` on `func.func` → error message confirmed: `must reference a global defined by 'llvm.mlir.global', 'llvm.mlir.alias' or 'llvm.func'`.
- `llvm.call %fn_ptr(%env, %arg) : !llvm.ptr, (!llvm.ptr, i64) -> i64` — correct indirect call syntax for LLVM 20 MLIR dialect. **Confirmed.**
- `func.func` with `!llvm.ptr` parameter and return types (mixed dialect) compiles correctly. **Confirmed.**
- Linear GEP `%env[N]` with `i64` element type accesses byte offset `N * 8`. Loading fn_ptr via `llvm.load %env : !llvm.ptr -> !llvm.ptr` reads first 8 bytes. **Confirmed.**

### Secondary (HIGH confidence — project source)

- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/MlirIR.fs` — existing `MlirOp` DU shape, `FuncOp` structure, `MlirType` variants
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — `ElabEnv`, `FuncSignature`, `freshName`, `LetRec` / `App` elaboration patterns from Phase 4
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Printer.fs` — `printOp`, `printFuncOp`, `printType` functions
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Pipeline.fs` — `loweringPasses` unchanged; no new passes needed
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — `Lambda of param: string * body: Expr * span: Span`, `App of func: Expr * arg: Expr * span: Span`
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Parser.fsy` — `let name params = body in inExpr` desugars to `Let(name, foldBack Lambda params body, inExpr)`. So `let add_n n = fun x -> x + n in ...` → `Let("add_n", Lambda("n", Lambda("x", ...)), ...)`.

---

## Metadata

**Confidence breakdown:**
- llvm.func + llvm.mlir.addressof + indirect call: HIGH — verified e2e multiple times
- Caller-allocates vs callee-allocates correctness: HIGH — segfault confirmed for callee-allocates with intermediate frame
- MlirOp design (7 new ops): HIGH — each op maps to a verified MLIR statement
- FuncOp.IsLlvmFunc extension: HIGH — mechanical change, no MLIR risk
- FuncSignature.ClosureInfo extension: HIGH — straightforward data extension
- Free variable analysis algorithm: HIGH — standard textbook algorithm, verified by test case
- Elaboration for Let + Lambda: MEDIUM — design is solid but has more moving parts; careful implementation needed
- App dispatch for 3 cases: MEDIUM — more complex than Phase 4; test each case

**Research date:** 2026-03-26
**Valid until:** 2026-04-25 (MLIR 20.x stable; LangThree Ast locked; all patterns verified)
