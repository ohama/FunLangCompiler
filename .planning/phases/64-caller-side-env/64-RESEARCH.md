# Phase 64: Caller-Side Closure Env Population - Research

**Researched:** 2026-04-01
**Domain:** FunLang compiler elaboration -- 2-lambda closure pattern SSA scope fix
**Confidence:** HIGH

## Summary

The 2-lambda pattern (Elaboration.fs line 713) compiles `let f a b = body` into a closure-maker `func.func` + an inner `llvm.func`. The maker stores captured variables into the closure env struct. The bug: when the maker references `env.Vars` for non-outerParam captures, it uses SSA values (e.g., `%t5`) from the **outer elaboration scope** that are invalid inside the maker's separate `func.func` body. This is an SSA scope violation.

The v19.0 guard (`when innerBody is not Lambda`) prevents 3+ lambda from entering 2-lambda, but then they fall through to the general `Let` (line 953) which only adds to `env.Vars` (not `KnownFuncs`), making them invisible in LetRec bodies. The single-arg Let-Lambda pattern (line 894) also rejects them because it requires zero captures.

**Primary recommendation:** Use Approach A (caller-side env population). Move capture stores from the maker `func.func` to the call site. This requires the least structural change: the maker only stores `addressof(fn_ptr)` and returns env, while the caller stores captures using its own in-scope SSA values. Then remove the 3+ lambda guard so all multi-lambda functions use the 2-lambda path.

## The Bug in Detail

### Current 2-Lambda Flow

```
Call site (in elaborateExpr, line 2608):
  1. GC_malloc env (numCaptures+1 slots)
  2. DirectCallOp(@maker, [outerParam_as_i64, envPtr])

Maker func.func (line 799-848):
  1. %arg0 = outerParam (I64)
  2. %arg1 = envPtr (Ptr)
  3. addressof(closureFnName) -> store at env[0]     // OK
  4. For each capture:
     if capName == outerParam: use %arg0              // OK
     else: use env.Vars[capName]                      // BUG: SSA leak!
  5. return %arg1
```

The `env.Vars[capName]` lookup (line 830) returns an MlirValue like `{Name="%t5"; Type=I64}` from the outer elaboration scope. This value does not exist inside the maker's `func.func`.

### Why the Guard Creates Issue #5

With the guard `when (match stripAnnot innerBody with Lambda _ -> false | _ -> true)`:

1. `let f a b c = body_using_outer` has innerBody = `Lambda(c, real_body)` -- guard FAILS
2. Falls to single-arg Let-Lambda (line 894) -- guard `freeVars.isEmpty` FAILS (has captures)
3. Falls to general `Let` (line 953) -- adds `f` to `env.Vars` as a closure Ptr value
4. LetRec body looks up `f` in `env.KnownFuncs` -- NOT FOUND
5. Falls to `env.Vars` lookup -- found as Ptr -- uses IndirectCallOp (slower, loses optimizations)
6. In LetRec specifically: the LetRec pattern (line 1356) only pre-registers in `KnownFuncs`, so `f` defined via general Let is invisible to other LetRec bindings

### Contrast with Standalone Lambda (line 3179)

The standalone `Lambda` handler at line 3179 does env allocation and capture stores **inline in the caller's scope**. It uses `Map.find capName env.Vars` -- which is correct because the stores happen in the same function body where the SSA values live. This is exactly the pattern we should replicate for the 2-lambda maker.

## Architecture Patterns

### Pattern: Caller-Side Env Population (Approach A) -- RECOMMENDED

**What:** Move capture stores out of the maker `func.func` and into the call site.

**Current call site** (line 2608-2628):
```fsharp
// CLOSURE-MAKING CALL -- allocate env on GC heap, then call
let ci = sig_.ClosureInfo.Value
let (argVal, argOps) = elaborateExpr env argExpr
let bytesVal = { Name = freshName env; Type = I64 }
let envPtrVal = { Name = freshName env; Type = Ptr }
let resultVal = { Name = freshName env; Type = Ptr }
let setupOps = [
    ArithConstantOp(bytesVal, int64 ((ci.NumCaptures + 1) * 8))
    LlvmCallOp(envPtrVal, "@GC_malloc", [bytesVal])
]
let callOp = DirectCallOp(resultVal, sig_.MlirName, [i64ArgVal; envPtrVal])
(resultVal, argOps @ setupOps @ coerceOps @ [callOp])
```

