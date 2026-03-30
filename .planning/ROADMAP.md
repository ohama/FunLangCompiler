# Roadmap: LangBackend v10.0

## Overview

v10.0 resolves the remaining blockers for FunLexYacc self-hosting: compiler bug fixes that affect real-world code, hashtable string key support, CLI argument access, format string output, and multi-file import. Bug fixes come first (they affect all subsequent work), then runtime extensions build incrementally, and multi-file import caps the milestone as the most complex compiler change.

## Milestones

- ✅ **v1.0–v9.0** — Phases 1–35 (shipped, see MILESTONES.md)
- 🚧 **v10.0 FunLexYacc 네이티브 컴파일 지원** — Phases 36–41 (in progress)

## Phases

- [x] **Phase 36: Bug Fixes** — Fix known compiler bugs blocking real-world code patterns
- [x] **Phase 37: Hashtable String Keys** — C runtime hash/compare extension for string struct keys
- [x] **Phase 38: CLI Arguments** — @main signature change + get_args runtime helper
- [x] **Phase 39: Format Strings** — sprintf/printfn via C runtime snprintf delegation
- [x] **Phase 40: Multi-file Import** — AST flattening for `open "file.fun"` before elaboration
- [ ] **Phase 41: Prelude Sync Compiler Changes** — OpenDecl 구현 + 연산자 MLIR 이름 sanitization + Prelude LangThree 완전 동기화

## Phase Details

### Phase 36: Bug Fixes
**Goal**: Real-world LangThree code patterns compile without workarounds
**Depends on**: Nothing (first phase of v10.0)
**Requirements**: FIX-01, FIX-02, FIX-03
**Success Criteria** (what must be TRUE):
  1. A for-in loop that captures a `let mut` variable in a closure runs without segfault
  2. Two consecutive `if` expressions in the same block produce valid MLIR and execute correctly
  3. A module function returning Bool can be used directly as an `if` condition without `<> 0`
**Plans**: 1 plan

Plans:
- [x] 36-01-PLAN.md — Fix FIX-01/02/03 in Elaboration.fs + E2E tests

### Phase 37: Hashtable String Keys
**Goal**: Hashtable works with string keys, not just integer keys
**Depends on**: Phase 36 (bug fixes may affect hashtable codegen)
**Requirements**: RT-01, RT-02
**Success Criteria** (what must be TRUE):
  1. `Hashtable.create ()` followed by `ht.["hello"] <- 42` stores and retrieves correctly
  2. `containsKey`, `remove`, and `keys` work with string keys
  3. String keys with identical content but different allocations hash to the same bucket
**Plans**: 2 plans

Plans:
- [x] 37-01-PLAN.md — Add LangHashtableStr structs + _str C runtime functions
- [x] 37-02-PLAN.md — Elaboration key-type dispatch + Prelude + E2E tests

### Phase 38: CLI Arguments
**Goal**: Compiled binaries can access command-line arguments as a string list
**Depends on**: Phase 37 (string handling must be solid)
**Requirements**: RT-03, RT-04
**Success Criteria** (what must be TRUE):
  1. `@main` accepts `(i64, ptr)` (argc, argv) and passes them to the runtime
  2. `get_args ()` returns a `string list` containing all CLI arguments
  3. A compiled binary invoked with `./prog foo bar` can print each argument
**Plans**: 1 plan

Plans:
- [x] 38-01-PLAN.md — C runtime args + Elaboration @main/get_args + E2E test

### Phase 39: Format Strings
**Goal**: sprintf and printfn produce formatted output using C snprintf delegation
**Depends on**: Phase 38 (string infrastructure from earlier phases)
**Requirements**: RT-05, RT-06, RT-07, RT-08
**Success Criteria** (what must be TRUE):
  1. `sprintf "%d" 42` returns the string `"42"`
  2. `sprintf "%s=%d" name value` handles multiple format arguments
  3. `printfn "%d states" n` prints formatted output to stdout with newline
  4. Format specifiers `%d`, `%s`, `%x`, `%02x`, `%c` all produce correct output
**Plans**: 1 plan

Plans:
- [x] 39-01-PLAN.md — C sprintf wrappers + Elaboration dispatch + printfn desugar + E2E tests

### Phase 40: Multi-file Import
**Goal**: `open "file.fun"` imports another file's bindings into the current scope
**Depends on**: Phase 39 (all runtime features complete; this is a compiler-only change)
**Requirements**: COMP-01, COMP-02, COMP-03, COMP-04
**Success Criteria** (what must be TRUE):
  1. `open "utils.fun"` makes all top-level bindings from utils.fun available in the importing file
  2. Recursive imports work: if A opens B and B opens C, A sees C's bindings
  3. Circular import (A opens B, B opens A) produces a clear error message instead of infinite loop
  4. Relative paths resolve from the importing file's directory, not the working directory
**Plans**: 1 plan

Plans:
- [x] 40-01-PLAN.md — expandImports in Program.fs + 5 E2E tests

### Phase 41: Prelude Sync Compiler Changes
**Goal**: `open Module` brings module members into scope, custom operators compile inside modules, and all Prelude files match LangThree exactly
**Depends on**: Phase 40 (all v10.0 features complete; this extends the module system)
**Requirements**: OPEN-01, OPEN-02, OPEN-03
**Success Criteria** (what must be TRUE):
  1. `open Core` after `module Core = let id x = x` makes `id` available without `Core.` prefix
  2. `let (^^) a b = string_concat a b` inside a module compiles to valid MLIR (no invalid symbol names)
  3. All 12 Prelude .fun files are byte-identical to ../LangThree/Prelude/ and existing E2E tests pass
  4. `List.take 2 [1;2;3]` and `List.drop 1 [1;2;3]` work in compiled programs
**Plans**: 2 plans

Plans:
- [ ] 41-01-PLAN.md — OpenDecl implementation in flattenDecls + 3 E2E tests
- [ ] 41-02-PLAN.md — Prelude file sync to LangThree + List.take/drop E2E test

**Details:**
Three compiler changes required:
1. **OpenDecl implementation** — `flattenDecls` currently discards `OpenDecl`. Must emit alias `LetDecl`s that re-bind module members at top level (e.g., `module Core = let id x = x` + `open Core` → additional `let id = Core_id`). Affects `Elaboration.fs` flattenDecls/extractMainExpr.
2. **Operator MLIR name sanitization** — Module-prefixed operators like `Core_^^` or `List_++` are invalid MLIR symbol names. `sanitizeMlirName` already added to Printer.fs; needs integration testing. Char mapping: `^→_caret_`, `+→_plus_`, `|→_pipe_`, `>→_gt_`, `<→_lt_`.
3. **Prelude file sync** — Match LangThree exactly: Core.fun (module wrapper + `(^^)` + `open Core`), List.fun (+zip/take/drop/`(++)` + `open List`), Option.fun (optionMap naming + `(<|>)` + `open Option`), Result.fun (resultMap naming + missing functions + `open Result`). Hashtable.fun keeps backend-specific `createStr`/`keysStr`.

## Progress

**Execution Order:** 36 -> 37 -> 38 -> 39 -> 40 -> 41

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 36. Bug Fixes | 1/1 | Complete | 2026-03-30 |
| 37. Hashtable String Keys | 2/2 | Complete | 2026-03-30 |
| 38. CLI Arguments | 1/1 | Complete | 2026-03-30 |
| 39. Format Strings | 1/1 | Complete | 2026-03-30 |
| 40. Multi-file Import | 1/1 | Complete | 2026-03-30 |
| 41. Prelude Sync Compiler Changes | 0/2 | Not Started | - |
