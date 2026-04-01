---
phase: 44-error-location-foundation
verified: 2026-03-31T12:00:00Z
status: gaps_found
score: 3/5 must-haves verified
gaps:
  - truth: "Unbound variable error shows actual file name, line number, and column"
    status: partial
    reason: "Format is correct (file:line:col: message) but parser creates AST nodes with zeroed spans — output shows ':0:0:' with empty filename"
    artifacts:
      - path: "src/FunLangCompiler.Compiler/Elaboration.fs"
        issue: "failWithSpan correctly reads Span fields, but upstream parser/lexer does not populate them"
    missing:
      - "Parser/lexer span propagation (FsLexYacc filtered tokenizer does not update lexbuf positions)"
  - truth: "Pattern matching errors show actual source location"
    status: partial
    reason: "Same root cause — Ast.patternSpanOf returns zeroed spans because parser never sets them"
    artifacts:
      - path: "src/FunLangCompiler.Compiler/Elaboration.fs"
        issue: "Infrastructure correct, upstream data missing"
    missing:
      - "Parser/lexer span propagation"
---

# Phase 44: Error Location Foundation Verification Report

**Phase Goal:** Every Elaboration error message includes file:line:col source location
**Verified:** 2026-03-31
**Status:** gaps_found
**Re-verification:** No -- initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | failWithSpan helper exists and formats errors as file:line:col: message | VERIFIED | Elaboration.fs:63-67 uses `sprintf "%s:%d:%d"` with span fields |
| 2 | All 18 user-facing failwithf sites use failWithSpan | VERIFIED | 19 grep matches (1 definition + 18 calls), 0 remaining failwithf calls |
| 3 | Unbound variable error shows file:line:col | PARTIAL | Output is `:0:0: Elaboration: unbound variable 'y'` -- format correct but values are zeroed |
| 4 | Pattern matching errors show source location | PARTIAL | Output is `:0:0: Elaboration: unsupported sub-pattern in TuplePat...` -- same zeroed span issue |
| 5 | Project compiles and all tests pass | VERIFIED | Build: 0 errors 0 warnings; E2E tests run successfully |

**Score:** 3/5 truths fully verified, 2 partial

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/FunLangCompiler.Compiler/Elaboration.fs` | failWithSpan helper + 18 converted sites | VERIFIED | Helper at line 63, 18 call sites, 0 remaining failwithf |
| `tests/compiler/44-01-error-location-unbound.flt` | E2E test for unbound variable error | VERIFIED | Exists, test passes |
| `tests/compiler/44-02-error-location-pattern.flt` | E2E test for pattern error | VERIFIED | Exists, test passes |
| `tests/compiler/44-03-error-location-field.flt` | E2E test for field access error | VERIFIED | Exists, test passes |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| failWithSpan | Ast.Span | span.FileName, span.StartLine, span.StartColumn | WIRED | Line 65: `sprintf "%s:%d:%d" span.FileName span.StartLine span.StartColumn` |
| failWithSpan | Ast.patternSpanOf | pattern error sites | WIRED | 4 call sites use `Ast.patternSpanOf` |
| failWithSpan | Ast.spanOf | generic expression error | WIRED | Line 3819: `failWithSpan (Ast.spanOf expr)` |
| Parser/Lexer | Ast.Span | lexbuf positions | NOT WIRED | FsLexYacc filtered tokenizer does not propagate positions to AST nodes |

### Requirements Coverage

| Requirement | Status | Blocking Issue |
|-------------|--------|----------------|
| LOC-01: Elaboration errors include file:line:col (~18 sites) | PARTIAL | Format correct, values zeroed due to parser |
| LOC-02: failWithSpan helper Span -> string -> 'a | SATISFIED | Exists at line 63, inline, uses ksprintf |
| LOC-03: Pattern matching errors include source location | PARTIAL | Format correct, values zeroed |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| Elaboration.fs | 747 | `Ast.unknownSpan` used for closure capture error | Warning | 1 of 18 sites uses unknownSpan intentionally (documented in d44-01-02) |
| test 44-02 .flt | - | Expected output includes %A debug dump of AST node | Warning | Error message exposes internal AST structure to user |

### Human Verification Required

None required -- all checks are structural and automatable.

### Gaps Summary

The phase achieves its **infrastructure goal**: `failWithSpan` helper exists, is correctly implemented, and all 18 error sites are converted. The error output format is `file:line:col: message` as designed.

However, the **end-user observable goal** ("Every Elaboration error message includes file:line:col source location") is only partially achieved because the upstream parser/lexer (FsLexYacc filtered tokenizer) does not propagate lexbuf positions into AST span fields. All spans are zeroed, so every error shows `:0:0:` with an empty filename.

This is a **known limitation** documented in the SUMMARY (decision d44-02-02). The fix requires changes to the parser/lexer layer, which is outside the scope of Phase 44 as defined (Phase 44 focuses on Elaboration.fs infrastructure). The parser is a separate concern that could be addressed in a future phase.

**Recommendation:** If the phase goal is interpreted as "infrastructure for source locations in error messages" then this phase passes. If interpreted literally as "errors show actual file:line:col", then the parser span propagation gap must be addressed first.

---

_Verified: 2026-03-31_
_Verifier: Claude (gsd-verifier)_
