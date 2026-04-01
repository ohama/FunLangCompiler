# Phase 65: Partial Env Pattern Implementation - Research

**Researched:** 2026-04-02
**Domain:** FunLang compiler elaboration — definition-site env pre-allocation for LetRec body closure calls
**Confidence:** HIGH

## Summary

Phase 64 (v20.0) implemented caller-side env population: non-outerParam captures are stored at the call site where the SSA values are in scope. This fixed the SSA scope violation in the 2-lambda maker. However, a new problem surfaces in LetRec bodies: when a LetRec binding body calls a 2-lambda KnownFunc that has non-outerParam captures, the call site has `env.Vars = Map.ofList [(param, %arg0)]` only (LetRec resets Vars). The captures are not in scope at the LetRec call site, so they cannot be stored. Phase 64 added a fallback that tries `env.Vars[funcName]` for an indirect call — but the function is only in `KnownFuncs`, never in `env.Vars`, so the fallback also fails with a hard crash.

The fix is the "partial env" pattern: at the **definition site** (when the 2-lambda Let is being elaborated), if the function has non-outerParam captures, allocate a GC_malloc'd env and immediately store the fn_ptr and all non-outerParam captures into it. Store this "template env" pointer in `env.Vars[name]` alongside the `KnownFuncs` entry. At the **call site**, when captures are not in scope, use the template env: GC_malloc a new env of the same size, copy all slots field-by-field from the template (GEP+load+GEP+store per slot), then store outerParam at its slot, and call the inner function directly via IndirectCallOp.

**Primary recommendation:** At the 2-lambda definition site (Elaboration.fs line 713), after building the inner func and maker, check if `numCaptures > 1` (i.e., there are non-outerParam captures). If so, emit GC_malloc + fn_ptr store + capture stores for all non-outerParam slots. Store the resulting env ptr in `env.Vars[name]` (type Ptr). The call site already handles the fallback case where `Map.tryFind name env.Vars` succeeds — update that fallback to do the template-copy + outerParam-fill instead of the broken indirect call.

## Standard Stack

This is an internal compiler change — no external libraries involved.

### Core Components
| Component | Location | Purpose | Notes |
|-----------|----------|---------|-------|
| `ElabEnv.Vars` | Elaboration.fs line 32 | SSA value map for runtime values | Currently only has KnownFuncs entries for 2-lambda, not Vars entries |
| `ClosureInfo` | Elaboration.fs line 8-14 | Metadata for closure-making call sites | Already has CaptureNames, OuterParamName from Phase 64 |
| 2-lambda Let handler | Elaboration.fs line 713-874 | Compiles Let(f, Lambda(a, Lambda(b, body)), cont) | Must emit definition-site env allocation when captures exist |
| KnownFuncs call site | Elaboration.fs line 2600-2659 | App(Var name, arg) with ClosureInfo.IsSome | Must use template-copy path when captures not in scope |
| `MlirOp.LlvmGEPLinearOp` | MlirIR.fs line 57 | GEP for array-style slot access | Used for env slot indexing |
| `MlirOp.LlvmLoadOp` | MlirIR.fs line 55 | Load a value from a pointer | Used for reading template env slots |
| `MlirOp.LlvmCallOp` | MlirIR.fs line 63 | External function call (GC_malloc) | Used for new env allocation |
| `MlirOp.IndirectCallOp` | MlirIR.fs line 61 | Call a closure via fn_ptr + env_ptr + arg | Used in the template-copy call path |

## Architecture Patterns

### Recommended Project Structure
```
src/FunLangCompiler.Compiler/
    Elaboration.fs    # All changes in this file
        line 713-874  # 2-lambda Let handler: add definition-site env allocation
        line 2600-2659 # KnownFuncs call site: update fallback to use template env
tests/compiler/
    65-01-letrec-3arg-outer-capture.sh    # NEW: LetRec + 3-arg + outer captures
    65-01-letrec-3arg-outer-capture.flt   # NEW: expected output
    65-02-nested-letrec-outer-capture.sh  # NEW: nested LetRec + outer captures
    65-02-nested-letrec-outer-capture.flt # NEW: expected output
```

### Pattern 1: Definition-Site Partial Env Allocation

**What:** When the 2-lambda handler compiles `let f a b = body` and there are non-outerParam captures (captures that are NOT `outerParam`), it must pre-allocate an env struct and fill in fn_ptr + non-outerParam captures at definition time. Store the resulting env pointer in `env.Vars[name]`.

