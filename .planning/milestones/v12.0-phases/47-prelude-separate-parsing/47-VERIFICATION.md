---
phase: 47-prelude-separate-parsing
verified: 2026-03-31T23:11:36Z
status: passed
score: 4/4 must-haves verified
---

# Phase 47: Prelude Separate Parsing Verification Report

**Phase Goal:** 에러 메시지의 줄 번호가 유저 소스 파일 기준으로 정확하게 표시됨
**Verified:** 2026-03-31T23:11:36Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth                                                   | Status     | Evidence                                                                                     |
| --- | ------------------------------------------------------- | ---------- | -------------------------------------------------------------------------------------------- |
| 1   | User code line 1 error shows file:1:col (not file:174:col) | VERIFIED | 46-04-error-category-elab.flt: `1:17` confirmed; live run shows `fun:1:col` pattern         |
| 2   | User code line 2 error shows file:2:col (not file:175:col) | VERIFIED | 44-01-error-location-unbound.fun:2:17 live confirmed (`2:17` not `175:17`)                  |
| 3   | Prelude-internal error shows `<prelude>:line:col` path    | VERIFIED | Program.fs line 173: `parseProgram preludeSrc "<prelude>"` — Prelude errors carry filename  |
| 4   | All 217 E2E tests pass                                  | VERIFIED   | `fslit tests/compiler` output: `Results: 217/217 passed, 0 failed`                          |

**Score:** 4/4 truths verified

### Required Artifacts

| Artifact                             | Expected                                     | Status      | Details                                                      |
| ------------------------------------ | -------------------------------------------- | ----------- | ------------------------------------------------------------ |
| `src/FunLangCompiler.Cli/Program.fs`     | Separate Prelude/user parsing with AST merge | VERIFIED    | Lines 170-183: two-phase parse, `preludeDecls @ userDecls`   |
| `src/FunLangCompiler.Cli/Program.fs`     | Prelude parsed under `<prelude>` filename    | VERIFIED    | Line 173: `parseProgram preludeSrc "<prelude>"`              |

Both artifacts: EXISTS (221 lines), SUBSTANTIVE (no stubs), WIRED (used in main execution path).

### Key Link Verification

| From                             | To                  | Via                              | Status  | Details                                                        |
| -------------------------------- | ------------------- | -------------------------------- | ------- | -------------------------------------------------------------- |
| `src/FunLangCompiler.Cli/Program.fs` | `parseProgram`      | Two separate parseProgram calls  | WIRED   | Line 173: `parseProgram preludeSrc "<prelude>"`, line 178: `parseProgram src inputPath` |
| `src/FunLangCompiler.Cli/Program.fs` | `elaborateProgram`  | Merged AST (`preludeDecls @ userDecls`) | WIRED | Line 183: `Ast.Module(preludeDecls @ userDecls, userSpan)`, passed to `elaborateProgram` at line 199 |

### Requirements Coverage

| Requirement                                        | Status    | Blocking Issue |
| -------------------------------------------------- | --------- | -------------- |
| Prelude와 유저 코드가 별도로 파싱되어 AST가 merge됨 | SATISFIED | none           |
| 유저 코드 1행 에러가 file:1:col로 표시됨           | SATISFIED | none           |
| Prelude 내부 에러 발생 시 `<prelude>` 경로로 구분됨 | SATISFIED | none (structural: `<prelude>` filename set at parse time) |
| 기존 217개 E2E 테스트 모두 통과                    | SATISFIED | none           |

### Anti-Patterns Found

None. No TODO/FIXME, no placeholder content, no empty returns in modified code paths.

### Human Verification Required

None. All truths were verifiable programmatically:
- Line number correctness verified via live compiler run (44-01: `2:17` not `175:17`)
- Full test suite run confirmed 217/217 pass
- Pattern matching on `<prelude>` filename is structural (not runtime-dependent on Prelude having errors)

### Updated Test Files Confirmed

All 7 .flt files updated with correct 1-based user line numbers:

| File                              | Old         | New    | Confirmed |
| --------------------------------- | ----------- | ------ | --------- |
| 44-01-error-location-unbound.flt  | `175:17`    | `2:17` | yes       |
| 44-02-error-location-pattern.flt  | `174:4`, StartLine/EndLine=174 | `1:4`, StartLine/EndLine=1 | yes |
| 44-03-error-location-field.flt    | `176:17`    | `3:17` | yes       |
| 46-01-record-type-hint.flt        | `175:6`     | `2:6`  | yes       |
| 46-02-field-hint.flt              | `176:17`    | `3:17` | yes       |
| 46-03-function-hint.flt           | `175:27`    | `2:27` | yes       |
| 46-04-error-category-elab.flt     | `174:17`    | `1:17` | yes       |

### Note on SUMMARY claim vs actual result

SUMMARY reports 216/217 (43-02 pre-existing failure). Actual run: **217/217 passed**. The 43-02 test may have been fixed since or was environment-specific. Current state is fully passing.

---

_Verified: 2026-03-31T23:11:36Z_
_Verifier: Claude (gsd-verifier)_
