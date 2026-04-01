---
phase: 65-partial-env-pattern-implementation
verified: 2026-04-01T23:30:48Z
status: passed
score: 10/10 must-haves verified
re_verification: false
---

# Phase 65: Partial Env Pattern Implementation Verification Report

**Phase Goal:** LetRec body에서 3+ arg curried function + outer capture 호출이 crash 없이 올바른 값을 반환한다
**Verified:** 2026-04-01T23:30:48Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Definition site emits GC_malloc + fn_ptr store + non-outerParam capture stores when hasNonOuterCaptures is true | VERIFIED | Elaboration.fs lines 881-923: `hasNonOuterCaptures` guard, `LlvmCallOp(@GC_malloc)`, `LlvmStoreOp(fnPtrVal, tEnvPtr)`, per-capture store loop |
| 2 | Template env ptr is stored in LLVM global `@__tenv_<name>` so LetRec bodies can find it | VERIFIED | Elaboration.fs lines 916-921: `globalName = "@__tenv_" + name.Replace(".", "_")`, `TplGlobals.Value <- ... @ [globalName]`, `LlvmAddressOfOp` + `LlvmStoreOp` to global |
| 3 | Call site fallback clones template env, fills outerParam slot, and returns Ptr closure | VERIFIED | Elaboration.fs lines 2700-2743: `LlvmLoadOp(templatePtr, globalAddrVal)`, full slot-copy loop (0..NumCaptures), outerParam overwrite, returns `newEnvPtr` (Ptr) |
| 4 | Fast path (all captures in scope) uses DirectCallOp unchanged | VERIFIED | Elaboration.fs lines 2695-2698: `captureStoreResult |> List.forall Option.isSome` guard, `DirectCallOp` path unchanged |
| 5 | Functions with no non-outerParam captures skip template allocation entirely | VERIFIED | Elaboration.fs lines 883-884: `if not hasNonOuterCaptures then ([], env')` |
| 6 | Maker func body only stores fn_ptr and outerParam (not other captures) | VERIFIED | Elaboration.fs lines 818-835: makerOps = `[fn_ptr store]`, outerParamStoreOps = one slot store, then `ReturnOp [makerArg1]` |
| 7 | TplGlobals are emitted as MutablePtrGlobal in module output | VERIFIED | Elaboration.fs lines 4351-4352 and 4750-4751: `env.TplGlobals.Value |> List.map MutablePtrGlobal` in both compilation paths |
| 8 | LetRec body + 3-arg curried function + outer_val=100, loop 3 → 315 | VERIFIED | `bash tests/compiler/65-01-letrec-3arg-outer-capture.sh` prints `315` |
| 9 | Two LetRec bodies sharing template global via add_base + base=10 → 66 | VERIFIED | `bash tests/compiler/65-02-nested-letrec-outer-capture.sh` prints `66` |
| 10 | All 248 E2E tests pass with zero regressions | VERIFIED | `dotnet run -- tests/compiler/` output: `Results: 248/248 passed, 0 failed` |

