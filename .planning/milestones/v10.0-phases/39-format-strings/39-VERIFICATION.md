---
phase: 39-format-strings
verified: 2026-03-29T22:38:01Z
status: passed
score: 4/4 must-haves verified
---

# Phase 39: Format Strings Verification Report

**Phase Goal:** sprintf and printfn produce formatted output using C snprintf delegation
**Verified:** 2026-03-29T22:38:01Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `sprintf "%d" 42` returns the string `"42"` | VERIFIED | 39-01 test: actual output `42`, exit 0 |
| 2 | `sprintf "%s=%d" name value` handles multiple format arguments | VERIFIED | 39-02 test: `key=99` and `10+20` produced correctly, exit 0 |
| 3 | `printfn "%d states" n` prints formatted output to stdout with newline | VERIFIED | 39-03 test: output `5 states`, exit 0 |
| 4 | Format specifiers `%d`, `%s`, `%x`, `%02x`, `%c` all produce correct output | VERIFIED | 39-01 test: `42` / `ff` / `0a` / `A` all match expected output |

**Score:** 4/4 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/FunLangCompiler.Compiler/lang_runtime.c` | 6 typed snprintf wrapper functions | VERIFIED | All 6 at lines 1251–1315; two-pass snprintf idiom with GC_malloc; substantive (18+ lines each) |
| `src/FunLangCompiler.Compiler/lang_runtime.h` | Declarations for all 6 wrappers | VERIFIED | All 6 declarations at lines 200–205 |
| `src/FunLangCompiler.Compiler/Elaboration.fs` | FmtSpec DU, fmtSpecTypes, coerceToI64Arg, sprintf/printfn arms, ExternalFuncDecl in both lists | VERIFIED | FmtSpec at line 269; fmtSpecTypes at lines 273–301; coerceToI64Arg at lines 305–316; printfn arms lines 1698–1713; sprintf arms lines 1715–1795; ExternalFuncDecl in both lists (lines ~3617–3622 and ~3882–3887) |
| `tests/compiler/39-01-sprintf-int.flt` | E2E test: %d, %x, %02x, %c specifiers | VERIFIED | Exists, substantive, executed and passed (output: `42 / ff / 0a / A / 0`) |
| `tests/compiler/39-02-sprintf-multi.flt` | E2E test: 2-arg format strings | VERIFIED | Exists, substantive, executed and passed (output: `key=99 / 10+20 / 0`) |
| `tests/compiler/39-03-printfn.flt` | E2E test: printfn with format arg and plain string | VERIFIED | Exists, substantive, executed and passed (output: `5 states / hello / 0`) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Elaboration.fs sprintf arms | lang_runtime.c wrappers | LlvmCallOp to `@lang_sprintf_1i` / `@lang_sprintf_2si` etc. | WIRED | `LlvmCallOp(result, "@lang_sprintf_1i", ...)` confirmed at line 1778; `@lang_sprintf_2ss` at line 1763 |
| Elaboration.fs printfn arms | Elaboration.fs sprintf arms + println | Desugar at elaboration time: `App(Var("println"), App(Var("sprintf"), ...))` | WIRED | Lines 1699–1713: printfn 2-arg, 1-arg, 0-arg all desugar correctly |
| Elaboration.fs externalFuncs (both lists) | MLIR func declarations | ExternalFuncDecl entries for all 6 wrappers | WIRED | `@lang_sprintf_1i` appears at lines 3617 and 3882 (both lists confirmed); `@lang_sprintf_2ss` at lines 3622 and 3887 |
| Elaboration.fs fmtSpecTypes | sprintf arm guards | `when (let specs = fmtSpecTypes fmt in ...)` | WIRED | Pattern guards at lines 1717, 1770, 1784 use fmtSpecTypes to dispatch to correct wrapper |

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| RT-05: `sprintf "%d" 42` returns `"42"` | SATISFIED | 39-01 test passes |
| RT-06: Format specifiers %d, %s, %x, %02x, %c | SATISFIED | All 4 int-type specifiers pass in 39-01; %s in 39-02 |
| RT-07: `printfn "%d states" n` prints `5 states\n` | SATISFIED | 39-03 test passes |
| RT-08: `sprintf "%s=%d" name value` multi-arg formatting | SATISFIED | 39-02 test passes |

### Anti-Patterns Found

None. No TODO/FIXME, no placeholder content, no empty returns in any of the new code. All 6 C wrappers are real two-pass snprintf implementations.

### Regression Check

| Test | Status |
|------|--------|
| 36-01-forin-mutable-capture.flt | PASS |
| 36-02-sequential-if.flt | PASS |
| 36-03-bool-and-or-while.flt | PASS |
| 35-01-string-module.flt | PASS |
| 37-01-hashtable-string-keys.flt | PASS |
| 37-02-hashtable-string-content-equality.flt | PASS |
| 38-01-cli-args.flt | PASS |

No regressions detected in 7 tests from prior phases.

## Summary

Phase 39 goal is fully achieved. All four observable truths are verified by actual test execution:

- The 6 typed C snprintf wrappers exist in `lang_runtime.c/h` with proper two-pass snprintf logic and GC_malloc allocation.
- `fmtSpecTypes` correctly parses format specifiers (`%d`, `%x`, `%02x`, `%c` → IntSpec; `%s` → StrSpec) at compile time to select the right wrapper.
- Elaboration.fs dispatches sprintf correctly with 2-arg arms ordered before 1-arg arms (critical ordering preserved).
- printfn desugars to `println(sprintf ...)` at elaboration time with zero new C functions.
- ExternalFuncDecl entries appear in BOTH externalFuncs lists (module-path at ~line 3617 and program-path at ~line 3882).
- All 3 E2E tests produce exact expected output and exit with code 0.
- No regressions in 7 prior-phase tests.

---
*Verified: 2026-03-29T22:38:01Z*
*Verifier: Claude (gsd-verifier)*