**When to use:** `numCaptures > 0` and there exists at least one capture that is NOT the outerParam. Equivalently: `captures |> List.exists (fun c -> c <> outerParam)`.

**Implementation (to be added after Step 6 in the 2-lambda handler, before "Step 7: Elaborate inExpr"):**

```fsharp
// Phase 65: If there are non-outerParam captures, pre-allocate a template env
// and store fn_ptr + non-outerParam captures at definition time.
// This allows LetRec bodies to use this env when calling f (captures not in scope at LetRec call site).
let hasNonOuterCaptures = captures |> List.exists (fun c -> c <> outerParam)
let (templateEnvOps, envWithTemplateVar) =
    if not hasNonOuterCaptures then
        ([], env')
    else
        let tBytesVal = { Name = freshName env; Type = I64 }
        let tEnvPtr   = { Name = freshName env; Type = Ptr }
        let tFnPtrVal = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(tBytesVal, int64 ((numCaptures + 1) * 8))
            LlvmCallOp(tEnvPtr, "@GC_malloc", [tBytesVal])
            LlvmAddressOfOp(tFnPtrVal, closureFnName)
            LlvmStoreOp(tFnPtrVal, tEnvPtr)     // env[0] = fn_ptr
        ]
        let captureStoreOps =
            captures |> List.mapi (fun i capName ->
                if capName = outerParam then []   // outerParam stored per-call, skip here
                else
                    match Map.tryFind capName env.Vars with
                    | None -> []  // capture not in scope yet (shouldn't happen at top-level Let)
                    | Some capVal ->
                        let slotVal = { Name = freshName env; Type = Ptr }
                        if capVal.Type = I64 then
                            let ptrVal = { Name = freshName env; Type = Ptr }
                            [ LlvmGEPLinearOp(slotVal, tEnvPtr, i + 1)
                              LlvmIntToPtrOp(ptrVal, capVal)
                              LlvmStoreOp(ptrVal, slotVal) ]
                        else
                            [ LlvmGEPLinearOp(slotVal, tEnvPtr, i + 1)
                              LlvmStoreOp(capVal, slotVal) ]
            ) |> List.concat
        // Store template env ptr in Vars so LetRec bodies can find it
        let envWithVar = { env' with Vars = Map.add name tEnvPtr env'.Vars }
        (allocOps @ captureStoreOps, envWithVar)
```

**Note:** The template ops (`templateEnvOps`) must be emitted BEFORE the `inExpr` elaboration but AFTER the func.func definitions are registered. They are part of the current function's SSA body. The updated env is passed to `elaborateExpr envWithTemplateVar inExpr`.

### Pattern 2: Template-Copy Call Path (at Call Site)

**What:** When `captureStoreResult` has at least one `None` (capture not in scope), instead of the current broken indirect fallback, check if `env.Vars[name]` exists as a template env ptr. If so, clone the template + fill outerParam + call inner directly.

**When to use:** `captureStoreResult |> List.exists Option.isNone` AND `Map.tryFind name env.Vars` is `Some templatePtr`.

**Implementation (replaces the `else` fallback branch at line 2644-2659):**

```fsharp
else
    // Fallback: captures not in scope at call site (e.g., LetRec body).
    // Use the pre-allocated template env from the definition site.
    match Map.tryFind name env.Vars with
    | Some templatePtr ->
        // Clone the template env: allocate new env, copy all slots, fill in outerParam
        let copyBytesVal = { Name = freshName env; Type = I64 }
        let newEnvPtr = { Name = freshName env; Type = Ptr }
        let copyOps = [
            ArithConstantOp(copyBytesVal, int64 ((ci.NumCaptures + 1) * 8))
            LlvmCallOp(newEnvPtr, "@GC_malloc", [copyBytesVal])
        ]
        // Copy all slots from template (fn_ptr at slot 0, captures at slots 1..n)
        let slotCopyOps =
            [ 0 .. ci.NumCaptures ] |> List.collect (fun i ->
                let srcSlot = { Name = freshName env; Type = Ptr }
                let loadedVal = { Name = freshName env; Type = Ptr }
                let dstSlot = { Name = freshName env; Type = Ptr }
                [ LlvmGEPLinearOp(srcSlot, templatePtr, i)
                  LlvmLoadOp(loadedVal, srcSlot)
                  LlvmGEPLinearOp(dstSlot, newEnvPtr, i)
                  LlvmStoreOp(loadedVal, dstSlot) ])
        // Fill in outerParam at its slot in the new env
        let outerParamIdx =
            match List.tryFindIndex ((=) ci.OuterParamName) ci.CaptureNames with
            | Some idx -> idx + 1  // +1 because slot 0 = fn_ptr
            | None -> failwith "Elaboration: outerParam not found in CaptureNames"
        let outerSlotVal = { Name = freshName env; Type = Ptr }
        let outerPtrVal  = { Name = freshName env; Type = Ptr }
        let outerStoreOps =
            [ LlvmGEPLinearOp(outerSlotVal, newEnvPtr, outerParamIdx)
              LlvmIntToPtrOp(outerPtrVal, i64ArgVal)  // i64ArgVal = coerced arg
              LlvmStoreOp(outerPtrVal, outerSlotVal) ]
        // Load fn_ptr from new env slot 0, then indirect call
        let fnPtrVal  = { Name = freshName env; Type = Ptr }
        let resultVal = { Name = freshName env; Type = I64 }
        let callOps = [
            LlvmLoadOp(fnPtrVal, newEnvPtr)
            IndirectCallOp(resultVal, fnPtrVal, newEnvPtr, argVal)
        ]
        (resultVal, argOps @ copyOps @ slotCopyOps @ outerStoreOps @ callOps)
    | None ->
        failwith (sprintf "Elaboration: '%s' has captures not in scope and no template env available" name)
```