**Score:** 10/10 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/FunLangCompiler.Compiler/Elaboration.fs` | Definition-site template env + template-copy call path | VERIFIED | 4873 lines, substantive, no stubs. `hasNonOuterCaptures` guard at line 881, template alloc at lines 886-923, fallback at lines 2700-2745 |
| `src/FunLangCompiler.Compiler/MlirIR.fs` | MutablePtrGlobal IR variant | VERIFIED | 138 lines, `MutablePtrGlobal of name: string` added to `MlirGlobal` union at line 22 |
| `src/FunLangCompiler.Compiler/Printer.fs` | MutablePtrGlobal printer | VERIFIED | 225 lines, `MutablePtrGlobal(name)` case emits null-initialized LLVM global ptr at lines 46-48 |
| `tests/compiler/65-01-letrec-3arg-outer-capture.sh` | E2E test script TEST-01 | VERIFIED | Executable (chmod +x), 25 lines, FunLang source with outer_val=100, combine3 3-arg, loop LetRec |
| `tests/compiler/65-01-letrec-3arg-outer-capture.flt` | E2E expected output TEST-01 | VERIFIED | Expected output: `315` |
| `tests/compiler/65-02-nested-letrec-outer-capture.sh` | E2E test script TEST-02 | VERIFIED | Executable, 28 lines, two sequential LetRec bodies sharing add_base template global |
| `tests/compiler/65-02-nested-letrec-outer-capture.flt` | E2E expected output TEST-02 | VERIFIED | Expected output: `66` (redesigned from original plan's `26`) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| 2-lambda Let handler (Elaboration.fs ~line 881) | LLVM global `@__tenv_<name>` | `TplGlobals.Value <- ... @ [globalName]` + `LlvmStoreOp` to global addr | WIRED | Lines 916-921 emit addressof+store; TplGlobals ref cell shared via `env.TplGlobals` |
| TplGlobals ref cell | Module `globals` output | `env.TplGlobals.Value |> List.map MutablePtrGlobal` | WIRED | Lines 4351-4352 and 4750-4751 in both module-emit paths |
| KnownFuncs fallback (Elaboration.fs ~line 2703) | Template global | `List.contains globalName env.TplGlobals.Value` + `LlvmLoadOp` | WIRED | Lines 2703-2711: global name computed identically to definition site, checked in TplGlobals, loaded via addressof+load |
| inner func body (closure_fn_N) | env ptr `%arg0` | `LlvmGEPLinearOp` + `LlvmLoadOp` per capture slot | WIRED | Lines 762-778: captures loaded from `%arg0` slots, not outer SSA |
| Printer.fs `MutablePtrGlobal` case | LLVM IR output | `sprintf "llvm.mlir.global internal %s() ..."` | WIRED | Line 46-48 |

### Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| ENV-01: Definition site GC_mallocs env and stores captures immediately | SATISFIED | Elaboration.fs lines 889-913: `LlvmCallOp(@GC_malloc)`, fn_ptr store, per-capture store loop |
| ENV-02: Call site stores only outerParam into env | SATISFIED | Elaboration.fs lines 2729-2740: only `outerStoreOps` writes to env at call site; maker already stored fn_ptr+outerParam |
| ENV-03: Maker func body does not reference captures SSA from outer scope | SATISFIED | Elaboration.fs lines 818-835: makerOps only use `makerArg1` (env ptr) and `closureFnName` constant — no outer Vars SSA |
| REC-01: LetRec body captures already in env at call time | SATISFIED | Template global pre-populated at definition site; fallback clones it so all non-outerParam captures already present |
| REC-02: LetRec body can use direct fallback (global load + copy) without indirect call | SATISFIED | Fallback returns `newEnvPtr` (Ptr closure) directly — no IndirectCallOp; next application handles the actual call |
| REG-01: 2-arg curried function normal operation | SATISFIED | 248/248 E2E pass includes existing 2-arg curried function tests |
| REG-02: Capture-free curried function normal operation | SATISFIED | 248/248 E2E pass; `hasNonOuterCaptures = false` path unchanged |
| REG-03: All existing E2E tests pass | SATISFIED | `Results: 248/248 passed, 0 failed` |
| TEST-01: LetRec body + 3+ arg curried function + outer capture E2E test | SATISFIED | 65-01-letrec-3arg-outer-capture.flt: output 315, passes in full suite |
| TEST-02: Two LetRec bodies sharing template global E2E test | SATISFIED | 65-02-nested-letrec-outer-capture.flt: output 66 (redesigned), passes in full suite |

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| None | — | — | — |

No TODO/FIXME/placeholder/stub patterns found in any modified source files.

### Human Verification Required

None. All goal-relevant behaviors are verified programmatically:
- Correct numeric outputs (315, 66) confirmed by running compiled binaries
- No visual, real-time, or external-service dependencies
- Full E2E suite executed (248/248)

### Notable Deviation: TEST-02 Redesign

The original plan specified TEST-02 as "nested LetRec where inner loop captures outer loop parameter `n`". During Plan 65-02, this was found unsupported (unbound variable error) — capturing a parent LetRec's parameter is outside current scope. TEST-02 was redesigned to test two sequential LetRec bodies at the same level both using the same `add_base` template global (output: 66). The .flt file reflects this redesign. This deviation was auto-fixed and committed in `c6063f0`. The TEST-02 requirement (cross-LetRec template global reuse) is still meaningfully covered.

### Summary

Phase 65 achieves its goal. The partial env pattern is fully implemented:

1. **Definition site** (Elaboration.fs 2-lambda Let handler): When a curried function has non-outerParam captures, a template env is GC_malloc'd, fn_ptr and all non-outerParam captures are stored, and the ptr is saved to an LLVM mutable global (`@__tenv_<name>`).

2. **Call site fallback** (Elaboration.fs KnownFuncs else branch): When captures are not in scope (LetRec body func.func), the template global is loaded, cloned into a fresh env, the outerParam slot is overwritten with the actual argument, and the cloned env is returned as the closure — exactly mirroring what the maker function returns.

3. **Module output**: TplGlobals are emitted as `MutablePtrGlobal` entries via `MlirIR.fs` + `Printer.fs`.

4. **E2E**: TEST-01 (315) and TEST-02 (66) pass. 248/248 total tests pass.

---

_Verified: 2026-04-01T23:30:48Z_
_Verifier: Claude (gsd-verifier)_
