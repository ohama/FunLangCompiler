# Phase 42: If-Match Nested Empty Block Fix - Research

**Researched:** 2026-03-30
**Domain:** F# compiler backend — MLIR block generation for nested if/match
**Confidence:** HIGH (bug root-caused with MLIR dump)

## Summary

`if...then...else match...` generates an empty match merge block because the If handler puts the continuation branch (`cf.br ^if_merge`) in the else block instead of the match's merge block. The else block ends up with TWO branches (one to match dispatch, one unreachable to if merge), while the match merge block stays empty.

## Root Cause

**File:** `Elaboration.fs` lines 872-920 (If handler)

The If handler creates branches as:
```
^else1:
  <elseOps — match dispatch, ends with terminator cf.br ^match_body>
  cf.br ^if_merge(%matchResult)   ← UNREACHABLE, %matchResult is out of scope
^match_merge(%matchResult):
  ← EMPTY! Should have: cf.br ^if_merge(%matchResult)
```

**Why:** Lines 898-899 unconditionally put `elseOps @ [CfBrOp(mergeLabel, [elseVal])]` into the else block. When `elseExpr` is a Match, `elseOps` already ends with a terminator (dispatch to first match arm). The `CfBrOp` to if_merge is appended after the terminator (unreachable), and the match's own merge block (where `elseVal` lives as a block arg) stays empty.

**Same issue affects:** then branch when `thenExpr` is a Match.

## Fix Pattern

Apply the same pattern as FIX-02 (Let handler, lines 705-713): detect when branch ops end with a terminator and side blocks were created, then patch the continuation into the LAST side block (the match's merge block) instead of appending inline.

```fsharp
// For then branch:
let blocksBefore = env.Blocks.Value.Length
let (thenVal, thenOps) = elaborateExpr env thenExpr
let blocksAfterThen = env.Blocks.Value.Length

// When building then block:
if thenOps ends with terminator AND blocksAfterThen > blocksBefore then
    // thenExpr created side blocks (match/if inside)
    // Put thenOps in then block (dispatch ops)
    // Patch CfBrOp(mergeLabel) into the LAST side block created by thenExpr
else
    // Normal: thenOps @ [CfBrOp(mergeLabel)] all in then block
```

Same for else branch (track blocks before/after elseExpr elaboration).

## MLIR Dump Evidence

From `/tmp/debug_langbackend.mlir` for `let f xs = if xs = 0 then 10 else match xs with | _ -> 20`:

```mlir
llvm.func @closure_fn_0(%arg0: !llvm.ptr, %arg1: i64) -> i64 {
    %t0 = arith.constant 0 : i64
    %t1 = arith.cmpi eq, %arg1, %t0 : i64
    cf.cond_br %t1, ^then0, ^else1
  ^match_body05:
    %t3 = arith.constant 20 : i64
    ...
    cf.br ^match_merge3(%t7 : i64)
  ^match_fail4:
    llvm.unreachable
  ^match_merge3(%t8: i64):
                              ← EMPTY! needs: cf.br ^merge2(%t8 : i64)
  ^then0:
    %t2 = arith.constant 10 : i64
    cf.br ^merge2(%t2 : i64)
  ^else1:
    cf.br ^match_body05       ← match dispatch (terminator)
    cf.br ^merge2(%t8 : i64)  ← unreachable, %t8 out of scope
  ^merge2(%t9: i64):
    llvm.return %t9 : i64
}
```

## Confirmed Working/Failing Cases

| Pattern | Result |
|---------|--------|
| `if ... then X else Y` (no match) | OK |
| `match xs with ...` (no if) | OK |
| `if ... then X else match ...` | FAIL |
| `if ... then match ... else X` | FAIL |
| `if ... then 10 else match xs with \| _ -> 20` | FAIL |

## Metadata

**Confidence:** HIGH — exact MLIR dump shows the empty block and misplaced branches
**Research date:** 2026-03-30
