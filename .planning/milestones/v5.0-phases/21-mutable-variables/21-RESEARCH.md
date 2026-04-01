# Phase 21: Mutable Variables - Research

**Researched:** 2026-03-27
**Domain:** GC heap ref-cell allocation + transparent deref + closure capture in F# compiler backend (MLIR/LLVM)
**Confidence:** HIGH

## Summary

Phase 21 implements `LetMut`/`Assign`/`LetMutDecl` mutable variable support. The AST nodes already exist in FunLang (confirmed in `Ast.fs`). The elaborator (`Elaboration.fs`) has no cases for them yet — they fall through the catch-all `| _ -> Set.empty` in `freeVars` and would hit `failwithf "unbound variable"` in `elaborateExpr`. The implementation is entirely within `Elaboration.fs`: no new `MlirOp` DU cases, no `Printer.fs` changes, no `Pipeline.fs` changes, no C runtime additions.

The core mechanism is a GC heap ref cell: `LetMut(name, initExpr, body)` allocates 8 bytes via `GC_malloc(8)`, stores the initial value, and binds `name` in `env.Vars` to the cell `Ptr`. `Var(name)` for a mutable name emits `LlvmLoadOp` through that pointer. `Assign(name, valExpr)` emits `LlvmStoreOp` to the same pointer. A `MutableVars: Set<string>` field on `ElabEnv` distinguishes ref cell pointers from ordinary `Ptr`-typed values (closures, records, lists) that must not be auto-dereferenced. Module-level `LetMutDecl` is handled by extending `extractMainExpr` to desugar it into a nested `LetMut` expression — identical to the existing `LetDecl` → `Let` pattern.

The single hardest correctness concern is closure capture: closures must capture the ref cell **pointer** (not the current value), so mutations made after closure creation are visible inside the closure. The existing closure capture code hardcodes `I64` for all captured values and loads each captured value from `env.Vars`; this must be extended to detect mutable names and capture the `Ptr` cell pointer instead. The `freeVars` function must also be updated before any codegen work because its catch-all currently silently discards free variables inside `LetMut`/`Assign` nodes.

**Primary recommendation:** Implement in dependency order — freeVars fix first, then ElabEnv extension, then LetMut/Assign/Var elaboration, then LetMutDecl desugaring, then closure capture fix, then tests.

## Standard Stack

This phase introduces no new libraries or tools.

### Core (already present)

| Component | Location | Purpose | Status |
|-----------|----------|---------|--------|
| `Elaboration.fs` | `src/FunLangCompiler.Compiler/` | AST → MlirIR: add LetMut/Assign/Var/LetMutDecl cases | Extend |
| `MlirIR.fs` | `src/FunLangCompiler.Compiler/` | DU: MlirType/MlirOp — no changes needed | Unchanged |
| `Printer.fs` | `src/FunLangCompiler.Compiler/` | MlirIR → MLIR text — no changes needed | Unchanged |
| `Pipeline.fs` | `src/FunLangCompiler.Compiler/` | Shell pipeline — no changes needed | Unchanged |
| `lang_runtime.c` | `src/FunLangCompiler.Compiler/` | C runtime — no changes needed for Phase 21 | Unchanged |
| FunLang AST | `../FunLang/src/FunLang/Ast.fs` | Defines LetMut, Assign, LetMutDecl nodes | Read-only reference |

### Existing MlirOp Cases Used (no new cases needed)

| Op | Used For |
|----|----------|
| `LlvmCallOp(result, "@GC_malloc", [sizeVal])` | Allocate 8-byte ref cell |
| `ArithConstantOp(sizeVal, 8L)` | Constant 8 for GC_malloc size arg |
| `LlvmStoreOp(value, ptr)` | Store initial value / Assign new value |
| `LlvmLoadOp(result, ptr)` | Dereference ref cell on Var read |
| `ArithConstantOp(unitVal, 0L)` | Return unit (0) from Assign |

**Installation:** No new packages. Build with existing `dotnet build`.

## Architecture Patterns

### Recommended Implementation Order

```
1. freeVars: add LetMut/Assign cases           (blocks all closure tests if missing)
2. ElabEnv: add MutableVars field              (needed by Var + closure capture)
3. LetMut elaboration                          (core allocation pattern)
4. Var transparent deref                       (needed by all mutable var reads)
5. Assign elaboration                          (mutation, returns unit)
6. LetMutDecl desugaring in extractMainExpr    (module-level mutable decls)
7. Closure capture fix                         (mutable vars captured by pointer)
8. E2E tests: 21-01 through 21-06
```

