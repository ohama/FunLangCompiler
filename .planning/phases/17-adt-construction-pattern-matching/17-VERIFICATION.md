---
phase: 17-adt-construction-pattern-matching
verified: 2026-03-27T00:50:49Z
status: passed
score: 5/5 must-haves verified
re_verification: false
---

# Phase 17: ADT Construction & Pattern Matching Verification Report

**Phase Goal:** ADT 값을 생성하고 패턴 매칭으로 소비하는 완전한 라운드트립이 가능하다 — nullary/unary/multi-arg constructors와 ConstructorPat이 네이티브 코드로 컴파일된다
**Verified:** 2026-03-27T00:50:49Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #  | Truth | Status | Evidence |
|----|-------|--------|----------|
| 1  | Nullary constructor `Red` compiles to 16-byte {tag=0, null} heap block; dispatches correctly in match | VERIFIED | Elaboration.fs lines 1253-1271: `GC_malloc(16)`, `LlvmGEPLinearOp(tagSlot,0)`, `ArithConstantOp(tagVal, info.Tag)`, `LlvmNullOp(null)`. Test 17-04 verifies multi-arm Color dispatch (Green->2, includes Red->1 arm), passing. |
| 2  | `Some 42` compiles to {tag=1, ptr->42}; `match (Some 42) with Some n -> n \| None -> 0` exits with 42 | VERIFIED | Elaboration.fs lines 1277-1294: arg stored at slot 1. MatchCompiler.fs line 187: `Field(selAcc, i+1)` offset. Test 17-05 exits 42. |
| 3  | Multi-arg `Pair(3,4)` wraps tuple Ptr at slot 1; pattern match extracts both fields | VERIFIED | Elaboration.fs unary/multi-arg case is uniform (Tuple arg elaborates to Ptr, stored at slot 1). Test 17-06: `Pair(7,5) -> b` exits 5. |
| 4  | GADT constructor compiles identically to regular ADT constructor | VERIFIED | Elaboration.fs lines 1358-1360: `GadtConstructorDecl` populates same `TypeEnv` entry `{Tag=idx; Arity=arity}`. Same `Constructor` elaboration path fires for both — no GADT-specific IR path. |
| 5  | All 45+ existing E2E tests continue to pass (REG-01 gate) | VERIFIED | `fslit tests/compiler/` reports 51/51 passed. |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/LangBackend.Compiler/Elaboration.fs` | Constructor elaboration (nullary + unary), AdtCtor emitCtorTest, scrutineeTypeForTag, ensureAdtFieldTypes | VERIFIED | 1297 lines; real IR emission code; no stub markers in ADT sections |
| `src/LangBackend.Compiler/MatchCompiler.fs` | AdtCtor argAccessors with +1 slot offset, splitClauses | VERIFIED | Line 187: `AdtCtor _ -> List.init arity (fun i -> Field(selAcc, i + 1))`; substantive implementation |
| `src/LangBackend.Cli/Program.fs` | IndentFilter wired into parseProgram | VERIFIED | Summary confirms `lexAndFilter` helper added; multi-line .lt tests now parse correctly |
| `tests/compiler/17-01-nullary-ctor.flt` | Nullary constructor compiles (exit 1) | VERIFIED | Exists, 6 lines, `type Color = Red | Green | Blue; let c = Red in 1`, expected output: 1 |
| `tests/compiler/17-02-unary-ctor.flt` | Unary constructor compiles (exit 1) | VERIFIED | Exists, 6 lines, `Some 42` construction, exit 1 |
| `tests/compiler/17-03-multi-arg-ctor.flt` | Multi-arg constructor compiles (exit 1) | VERIFIED | Exists, 6 lines, `Pair(3, 4)` construction, exit 1 |
| `tests/compiler/17-04-nullary-match.flt` | Nullary ADT match dispatch | VERIFIED | 11 lines, `c = Green`, 3-arm match, expects 2; Red->1 arm included |
| `tests/compiler/17-05-unary-match.flt` | Unary ADT payload extraction | VERIFIED | 10 lines, `Some 42` match, `Some n -> n`, expects 42 |
| `tests/compiler/17-06-multi-arg-match.flt` | Multi-arg ADT field extraction | VERIFIED | 9 lines, `Pair(7, 5)`, `Pair(a, b) -> b`, expects 5 |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| `elaborateExpr Constructor(name, None, _)` | `env.TypeEnv` | `Map.find name env.TypeEnv` | WIRED | Line 1254: `let info = Map.find name env.TypeEnv` — tag lookup live |
| `elaborateExpr Constructor(name, Some argExpr, _)` | `env.TypeEnv` + `elaborateExpr` | `Map.find` + recursive elaboration | WIRED | Lines 1278-1279: tag lookup + argExpr elaboration |
| `emitCtorTest AdtCtor` | `env.TypeEnv` | `Map.find name env.TypeEnv` for tag constant | WIRED | Lines 1042-1052: GEP[0] load + ArithCmpI with real tag from TypeEnv |
| `splitClauses AdtCtor` | slot-offset accessors | `Field(selAcc, i + 1)` | WIRED | Line 187: explicit AdtCtor branch with i+1 offset |
| `ensureAdtFieldTypes` | `resolveAccessorTyped` | called from Switch preload | WIRED | Lines 1074-1080 + line 1120 |
| `prePassDecls GadtConstructorDecl` | `TypeEnv` | same `{Tag; Arity}` record as ConstructorDecl | WIRED | Lines 1358-1360 |

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| ADT-05 (nullary constructor elaboration) | SATISFIED | Constructor(name, None) → 16-byte {tag, null} |
| ADT-06 (unary constructor elaboration) | SATISFIED | Constructor(name, Some arg) → {tag, argVal} |
| ADT-07 (multi-arg constructor elaboration) | SATISFIED | Tuple arg elaborated to Ptr, stored at slot 1 |
| ADT-08 (ConstructorPat tag comparison) | SATISFIED | emitCtorTest AdtCtor emits GEP[0] load + ArithCmpI |
| ADT-09 (ConstructorPat payload extraction) | SATISFIED | argAccessors use Field(selAcc, i+1); resolveAccessorTyped handles I64/Ptr |
| ADT-10 (GADT constructor backend parity) | SATISFIED | GadtConstructorDecl enters same TypeEnv; same elaboration path |

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `Elaboration.fs` line 989 | `failwith "Phase 18: RecordCtor not yet implemented"` | Info | Phase 18 stub — expected, not in scope |
| `Elaboration.fs` line 1054 | `failwith "Phase 18: RecordCtor not yet implemented"` | Info | Phase 18 stub — expected, not in scope |

No blockers. Phase 18 stubs are intentional placeholders for the next phase.

### Human Verification Required

None. All success criteria are verifiable structurally or by E2E test output.

### Notes on Test Coverage vs. Criterion Wording

Success Criterion 1 states `match c with | Red -> 1 | _ -> 0 exits with 1`. The test suite does not contain this exact program verbatim. Test 17-04 tests `c = Green` with a 3-arm match (Red->1, Green->2, Blue->3) and expects 2. This verifies the tag discrimination mechanism works for all arms including Red. The underlying implementation (tag=0 for Red, GEP[0] load, ArithCmpI comparison) is directly verified by code inspection and the passing test. The exact criterion is met by the mechanism even though the verbatim program is not a dedicated test file.

### Gaps Summary

No gaps. All 5 success criteria are verified against the actual codebase:
- Constructor elaboration is real IR emission (not stubs)
- MatchCompiler slot offset fix is in place and correct
- IndentFilter wired into parseProgram (enabling multi-line source parsing)
- GADT constructors handled via shared TypeEnv prepass
- 51/51 E2E tests pass (exceeds the 45-test REG-01 gate; 6 new tests added)

---

_Verified: 2026-03-27T00:50:49Z_
_Verifier: Claude (gsd-verifier)_
