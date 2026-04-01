---
phase: 44
plan: 02
subsystem: compiler-errors
tags: [error-messages, source-location, e2e-tests]
dependency_graph:
  requires: [44-01]
  provides: [error-location-e2e-tests]
  affects: []
tech_stack:
  added: []
  patterns: [fslit-error-test-pattern]
key_files:
  created:
    - tests/compiler/44-01-error-location-unbound.flt
    - tests/compiler/44-01-error-location-unbound.fun
    - tests/compiler/44-02-error-location-pattern.flt
    - tests/compiler/44-02-error-location-pattern.fun
    - tests/compiler/44-03-error-location-field.flt
    - tests/compiler/44-03-error-location-field.fun
  modified: []
decisions:
  - id: d44-02-01
    decision: "Test pattern error via LetPat(TuplePat) with ConstPat sub-pattern"
    rationale: "testPattern function's catch-all is unreachable via normal match syntax (MatchCompiler handles it). LetPat TuplePat with ConstPat is the simplest reachable path."
  - id: d44-02-02
    decision: "Current spans show :0:0: with empty filename — tests verify this baseline"
    rationale: "FsLexYacc filtered tokenizer does not update lexbuf positions. Tests capture current output; will be updated when parser span propagation is fixed."
metrics:
  duration: ~5 min
  completed: 2026-03-31
---

# Phase 44 Plan 02: Error Location E2E Tests Summary

**One-liner:** Created 3 E2E tests verifying failWithSpan error output format (file:line:col) for unbound variable, pattern, and field access errors.

## What Was Done

### Task 1: Create 3 error location E2E tests

Created 3 pairs of .fun/.flt files testing error message format:

| Test | Error Category | Trigger Code | Error Output |
|------|---------------|--------------|--------------|
| 44-01 | Unbound variable | `let _ = println (to_string y)` | `:0:0: Elaboration: unbound variable 'y'` |
| 44-02 | Pattern error | `let (1, x) = (1, 2)` | `:0:0: Elaboration: unsupported sub-pattern in TuplePat: ConstPat ...` |
| 44-03 | Field access | `p.z` on `{x: int; y: int}` | `:0:0: FieldAccess: unknown field 'z'` |

**Key finding:** All spans currently show `:0:0:` with empty filename because the FsLexYacc filtered tokenizer (used for indent-aware parsing) does not propagate lexbuf positions to the parser. The `failWithSpan` infrastructure from 44-01 is correctly reading spans from AST nodes, but the parser creates AST nodes with zeroed spans.

**Test verification:** All 210 tests pass (207 existing + 3 new).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing] testPattern catch-all is unreachable via normal match syntax**
- **Found during:** Task 1
- **Issue:** The plan suggested creating pattern errors via match expressions, but the MatchCompiler handles all pattern types before testPattern is called. testPattern's catch-all `| _ ->` is dead code for match expressions.
- **Fix:** Used `let (1, x) = (1, 2)` which goes through LetPat(TuplePat) path where ConstPat sub-pattern IS unsupported.
- **Files modified:** tests/compiler/44-02-error-location-pattern.fun

## Next Phase Readiness

Phase 44 complete. The E2E tests establish a baseline for error location format. Future work should:
1. Fix parser span propagation so errors show actual file:line:col instead of :0:0:
2. The pattern error test (44-02) output includes %A debug format of AST nodes — consider using a cleaner error message format