### Pattern 3: Template Env Ops Emission

**What:** The `templateEnvOps` from Pattern 1 must be woven into the current elaboration output. The 2-lambda handler currently returns `elaborateExpr env' inExpr`. With the template, it must prepend `templateEnvOps` to the output ops.

**Implementation change to Step 7:**

```fsharp
// Step 7: Elaborate inExpr with updated env (including template var if applicable)
let (inVal, inOps) = elaborateExpr envWithTemplateVar inExpr
(inVal, templateEnvOps @ inOps)
```

Currently Step 7 is just `elaborateExpr env' inExpr` (line 874). We need to capture the ops and prepend templateEnvOps.

### Anti-Patterns to Avoid

- **Mutating the template env at call time**: The template env ptr is shared across all calls. If a call site stores outerParam directly into the template, the next call will see the previous outerParam value. Always allocate a new env and copy from template.
- **Skipping fn_ptr in the copy**: Slot 0 of the template stores fn_ptr. The copy loop must include slot 0 (`[0..numCaptures]`, not `[1..numCaptures]`).
- **Using template when all captures are in scope**: The fast path (all captures in env.Vars) should continue to work without using the template. Template is only for the fallback path.
- **outerParam slot left uninitialized in new env**: After copying the template (which has outerParam slot = either zero or from template initialization), always overwrite the outerParam slot with the current call's argument value.
- **Adding template env to env.Vars unconditionally**: Only store in Vars when `hasNonOuterCaptures`. For functions with no outer captures (captures = [outerParam] only), no template is needed and `env.Vars[name]` should remain absent.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Env cloning | A new `@lang_env_clone` C function | Field-by-field GEP+load+GEP+store | No external dependency needed; env sizes are statically known (numCaptures+1 slots) |
| memcpy | Add `@memcpy` to external decls | Field-by-field copy | Simpler, already proven pattern in existing codebase; avoids new external decl |
| Template as a global constant | llvm.global with initial capture values | Runtime GC_malloc with GC'd pointers | Captures are GC-managed heap values (Ptr type); they cannot be LLVM globals |
| Env copy on every call regardless | Always use template | Only use template in fallback path | Performance regression for all callers; fast path (captures in scope) should remain unchanged |

**Key insight:** The existing codebase already does per-slot GEP+load+store patterns everywhere (see record copy at line 3466, capture load in inner func at line 758-772). No new mechanisms needed.

## Common Pitfalls

### Pitfall 1: Emitting templateEnvOps at Wrong Nesting Level

**What goes wrong:** The template env ops (`GC_malloc`, `addressof`, capture stores) are emitted as SSA operations in the current function body. If they are placed inside a block that only executes conditionally (e.g., inside an if-then-else branch), the template ptr may not be defined on all paths.

**Why it happens:** The 2-lambda handler is called from within `elaborateExpr` which may be in a conditional branch.

**How to avoid:** The template ops are prepended to `inOps` (the result of elaborating `inExpr`). Since `inExpr` is the continuation that uses `f`, this is correct — the template is allocated exactly once at the point where `f` is bound, before any use. This mirrors how `let x = expr in body` works: `x` is allocated when the binding is processed.

