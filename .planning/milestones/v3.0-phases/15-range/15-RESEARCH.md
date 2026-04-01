# Phase 15: Range - Research

**Researched:** 2026-03-26
**Domain:** Compiler backend — range syntax desugaring to cons lists via C runtime helper
**Confidence:** HIGH

## Summary

Phase 15 adds support for the range syntax `[start..stop]` (RNG-01) and `[start..step..stop]`
(RNG-02). The LangThree AST already has a `Range` node (added in Phase 18 of LangThree's own
numbering). The backend simply needs to handle that node in Elaboration.fs.

The standard approach is **Option A**: emit a single C runtime call `lang_range(start, stop, step)`
that returns a `ptr` to a cons list. This mirrors how other runtime helpers work in this codebase
(`lang_string_concat`, `lang_to_string_int`, etc.) and avoids any MLIR-level looping constructs.

The runtime function builds a standard singly-linked cons list of 16-byte cells (head: i64 at
offset 0, tail: ptr at offset 8), identical to the layout produced by the `Cons` elaboration in
Phase 10. This means all existing list-processing code (`length`, `sum`, pattern matching) works
on range-produced lists without modification.

**Primary recommendation:** Add `lang_range(i64 start, i64 stop, i64 step) -> ptr` to
`lang_runtime.c`, declare it in the `externalFuncs` list in `Elaboration.fs`, and handle the
`Range` AST node with three elaborated operands (defaulting step to constant `1` when `None`).

---

## Standard Stack

### Core
| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| Boehm GC (`GC_malloc`) | system (via -lgc) | Allocate cons cells in runtime helper | All heap allocation in this compiler uses GC_malloc |
| `lang_runtime.c` | project-local | Home for all C runtime helpers | Precedent: string, to_string, match_failure all live here |
| `MlirIR.LlvmCallOp` | — | Call `@lang_range` from MLIR | Same op used for all external calls in Elaboration.fs |

### Supporting
| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| `ExternalFuncDecl` in `Elaboration.fs` | — | Forward-declare `@lang_range` | Needed so mlir-opt knows the signature |
| Constant `1L` `ArithConstantOp` | — | Emit default step=1 when `Range.step = None` | Same pattern as other integer constants in elaboration |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| C runtime `lang_range` | Desugar to LetRec in Elaboration | LetRec approach works but requires emitting a recursive MLIR function just for ranges; more MLIR code, harder to debug |
| C runtime `lang_range` | Inline MLIR loop | MLIR `scf.for` not in scope; `cf` branch loops require hand-writing back-edges — significant complexity |

---

## Architecture Patterns

### AST Node (LangThree)
```fsharp
// Ast.fs in LangThree — already present
| Range of start: Expr * stop: Expr * step: Expr option * Span
```

### Cons Cell Memory Layout (Phase 10, already established)
```
offset 0 : i64   head value
offset 8 : ptr   tail pointer (null = empty list)
```
Cons cells are 16 bytes, allocated with `GC_malloc(16)`.

### Pattern: New Runtime Function
Every new C helper follows this exact sequence:

1. Add C function to `lang_runtime.c`
2. Add `ExternalFuncDecl` to the `externalFuncs` list in `Elaboration.fs` (bottom of file, around line 1038)
3. Handle the AST node in `elaborateExpr` with `LlvmCallOp(result, "@lang_range", [startVal; stopVal; stepVal])`

**Precedent — lang_string_concat:**
```fsharp
// Elaboration.fs ~line 596
| StringConcat(aExpr, bExpr, _) ->
    let (aVal, aOps) = elaborateExpr env aExpr
    let (bVal, bOps) = elaborateExpr env bExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, aOps @ bOps @ [LlvmCallOp(result, "@lang_string_concat", [aVal; bVal])])
```

**Range elaboration will mirror this:**
```fsharp
| Range(startExpr, stopExpr, stepOpt, _) ->
    let (startVal, startOps) = elaborateExpr env startExpr
    let (stopVal,  stopOps)  = elaborateExpr env stopExpr
    let (stepVal,  stepOps)  =
        match stepOpt with
        | Some stepExpr -> elaborateExpr env stepExpr
        | None ->
            let v = { Name = freshName env; Type = I64 }
            (v, [ArithConstantOp(v, 1L)])
    let result = { Name = freshName env; Type = Ptr }
    (result, startOps @ stopOps @ stepOps @ [LlvmCallOp(result, "@lang_range", [startVal; stopVal; stepVal])])
```