### Pattern 1: ElabEnv Extension with MutableVars

**What:** Add `MutableVars: Set<string>` to `ElabEnv` to track which bound names are ref cell pointers.

**Why needed:** The existing `Var` case returns `env.Vars[name]` directly. For mutable variables, that value is `Ptr` (the cell pointer); consuming code expects `I64`. A naive check `v.Type = Ptr` would also fire for closure pointers, record pointers, and list pointers — all of which must NOT be auto-dereferenced. `MutableVars` provides an explicit tag.

```fsharp
// Source: ARCHITECTURE.md analysis of Elaboration.fs
type ElabEnv = {
    Vars:           Map<string, MlirValue>
    // ... existing fields ...
    MutableVars:    Set<string>   // NEW: names bound to GC_malloc'd ref cells
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0; (* ... *)
      MutableVars = Set.empty }
```

The `MutableVars` field must be propagated wherever `ElabEnv` is constructed/copied in the closure inner env setup (line ~359 in Elaboration.fs).

### Pattern 2: LetMut Elaboration

**What:** Allocate GC heap ref cell, store initial value, bind name as `Ptr` in both `env.Vars` and `env.MutableVars`.

```fsharp
// Source: ARCHITECTURE.md + direct code analysis
| LetMut (name, initExpr, bodyExpr, _) ->
    let (initVal, initOps) = elaborateExpr env initExpr
    let sizeVal  = { Name = freshName env; Type = I64 }
    let cellPtr  = { Name = freshName env; Type = Ptr }
    let allocOps = [
        ArithConstantOp(sizeVal, 8L)
        LlvmCallOp(cellPtr, "@GC_malloc", [sizeVal])
        LlvmStoreOp(initVal, cellPtr)
    ]
    let env' = { env with
                    Vars        = Map.add name cellPtr env.Vars
                    MutableVars = Set.add name env.MutableVars }
    let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
    (bodyVal, initOps @ allocOps @ bodyOps)
```

### Pattern 3: Var Transparent Deref

**What:** When reading a mutable variable, emit `LlvmLoadOp` through the cell pointer.

```fsharp
// Source: ARCHITECTURE.md + Elaboration.fs line 332-335
| Var (name, _) ->
    match Map.tryFind name env.Vars with
    | Some v ->
        if Set.contains name env.MutableVars then
            // Mutable variable — dereference ref cell
            let loaded = { Name = freshName env; Type = I64 }
            (loaded, [LlvmLoadOp(loaded, v)])
        else
            (v, [])
    | None -> failwithf "Elaboration: unbound variable '%s'" name
```

### Pattern 4: Assign Elaboration

**What:** Store new value into ref cell. Return unit (0). Do NOT update `env.Vars`.

```fsharp
// Source: ARCHITECTURE.md + PITFALLS.md Pitfall M-19
| Assign (name, valExpr, _) ->
    let (newVal, valOps) = elaborateExpr env valExpr
    let cellPtr =
        match Map.tryFind name env.Vars with
        | Some v -> v
        | None -> failwithf "Elaboration: unbound mutable variable '%s' in Assign" name
    let unitVal = { Name = freshName env; Type = I64 }
    // INVARIANT: Do NOT update env.Vars here — cell pointer is fixed.
    (unitVal, valOps @ [LlvmStoreOp(newVal, cellPtr); ArithConstantOp(unitVal, 0L)])
```

### Pattern 5: LetMutDecl Desugaring in extractMainExpr

**What:** Module-level `let mut x = e` is desugared into a `LetMut` expression during `extractMainExpr`.

```fsharp
// Source: ARCHITECTURE.md + Elaboration.fs extractMainExpr pattern (line 2049-2082)
// In the `build` function inside `extractMainExpr`:
| Ast.Decl.LetMutDecl(name, body, _) :: rest ->
    LetMut(name, body, build rest, s)
```

Also add `LetMutDecl` to the filter in the `exprDecls` list:
```fsharp
decls |> List.filter (fun d ->
    match d with
    | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _
    | Ast.Decl.LetMutDecl _ -> true    // ADD THIS
    | _ -> false)
```

### Pattern 6: freeVars Extension (Must Be First)

**What:** Add explicit `freeVars` cases for `LetMut` and `Assign` before any elaboration work.

