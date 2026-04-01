---
phase: 35-prelude-modules
plan: "03"
subsystem: cli-prelude-loading
tags: [fsharp, cli, prelude, module-qualified-naming, elaboration]

dependency-graph:
  requires: ["35-01", "35-02"]
  provides: ["cli-prelude-auto-loading", "module-qualified-mlir-names"]
  affects: ["all-future-programs-using-prelude-modules"]

tech-stack:
  added: []
  patterns:
    - input-file-directory-based prelude discovery (walk up tree from input file)
    - module-qualified naming in flattenDecls (ModuleName_funcName)
    - short-name alias in LetRec/Let/two-Lambda elaboration arms

file-tracking:
  created:
    - tests/compiler/35-10-cli-prelude.flt
  modified:
    - src/FunLangCompiler.Cli/Program.fs
    - src/FunLangCompiler.Compiler/Elaboration.fs
    - Prelude/List.fun

decisions:
  - id: D1
    choice: "Walk up from input file directory (not CWD) to discover Prelude/"
    rationale: "CWD-based search caused all 182 existing tests to fail when fslit runs from project root (CWD=project root, Prelude/ found, modules prepended to all tests)"
  - id: D2
    choice: "Module-qualified naming in flattenDecls (Option_map, String_endsWith)"
    rationale: "Both Option.fun and Result.fun define map/bind/filter producing duplicate @map/@bind symbols in MLIR; prefix with module name eliminates collision"
  - id: D3
    choice: "Short-name alias in LetRec/Let/two-Lambda arms"
    rationale: "Internal recursive calls (sort calls _insert, head calls hd) stopped resolving after rename; alias maps both 'List_sort' and 'sort' to same FuncSig"
  - id: D4
    choice: "cp %input to project root in E2E test command"
    rationale: "fslit writes %input to /var/folders (macOS temp); walk-up from there never finds Prelude/; copying to project root makes it discoverable"
  - id: D5
    choice: "Remove take/drop/zip from Prelude/List.fun"
    rationale: "Pre-existing if-else-match MLIR bug: 'if n=0 then [] else match xs with ...' generates empty basic blocks; removed to unblock phase"

metrics:
  duration: "~3 hours (includes debugging CWD vs input-file-directory prelude discovery)"
  completed: "2026-03-29"
---

# Phase 35 Plan 03: CLI Prelude Auto-Loading Summary

**One-liner:** CLI discovers Prelude/ by walking up from input file; module-qualified MLIR names (Option_map, String_endsWith) eliminate symbol collisions; 183/183 tests pass.

## What Was Built

### Task 1: CLI Prelude Auto-Loading + Elaboration Module Qualification

**Program.fs** — `findPreludeDir` walks up the directory tree from the input file's directory until it finds a `Prelude/` directory (or falls back to the assembly directory). When found, all 8 `.fun` files are loaded in dependency order (Option, Result, String, Char, Hashtable, StringBuilder, List, Array) and prepended to user source before parsing.

**Elaboration.fs** — Three coordinated changes to support loading all 8 modules simultaneously without MLIR symbol collisions:

1. `flattenDecls` now accepts a `modName` parameter. Declarations inside `module Option = ...` become `Option_map`, `Option_bind`, etc. rather than flat `map`, `bind`.

2. `FieldAccess(Constructor("Option"), "map")` desugars to `Var("Option_map")`, and `App(FieldAccess(Constructor("String"), "endsWith"), arg)` desugars to `App(Var("String_endsWith"), arg)`.

3. Short-name alias: after adding `List_sort` to `KnownFuncs`, also add `sort` (the unqualified name) so that internal recursive body calls (`_insert h (sort t)`) still resolve.

**Prelude/List.fun** — Removed `take`, `drop`, `zip` which used `if n=0 then [] else match xs with ...` — a pre-existing compiler bug where `if-else-match` generates empty MLIR basic blocks (unreachable code in the else branch).

### Task 2: E2E Test 35-10

`tests/compiler/35-10-cli-prelude.flt` — validates that a bare program using `String.endsWith`, `String.length`, and `Option.isSome` compiles and runs correctly without any inline module definitions, relying entirely on CLI prelude auto-loading.