**Proposed call site:**
```fsharp
// CLOSURE-MAKING CALL -- allocate env, store captures, then call maker
let ci = sig_.ClosureInfo.Value
let (argVal, argOps) = elaborateExpr env argExpr
let bytesVal = { Name = freshName env; Type = I64 }
let envPtrVal = { Name = freshName env; Type = Ptr }
let resultVal = { Name = freshName env; Type = Ptr }
let setupOps = [
    ArithConstantOp(bytesVal, int64 ((ci.NumCaptures + 1) * 8))
    LlvmCallOp(envPtrVal, "@GC_malloc", [bytesVal])
]
// Store captures at call site (SSA values are in scope here)
let captureStoreOps =
    ci.CaptureNames |> List.mapi (fun i capName ->
        let slotVal = { Name = freshName env; Type = Ptr }
        let capVal = Map.find capName env.Vars
        if capVal.Type = I64 then
            let ptrVal = { Name = freshName env; Type = Ptr }
            [LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
             LlvmIntToPtrOp(ptrVal, capVal)
             LlvmStoreOp(ptrVal, slotVal)]
        else
            [LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
             LlvmStoreOp(capVal, slotVal)]
    ) |> List.concat
let callOp = DirectCallOp(resultVal, sig_.MlirName, [i64ArgVal; envPtrVal])
(resultVal, argOps @ setupOps @ coerceOps @ captureStoreOps @ [callOp])
```

**Proposed maker (simplified):**
```fsharp
// Maker only stores fn_ptr at env[0] and outerParam at the right slot
let makerOps =
    [ LlvmAddressOfOp(fnPtrVal, closureFnName)
      LlvmStoreOp(fnPtrVal, makerArg1) ]
// Store outerParam at its capture slot (if it appears in captures)
let outerParamStoreOps =
    match List.tryFindIndex ((=) outerParam) captures with
    | Some idx ->
        let slotVal = { Name = nextMakerName(); Type = Ptr }
        let ptrVal = { Name = nextMakerName(); Type = Ptr }
        [LlvmGEPLinearOp(slotVal, makerArg1, idx + 1)
         LlvmIntToPtrOp(ptrVal, makerArg0)
         LlvmStoreOp(ptrVal, slotVal)]
    | None -> []
let makerBodyOps = makerOps @ outerParamStoreOps @ [ReturnOp [makerArg1]]
```

### Data Flow Changes

**ClosureInfo must carry capture names** so the call site knows which env.Vars to store:

```fsharp
type ClosureInfo = {
    InnerLambdaFn: string
    NumCaptures: int
    InnerReturnIsBool: bool
    CaptureNames: string list        // NEW: ordered list of capture variable names
    OuterParamName: string           // NEW: which capture is the outerParam (stored by maker)
}
```

At the call site, `CaptureNames` tells us which variables to store, and `OuterParamName` tells us which one to skip (the maker handles it because it receives outerParam as %arg0).

### Guard Removal

After implementing caller-side capture stores, the `when (match stripAnnot innerBody with Lambda _ -> false | _ -> true)` guard on line 714 can be safely removed. The maker no longer references outer SSA values (except %arg0 = outerParam), so 3+ lambda functions work correctly through the 2-lambda path.

### Recommended Project Structure for Changes

```
src/FunLangCompiler.Compiler/
    Elaboration.fs    # Lines to modify:
                      #   8-12:    ClosureInfo type (add CaptureNames, OuterParamName)
                      #   714:     Remove 3+ lambda guard
                      #   818-838: Simplify maker (remove non-outerParam capture stores)
                      #   860-862: Add CaptureNames/OuterParamName to ClosureInfo construction
                      #   2608-2628: Add capture stores at call site
```

## Approach Comparison

### Approach A: Caller-Side Env Population -- RECOMMENDED

**Changes required:**
1. Add `CaptureNames` and `OuterParamName` to ClosureInfo (2 fields)
2. Simplify maker body: remove capture store loop for non-outerParam captures (~15 lines removed)
3. Add capture store loop at call site (~15 lines added)
4. Remove 3+ lambda guard (1 line)

