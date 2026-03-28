# Phase 29: Loop Constructs - Research

**Researched:** 2026-03-27
**Domain:** MLIR CFG codegen — WhileExpr / ForExpr via `cf.br` / `cf.cond_br` block graphs
**Confidence:** HIGH

---

## Summary

Phase 29 implements `while cond do body` and `for i = start to/downto stop do body` loop
constructs, compiling them to native code through the existing MLIR codegen pipeline.

The AST nodes `WhileExpr` and `ForExpr` already exist in `LangThree/Ast.fs` (Phase 46
annotation in that file), and the interpreter handles them in `Eval.fs`. The compiler
(`Elaboration.fs`) has no cases for either node yet — every path hits the final
`| _ -> Set.empty` fallback in `freeVars` and would fail at elaboration time.

The only viable implementation strategy for this compiler is **Option B: CFG blocks using
`cf.br` and `cf.cond_br`**. The compiler already uses this dialect for `if-else`, `and`,
`or`, `match`, and `TryWith`. No new MLIR dialects, no new MlirOp constructors, and no
new MlirIR types are required. The implementation is purely an extension of `elaborateExpr`
in `Elaboration.fs` and the `freeVars` function in the same file.

**Primary recommendation:** Implement `WhileExpr` and `ForExpr` in `elaborateExpr` using
the same `env.Blocks.Value <- ...` side-effecting block accumulation pattern used by `If`,
`And`, `Or`, and `TryWith`. Return a fresh `I64` constant `0L` as the unit result.

---

## Standard Stack

This phase is entirely internal to the LangBackend compiler. There are no new
library dependencies.

### Core
| Component | Location | Purpose | Why This |
|-----------|----------|---------|----------|
| `Elaboration.fs` | `src/LangBackend.Compiler/` | Main elaboration logic | Only file to change for codegen |
| `MlirIR.fs` | `src/LangBackend.Compiler/` | IR data types | No changes needed — `CfBrOp`/`CfCondBrOp`/`ArithConstantOp`/`ArithCmpIOp`/`ArithAddIOp`/`ArithSubIOp` already exist |
| `Printer.fs` | `src/LangBackend.Compiler/` | MLIR text serializer | No changes needed — block args already printed |
| `tests/compiler/` | repo root | E2E `.flt` test fixtures | New `29-*.flt` files needed |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `cf.br`/`cf.cond_br` | `scf.while`/`scf.for` | scf requires adding a new lowering pass and new dialect; cf is already in use |
| `cf.br`/`cf.cond_br` | Tail-recursive `FuncOp` | Requires new FuncOp mid-elaboration; closure env setup is non-trivial |
| `cf.br`/`cf.cond_br` | C runtime helper | Body needs access to enclosing scope; closure call overhead; limited |

---

## Architecture Patterns

### How the Existing Block-Accumulation Pattern Works

`elaborateExpr` returns `(MlirValue * MlirOp list)`. The op list is the "entry-block
fragment" for the current expression. Side blocks (then/else/merge/loop header/body/exit)
are pushed onto `env.Blocks.Value` as a mutable side effect. At the top level,
`elaborateModule` collects `env.Blocks.Value` and assembles them after the entry block.

**Crucially**: the entry-block fragment returned by `elaborateExpr` MUST end with a
terminator (`CfBrOp`, `CfCondBrOp`) when the expression emits side blocks. The merge/join
block is pushed with an empty `Body = []`; the caller (`Let`, `LetPat`) patches it by
prepending continuation ops into that final empty block (see lines 553-558 in
`Elaboration.fs`).

### Pattern 1: WhileExpr CFG Shape

```
entry fragment (returned ops):
  <elaborate cond into condVal>          -- ops for first evaluation of condition
  cf.cond_br %condVal, ^while_body, ^while_exit

side blocks pushed to env.Blocks.Value:
  ^while_body:
    <elaborate body, discard value>
    <re-elaborate cond into condVal2>    -- condition must be re-evaluated each iteration
    cf.cond_br %condVal2, ^while_body, ^while_exit

  ^while_exit(%unit_result : i64):       -- block arg carries the unit value out
    (empty body — caller patches continuation here)
```

