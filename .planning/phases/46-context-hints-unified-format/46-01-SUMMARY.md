---
phase: 46
plan: 01
subsystem: compiler-errors
tags: [error-messages, context-hints, error-categorization]
dependency-graph:
  requires: [44, 45]
  provides: [context-hints, unified-error-format]
  affects: []
tech-stack:
  added: []
  patterns: [error-prefix-categorization, context-hint-injection]
key-files:
  created:
    - tests/compiler/46-01-record-type-hint.flt
    - tests/compiler/46-01-record-type-hint.fun
    - tests/compiler/46-02-field-hint.flt
    - tests/compiler/46-02-field-hint.fun
    - tests/compiler/46-03-function-hint.flt
    - tests/compiler/46-03-function-hint.fun
    - tests/compiler/46-04-error-category-elab.flt
    - tests/compiler/46-04-error-category-elab.fun
    - tests/compiler/46-05-error-category-parse.flt
    - tests/compiler/46-05-error-category-parse.fun
  modified:
    - src/LangBackend.Compiler/Elaboration.fs
    - src/LangBackend.Cli/Program.fs
    - tests/compiler/44-01-error-location-unbound.flt
    - tests/compiler/44-02-error-location-pattern.flt
    - tests/compiler/44-03-error-location-field.flt
    - tests/compiler/45-01-parse-error-preserved.flt
    - tests/compiler/45-02-parse-error-position.flt
decisions:
  - id: CTX-01
    description: "Record type errors list all available record type names from env.RecordEnv"
  - id: CTX-02
    description: "Field access errors list all known records with their fields"
  - id: CTX-03
    description: "Function errors list up to 10 in-scope names from KnownFuncs + Vars"
  - id: CAT-01
    description: "failWithSpan prepends [Elaboration] prefix; Program.fs adds [Parse] for parse errors"
  - id: CAT-02
    description: "Pipeline errors (mlir-opt, mlir-translate, clang) get [Compile] prefix"
metrics:
  duration: ~5 min
  completed: 2026-03-31
---

# Phase 46 Plan 01: Context Hints & Unified Error Format Summary

Context hints added to record/field/function errors; all errors categorized with [Parse]/[Elaboration]/[Compile] prefixes.

## What Was Done

### Task 1: Context hints + error categorization

**failWithSpan [Elaboration] prefix:** Updated the central `failWithSpan` function to prepend `[Elaboration]` to all elaboration error messages, providing immediate category identification.

**Record type hints (4 sites):** `ensureRecordFieldTypes`, `RecordExpr`, `RecordUpdate`, `ensureRecordFieldTypes2` now append "Available record types: ..." listing all registered type names from `env.RecordEnv`.

**Field access hints (2 sites):** `FieldAccess` and `SetField` errors now append "Known records: ..." listing each record type and its fields (e.g., `Point: {x; y}`).

**Function hints (1 site):** The "not a known function" error now appends "In scope: ..." listing up to 10 names from `KnownFuncs` and `Vars`, filtered to exclude internal names starting with `%` or `@`.

**Error categorization in Program.fs:** The catch-all handler now routes errors:
- Messages already starting with `[Elaboration]` pass through unchanged
- Messages containing "parse error" get `[Parse]` prefix
- All others get `[Elaboration]` prefix
- Pipeline errors (`MlirOptFailed`, `TranslateFailed`, `ClangFailed`) get `[Compile]` prefix

**Existing test updates:** Updated 5 existing .flt files (44-01, 44-02, 44-03, 45-01, 45-02) to match new prefixes.

### Task 2: E2E tests

Created 5 test pairs:
- **46-01:** Record type error shows "Available record types: Point"
- **46-02:** Field access error shows "Known records: Point: {x; y}"
- **46-03:** Function error shows "In scope: ++, <|>, Array_create, ..."
- **46-04:** Elaboration error starts with `[Elaboration]`
- **46-05:** Parse error starts with `[Parse]`

## Decisions Made

| ID | Decision | Rationale |
|----|----------|-----------|
| CTX-01 | List all record type names in hint | Simple, always helpful, low noise |
| CTX-02 | List all records with fields for field errors | User can see valid fields at a glance |
| CTX-03 | Truncate in-scope names to 10 | Prevents huge messages from Prelude |
| CAT-01 | [Elaboration] via failWithSpan, [Parse] via string match | Single injection point for elaboration; parse errors come from FsLex/FsYacc |
| CAT-02 | [Compile] for pipeline errors | Distinguishes MLIR/clang failures from language-level errors |

## Deviations from Plan

None - plan executed exactly as written.

## Test Results

217/217 E2E tests pass (5 new + 212 existing, all green).

## Next Phase Readiness

v11.0 (Compiler Error Messages) is now complete:
- Phase 44: Error location foundation (failWithSpan infrastructure)
- Phase 45: Error preservation (parse error forwarding, MLIR file preservation)
- Phase 46: Context hints and unified error format