```fsharp
// Source: PITFALLS.md Pitfall C-18 + ARCHITECTURE.md freeVars section
// Current catch-all at line 151: | _ -> Set.empty

// Add BEFORE the catch-all:
| LetMut (name, initExpr, bodyExpr, _) ->
    Set.union (freeVars boundVars initExpr)
              (freeVars (Set.add name boundVars) bodyExpr)
| Assign (name, valExpr, _) ->
    // name is a mutation target — it is free if not locally bound
    let nameFree =
        if Set.contains name boundVars then Set.empty
        else Set.singleton name
    Set.union nameFree (freeVars boundVars valExpr)
```

### Pattern 7: Closure Capture of Mutable Ref Cell Pointer

**What:** When a closure captures a mutable variable, it must capture the **cell pointer** (not load the value). Inside the closure body, reads of the captured mutable name emit `LlvmLoadOp` through the captured pointer.

The existing closure capture code is in the `Let(name, Lambda(outerParam, Lambda(...)), ...)` case at line ~338. The capture store section (line ~428-442) looks up `captureVal = env.Vars[capName]` and stores it. For mutable variables, `env.Vars[capName]` IS the cell pointer (`Ptr`), so the store is correct as-is.

The capture load section (line ~368-381) inside the `innerEnv` setup hardcodes `Type = I64` for captured values:
```fsharp
let capVal = { Name = sprintf "%%t%d" (i + numCaptures); Type = I64 }
```

For mutable captures, the loaded value from the closure env is the cell pointer (`Ptr`), not an `I64`. So this type must be conditional:

```fsharp
// Source: direct analysis of Elaboration.fs lines 370-381
let capType =
    if Set.contains capName env.MutableVars then Ptr else I64
let capVal = { Name = sprintf "%%t%d" (i + numCaptures); Type = capType }
```

AND the `innerEnv` must propagate `MutableVars` so that `Var(name)` inside the closure body knows to emit `LlvmLoadOp`:
```fsharp
let innerEnv : ElabEnv =
    { Vars = Map.ofList [(innerParam, arg1Val)]
      (* ... *)
      MutableVars = env.MutableVars }  // propagate mutable tracking
```

When `capName` is in `MutableVars`, the captured value in `innerEnvWithCaptures.Vars` will be `Ptr`-typed, and `MutableVars` contains the name, so the `Var` case will correctly emit `LlvmLoadOp` through it.

### Anti-Patterns to Avoid

- **Updating `env.Vars` in `Assign`:** This replaces the stable cell pointer with a new SSA value — subsequent Assigns have no effect. The cell pointer must never change. (Pitfall M-19)
- **Auto-deref all `Ptr`-typed vars in `Var`:** This would break closure pointers, record pointers, and list pointers. Only deref when `name ∈ env.MutableVars`. (ARCHITECTURE.md caveat)
- **Using `alloca` for the ref cell:** An `alloca`'d cell would become a dangling stack pointer when a closure outlives the allocating function. Always use `GC_malloc(8)`. (ARCHITECTURE.md Decision: GC_malloc)
- **Implementing `freeVars` changes after elaboration code:** The catch-all drops free vars inside `LetMut`/`Assign`, causing KeyNotFoundException at closure capture time. Fix `freeVars` first. (Pitfall C-18)

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Ref cell allocation | Custom alloca or stack array | `GC_malloc(8)` | Closures can outlive the allocating stack frame; heap allocation is required for safe capture |
| Tracking mutable names | Type-dispatch on `v.Type = Ptr` in Var | `env.MutableVars: Set<string>` | All heap values are `Ptr`; type alone cannot distinguish ref cells from closures/records/lists |
| Module-level LetMutDecl | New elaboration entry point | `extractMainExpr` extension | Existing desugaring pattern already handles the `Decl → Expr` transform; reuse it |

**Key insight:** The entire phase is an extension of existing elaboration patterns. No new MlirOp cases, no new Printer cases, no new runtime C functions. All five new behaviors (LetMut, Assign, Var-deref, LetMutDecl, closure capture) compose from existing MLIR ops: `LlvmCallOp(@GC_malloc)`, `LlvmStoreOp`, `LlvmLoadOp`, `ArithConstantOp`.

## Common Pitfalls

### Pitfall 1: freeVars Catch-All Silently Drops Free Vars in LetMut/Assign (C-18)