**Result value returned**: the block-arg of `^while_exit`, typed `I64` (unit = 0).

**Critical detail**: the condition expression must be elaborated TWICE: once before the
first branch (in the entry fragment), and once inside `^while_body` before the back-edge
branch. This is because MLIR ops are in SSA form — you cannot reuse `%condVal` across
block boundaries without a block argument. Re-elaborating is the cleanest approach; block
arguments carrying a fresh condition value would complicate the body block.

Alternatively, the standard academic CFG shape uses a dedicated header block:

```
entry fragment:
  cf.br ^while_header

^while_header:
  <elaborate cond>
  cf.cond_br %cond, ^while_body, ^while_exit

^while_body:
  <elaborate body>
  cf.br ^while_header            -- back-edge

^while_exit(%unit : i64):
  (empty — patched by continuation)
```

This header-block pattern is cleaner (single condition elaboration) but requires the
condition to be re-entered via a `cf.br` back-edge. The `^while_header` block needs no
args; all values it uses from the outer scope are available as SSA values defined before
the loop (the MLIR dominator rules allow this because `^while_header` is dominated by the
entry block).

**Recommended**: use the header-block pattern (3 side blocks + entry `cf.br`) because:
1. No double elaboration of `cond`.
2. No SSA violations (values from outer scope are visible in dominated blocks).
3. Matches the standard textbook CFG for while loops.
4. Consistent with how `TryWith` uses an explicit entry `cf.br ^try_body`.

### Pattern 2: ForExpr CFG Shape (ascending `to`)

```
entry fragment:
  <elaborate start into %start : i64>
  <elaborate stop into %stop : i64>
  cf.br ^for_header(%start : i64)

^for_header(%i : i64):
  %cmp = arith.cmpi "sle", %i, %stop : i64    -- sle for ascending
  cf.cond_br %cmp, ^for_body, ^for_exit

^for_body:
  <elaborate body with env[varName] = %i>      -- bind %i as immutable (not in MutableVars)
  %one = arith.constant 1 : i64
  %next = arith.addi %i, %one : i64
  cf.br ^for_header(%next : i64)

^for_exit(%unit : i64):
  (empty — patched by continuation)
```

For `downto`, change `"sle"` to `"sge"` and `arith.addi` to `arith.subi`.

**Block argument for loop variable**: `^for_header` takes `%i : i64` as a block argument.
This is the SSA-correct way to thread a mutable loop counter through the back-edge. The
`Printer.fs` already supports block args (see `printBlock` which formats `Args` as
`(%name: type, ...)`). The `CfBrOp` already accepts `args: MlirValue list`, and
`CfCondBrOp` also accepts `trueArgs`/`falseArgs` — so no new ops or printer changes are
needed.

**LOOP-04 (immutable for-variable)**: The for-variable `%i` is a block argument of
`^for_header`. It is bound in `env.Vars` but NOT added to `env.MutableVars`. This
makes it immutable for elaboration purposes (reading it does a direct SSA use, no load).
This matches the semantics: the user cannot assign to `i` inside the body.

### Pattern 3: freeVars Extension (LOOP-05)

`freeVars` in `Elaboration.fs` has a final `| _ -> Set.empty` fallback. Currently neither
`WhileExpr` nor `ForExpr` matches any prior case. Add explicit cases:

```fsharp
| WhileExpr (cond, body, _) ->
    Set.union (freeVars boundVars cond) (freeVars boundVars body)

| ForExpr (var, startExpr, _, stopExpr, body, _) ->
    Set.unionMany [
        freeVars boundVars startExpr
        freeVars boundVars stopExpr
        freeVars (Set.add var boundVars) body   // var is bound inside body
    ]
```

The `_ -> Set.empty` fallback is conservative (treats the expr as having no free vars),
which causes incorrect closure capture when a `while`/`for` body closes over outer vars.
The explicit cases ensure closures that contain loops are compiled correctly.

### Anti-Patterns to Avoid