**Key design:** The test command uses `cp %input "$TMPLT"` to copy the fslit-generated input file (in macOS temp dir `/var/folders/`) to the project root. This makes the input file's directory walk-up discover `Prelude/` in the project root.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] CWD-based prelude discovery broke all 182 existing tests**

- **Found during:** Task 1 testing
- **Issue:** Plan specified CWD-based discovery. When `fslit tests/compiler/` runs from the project root, every test invocation had CWD = project root, which contains `Prelude/`. This prepended all 8 prelude modules to every test, causing: (1) parse failures for bare-expression tests, (2) MLIR symbol collisions for tests with inline modules.
- **Fix:** Changed to input-file-directory-based walk-up. Tests in `/tmp/` or `tests/compiler/` don't accidentally find `Prelude/`.
- **Files modified:** `src/FunLangCompiler.Cli/Program.fs`
- **Commit:** 6bc035a

**2. [Rule 1 - Bug] Module name collision: Option.map and Result.map both produce @map in MLIR**

- **Found during:** Task 1 testing — MLIR-opt reported `error: symbol 'map' is already defined`
- **Issue:** `flattenDecls` stripped module wrappers and kept original names, causing duplicate function definitions when multiple modules define `map`, `bind`, `filter`, `iter`, `fold`, `defaultValue`.
- **Fix:** Module-qualified naming in `flattenDecls` + FieldAccess desugar update.
- **Files modified:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **Commit:** 6bc035a

**3. [Rule 1 - Bug] Internal recursive calls broken after renaming**

- **Found during:** Task 1 testing — `sort` called `_insert` which was renamed to `List__insert`
- **Issue:** Body of `List_sort` called `sort t` (which became `List_sort t`) and `_insert h ...` (which looked up `_insert` in KnownFuncs but only `List__insert` was there).
- **Fix:** Short-name alias in LetRec/Let/two-Lambda elaboration arms.
- **Files modified:** `src/FunLangCompiler.Compiler/Elaboration.fs`
- **Commit:** 6bc035a

**4. [Rule 1 - Bug] take/drop/zip in List.fun trigger pre-existing if-else-match MLIR bug**

- **Found during:** Task 1 testing — `mlir-opt` failed with `error: expected non-empty block`
- **Issue:** `let rec take n = fun xs -> if n = 0 then [] else match xs with ...` generates an empty MLIR basic block in the else branch (pre-existing compiler limitation).
- **Fix:** Removed take/drop/zip from Prelude/List.fun.
- **Files modified:** `Prelude/List.fun`
- **Commit:** 6bc035a

**5. [Rule 3 - Blocking] %input goes to /var/folders/, prelude not found**

- **Found during:** Task 2 E2E test creation
- **Issue:** fslit writes `%input` to macOS temp dir `/var/folders/`. Walking up from there never finds `Prelude/` in the project root.
- **Fix:** Test command uses `cp %input "$TMPLT"` to copy the input file to the project root directory before compiling.
- **Files modified:** `tests/compiler/35-10-cli-prelude.flt`
- **Commit:** 17b16c6

## Test Results

All 183/183 tests pass:
- 182 existing tests: no regressions
- Test 35-10 (new): PASS — CLI prelude auto-loading works end-to-end

## Decisions Made

| ID | Decision | Rationale |
|----|----------|-----------|
| D1 | Walk up from input file dir to find Prelude/ | CWD search broke all 182 existing tests |
| D2 | Module-qualified naming (Option_map etc.) | MLIR symbol collision when multiple modules share function names |
| D3 | Short-name alias for internal calls | After rename, internal recursive calls couldn't find their callees |
| D4 | cp %input to project root in E2E test | fslit %input goes to /var/folders/, not in project tree |
| D5 | Remove take/drop/zip from List.fun | Pre-existing if-else-match MLIR empty-block bug |

## Next Phase Readiness

Phase 35 plan 03 complete. The CLI now auto-loads all 8 prelude modules for programs in a project containing `Prelude/`. Users can write programs using `String.endsWith`, `Option.map`, `List.filter`, `Array.map`, etc. without any module declarations.

**Remaining for Phase 35:** Plans 04+ (if any) — may involve additional prelude functions or integration polish.

**Note for future sessions:** `take`, `drop`, and `zip` are not in Prelude/List.fun due to the pre-existing `if-else-match` MLIR bug. If the compiler bug is fixed, these can be re-added.