**Warning signs:** If the template ptr is a dangling/undefined SSA value at a call site, MLIR will report "use of undefined value".

### Pitfall 2: Template Env Missing fn_ptr Slot

**What goes wrong:** If fn_ptr is NOT stored in the template env slot 0, and the template-copy path loads fn_ptr from the copied env, it will load garbage (uninitialized memory from GC_malloc).

**Why it happens:** Developer might think "the copy will get fn_ptr from the template" but forgets to store fn_ptr into the template in the first place.

**How to avoid:** In the definition-site allocation (Pattern 1), explicitly include `LlvmAddressOfOp(tFnPtrVal, closureFnName)` and `LlvmStoreOp(tFnPtrVal, tEnvPtr)` (env[0] = fn_ptr). Verify the template allocation always includes the fn_ptr store op.

### Pitfall 3: outerParam Slot Index Calculation Off-by-One

**What goes wrong:** The outerParam is at `CaptureNames.[outerParamIdx]` (0-indexed in CaptureNames), which means env slot `outerParamIdx + 1` (because slot 0 = fn_ptr). An off-by-one stores outerParam at the wrong slot, corrupting a capture or fn_ptr.

**Why it happens:** The CaptureNames list is 0-indexed but env slots are 1-indexed for captures.

**How to avoid:** Use `List.tryFindIndex ((=) ci.OuterParamName) ci.CaptureNames |> Option.get` then add 1 to get the env slot index.

### Pitfall 4: Template Path Used in Prelude / Module-Qualified Names

**What goes wrong:** Module-qualified names (e.g., `List_map`) also use the 2-lambda path. If the template env allocation is emitted in a module-level scope where there are no surrounding SSA values, this could cause issues.

**Why it happens:** `twoLambdaShortAlias` is already handled at line 864-870. Template env is emitted as part of the Let binding's continuation, which is in the module-level elaboration sequence.

**How to avoid:** The template allocation ops are part of the normal elaboration output; they appear before `inExpr`. This is exactly how other inline allocations work (e.g., string literal GC_malloc in `let s = "hello" in body`). No special handling needed.

### Pitfall 5: Fast Path Regression

**What goes wrong:** Existing 2-arg curried function calls (where all captures are in scope) might accidentally use the template-copy path instead of the direct call path, causing performance regression and potential correctness issues.

**Why it happens:** The condition change in the fallback branch might be too broad.

**How to avoid:** The fast path condition remains `captureStoreResult |> List.forall Option.isSome`. Only when this is false (at least one capture missing) do we enter the fallback. The fallback is only reached when a LetRec body (or other scope where captures are not in env.Vars) calls the function. This is the exact same guard as before, just with a better fallback implementation.

### Pitfall 6: LetRec body using module-level `let` as captures

**What goes wrong:** A `let outer = 10` at module level is compiled via the general `Let` arm (line 945). Its SSA value (e.g., `%c0 = arith.constant 10`) lives in the module-level `@main` function body. When the 2-lambda pattern tries to store `outer` in the template env, `Map.tryFind "outer" env.Vars` at the definition site should return this SSA value (`%c0`). BUT: `outer` might be an `ArithConstantOp` value stored in `env.Vars` as `{Name="%c0"; Type=I64}`. This should be in scope at the definition site (since the 2-lambda definition comes after `let outer = 10`). The template allocation captures it correctly.

**Why it happens:** This is the core Issue #5 scenario. The DEFINITION site is in `@main`; the CALL site (inside LetRec body, which is a separate `func.func`) cannot see `%c0` from `@main`.

**How to avoid:** The definition-site allocation runs in the outer scope (where `outer` IS in `env.Vars`). The template env stores the runtime value of `outer`. The LetRec call site just uses the template (no need to look up `outer` in env.Vars at call time). This is the correct fix.

## Code Examples

### Example: What Gets Generated for `let combine3 a b c = a + b + c + outer1 + outer2`

The 2-lambda handler sees:
- `outerParam = "a"`, `innerParam = "b"`, `innerBody = Lambda(c, a+b+c+outer1+outer2)`
- `captures = ["a"; "outer1"; "outer2"]` (sorted, filtered to env.Vars)
- `numCaptures = 3`
- `hasNonOuterCaptures = true` (outer1, outer2 are not outerParam "a")

