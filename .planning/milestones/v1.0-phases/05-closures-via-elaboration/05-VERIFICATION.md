---
phase: 05-closures-via-elaboration
verified: 2026-03-26T03:52:17Z
status: passed
score: 4/4 must-haves verified
---

# Phase 5: Closures via Elaboration Verification Report

**Phase Goal:** Lambda expressions that capture free variables are elaborated into MlirIR closure representations (flat struct with {fn_ptr, env_fields}) and emitted with indirect call dispatch, enabling higher-order functions to work correctly
**Verified:** 2026-03-26T03:52:17Z
**Status:** PASSED
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|---------|
| 1 | `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3` compiles and binary exits with 8 | VERIFIED | `dotnet run` on the expression exits with code 8; `fslit tests/compiler/05-01-closure-basic.flt` PASS |
| 2 | Every lambda with captured variables produces a `LlvmAllocaOp` in MlirIR that the Printer serializes to a flat closure struct in the llvm dialect | VERIFIED | MLIR output contains `%t2 = llvm.alloca %t1 x !llvm.struct<(ptr, i64)> : (i64) -> !llvm.ptr`; `LlvmAllocaOp` case in MlirIR.fs line 34, printOp case in Printer.fs lines 52-57 |
| 3 | Known let-rec functions emit `DirectCallOp`, closure values emit `IndirectCallOp` with fn_ptr load from closure struct | VERIFIED | MLIR output has `func.call @add_n` (DirectCallOp for closure-maker) and `llvm.call %t5(%t3, %t4)` (IndirectCallOp for closure application); 3-way App dispatch in Elaboration.fs lines 336-372 |
| 4 | FsLit tests for all feature categories (arithmetic, comparison, if-else, let, let-rec, lambda) pass together | VERIFIED | 13/13 FsLit tests pass: `01-return42`, `02-01` through `02-03`, `03-01` through `03-05`, `04-01-fact`, `04-02-fib`, `05-01-closure-basic`, `05-02-closure-no-capture` all PASS |

