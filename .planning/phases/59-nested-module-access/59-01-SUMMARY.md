---
phase: 59-nested-module-access
plan: 01
subsystem: compiler-elaboration
tags: [nested-modules, qualified-access, elaboration, module-system]

dependency-graph:
  requires:
    - 58-01: namespace removal (eliminated NamespacedModule dead branch)
    - 41-01: open declaration handling (flattenDecls OpenDecl base)
    - 35-01: module qualified naming (flattenDecls full-prefix base)
    - 25-01: single-level module qualified access (FieldAccess/App arms base)
  provides:
    - nested module qualified access (Outer.Inner.value, Outer.Inner.f arg)
    - open Outer.Inner full-path lookup
    - arbitrary-depth module path decoding via tryDecodeModulePath
  affects: []

tech-stack:
  added: []
  patterns:
    - tryDecodeModulePath: recursive FieldAccess chain decoder — returns string list option
    - dotPath+underPath threading: separate keys for map lookup vs name generation

key-files:
  created:
    - tests/compiler/59-01-nested-module-access.flt
    - tests/compiler/59-02-nested-module-open.flt
  modified:
    - src/FunLangCompiler.Compiler/Elaboration.fs

decisions:
  - id: two-parameter-scan
    choice: "scan takes dotPath + underPath separately"
    rationale: "map key uses dots for open lookup, member names use underscores — keeping them separate avoids Replace calls in the hot path"
  - id: flt-split-files
    choice: "split two test cases into 59-01 and 59-02 separate .flt files"
    rationale: "fslit treats multi-Command files as a single combined test case; separate files allow independent pass/fail reporting"

metrics:
  duration: "8 minutes"
  completed: "2026-04-01"
---

# Phase 59 Plan 01: Nested Module Access Summary

**One-liner:** Full-path prefix threading in flattenDecls/collectModuleMembers plus recursive FieldAccess decoder for Outer.Inner.foo → Var("Outer_Inner_foo")

## What Was Done

Added nested module qualified access to the elaboration phase. Source patterns `Outer.Inner.value` and `Outer.Inner.f arg` now desugar correctly. `open Outer.Inner` now looks up by full dot-path key.

## Changes Made

### 1. collectModuleMembers — dotPath + underPath threading (NEST-02)

Rewrote `scan` from one-parameter `(modName: string)` to two-parameter `(dotPath: string) (underPath: string)`. ModuleDecl arm computes `childDot` (dot-joined, used as map key for open lookup) and `childUnder` (underscore-joined, used as member name prefix). Map key is now `"Outer.Inner"` not `"Inner"`.

### 2. flattenDecls ModuleDecl arm — full-path prefix threading (NEST-01)

Changed `flattenDecls moduleMembers name innerDecls` to compute `childPrefix = if modName = "" then name else modName + "_" + name` then pass `childPrefix`. Previously the innermost module name was used as prefix (`Inner_foo`); now the full path is (`Outer_Inner_foo`). Single-level modules are unaffected: entering `ModuleDecl("List")` with `modName=""` gives `childPrefix = "List"` as before.

### 3. flattenDecls OpenDecl arm — full dot-path key (NEST-04)

Changed `path |> List.last` (innermost segment only) to `path |> String.concat "."` (full path). `shortName` extraction uses `openedKey.Replace(".", "_")` to get the underscore prefix, then `Substring(underscorePrefix.Length + 1)`.

### 4. tryDecodeModulePath helper (NEST-03)

Added private recursive helper before `elaborateExpr`. Matches `Constructor(name, None, _) -> Some [name]` and `FieldAccess(inner, field, _) -> recurse`. Returns `None` for anything else. The `None` guard on Constructor is critical to exclude data constructors with arguments.

### 5. New elaboration arms (NEST-03)

Added two new arms:

- **Nested App arm** (`App(FieldAccess(FieldAccess(...)) as innerExpr, memberName, ...)`): inserted before existing `App(FieldAccess(Constructor(...)))` arm. Guards with `tryDecodeModulePath innerExpr).IsSome && not constructor`. Joins all segments with `_`.
- **Nested FieldAccess arm** (`FieldAccess(FieldAccess(...) as innerExpr, memberName, ...)`): inserted before existing `FieldAccess(Constructor(...))` arm. Guards with `tryDecodeModulePath.IsSome`. Handles TypeEnv constructor check same as single-level arm.

Both use `FieldAccess(FieldAccess(...) as innerExpr, ...)` pattern to match only when inner is itself a FieldAccess — single-level `FieldAccess(Constructor(...))` cases fall through to the unchanged existing arm.

## Test Results

- 59-01-nested-module-access.flt: PASS (Outer.Inner.value=42, Outer.Inner.double 5=10)
- 59-02-nested-module-open.flt: PASS (open Outer.Inner; value=99, triple 4=12)
- Full suite: 234/234 passed (232 existing + 2 new), 0 failures

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical functionality] Split multi-case test into two separate .flt files**

- **Found during:** Task 2 (test verification)
- **Issue:** RESEARCH.md example showed two `// --- Command:` blocks in one file. fslit treats multi-Command files as a single combined test case, producing combined output that does not match per-case expected output.
- **Fix:** Created 59-01 for qualified access and 59-02 for open Outer.Inner as separate files.
- **Files modified:** tests/compiler/59-01-nested-module-access.flt, tests/compiler/59-02-nested-module-open.flt
- **Commit:** 3a8b09a

## Next Phase Readiness

No blockers. Phase 59 / v16.0 is complete.
