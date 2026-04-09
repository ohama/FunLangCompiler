# Roadmap: FunLangCompiler — v23.0 FunLang v14.0 Sync + Prelude Unification

## Overview

v23.0 fixes a critical string indexing bug (Issue #22) blocking FunLexYacc, then synchronizes the FunLang submodule to v14.0 and unifies the Prelude with FunLang's canonical sources. The result: 12 Prelude files share source with FunLang, compiler-only extensions are preserved, and annotated multi-param style compiles correctly.

## Milestones

- ✅ **v1.0–v22.0** — Phases 1–93 (shipped)
- 🚧 **v23.0 FunLang v14.0 Sync + Prelude Unification** — Phases 94–97 (in progress)

## Phases

<details>
<summary>✅ v1.0–v22.0 (Phases 1–93) — SHIPPED</summary>

See MILESTONES.md for full history. Last phase: 93 (v13.1).

</details>

### 🚧 v23.0 FunLang v14.0 Sync + Prelude Unification (In Progress)

**Milestone Goal:** Fix string parameter indexing bug, sync FunLang submodule to v14.0, and unify all 12 Prelude files with FunLang's canonical sources while preserving compiler-only extensions.

---

#### Phase 94: String Parameter Indexing Bug Fix

**Goal**: `s.[i]` returns the correct character value when `s` is a function parameter (Issue #22)
**Depends on**: Nothing (standalone bug fix)
**Requirements**: BUG-01
**Success Criteria** (what must be TRUE):
  1. `s.[i]` on a string received as a function parameter returns the same value as on a string bound by `let`
  2. FunLexYacc programs that use string parameter indexing compile and produce correct output
  3. An E2E test exercises the bug scenario and passes

**Plans**: TBD

Plans:
- [ ] 94-01: Diagnose root cause of string parameter indexing and fix

---

#### Phase 95: FunLang v14.0 Type System Sync

**Goal**: ElabHelpers.fs recognizes all FunLang v14.0 collection types with no build warnings or silent bugs; FunLang submodule committed at v14.0 (8da0af2)
**Depends on**: Phase 94
**Requirements**: TYPE-01, TYPE-02, SUB-01
**Success Criteria** (what must be TRUE):
  1. `dotnet build` produces zero incomplete-pattern-match warnings for THashSet/TQueue/TMutableList/TStringBuilder
  2. `detectCollectionKind` handles all v14.0 collection union cases without falling through to default
  3. `git submodule status` shows FunLang pinned at 8da0af2

**Plans**: TBD

Plans:
- [ ] 95-01: Patch ElabHelpers.fs typeNeedsPtr + detectCollectionKind; commit submodule

---

#### Phase 96: Prelude Trivial Sync (9 files)

**Goal**: 7 copy-only Prelude files and 2 copy+append files are byte-for-byte identical to FunLang v14.0 Prelude (plus compiler-only additions)
**Depends on**: Phase 95
**Requirements**: PRE-01, PRE-02, PRE-03
**Success Criteria** (what must be TRUE):
  1. Array, Char, Hashtable, Int, Queue, String, StringBuilder Prelude files are identical to FunLang v14.0 originals
  2. HashSet.fun contains all FunLang content plus `keys` and `toList` compiler-only functions
  3. MutableList.fun contains all FunLang content plus `toList` compiler-only function
  4. All existing E2E tests pass after the file replacements

**Plans**: TBD

Plans:
- [ ] 96-01: Copy 7 trivial files; copy+append HashSet and MutableList

---

#### Phase 97: Prelude Manual Merge (5 files)

**Goal**: Core, List, Option, Result, and Typeclass Prelude files use FunLang v14.0 multi-param style while preserving compiler-only functions; all 260+ E2E tests pass
**Depends on**: Phase 96
**Requirements**: PRE-04, PRE-05, PRE-06, PRE-07, PRE-08
**Success Criteria** (what must be TRUE):
  1. Core.fun includes `char_to_int` and `int_to_char` compiler-only functions alongside the FunLang v14.0 content
  2. Option.fun compiles with multi-param style (`let f (x:T) (y:U) = ...`) and all existing Option E2E tests pass
  3. List.fun, Result.fun, and Typeclass.fun compile with multi-param style; all related E2E tests pass
  4. `dotnet run -- tests/compiler/` reports 260+ tests passing with no regressions

**Plans**: TBD

Plans:
- [ ] 97-01: Merge Core.fun + Option.fun (validate multi-param style first)
- [ ] 97-02: Merge List.fun + Result.fun + Typeclass.fun; full E2E pass

---

## Progress

**Execution Order:** 94 → 95 → 96 → 97

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 94. String Parameter Indexing Bug Fix | v23.0 | 0/TBD | Not started | - |
| 95. FunLang v14.0 Type System Sync | v23.0 | 0/TBD | Not started | - |
| 96. Prelude Trivial Sync (9 files) | v23.0 | 0/TBD | Not started | - |
| 97. Prelude Manual Merge (5 files) | v23.0 | 0/TBD | Not started | - |