**What goes wrong:** The existing `| _ -> Set.empty` at line 151 of `Elaboration.fs` discards free variables in any AST node without an explicit case. `LetMut` and `Assign` have no explicit case. A closure containing `Assign(outerVar, ...)` will not include `outerVar` in its capture list. At codegen time, `Map.tryFind outerVar innerEnv.Vars = None` → `failwithf "unbound variable"` or reads uninitialized memory.

**Why it happens:** FunLang added `LetMut`/`Assign` AST nodes without the compiler being updated.

**How to avoid:** Add explicit `freeVars` cases for `LetMut` and `Assign` as the very first change in this phase. See Pattern 6 above.

**Warning signs:** `System.Collections.Generic.KeyNotFoundException` at a `Var` node; closures produce compile-time `unbound variable` errors for names that are in scope; test 21-02 (closure capture) fails immediately.

### Pitfall 2: Closure Captures Mutable Variable Value Instead of Ref Cell Pointer (C-17)

**What goes wrong:** The capture load code inside the `innerEnv` setup hardcodes `Type = I64` for all captured values. For a mutable variable, the captured value is the cell `Ptr`. If loaded as `I64`, the closure body reads the wrong type and dereferences a number as an address, producing garbage or a crash.

**Why it happens:** Existing closure code was written before mutable variables existed. All captures were assumed to be immutable `I64` scalars.

**How to avoid:** Make capture type conditional on `env.MutableVars` membership. Propagate `MutableVars` into `innerEnv`. See Pattern 7 above.

**Warning signs:** Closure over mutable variable returns the value at creation time, not the current value; `let mut counter = 0; let inc = fun _ -> counter <- counter + 1; counter in ...` always returns 0.

### Pitfall 3: Assign Updates env.Vars Instead of Storing to Cell (M-19)

**What goes wrong:** Writing `env.Vars <- Map.add name newVal env.Vars` in the `Assign` case replaces the stable cell pointer with the new SSA value. Subsequent `Assign`s can no longer find the cell pointer; subsequent `Var` reads return an SSA snapshot (immutable).

**Why it happens:** The interpreter pattern is "update the binding in the environment." In SSA codegen, mutation goes through memory — the binding (cell pointer) never changes.

**How to avoid:** The `Assign` case must ONLY emit `LlvmStoreOp(newVal, cellPtr)`. Never touch `env.Vars`.

**Warning signs:** `x <- 2; x <- 3; x` returns 2 instead of 3; the second Assign has no observable effect.

### Pitfall 4: LetMutDecl Not Filtered Into exprDecls

**What goes wrong:** The `exprDecls` filter in `extractMainExpr` only passes `LetDecl` and `LetRecDecl`. If `LetMutDecl` is not added to this filter, module-level `let mut x = e` declarations are silently dropped. The program compiles without error but the variable is undefined.

**Why it happens:** New Decl variants require updating both the filter and the `build` function.

**How to avoid:** Update the filter to include `Ast.Decl.LetMutDecl _` alongside `LetDecl` and `LetRecDecl`. Then add the `LetMutDecl` case to `build`. See Pattern 5 above.

**Warning signs:** Module-level mutable variable produces `unbound variable` at the use site; `let mut x = 5` at top level followed by `let _ = ...` that uses `x` fails at elaboration.

### Pitfall 5: innerEnv Does Not Propagate MutableVars

**What goes wrong:** When the closure inner env is constructed (line ~358), `MutableVars` starts empty. If a closure body references an outer mutable variable via its captured cell pointer, but `MutableVars` is empty in the inner env, the `Var` case returns the `Ptr`-typed cell pointer as-is instead of emitting `LlvmLoadOp`. The pointer value is then used as an `I64` operand in arithmetic — wrong type.

**How to avoid:** Propagate `MutableVars = env.MutableVars` when constructing `innerEnv`. See Pattern 7 above.

**Warning signs:** MLIR verifier error about type mismatch (`!llvm.ptr` vs `i64`) in closure body; closure reads return a pointer-as-integer.

## Code Examples

### Full LetMut Codegen Example

For `let mut x = 5 in (x <- 10; x)`:

```mlir
; --- generated MLIR (simplified) ---
; LetMut("x", Number 5, Seq(Assign("x", 10), Var "x"))

; 1. Evaluate init
%t0 = arith.constant 5 : i64

; 2. Allocate ref cell
%t1 = arith.constant 8 : i64
%t2 = llvm.call @GC_malloc(%t1) : (i64) -> !llvm.ptr

; 3. Store init
llvm.store %t0, %t2 : i64, !llvm.ptr

; 4. Assign: store 10 to cell
%t3 = arith.constant 10 : i64
llvm.store %t3, %t2 : i64, !llvm.ptr
%t4 = arith.constant 0 : i64    ; unit result of Assign

; 5. Var("x"): load from cell
%t5 = llvm.load %t2 : !llvm.ptr -> i64

; program returns %t5 = 10
```