- **Double condition elaboration in WhileExpr**: Do not elaborate `cond` twice (entry + body).
  Use the header-block pattern with a single condition elaboration site.
- **Adding loop variable to `MutableVars` in ForExpr**: The for-variable is immutable by
  spec (LOOP-04). Do NOT add it to `env.MutableVars`. Bind it directly in `env.Vars`.
- **New MlirOp cases for loops**: All needed ops exist. Adding new DU cases would require
  updating `Printer.fs` and risk breaking existing tests.
- **Forgetting the `^exit` block arg**: WhileExpr and ForExpr both return unit (value 0).
  The exit block needs a block arg to carry this unit value, so the merge pattern used by
  `Let`/`LetPat` (patching rops into the empty last block) continues to work correctly.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Loop counter threading | GC ref cell (GC_malloc + store/load) | Block argument on `^for_header` | Block args are SSA-native; ref cells add GC overhead and require `LetMut` |
| Condition re-evaluation | Multiple SSA names with same semantics | Header block with back-edge | SSA forbids value reuse across blocks |
| Unit return value | New `UnitOp` or new `MlirType` | `ArithConstantOp(unitVal, 0L)` as exit block arg | Consistent with how `Assign` and `If(cond,then,unit)` return unit |

**Key insight**: Block arguments (`cf.br ^label(val : type)`) are the SSA-correct way to
pass values through loop back-edges. The existing IR and printer already support this
fully. No IR changes needed.

---

## Common Pitfalls

### Pitfall 1: SSA Violation — Using Entry-Block Values in Side Blocks

**What goes wrong:** Condition value `%cond` computed in the entry fragment is used in
`^while_body` block, but it is defined in the entry block. In SSA/dominator terms this is
fine IF the entry block dominates `^while_body`. With the header-block shape, the entry
block has a single unconditional branch to `^while_header`, which dominates `^while_body`,
which dominates `^while_exit`. Outer-scope values are visible everywhere.

**Why it happens:** Developers familiar with LLVM textual IR may worry about scoping, but
MLIR's `cf` dialect respects dominance, not lexical scope. Values from dominating blocks
are visible.

**How to avoid:** Use the header-block pattern. The entry block branches to `^while_header`
unconditionally. All outer-scope SSA values defined before the loop are available in all
loop blocks because they are dominated by the entry block.

### Pitfall 2: Last-Block Patching Breaks When WhileExpr Is Used in Sequence

**What goes wrong:** When `while cond do body; expr2` is desugared to
`LetPat(WildcardPat, WhileExpr(...), expr2)`, the `LetPat` handler checks whether `bops`
(the result of elaborating the while) ends with a terminator and patches `rops` (the
continuation) into the last side block. This works correctly IF the while's exit block is
the last entry in `env.Blocks.Value` with an empty `Body = []`. Any ops accidentally
appended to the exit block before the patching step will break the layout.

**Why it happens:** If the exit block gets a `cf.br` terminator appended by the while
elaboration itself, the `LetPat` patching cannot prepend `rops` before the terminator
(since `rops @ [terminator]` would put ops after a terminator, which is invalid MLIR).

**How to avoid:** Leave the `^while_exit` and `^for_exit` blocks with `Body = []` (no
ops, no terminator). The `LetPat`/`Let` patching mechanism will prepend continuation ops
into this empty block. This is exactly the same pattern used by `If` (the `merge` block
is pushed empty) and `TryWith` (the `try_merge` block is pushed empty).

### Pitfall 3: For-Loop Empty Range (start > stop for `to`)

**What goes wrong:** For `for i = 5 to 3 do body`, the condition `%i <= %stop` is false
immediately, so the body never executes. The CFG handles this correctly (the header block
branches to exit immediately). No special case is needed.

**Why it happens:** Developers may worry about needing an `if` guard before the loop. The
`sle`/`sge` comparison in the header handles it automatically.

**How to avoid:** No action needed — the CFG is inherently correct for empty ranges.

### Pitfall 4: Body Returns Non-I64 Value