**Pros:**
- SSA values at call site are guaranteed in scope
- Maker becomes trivially simple (only fn_ptr + outerParam)
- Minimal structural change to existing code
- 3+ lambda "just works" because the inner Lambda chain is elaborated by the existing recursion

**Cons:**
- Call site becomes slightly more complex
- ClosureInfo carries more data (but only 2 extra fields)

**Risk:** LOW -- the standalone Lambda pattern (line 3179) already does caller-side capture stores successfully.

### Approach B: Pass Captures as Maker Parameters -- NOT RECOMMENDED

**Changes required:**
1. Maker gets variable number of extra parameters
2. FuncSignature.ParamTypes changes per function
3. Call site must pass all captures as args
4. Maker stores them from its own args

**Pros:** Clean separation of concerns
**Cons:** Variable-arity makers break the uniform `(I64, Ptr) -> Ptr` signature. Every call site must match parameter count. Complex calling convention change. Much more code churn.

**Risk:** HIGH -- breaks uniformity assumptions throughout codebase.

### Approach C: Pre-Populate Env at Definition Site -- NOT RECOMMENDED

**Changes required:**
1. At `Let(f, Lambda(...), cont)`, allocate env and store captures immediately
2. Store env pointer in env.Vars alongside the KnownFuncs entry
3. At call site, only store outerParam

**Pros:** Single env allocation for captures
**Cons:** Cannot reuse env for multiple calls (each call has different outerParam). Would need to either clone env per call or restructure env layout. Significantly more complex.

**Risk:** MEDIUM -- env reuse semantics are tricky.

### Approach D: Remove Guard, Fix SSA Leak Differently -- NOT RECOMMENDED

**Changes required:**
1. Build an isolated SSA naming scope for maker body
2. Somehow re-map outer SSA values to maker-local values

**Pros:** Conceptually minimal
**Cons:** Cannot solve the fundamental problem -- maker is a separate func.func and simply cannot access outer SSA values. There is no "isolated env for SSA naming" that fixes this; the maker needs the actual runtime values, not just names. Would require passing values as arguments (which is Approach B).

**Risk:** HIGH -- this doesn't actually have a viable solution path.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Capture value passing | A new "SSA remapping" pass | Caller-side stores (like standalone Lambda does) | The standalone Lambda handler already solves this correctly |
| N-ary lambda detection | A general N-ary lambda flattener | Let the 2-lambda pattern handle all depths via recursion | Inner Lambda body gets elaborated recursively, creating nested closures naturally |
| Variable-arity makers | Custom calling convention per function | Uniform `(I64, Ptr) -> Ptr` + caller stores | Uniformity is essential for the KnownFuncs dispatch mechanism |

## Common Pitfalls

### Pitfall 1: Forgetting to Skip outerParam at Call Site

**What goes wrong:** The call site stores ALL captures including outerParam. But outerParam's value at the call site is the argument being passed, which the maker receives as %arg0 and stores itself. Double-storing is wasteful but not incorrect. However, if the call site tries `Map.find outerParam env.Vars` and outerParam is NOT in env.Vars (it might only be known as the argument expression), it crashes.

**How to avoid:** The call site should skip captures that equal `OuterParamName`. The maker handles those. Alternatively, have the call site store ALL captures and the maker only store fn_ptr (not outerParam). This is simpler but changes the maker signature since it no longer needs %arg0 for outerParam.

**Recommended:** Keep the current split -- maker stores fn_ptr + outerParam, caller stores the rest. This minimizes changes.

### Pitfall 2: Capture Names Not Available at Call Site

**What goes wrong:** The call site is in a different part of `elaborateExpr` (the `App` pattern at line 2608). It uses `sig_.ClosureInfo` to know about the closure. If `CaptureNames` is not stored in ClosureInfo, the call site cannot know which variables to store.

**How to avoid:** Add `CaptureNames` to ClosureInfo at construction time (line 861). The names are already computed as `captures` (line 719-723).

### Pitfall 3: Capture Variable Not in env.Vars at Call Site

**What goes wrong:** When a captured variable is a KnownFunc (not in env.Vars), the call site `Map.find capName env.Vars` fails.