### Closure Capture of Mutable Variable

For `let mut c = 0 in let f = fun _ -> c <- c + 1; c in (f 0; f 0; f 0)`:

The closure `f` captures `c`'s cell pointer. In the closure env struct slot, the value stored is the `Ptr` (address of the ref cell), not 0. Each call to `f` loads `c` from the cell, increments, stores back, and returns the new value: 1, 2, 3.

```
closure_env = { fn_ptr, cell_ptr_for_c }
                         ^--- This is Ptr(%t2), the GC_malloc'd ref cell
```

Inside the closure inner function:
```mlir
; Capture load: GEP(env, 1) → load Ptr (not I64)
%t0 = llvm.getelementptr %arg0[1] : !llvm.ptr
%t1 = llvm.load %t0 : !llvm.ptr -> !llvm.ptr  ; Type is Ptr, not I64

; Var("c") → LlvmLoadOp through captured Ptr
%t2 = llvm.load %t1 : !llvm.ptr -> i64
```

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|-----------------|--------|
| No mutable variables (v4.0) | GC heap ref cell via `GC_malloc(8)` | Enables FunLang imperative patterns |
| All `env.Vars` bindings are `I64` | `env.Vars` can hold `Ptr` (cell pointer) for mutable names; `MutableVars` set distinguishes them | Var case must branch on `MutableVars` membership |
| `freeVars` only handles known AST nodes | Add `LetMut`/`Assign` cases before codegen | Prevents silent capture omission |
| Closure captures always load `I64` from env slot | For mutable captures: env slot holds `Ptr`, capture type is conditional | Closure mutations become visible |

**No deprecated patterns.** This phase extends the existing architecture without replacing anything.

## Open Questions

1. **Semicolon sequencing in FunLang**
   - What we know: `x <- 10; x` is parsed as a sequencing expression. In FunLang, this likely desugars to `Let("_", Assign(...), Var("x"))` or is a distinct `Seq` node.
   - What's unclear: The exact AST shape of `x <- 10; x` after parsing — whether it becomes `Let("_", Assign, Var)` or a `Sequence` node.
   - Recommendation: Check by printing the AST for a simple mutable test before writing elaboration code. If it's `Let("_", Assign, body)`, the existing `Let` case handles sequencing automatically and `Assign` just needs to return a dummy unit value. If it's a `Sequence` node, a new elaboration case is needed.

2. **Assign return type interaction with Let**
   - What we know: `Assign` returns unit (0 as `I64`). `Let("_", Assign(...), body)` binds `_` to 0 and continues.
   - What's unclear: Whether the existing `Let("_", ...)` case handles the unit-typed binding correctly when the bind result is used in a subsequent expression.
   - Recommendation: The existing `LetPat(WildcardPat, ...)` case (line 492) already discards the bind value. If `x <- 10; x` parses as `LetPat(WildcardPat, Assign, Var)` this works immediately.

## Sources

### Primary (HIGH confidence)
- Direct code analysis of `src/FunLangCompiler.Compiler/Elaboration.fs` — all existing patterns confirmed by reading the file
- Direct code analysis of `deps/FunLang/src/FunLang/Ast.fs` — `LetMut`, `Assign`, `LetMutDecl` node shapes confirmed
- `.planning/research/ARCHITECTURE.md` — Feature 1 section on mutable variables (HIGH confidence, based on same code analysis)
- `.planning/research/PITFALLS.md` — Pitfalls C-17, C-18, M-19 (HIGH confidence)

### Secondary (MEDIUM confidence)
- `MlirIR.fs` confirmed: existing `LlvmCallOp`, `LlvmStoreOp`, `LlvmLoadOp`, `ArithConstantOp` are sufficient — no new DU cases needed

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — confirmed by reading actual source files; no new dependencies
- Architecture: HIGH — existing patterns (GC_malloc, LlvmStoreOp, LlvmLoadOp) are proven by v4.0; new patterns are straightforward extensions
- Pitfalls: HIGH — C-17, C-18, M-19 derived from direct code analysis; exact line numbers confirmed

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (stable domain — only changes if FunLang AST or Elaboration architecture changes)
