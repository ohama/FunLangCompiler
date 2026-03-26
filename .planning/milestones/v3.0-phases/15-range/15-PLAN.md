# Phase 15: Range — Plan

**Planned:** 2026-03-26
**Requirements:** RNG-01 (`[start..stop]`), RNG-02 (`[start..step..stop]`)
**Approach:** C runtime helper `lang_range(start, stop, step) -> ptr`

## Success Criteria

1. `sum [1..5]` compiles and exits with code 15
2. `[1..2..10]` generates `[1; 3; 5; 7; 9]` — length 5

---

## Tasks

### Task 1 — Add `lang_range` to C runtime

**File:** `src/LangBackend.Compiler/lang_runtime.c`

Add after `lang_match_failure`:

```c
/* Cons cell layout: {int64_t head @ offset 0, ConsCell* tail @ offset 8} — 16 bytes total */
/* Matches Phase 10 GC_malloc(16) cons cell layout exactly. */
typedef struct LangCons {
    int64_t         head;
    struct LangCons* tail;
} LangCons;

/* lang_range: build inclusive cons list [start..step..stop].
   step must be non-zero. Returns NULL (empty list) when range is immediately empty. */
LangCons* lang_range(int64_t start, int64_t stop, int64_t step) {
    if (step == 0) {
        fprintf(stderr, "Fatal: range step cannot be zero\n");
        exit(1);
    }
    LangCons* head = NULL;
    LangCons** cursor = &head;
    if (step > 0) {
        for (int64_t i = start; i <= stop; i += step) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->head = i;
            cell->tail = NULL;
            *cursor = cell;
            cursor = &cell->tail;
        }
    } else {
        for (int64_t i = start; i >= stop; i += step) {
            LangCons* cell = (LangCons*)GC_malloc(sizeof(LangCons));
            cell->head = i;
            cell->tail = NULL;
            *cursor = cell;
            cursor = &cell->tail;
        }
    }
    return head;
}
```

**Verification:** `sizeof(LangCons) == 16` on 64-bit (i64 = 8 bytes + ptr = 8 bytes).

---

### Task 2 — Declare `@lang_range` in Elaboration.fs external funcs

**File:** `src/LangBackend.Compiler/Elaboration.fs`

In the `externalFuncs` list (around line 1038–1047), add:

```fsharp
{ ExtName = "@lang_range"; ExtParams = [I64; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false }
```

Full updated list will look like:
```fsharp
let externalFuncs = [
    { ExtName = "@GC_init";              ExtParams = [];             ExtReturn = None;     IsVarArg = false }
    { ExtName = "@GC_malloc";            ExtParams = [I64];          ExtReturn = Some Ptr; IsVarArg = false }
    { ExtName = "@printf";               ExtParams = [Ptr];          ExtReturn = Some I32; IsVarArg = true  }
    { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr];     ExtReturn = Some I32; IsVarArg = false }
    { ExtName = "@lang_string_concat";   ExtParams = [Ptr; Ptr];     ExtReturn = Some Ptr; IsVarArg = false }
    { ExtName = "@lang_to_string_int";   ExtParams = [I64];          ExtReturn = Some Ptr; IsVarArg = false }
    { ExtName = "@lang_to_string_bool";  ExtParams = [I64];          ExtReturn = Some Ptr; IsVarArg = false }
    { ExtName = "@lang_match_failure";   ExtParams = [];             ExtReturn = None;     IsVarArg = false }
    { ExtName = "@lang_range";           ExtParams = [I64; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false }
]
```

---

### Task 3 — Elaborate `Range` AST node in Elaboration.fs

**File:** `src/LangBackend.Compiler/Elaboration.fs`

In `elaborateExpr`, add a new case after the `List` case (~line 772). Insert:

```fsharp
// Phase 15: Range [start..stop] or [start..step..stop]
// Compiled to a call to C runtime lang_range(start, stop, step).
// Default step is 1 when not specified.
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

**Key decisions:**
- Return type is `Ptr` (cons list pointer), same as `EmptyList` and `Cons`.
- Default step is emitted as `ArithConstantOp(v, 1L)` — a compile-time constant.
- Ops are ordered: start, stop, step, then the call — all linear, no control flow.

---

### Task 4 — Add test: RNG-01 (`[start..stop]`, sum)

**File:** `tests/compiler/15-01-range-sum.flt`

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let rec sum lst = match lst with | [] -> 0 | h :: t -> h + sum t in sum [1..5]
// --- Output:
15
```

**Validates:** RNG-01, success criterion 1.

---

### Task 5 — Add test: RNG-02 (`[start..step..stop]`, length)

**File:** `tests/compiler/15-02-range-step.flt`

```
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/LangBackend.Cli/LangBackend.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let rec length lst = match lst with | [] -> 0 | _ :: t -> 1 + length t in length [1..2..10]
// --- Output:
5
```

**Validates:** RNG-02, success criterion 2.
(`[1..2..10]` = `[1; 3; 5; 7; 9]`, length = 5)

---

## Execution Order

```
Task 1 → lang_runtime.c (C function)
Task 2 → Elaboration.fs (ExternalFuncDecl)  [independent of Task 1 order]
Task 3 → Elaboration.fs (elaborateExpr case) [depends on Task 2]
Task 4 → tests/compiler/15-01-range-sum.flt  [depends on Tasks 1-3]
Task 5 → tests/compiler/15-02-range-step.flt [depends on Tasks 1-3]
```

Tasks 1 and 2 can be done in either order. Task 3 must follow Task 2. Tests follow Tasks 1-3.

---

## Files Changed

| File | Change |
|------|--------|
| `src/LangBackend.Compiler/lang_runtime.c` | Add `LangCons` typedef + `lang_range` function |
| `src/LangBackend.Compiler/Elaboration.fs` | Add `@lang_range` to `externalFuncs`; add `Range` case in `elaborateExpr` |
| `tests/compiler/15-01-range-sum.flt` | New test: `sum [1..5]` = 15 |
| `tests/compiler/15-02-range-step.flt` | New test: `length [1..2..10]` = 5 |

---

## Risk Assessment

**LOW risk overall.**

- The cons cell layout is already established and tested by Phase 10.
- The C runtime helper pattern is used by 4 existing functions.
- No new MLIR constructs required — just `LlvmCallOp` with `Ptr` return.
- The only subtlety is inclusive stop semantics (F# `..` is inclusive); the C loop uses `<=`/`>=`.