**How to avoid:** The `captures` list is already filtered by `Map.containsKey name env.Vars` (line 721), so only env.Vars entries appear. But verify this assumption holds when the guard is removed and 3+ lambda enters this path. For 3+ lambda, the `freeVars` computation with `{innerParam}` as bound may include names from deeper lambda layers that are not in env.Vars. Need to verify the freeVars + filter logic is still correct.

**Specifically:** For `let f a b c = body`, the 2-lambda sees `Lambda(a, Lambda(b, Lambda(c, body)))`. outerParam = `a`, innerParam = `b`, innerBody = `Lambda(c, body)`. freeVars with bound={b} will recurse into Lambda(c, body) adding c to bound, then find free vars of body. Variables from body that are in env.Vars become captures. This is correct -- the captures are outer-scope variables used deep in body.

### Pitfall 4: Breaking Existing 2-Arg Functions

**What goes wrong:** If the capture-store-at-call-site logic has a bug, all existing 2-arg closures break (244 tests fail).

**How to avoid:** The change is mechanical -- move the exact same GEP+store ops from maker to call site. For 2-arg functions with no outer captures (captures = [outerParam] only), the call site adds zero extra stores (outerParam is handled by maker). So most existing functions are unaffected.

### Pitfall 5: LetRec Pre-Registration Must Include ClosureInfo

**What goes wrong:** The LetRec pattern (line 1356) pre-registers all bindings with `ClosureInfo = None`. If a binding is a multi-lambda function that should use the 2-lambda path, it gets registered without ClosureInfo. When called from a sibling, it goes through the direct-call path instead of the closure-making path.

**How to avoid:** Currently, LetRec bindings are compiled as single-arg `func.func` (line 1380-1410). The inner Lambda body is elaborated recursively. For a 2-arg LetRec binding `let rec f a b = ...`, the body `Lambda(b, ...)` is elaborated as a standalone Lambda, producing an inline closure. The return type is Ptr. This works for the current design. The 2-lambda pattern only applies to `Let` bindings, not LetRec bindings directly.

For Issue #5 (LetRec visibility), the problem is that 3+ arg functions fall to general `Let` and are added to `env.Vars` only. After removing the guard, they enter the 2-lambda path and get added to `KnownFuncs` -- this fixes Issue #5 directly.

## Implementation Plan

### Step 1: Extend ClosureInfo Type (line 8-12)

```fsharp
type ClosureInfo = {
    InnerLambdaFn: string
    NumCaptures: int
    InnerReturnIsBool: bool
    CaptureNames: string list       // NEW
    OuterParamName: string          // NEW
}
```

### Step 2: Populate New Fields (line 861)

```fsharp
let closureInfo = {
    InnerLambdaFn = closureFnName
    NumCaptures = numCaptures
    InnerReturnIsBool = bodyReturnsBool innerBody
    CaptureNames = captures          // Already computed at line 719
    OuterParamName = outerParam      // Already available
}
```

### Step 3: Simplify Maker Body (lines 818-841)

Remove the capture store loop. Maker only does:
1. `addressof(closureFnName)` -> store at env[0]
2. If outerParam is in captures: GEP to its slot, inttoptr %arg0, store
3. `return %arg1`

### Step 4: Add Capture Stores at Call Site (lines 2608-2628)

After GC_malloc and before DirectCallOp, add:
```fsharp
let captureStoreOps =
    ci.CaptureNames
    |> List.mapi (fun i capName ->
        if capName = ci.OuterParamName then []  // Maker handles this
        else
            let slotVal = { Name = freshName env; Type = Ptr }
            let capVal = Map.find capName env.Vars
            if capVal.Type = I64 then
                let ptrVal = { Name = freshName env; Type = Ptr }
                [LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                 LlvmIntToPtrOp(ptrVal, capVal)
                 LlvmStoreOp(ptrVal, slotVal)]
            else
                [LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                 LlvmStoreOp(capVal, slotVal)]
    ) |> List.concat
```

### Step 5: Remove the 3+ Lambda Guard (line 714)

Change:
```fsharp
| Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, letSpan)
    when (match stripAnnot innerBody with Lambda _ -> false | _ -> true) ->
```
To:
```fsharp
| Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, letSpan) ->
```

### Step 6: Test

1. Run all 244 existing E2E tests
2. Add test: 3-arg curried function + outer variable capture
3. Add test: 3-arg curried function called from LetRec body
4. Verify Issue #5 scenario compiles and runs correctly