**Definition-site ops emitted (before inExpr):**
```mlir
%t_bytes = arith.constant 32 : i64          // (3+1) * 8
%tenv = llvm.call @GC_malloc(%t_bytes) : (i64) -> !llvm.ptr
%fnptr = llvm.mlir.addressof @closure_fn_0 : !llvm.ptr
llvm.store %fnptr, %tenv : !llvm.ptr, !llvm.ptr        // slot 0 = fn_ptr
// slot 1 = "a" = outerParam — SKIP (filled per-call)
// slot 2 = "outer1"
%slot2 = llvm.getelementptr %tenv[2] : (!llvm.ptr) -> !llvm.ptr, i64
%outer1_ptr = llvm.inttoptr %outer1_val : i64 to !llvm.ptr
llvm.store %outer1_ptr, %slot2 : !llvm.ptr, !llvm.ptr
// slot 3 = "outer2"
%slot3 = llvm.getelementptr %tenv[3] : (!llvm.ptr) -> !llvm.ptr, i64
%outer2_ptr = llvm.inttoptr %outer2_val : i64 to !llvm.ptr
llvm.store %outer2_ptr, %slot3 : !llvm.ptr, !llvm.ptr
```

**Call site ops (when called from LetRec body, captures not in scope):**
```mlir
// Fast: evaluate arg (outerParam value)
%arg = ... // elaborate arg expression
// Clone template env
%copy_bytes = arith.constant 32 : i64
%new_env = llvm.call @GC_malloc(%copy_bytes) : (i64) -> !llvm.ptr
// Copy all 4 slots from template (fn_ptr + 3 captures)
%src0 = llvm.getelementptr %tenv[0] ...
%v0 = llvm.load %src0 : ...
%dst0 = llvm.getelementptr %new_env[0] ...
llvm.store %v0, %dst0 ...
// ... (repeat for slots 1, 2, 3)
// Fill in outerParam (slot 1 = index of "a" in CaptureNames = 0, so env slot = 1)
%outer_slot = llvm.getelementptr %new_env[1] ...
%arg_as_ptr = llvm.inttoptr %arg : i64 to !llvm.ptr
llvm.store %arg_as_ptr, %outer_slot ...
// Indirect call
%fnptr_loaded = llvm.load %new_env : !llvm.ptr -> !llvm.ptr
%result = llvm.call %fnptr_loaded(%new_env, %arg) : ...
```

### Example: No-Captures Function (No Template Needed)

For `let f a b = a + b` (no outer captures): `captures = ["a"]`, `outerParam = "a"`, `hasNonOuterCaptures = false`. No template env is emitted. `env.Vars[name]` is NOT populated. Call site behavior unchanged (fast path only).

### Example: Test Scenario for TEST-01 (LetRec + 3-arg + outer captures)

```fsharp
let outer_val = 100

let combine3 (a : int) (b : int) (c : int) : int =
    a + b + c + outer_val

let main =
    let rec loop (n : int) : int =
        if n = 0 then 0
        else combine3 n 1 2 + loop (n - 1)
    let result = loop 3
    println (to_string result)
// Expected: loop(3) = combine3(3,1,2) + loop(2)
//                   = (3+1+2+100) + (combine3(2,1,2) + loop(1))
//                   = 106 + (105 + (combine3(1,1,2)+loop(0)))
//                   = 106 + 105 + (104 + 0) = 315
```

### Example: Test Scenario for TEST-02 (Nested LetRec + outer captures)

```fsharp
let multiplier = 5

let scale_add (factor : int) (base_val : int) (x : int) : int =
    x * factor + base_val + multiplier

let main =
    let rec outer_loop (n : int) : int =
        if n = 0 then 0
        else
            let rec inner_loop (m : int) : int =
                if m = 0 then 0
                else scale_add n 1 m + inner_loop (m - 1)
            inner_loop n + outer_loop (n - 1)
    println (to_string (outer_loop 2))
```

## State of the Art

| Old Approach (Phase 64 fallback) | Current Approach (Phase 65) | Impact |
|----------------------------------|------------------------------|--------|
| Indirect fallback tries `env.Vars[name]` → crashes because name is only in KnownFuncs | Template env pre-allocated at definition site; template-copy path used at LetRec call site | Correct behavior instead of crash |
| Maker func.func stores fn_ptr + outerParam | Maker unchanged; template also stores fn_ptr + non-outerParam captures | No maker change needed |
| All captures stored at call site (fast path) | Fast path unchanged; template-copy path added as fallback | Zero regression risk for existing tests |

**Deprecated/outdated:**
- The broken indirect fallback at lines 2644-2659 (fallback tries `env.Vars[name]` which is always None for 2-lambda KnownFuncs without template): replaced by template-copy path.

