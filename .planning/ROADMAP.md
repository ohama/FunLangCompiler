# Roadmap: LangBackend

## Milestones

- ✅ **v1.0 Core Compiler** - Phases 1-6 (shipped 2026-03-26)
- ✅ **v2.0 Data Types & Pattern Matching** - Phases 7-11 (shipped 2026-03-26)
- ✅ **v3.0 Language Completeness** - Phases 12-15 (shipped 2026-03-26)
- ✅ **v4.0 Type System & Error Handling** - Phases 16-20 (shipped 2026-03-27)
- ✅ **v5.0 Mutable & Collections** - Phases 21-24 (shipped 2026-03-28)
- 🚧 **v6.0 Modules & File I/O** - Phases 25-27 (in progress)

## Phases

<details>
<summary>✅ v1.0–v5.0 (Phases 1–24) — SHIPPED</summary>

See .planning/MILESTONES.md for full history. 24 phases, 45 plans, 92 E2E tests.

</details>

---

### 🚧 v6.0 Modules & File I/O (In Progress)

**Milestone Goal:** LangThree programs can use module declarations and perform file/stdio/environment I/O, compiled to native binaries with all 92 existing tests still passing.

#### Phase 25: Module System

- [ ] **Phase 25: Module System** — Programs using module declarations compile and execute correctly

**Goal**: LangThree programs that define and use modules compile to native binaries with correct behavior.

**Depends on**: Phase 24 (v5.0 complete)

**Requirements**: MOD-01, MOD-02, MOD-03, MOD-04, MOD-05, MOD-06

**Success Criteria** (what must be TRUE):
  1. A program with a `module M = { let x = ... }` declaration compiles and executes, producing the correct output
  2. Qualified names (`M.x`) in expressions resolve to the correct module member value at runtime
  3. `open M` in source does not cause a compiler error or incorrect codegen
  4. `let pat = expr` declarations inside a module are included in execution (not silently dropped)
  5. All 92 existing E2E tests continue to pass after module system changes

**Plans**: 2 plans

Plans:
- [x] 25-01-PLAN.md — prePassDecls recursion + flattenDecls + LetPatDecl + OpenDecl/NamespaceDecl no-ops (MOD-01/02/03/04/06)
- [x] 25-02-PLAN.md — Qualified name desugar in elaborateExpr (MOD-05)

---

#### Phase 26: File I/O Core

- [ ] **Phase 26: File I/O Core** — Basic file and stderr operations available as builtins

**Goal**: LangThree programs can read files, write files, check file existence, and print to stderr.

**Depends on**: Phase 25

**Requirements**: FIO-01, FIO-02, FIO-03, FIO-04, FIO-09, FIO-14

**Success Criteria** (what must be TRUE):
  1. `read_file "path"` returns the full contents of the file as a string
  2. `write_file "path" "content"` creates or overwrites a file with the given content
  3. `append_file "path" "content"` appends content to an existing file without truncating it
  4. `file_exists "path"` returns `true` for existing files and `false` for absent ones
  5. `eprint "msg"` and `eprintln "msg"` emit to stderr and do not appear on stdout

**Plans**: 1 plan

Plans:
- [x] 26-01-PLAN.md — C runtime file I/O functions + Elaboration.fs wiring + E2E tests (FIO-01/02/03/04/09/14)

---

#### Phase 27: File I/O Extended

- [ ] **Phase 27: File I/O Extended** — Line-oriented, stdin, environment, path, and directory builtins available

**Goal**: LangThree programs can read/write line lists, read stdin, query environment variables, get the current directory, join paths, and list directory contents.

**Depends on**: Phase 26

**Requirements**: FIO-05, FIO-06, FIO-07, FIO-08, FIO-10, FIO-11, FIO-12, FIO-13

**Success Criteria** (what must be TRUE):
  1. `read_lines "path"` returns a list of strings, one per line in the file
  2. `write_lines "path" lines` writes each string in `lines` as a separate line to the file
  3. `stdin_read_line ()` and `stdin_read_all ()` read from stdin and return the result as a string
  4. `get_env "VAR"` returns the value of the environment variable, and `get_cwd ()` returns the current working directory path
  5. `path_combine "a" "b"` joins path segments correctly, and `dir_files "dir"` returns a list of filenames in the directory

**Plans**: 2 plans

Plans:
- [x] 27-01-PLAN.md — C runtime functions (8 new functions in lang_runtime.c/h)
- [x] 27-02-PLAN.md — Elaboration.fs wiring + E2E tests (FIO-05/06/07/08/10/11/12/13)

---

## Progress

**Execution Order:** 25 → 26 → 27

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1–24 | v1.0–v5.0 | 45/45 | Complete | 2026-03-28 |
| 25. Module System | v6.0 | 2/2 | Complete | 2026-03-28 |
| 26. File I/O Core | v6.0 | 1/1 | Complete | 2026-03-28 |
| 27. File I/O Extended | v6.0 | 2/2 | Complete | 2026-03-28 |

**Regression gate (REG-01):** 92 E2E tests must pass at completion of every phase.