**What goes wrong:** The loop body may return a `Ptr` typed value (e.g., if the body
contains a string allocation). The loop discards the body's result value, but the ops that
produce it still appear in `^for_body`. This is fine — unused SSA values in MLIR are legal.

**Why it happens:** `elaborateExpr env body` returns `(bodyVal, bodyOps)`. The `bodyVal`
is simply ignored when constructing the body block.

**How to avoid:** Always discard the return value from elaborating the loop body. Do not
attempt to type-check it or unify it with the loop return type (unit/I64).

### Pitfall 5: Missing `freeVars` Cases Breaks Closure Capture

**What goes wrong:** If `WhileExpr`/`ForExpr` hit the `| _ -> Set.empty` fallback in
`freeVars`, closures that capture variables used inside loops will be missing those
variables from their capture set. The compiled code will read uninitialized memory.

**Why it happens:** The fallback exists as a conservative safety net but gets called for
unhandled expression types.

**How to avoid:** Add `WhileExpr` and `ForExpr` cases to `freeVars` before any tests that
combine loops with closures (LOOP-05 requirement).

---

## Code Examples

### WhileExpr Elaboration (header-block pattern)

```fsharp
// Source: Elaboration.fs — analogous to If elaboration at line 672
| WhileExpr (condExpr, bodyExpr, _) ->
    let headerLabel = freshLabel env "while_header"
    let bodyLabel   = freshLabel env "while_body"
    let exitLabel   = freshLabel env "while_exit"
    // Elaborate condition inside header block
    let (condVal, condOps) = elaborateExpr env condExpr
    // Elaborate body inside body block
    let (_bodyVal, bodyOps) = elaborateExpr env bodyExpr
    // unit result returned from exit block
    let unitArg = { Name = freshName env; Type = I64 }
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some headerLabel; Args = [];        Body = condOps @ [CfCondBrOp(condVal, bodyLabel, [], exitLabel, [{ Name = "%" + freshName env; Type = I64 } |> ignore; unitArg])] } ]
    // (see corrected version below — the exit block arg needs a constant source)
    ...
```

**Corrected, complete pattern** (the exit block arg value must come from a constant, not
just declared as an arg with no producer):

```fsharp
| WhileExpr (condExpr, bodyExpr, _) ->
    let headerLabel = freshLabel env "while_header"
    let bodyLabel   = freshLabel env "while_body"
    let exitLabel   = freshLabel env "while_exit"
    let (condVal, condOps)   = elaborateExpr env condExpr
    let (_bodyVal, bodyOps)  = elaborateExpr env bodyExpr
    // Produce unit constant to pass as the exit block argument
    let unitConst = { Name = freshName env; Type = I64 }
    let unitConstOp = ArithConstantOp(unitConst, 0L)
    // The exit block receives unit via block arg
    let exitArg = { Name = freshName env; Type = I64 }
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some headerLabel
            Args  = []
            Body  = condOps @ [ CfCondBrOp(condVal, bodyLabel, [], exitLabel, [unitConst]) ] }
          { Label = Some bodyLabel
            Args  = []
            Body  = bodyOps @ [ unitConstOp; CfCondBrOp(condVal (* stale! *), ... ] }  // WRONG — see note
          { Label = Some exitLabel
            Args  = [exitArg]
            Body  = [] } ]
    // Entry fragment: branch to header
    (exitArg, [ CfBrOp(headerLabel, []) ])
```

**Note on the body block**: The body block needs to re-evaluate the condition. Since
`condVal` is defined in the header block (which dominates the body block), we can reuse
the same `condVal` directly... but wait — the condition expression may have side effects
or depend on mutable variables. If `condExpr` reads a mutable ref cell that the body
modifies, we MUST re-elaborate the condition after the body executes.

**Final correct pattern for WhileExpr with mutable-safe condition re-evaluation**:

```fsharp
| WhileExpr (condExpr, bodyExpr, _) ->
    let headerLabel = freshLabel env "while_header"
    let bodyLabel   = freshLabel env "while_body"
    let exitLabel   = freshLabel env "while_exit"
    // Header: elaborate condition
    let (condVal, condOps)   = elaborateExpr env condExpr
    // Body: elaborate body, then re-elaborate condition for back-edge
    let (_bodyVal, bodyOps)  = elaborateExpr env bodyExpr
    let (condVal2, condOps2) = elaborateExpr env condExpr  // re-evaluate for back-edge
    let unitConst = { Name = freshName env; Type = I64 }
    let exitArg   = { Name = freshName env; Type = I64 }
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some headerLabel
            Args  = []
            Body  = condOps @ [ CfCondBrOp(condVal, bodyLabel, [], exitLabel, [unitConst]) ] }
          { Label = Some bodyLabel
            Args  = []
            Body  = bodyOps @ [ ArithConstantOp(unitConst, 0L) ] @ condOps2
                    @ [ CfCondBrOp(condVal2, bodyLabel, [], exitLabel, [unitConst]) ] }
          { Label = Some exitLabel
            Args  = [exitArg]
            Body  = [] } ]
    (exitArg, [ CfBrOp(headerLabel, []) ])
```

**Simpler alternative**: duplicate condition elaboration is the easiest correct approach.
The `freshName` counter ensures no SSA name collisions between the two elaborations.

### ForExpr Elaboration

```fsharp
| ForExpr (var, startExpr, isTo, stopExpr, bodyExpr, _) ->
    let headerLabel = freshLabel env "for_header"
    let bodyLabel   = freshLabel env "for_body"
    let exitLabel   = freshLabel env "for_exit"
    let (startVal, startOps) = elaborateExpr env startExpr
    let (stopVal,  stopOps)  = elaborateExpr env stopExpr
    // Block arg: loop variable %i
    let iArg    = { Name = freshName env; Type = I64 }
    // Bind loop variable (immutable — not in MutableVars)
    let bodyEnv = { env with Vars = Map.add var iArg env.Vars }
    let (_bodyVal, bodyOps) = elaborateExpr bodyEnv bodyExpr
    // Comparison predicate: sle for `to`, sge for `downto`
    let pred    = if isTo then "sle" else "sge"
    let cmpVal  = { Name = freshName env; Type = I1 }
    let oneConst  = { Name = freshName env; Type = I64 }
    let nextVal   = { Name = freshName env; Type = I64 }
    let unitConst = { Name = freshName env; Type = I64 }
    let exitArg   = { Name = freshName env; Type = I64 }
    let incrOp  = if isTo then ArithAddIOp(nextVal, iArg, oneConst)
                           else ArithSubIOp(nextVal, iArg, oneConst)
    env.Blocks.Value <- env.Blocks.Value @
        [ { Label = Some headerLabel
            Args  = [iArg]
            Body  = [ ArithCmpIOp(cmpVal, pred, iArg, stopVal)
                      CfCondBrOp(cmpVal, bodyLabel, [], exitLabel, [unitConst]) ] }
          { Label = Some bodyLabel
            Args  = []
            Body  = bodyOps
                    @ [ ArithConstantOp(unitConst, 0L)
                        ArithConstantOp(oneConst, 1L)
                        incrOp
                        CfBrOp(headerLabel, [nextVal]) ] }
          { Label = Some exitLabel
            Args  = [exitArg]
            Body  = [] } ]
    // Entry fragment: compute bounds, branch to header with start value
    (exitArg, startOps @ stopOps @ [ CfBrOp(headerLabel, [startVal]) ])
```

**Note on `unitConst` placement**: `unitConst` is used both in the header's `CfCondBrOp`
and in the body. Because MLIR requires SSA values to be defined before use in dominating
blocks, and because `unitConst` is referenced in the header (which is defined before the
body in the block list), we need `ArithConstantOp(unitConst, 0L)` to appear in the header
block or in a block that dominates the header. The simplest fix: define `unitConst` in the
entry fragment (not in the body block), placing `ArithConstantOp(unitConst, 0L)` in the
entry ops alongside the start/stop elaboration.

### freeVars Extension