## Open Questions

1. **outerParam slot in the template env (slot initialization)**
   - What we know: The template env is allocated with `(numCaptures + 1) * 8` bytes. The outerParam slot (slot `outerParamIdx + 1`) is NOT stored during template allocation (since outerParam is per-call). GC_malloc returns zeroed memory in Boehm GC.
   - What's unclear: Is it safe to copy an uninitialized outerParam slot from the template to the new env, then immediately overwrite it? Yes, because we always overwrite it before the call.
   - Recommendation: Document this in code comments; the copy of the outerParam slot from template is harmless since it's always overwritten.

2. **ClosureInfo needs to carry outerParam slot index**
   - What we know: `OuterParamName` is already in ClosureInfo (Phase 64). `CaptureNames` is already in ClosureInfo. We can compute the slot index at call site via `List.tryFindIndex ((=) ci.OuterParamName) ci.CaptureNames`.
   - What's unclear: Whether it's better to precompute this index in ClosureInfo.
   - Recommendation: Compute it at the call site from existing fields. No ClosureInfo change needed.

3. **Multiple LetRec bindings sharing the same outer capture**
   - What we know: If `let f a b = body_using_outer` is defined once, its template env stores `outer`'s SSA value at definition time. Multiple LetRec bodies calling `f` each clone the template independently.
   - What's unclear: Is there a risk of GC-ing the template before LetRec bodies run?
   - Recommendation: The template env ptr is stored in `env.Vars[name]`. Since `env.Vars` is passed through elaboration, and the template ptr is an SSA value in `@main`'s body, it is alive for the duration of the function. GC will not collect it (it's referenced from the stack). No issue.

4. **REC-02: can we eliminate indirect fallback entirely?**
   - What we know: REC-02 says "LetRec body에서 indirect fallback 없이 direct call 가능". The template-copy path uses `IndirectCallOp` (loads fn_ptr, calls via ptr). This is "indirect" in the LLVM sense.
   - What's unclear: Does REC-02 mean no IndirectCallOp, or just "no fallback to the broken indirect path from Phase 64"?
   - Recommendation: Interpret REC-02 as "no broken fallback" (since the Phase 64 fallback tried `env.Vars[name]` and crashed). The template-copy path with IndirectCallOp is correct and intentional. If a full direct-call optimization is desired, that's a separate future phase.

## Sources

### Primary (HIGH confidence)
- `src/FunLangCompiler.Compiler/Elaboration.fs` lines 713-874 — 2-lambda Let handler (read directly)
- `src/FunLangCompiler.Compiler/Elaboration.fs` lines 2600-2659 — KnownFuncs call site with Phase 64 fallback (read directly)
- `src/FunLangCompiler.Compiler/Elaboration.fs` lines 3208-3310 — standalone Lambda handler (reference implementation for inline capture stores, read directly)
- `src/FunLangCompiler.Compiler/MlirIR.fs` — all MlirOp types (read directly)
- `src/FunLangCompiler.Compiler/Printer.fs` — how IndirectCallOp and GEP ops are printed (read directly)
- `.planning/milestones/v20.0-phases/64-caller-side-env/64-01-SUMMARY.md` — Phase 64 decisions and pre-existing LetRec limitation (read directly)
- `.planning/milestones/v20.0-phases/64-caller-side-env/64-RESEARCH.md` — Phase 64 research, approach comparison (read directly)
- `tests/compiler/64-02-3arg-letrec.sh` — existing test (no outer captures, serves as baseline) (read directly)
- `git log -1 f8f6c47` — fallback commit details (read directly)

### Secondary (MEDIUM confidence)
- `REQUIREMENTS.md` — ENV-01 through TEST-02 requirements (read directly)
- `ROADMAP.md` — Phase 65 success criteria (read directly)

## Metadata

**Confidence breakdown:**
- Root cause analysis: HIGH — traced exact failure path from LetRec body through KnownFuncs to broken fallback
- Definition-site allocation pattern: HIGH — mirrors standalone Lambda inline allocation (lines 3279-3310), same ops
- Template-copy call path: HIGH — uses existing MlirOps (GEP+load+store), no new machinery needed
- Test scenarios: HIGH — computed expected outputs manually (315 for TEST-01)
- Edge cases (multiple calls, GC safety): MEDIUM — reasoned but not tested

**Research date:** 2026-04-02
**Valid until:** 2026-05-02 (stable compiler internals)