## Edge Cases to Consider

### Edge Case 1: All Captures are outerParam Only

For `let f a b = a + b`, captures = ["a"] (outerParam only). Call site stores nothing extra. Maker stores fn_ptr + outerParam as before. No behavior change.

### Edge Case 2: No Captures at All

For `let f a b = b + 1`, captures = []. NumCaptures = 0. Call site stores nothing. Maker stores only fn_ptr. Env is just 1 slot (fn_ptr).

### Edge Case 3: Multiple Non-outerParam Captures

For `let outer1 = 10; let outer2 = 20; let f a b = a + b + outer1 + outer2`:
- captures = ["a", "outer1", "outer2"] (sorted)
- outerParam = "a"
- Call site stores outer1 at slot 2, outer2 at slot 3
- Maker stores fn_ptr at slot 0, outerParam (a) at slot 1

### Edge Case 4: 3+ Lambda with Captures

For `let f a b c = a + b + c + outer`:
- 2-lambda sees: outerParam="a", innerParam="b", innerBody=Lambda(c, a+b+c+outer)
- freeVars({b}, Lambda(c, a+b+c+outer)) = {a, outer} (c is bound by Lambda)
- captures filtered by env.Vars || = outerParam: captures = ["a", "outer"]
- Call site stores "outer" at slot 2
- Maker stores fn_ptr at slot 0, "a" (outerParam) at slot 1
- innerBody Lambda(c, a+b+c+outer) is elaborated inside closure_fn, loads captures from env

## Open Questions

1. **Partial application of 2-lambda functions:**
   When `f` is partially applied (e.g., `let g = f 10`), the call site generates the closure-making call. The caller stores captures. If `f` is passed as a value and later applied, the caller at the point of `f 10` must have the captured variables in scope. Since captures are stored at allocation time (when the maker is called), and the caller IS the one calling the maker, the captured variables MUST be in the caller's env.Vars. This should work because the captures are outer-scope variables that are visible wherever `f` is called from -- but verify with a test case where `f` is called from a different scope than where it was defined.

   **Recommendation:** The captures list only includes variables from the definition site's env.Vars. At the call site (a different location), those same variable names might not be in env.Vars. This is a potential issue. **HOWEVER**, looking at the code more carefully: the call site at line 2608 is reached when `name` is found in `env.KnownFuncs`. KnownFuncs propagate through scope. The captures are from the DEFINITION site. At the CALL site, the same outer variables should be in scope (because they were defined before `f`). Unless `f` is called from a completely unrelated scope... but in FunLang's let-based scoping, if `f` is visible, its captures should also be visible.

   **Mitigation:** Add a runtime check: if `Map.tryFind capName env.Vars` returns None at the call site, emit a clear error message. This should never happen in correct programs but serves as a safety net.

2. **Module-level functions with no captures:**
   Most module-level 2-arg functions have captures = [outerParam] only. The call site change adds zero stores for these. Verify no regression.

## Sources

### Primary (HIGH confidence)
- Elaboration.fs lines 713-882: 2-lambda pattern (read directly)
- Elaboration.fs lines 2608-2628: KnownFuncs closure-making call site (read directly)
- Elaboration.fs lines 3179-3272: Standalone Lambda handler (read directly, serves as reference implementation for caller-side stores)
- Elaboration.fs lines 1356-1417: LetRec handler (read directly)
- Elaboration.fs lines 891-952: Single-arg Let-Lambda handler (read directly)
- MlirIR.fs: FuncOp and MlirOp types (read directly)
- Phase 63 RESEARCH.md: Previous research on the same bug domain

### Secondary (MEDIUM confidence)
- ROADMAP.md and REQUIREMENTS.md: Project requirements and success criteria

## Metadata

**Confidence breakdown:**
- Bug root cause analysis: HIGH -- traced exact SSA scope violation in code
- Approach A (caller-side): HIGH -- mirrors proven standalone Lambda pattern
- Approach comparison: HIGH -- all approaches analyzed against codebase
- Edge cases: MEDIUM -- need integration tests to verify all scenarios
- LetRec interaction: MEDIUM -- the fix should work but needs testing

**Research date:** 2026-04-01
**Valid until:** 2026-05-01 (stable compiler internals, unlikely to change)