```fsharp
// Add these cases to the freeVars function in Elaboration.fs
// before the final | _ -> Set.empty fallback
| WhileExpr (cond, body, _) ->
    Set.union (freeVars boundVars cond) (freeVars boundVars body)

| ForExpr (var, startExpr, _, stopExpr, body, _) ->
    Set.unionMany [
        freeVars boundVars startExpr
        freeVars boundVars stopExpr
        freeVars (Set.add var boundVars) body
    ]
```

### Test Fixture Format

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let x = ref 0 in
while !x < 3 do
  x := !x + 1;
!x
// --- Output:
3
```

*(Note: `ref`/`!`/`:=` are available via the mutable variable Phase 21 elaboration. For
tests that don't require mutable vars, use a simple bounded loop or print loop.)*

---

## State of the Art

| Old Approach | Current Approach | When Adopted | Impact |
|--------------|------------------|--------------|--------|
| No loop support | WhileExpr/ForExpr via CFG blocks | Phase 29 | Loops compile natively |
| `scf.while`/`scf.for` (structured) | `cf.br`/`cf.cond_br` (CFG) | Not applicable | No new dialects or passes |

**Deprecated/outdated:**
- `scf` dialect for loops: requires `mlir-opt` lowering pass; not used anywhere in this compiler.

---

## Open Questions

1. **Unit constant placement in ForExpr header**
   - What we know: The `cf.cond_br` in `^for_header` passes `unitConst` to `^for_exit`
     when the condition is false. `unitConst` must be defined in a block that dominates
     `^for_header`. The entry fragment dominates `^for_header`.
   - What's unclear: Whether MLIR's verifier requires the constant to be defined in the
     header block itself or whether a dominating entry-block definition is sufficient.
   - Recommendation: Define `ArithConstantOp(unitConst, 0L)` in the entry fragment ops
     (alongside start/stop ops). This is unambiguously correct per dominance rules.

2. **WhileExpr with mutable cond captured in closure**
   - What we know: If the body of a `while` loop is a closure call that modifies a ref
     cell that `condExpr` reads, the condition must be re-evaluated fresh each iteration.
   - What's unclear: Whether the double-elaboration approach (re-call `elaborateExpr env
     condExpr` in the body block) correctly handles all cases, or whether a shared GC ref
     cell approach (rare) causes SSA naming conflicts.
   - Recommendation: Use duplicate elaboration with the existing `freshName` counter —
     each elaboration call produces fresh SSA names, so no conflicts.

3. **Test coverage for LOOP-04 (for-var immutability)**
   - What we know: The spec says the for-variable is immutable (fresh binding per iteration).
   - What's unclear: Whether any existing tests attempt to assign to the for-variable, which
     should be a type error, not a runtime error.
   - Recommendation: Add a test that reads the for-variable but never writes it. The
     immutability is enforced by NOT adding the var to `MutableVars` — the type system
     already catches write attempts before codegen.

---

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Elaboration.fs` —
  complete `elaborateExpr` function, `freeVars`, `If`/`And`/`Or`/`TryWith` block patterns
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/MlirIR.fs` —
  complete `MlirOp` DU, `MlirBlock` with `Args`, `CfBrOp`/`CfCondBrOp` signatures
- `/Users/ohama/vibe-coding/LangBackend/src/LangBackend.Compiler/Printer.fs` —
  `printBlock` showing block argument formatting already implemented
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` —
  `WhileExpr`/`ForExpr` AST shapes confirmed (lines 116-117)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` —
  interpreter semantics for `WhileExpr`/`ForExpr` (lines 755-776)

### Secondary (MEDIUM confidence)
- MLIR `cf` dialect documentation — `cf.br` and `cf.cond_br` semantics and block-argument
  conventions are well-established; no new research needed given existing use in this codebase.

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new deps; all ops, blocks, printers verified in source
- Architecture: HIGH — `If`/`TryWith` patterns directly applicable; verified line-by-line
- Pitfalls: HIGH — identified from direct code inspection of patching logic in `Let`/`LetPat`

**Research date:** 2026-03-27
**Valid until:** 2026-04-27 (codebase is stable; no external dependencies)
