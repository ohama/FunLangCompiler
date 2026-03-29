# Roadmap: LangBackend v10.0

## Overview

v10.0 resolves the remaining blockers for FunLexYacc self-hosting: compiler bug fixes that affect real-world code, hashtable string key support, CLI argument access, format string output, and multi-file import. Bug fixes come first (they affect all subsequent work), then runtime extensions build incrementally, and multi-file import caps the milestone as the most complex compiler change.

## Milestones

- ✅ **v1.0–v9.0** — Phases 1–35 (shipped, see MILESTONES.md)
- 🚧 **v10.0 FunLexYacc 네이티브 컴파일 지원** — Phases 36–40 (in progress)

## Phases

- [x] **Phase 36: Bug Fixes** — Fix known compiler bugs blocking real-world code patterns
- [x] **Phase 37: Hashtable String Keys** — C runtime hash/compare extension for string struct keys
- [ ] **Phase 38: CLI Arguments** — @main signature change + get_args runtime helper
- [ ] **Phase 39: Format Strings** — sprintf/printfn via C runtime snprintf delegation
- [ ] **Phase 40: Multi-file Import** — AST flattening for `open "file.fun"` before elaboration

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
**Plans**: TBD

Plans:
- [ ] 38-01: TBD

### Phase 39: Format Strings
**Goal**: sprintf and printfn produce formatted output using C snprintf delegation
**Depends on**: Phase 38 (string infrastructure from earlier phases)
**Requirements**: RT-05, RT-06, RT-07, RT-08
**Success Criteria** (what must be TRUE):
  1. `sprintf "%d" 42` returns the string `"42"`
  2. `sprintf "%s=%d" name value` handles multiple format arguments
  3. `printfn "%d states" n` prints formatted output to stdout with newline
  4. Format specifiers `%d`, `%s`, `%x`, `%02x`, `%c` all produce correct output
**Plans**: TBD

Plans:
- [ ] 39-01: TBD

### Phase 40: Multi-file Import
**Goal**: `open "file.fun"` imports another file's bindings into the current scope
**Depends on**: Phase 39 (all runtime features complete; this is a compiler-only change)
**Requirements**: COMP-01, COMP-02, COMP-03, COMP-04
**Success Criteria** (what must be TRUE):
  1. `open "utils.fun"` makes all top-level bindings from utils.fun available in the importing file
  2. Recursive imports work: if A opens B and B opens C, A sees C's bindings
  3. Circular import (A opens B, B opens A) produces a clear error message instead of infinite loop
  4. Relative paths resolve from the importing file's directory, not the working directory
**Plans**: TBD

Plans:
- [ ] 40-01: TBD

## Progress

**Execution Order:** 36 → 37 → 38 → 39 → 40

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 36. Bug Fixes | 1/1 | Complete | 2026-03-30 |
| 37. Hashtable String Keys | 2/2 | Complete | 2026-03-30 |
| 38. CLI Arguments | 0/TBD | Not started | - |
| 39. Format Strings | 0/TBD | Not started | - |
| 40. Multi-file Import | 0/TBD | Not started | - |