### C Runtime Function
```c
/* Cons cell: {i64 head, ptr tail} — same as Phase 10 Cons */
typedef struct ConsCell {
    int64_t       head;
    struct ConsCell* tail;
} ConsCell;

ConsCell* lang_range(int64_t start, int64_t stop, int64_t step) {
    if (step == 0) { fprintf(stderr, "Fatal: range step cannot be zero\n"); exit(1); }
    ConsCell* head = NULL;
    ConsCell** cursor = &head;
    if (step > 0) {
        for (int64_t i = start; i <= stop; i += step) {
            ConsCell* cell = (ConsCell*)GC_malloc(sizeof(ConsCell));
            cell->head = i; cell->tail = NULL;
            *cursor = cell; cursor = &cell->tail;
        }
    } else {
        for (int64_t i = start; i >= stop; i += step) {
            ConsCell* cell = (ConsCell*)GC_malloc(sizeof(ConsCell));
            cell->head = i; cell->tail = NULL;
            *cursor = cell; cursor = &cell->tail;
        }
    }
    return head;
}
```

### Anti-Patterns to Avoid
- **Using `Ptr` for step when it should be `I64`:** The step is always an integer; declare `@lang_range` as `(i64, i64, i64) -> ptr`.
- **Forgetting to add `ExternalFuncDecl`:** mlir-opt will reject an undeclared external function reference; this is a common pitfall when adding new runtime calls.
- **Using `<=` vs `<` for stop boundary:** LangThree's evaluator uses `[start..step..stop]` which is inclusive on stop (F# semantics). Confirm: `[1..5]` should yield `[1;2;3;4;5]`.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Loop / iteration | MLIR `scf.for` or `cf` back-edges | C runtime function | `scf` is not in the lowering pipeline; `cf` back-edges require complex MLIR block structure |
| Cons cell allocation | Custom allocator | `GC_malloc(16)` | Consistent with Phase 10; GC tracks all allocations |

**Key insight:** The cons list representation is already defined by Phase 10. Reusing it from C is
trivial and keeps the MLIR output simple (one `llvm.call`).

---

## Common Pitfalls

### Pitfall 1: ExternalFuncDecl missing
**What goes wrong:** mlir-opt emits "use of undefined value" or "undeclared function" error.
**Why it happens:** Forgetting to add `{ ExtName = "@lang_range"; ExtParams = [I64; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false }` to the `externalFuncs` list.
**How to avoid:** Add the declaration immediately after adding the C function.
**Warning signs:** mlir-opt failure mentioning `@lang_range`.

### Pitfall 2: Inclusive vs exclusive stop
**What goes wrong:** `[1..5]` produces `[1;2;3;4]` (length 4) instead of `[1;2;3;4;5]` (length 5).
**Why it happens:** Using `i < stop` instead of `i <= stop` in the C loop.
**How to avoid:** Check LangThree Eval.fs — it uses `[start .. step .. stop]` which is F# inclusive.
**Warning signs:** `sum [1..5]` returns 10 instead of 15.

### Pitfall 3: Negative step direction
**What goes wrong:** `[5..1]` with implicit step=1 produces empty list (correct) but `[5..-1..1]` hangs or produces wrong result.
**Why it happens:** Using `i <= stop` unconditionally when step is negative.
**How to avoid:** The C function branches on `step > 0` vs `step < 0`.
**Warning signs:** Negative-step range test hangs.

---

## Code Examples

### ExternalFuncDecl addition (Elaboration.fs ~line 1046)
```fsharp
{ ExtName = "@lang_range"; ExtParams = [I64; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false }
```

### Test file pattern (matches existing .flt format)
```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let rec sum lst = match lst with | [] -> 0 | h :: t -> h + sum t in sum [1..5]
// --- Output:
15
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| N/A — Range not in backend | C runtime helper | Phase 15 | First Range support in backend |

---

## Open Questions

1. **Negative step with default:** `[5..1]` — should this produce empty list or error?
   - What we know: LangThree eval uses F# `[start..step..stop]` with step=1, so `[5..1]` = empty.
   - What's unclear: Whether the backend should match this or fail.
   - Recommendation: Match the interpreter — step=1, `5 <= 1` is false immediately, return NULL (empty list). Covered by the `i <= stop` guard.

2. **Type safety for non-integer bounds:** The type checker should reject `[1.0 .. 5.0]` but the elaborator need not handle it.
   - Recommendation: Add `failwithf` guard if either bound is not I64 (defensive, matches Eval.fs pattern).

---

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Ast.fs` — Range node definition confirmed
- `/Users/ohama/vibe-coding/LangThree/src/LangThree/Eval.fs` — Range evaluation semantics confirmed
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — Cons/List elaboration, externalFuncs list, LlvmCallOp pattern
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` — Runtime helper structure confirmed

### Secondary (MEDIUM confidence)
- Existing test files (`10-02-list-length.flt`, `11-05-list-sum.flt`) — test format and list-processing idioms

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — directly observed from existing codebase patterns
- Architecture: HIGH — directly mirrors Phase 10 Cons + runtime helper pattern
- Pitfalls: HIGH — derived from code analysis and F# inclusive-range semantics

**Research date:** 2026-03-26
**Valid until:** Stable (no external dependencies change)