**Score:** 4/4 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/FunLangCompiler.Compiler/MlirIR.fs` | Ptr type, 7 new MlirOp cases, IsLlvmFunc on FuncOp | VERIFIED | `\| Ptr` at line 8; `LlvmAllocaOp`, `LlvmStoreOp`, `LlvmLoadOp`, `LlvmAddressOfOp`, `LlvmGEPLinearOp`, `LlvmReturnOp`, `IndirectCallOp` at lines 34-40; `IsLlvmFunc: bool` at line 61; 94 lines total |
| `src/FunLangCompiler.Compiler/Printer.fs` | printType Ptr, 7 new printOp cases, IsLlvmFunc keyword switch | VERIFIED | `\| Ptr -> "!llvm.ptr"` at line 9; all 7 new op cases at lines 52-80; `if func.IsLlvmFunc then "llvm.func" else "func.func"` at line 105; 123 lines total |
| `src/FunLangCompiler.Compiler/Elaboration.fs` | freeVars, ClosureInfo, FuncSignature.ClosureInfo, ClosureCounter, Lambda compilation in Let handler, 3-way App dispatch | VERIFIED | `freeVars` at line 44; `ClosureInfo` type at lines 7-10; `ClosureInfo: ClosureInfo option` in FuncSignature at line 16; `ClosureCounter: int ref` in ElabEnv at line 26; Lambda Let handler at lines 103-229; 3-way App dispatch at lines 336-372; 396 lines total |
| `tests/compiler/05-01-closure-basic.flt` | E2E test for add_n closure (exit 8) | VERIFIED | File exists with correct FsLit format; input: `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3`; expected output: `8`; PASS confirmed |
| `tests/compiler/05-02-closure-no-capture.flt` | E2E test for zero-capture lambda (exit 5) | VERIFIED | File exists with correct FsLit format; input: `let f x = fun y -> y + 1 in let g = f 0 in g 4`; expected output: `5`; PASS confirmed |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Elaboration.fs Lambda Let handler | MlirIR.fs LlvmAllocaOp / LlvmAddressOfOp / LlvmStoreOp / LlvmGEPLinearOp / LlvmReturnOp | Op construction in elaborateExpr | WIRED | Lambda handler at lines 103-229 constructs all 5 LLVM op types; verified in generated MLIR output |
| Printer.fs printOp new cases | MlirIR.fs new MlirOp DU cases | Pattern matching on MlirOp | WIRED | All 7 new cases matched in printOp lines 52-80; `LlvmReturnOp` has two sub-patterns (empty and non-empty); no missing cases |
| Elaboration.fs App case (closure-making) | MlirIR.fs LlvmAllocaOp | Caller allocates env struct before calling closure-maker | WIRED | `LlvmAllocaOp(envPtrVal, countVal, ci.NumCaptures)` at line 354; verified in MLIR: `llvm.alloca %t1 x !llvm.struct<(ptr, i64)>` |
| Elaboration.fs App case (indirect call) | MlirIR.fs LlvmLoadOp + IndirectCallOp | Load fn_ptr from closure env[0], then indirect call | WIRED | `LlvmLoadOp(fnPtrVal, closureVal)` + `IndirectCallOp(result, fnPtrVal, closureVal, argVal)` at lines 366-367; verified in MLIR: `llvm.load %t3` + `llvm.call %t5(%t3, %t4)` |
| tests/compiler/05-01-closure-basic.flt | Full pipeline (Elaboration -> Printer -> mlir-opt -> mlir-translate -> clang) | FsLit test runner | WIRED | `fslit` command PASS; binary exits 8 |
| FuncSignature.ClosureInfo | App 3-way dispatch branch selection | `sig_.ClosureInfo.IsNone` check at App case | WIRED | `Some sig_ when sig_.ClosureInfo.IsNone` for direct call, `Some sig_` for closure-making, `None` + Ptr-typed Var for indirect call; lines 340-370 |

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| ELAB-03: Lambda expressions elaborated to closure IR | SATISFIED | Lambda Let handler produces llvm.func body + func.func closure-maker |
| ELAB-04: Indirect call dispatch for closure values | SATISFIED | 3-way App dispatch: Case 3 uses LlvmLoadOp + IndirectCallOp for Ptr-typed Vars |
| TEST-02: FsLit closure tests pass E2E | SATISFIED | 05-01-closure-basic.flt exits 8; 05-02-closure-no-capture.flt exits 5 |

### Anti-Patterns Found

None detected.

- No TODO/FIXME/placeholder comments in modified files
- No empty return stubs in new code paths
- No hardcoded values where dynamic behavior expected
- `freeVars` computes genuine free variables (not a stub returning empty set)
- Lambda compilation in Let handler is fully implemented (not a fallthrough to failwithf)

### Human Verification Required

None required. All success criteria are verifiable programmatically.

The critical end-to-end signal — `dotnet run` on the add_n expression exits 8 and all 13 FsLit tests PASS — provides full confidence in goal achievement.

### Gaps Summary

No gaps. All four phase success criteria are met by verified evidence from the codebase.

**Generated MLIR for `let add_n n = fun x -> x + n in let add5 = add_n 5 in add5 3`:**

```
module {
  llvm.func @closure_fn_0(%arg0: !llvm.ptr, %arg1: i64) -> i64 {
      %t0 = llvm.getelementptr %arg0[1] : (!llvm.ptr) -> !llvm.ptr, i64
      %t1 = llvm.load %t0 : !llvm.ptr -> i64
      %t2 = arith.addi %arg1, %t1 : i64
      llvm.return %t2 : i64
  }
  func.func @add_n(%arg0: i64, %arg1: !llvm.ptr) -> !llvm.ptr {
      %t0 = llvm.mlir.addressof @closure_fn_0 : !llvm.ptr
      llvm.store %t0, %arg1 : !llvm.ptr, !llvm.ptr
      %t1 = llvm.getelementptr %arg1[1] : (!llvm.ptr) -> !llvm.ptr, i64
      llvm.store %arg0, %t1 : i64, !llvm.ptr
      return %arg1 : !llvm.ptr
  }
  func.func @main() -> i64 {
      %t0 = arith.constant 5 : i64
      %t1 = arith.constant 1 : i64
      %t2 = llvm.alloca %t1 x !llvm.struct<(ptr, i64)> : (i64) -> !llvm.ptr
      %t3 = func.call @add_n(%t0, %t2) : (i64, !llvm.ptr) -> !llvm.ptr
      %t4 = arith.constant 3 : i64
      %t5 = llvm.load %t3 : !llvm.ptr -> !llvm.ptr
      %t6 = llvm.call %t5(%t3, %t4) : !llvm.ptr, (!llvm.ptr, i64) -> i64
      return %t6 : i64
  }
}
```

---

_Verified: 2026-03-26T03:52:17Z_
_Verifier: Claude (gsd-verifier)_
