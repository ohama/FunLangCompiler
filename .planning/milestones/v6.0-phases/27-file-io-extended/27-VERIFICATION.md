---
phase: 27-file-io-extended
verified: 2026-03-27T23:55:00Z
status: passed
score: 5/5 must-haves verified
re_verification: false
---

# Phase 27: File I/O Extended Verification Report

**Phase Goal:** LangThree programs can read/write line lists, read stdin, query environment variables, get the current directory, join paths, and list directory contents.
**Verified:** 2026-03-27T23:55:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth                                                         | Status     | Evidence                                                                          |
| --- | ------------------------------------------------------------- | ---------- | --------------------------------------------------------------------------------- |
| 1   | `read_lines "path"` returns a list of strings, one per line   | VERIFIED   | `lang_read_lines` in lang_runtime.c:459; elaboration arm at Elaboration.fs:1110; 27-01-read-lines.flt passes |
| 2   | `write_lines "path" lines` writes each string as a separate line | VERIFIED | `lang_write_lines` in lang_runtime.c:499; two-arg elaboration arm at Elaboration.fs:1095; 27-02-write-lines.flt passes |
| 3   | `stdin_read_line ()` and `stdin_read_all ()` read from stdin  | VERIFIED   | `lang_stdin_read_line` (line 512) and `lang_stdin_read_all` (line 533); unit-arg elaboration arms; 27-03 and 27-04 pass |
| 4   | `get_env "VAR"` and `get_cwd ()` return environment/directory strings | VERIFIED | `lang_get_env` (line 554) and `lang_get_cwd` (line 581); one-arg and unit-arg elaboration arms; 27-05 and 27-06 pass |
| 5   | `path_combine "a" "b"` joins path segments; `dir_files "dir"` returns list of filenames | VERIFIED | `lang_path_combine` (line 603) and `lang_dir_files` (line 617); two-arg and one-arg elaboration arms; 27-07 and 27-08 pass |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact                                        | Expected                                               | Status      | Details                                                                         |
| ----------------------------------------------- | ------------------------------------------------------ | ----------- | ------------------------------------------------------------------------------- |
| `src/FunLangCompiler.Compiler/lang_runtime.c`       | 8 new C functions for extended file I/O                | VERIFIED    | 8 functions at lines 459–660; 661 total lines; GC_malloc only; lang_throw for errors |
| `src/FunLangCompiler.Compiler/lang_runtime.h`       | 8 function declarations                                | VERIFIED    | Declarations at lines 69–76, all correct signatures                             |
| `src/FunLangCompiler.Compiler/Elaboration.fs`       | 8 elaboration arms + 16 ExternalFuncDecl entries       | VERIFIED    | 24 matches confirmed; arms at lines 1094–1143; ExternalFuncDecl at lines 2446–2453 and 2631–2638 |
| `tests/compiler/27-01-read-lines.flt`           | E2E test for read_lines                                | VERIFIED    | Substantive test, passes in suite                                               |
| `tests/compiler/27-02-write-lines.flt`          | E2E test for write_lines                               | VERIFIED    | Substantive test, passes in suite                                               |
| `tests/compiler/27-03-stdin-read-line.flt`      | E2E test for stdin_read_line                           | VERIFIED    | Pipes "hello" to stdin, expects "hello", passes                                 |
| `tests/compiler/27-04-stdin-read-all.flt`       | E2E test for stdin_read_all                            | VERIFIED    | Pipes "ab\ncd" via printf, passes                                               |
| `tests/compiler/27-05-get-env.flt`              | E2E test for get_env                                   | VERIFIED    | Sets LANG_TEST_VAR=hello42, passes                                              |
| `tests/compiler/27-06-get-cwd.flt`              | E2E test for get_cwd                                   | VERIFIED    | Verifies non-empty cwd string, passes                                           |
| `tests/compiler/27-07-path-combine.flt`         | E2E test for path_combine                              | VERIFIED    | Expects "dir/file.txt", passes                                                  |
| `tests/compiler/27-08-dir-files.flt`            | E2E test for dir_files                                 | VERIFIED    | Creates temp dir with a.txt, expects "found", passes                            |
| `tests/compiler/27-09-read-lines-missing.flt`   | Error handling: read_lines on missing file             | VERIFIED    | try/with catches exception, prints error message, passes                        |
| `tests/compiler/27-10-get-env-missing.flt`      | Error handling: get_env on missing variable            | VERIFIED    | try/with catches exception, prints "caught", passes                             |

### Key Link Verification

| From                                  | To                        | Via                                       | Status   | Details                                                                    |
| ------------------------------------- | ------------------------- | ----------------------------------------- | -------- | -------------------------------------------------------------------------- |
| `Elaboration.fs` elaborateExpr arms   | `lang_runtime.c` functions | `LlvmCallOp/@lang_*` external calls       | WIRED    | 8 arms (lines 1094–1143) emit LlvmCallOp/LlvmCallVoidOp to all 8 @lang_* names |
| `Elaboration.fs` externalFuncs list 1 | MLIR func.extern decls    | ExternalFuncDecl entries                  | WIRED    | 8 entries at lines 2446–2453 with correct ExtParams/ExtReturn              |
| `Elaboration.fs` externalFuncs list 2 | MLIR func.extern decls    | ExternalFuncDecl entries (second codegen list) | WIRED | Identical 8 entries at lines 2631–2638                                     |
| Two-arg arms (write_lines, path_combine) | One-arg arms           | Nested App(App(Var(...))) ordering        | WIRED    | Two-arg arms placed BEFORE one-arg arms (lines 1094–1107 before 1109+)     |
| `lang_runtime.c` pointer casts        | GC (no premature free)    | `(int64_t)(uintptr_t)` intermediate       | WIRED    | All pointer-to-int casts use uintptr_t; no malloc/realloc/free in phase 27 code |
| `lang_read_lines` / `lang_dir_files`  | `lang_throw`              | Error path on fopen/opendir failure       | WIRED    | Both call `lang_throw` with descriptive message on failure (verified at lines 472, 630) |
| `lang_get_env` / `lang_get_cwd`       | `lang_throw`              | Error path on getenv/getcwd failure       | WIRED    | Both call `lang_throw` on failure (lines 569, 591)                         |

### Requirements Coverage

| Requirement | Status    | Notes                                                            |
| ----------- | --------- | ---------------------------------------------------------------- |
| FIO-05      | SATISFIED | `read_lines` elaborated and tested; REQUIREMENTS.md status not updated (documentation artifact only) |
| FIO-06      | SATISFIED | `write_lines` elaborated and tested                              |
| FIO-07      | SATISFIED | `stdin_read_line` elaborated and tested with piped stdin         |
| FIO-08      | SATISFIED | `stdin_read_all` elaborated and tested with piped multi-line input |
| FIO-10      | SATISFIED | `get_env` elaborated and tested with real env var               |
| FIO-11      | SATISFIED | `get_cwd` elaborated and tested (verifies non-empty return)     |
| FIO-12      | SATISFIED | `path_combine` elaborated and tested                            |
| FIO-13      | SATISFIED | `dir_files` elaborated and tested with real temp directory      |

Note: REQUIREMENTS.md still shows FIO-05 through FIO-13 status as "Pending" — this is a documentation update that was not done after phase completion. The code implementations are fully verified.

### Anti-Patterns Found

None. No TODO/FIXME/placeholder patterns found in any phase 27 code. No empty handlers. No `malloc`/`realloc`/`free` calls in phase 27 functions (lines 459+). All allocations use `GC_malloc`.

### Human Verification Required

None for automated correctness verification. The following are informational only:

1. **stdin_read_line interactive use** — Test 27-03 uses piped stdin ("hello"). Interactive terminal stdin not tested, but the `fgetc` loop implementation is standard and correct.
2. **dir_files file ordering** — Test 27-08 uses wildcard match `_ :: _` (not checking specific filenames) because directory listing order is OS/filesystem dependent. Correct by design.

## Full Test Suite Results

**118/118 tests pass** (confirmed by running `fslit tests/compiler/` from project root).

- Baseline before phase 27: 108 tests
- Added by phase 27: 10 new tests (27-01 through 27-10)
- No regressions in existing 108 tests

## Summary

Phase 27 fully achieves its goal. All 8 extended file I/O builtins (`read_lines`, `write_lines`, `stdin_read_line`, `stdin_read_all`, `get_env`, `get_cwd`, `path_combine`, `dir_files`) are:

1. **Implemented in C** — `lang_runtime.c` lines 459–660, all using GC_malloc, all errors via `lang_throw` (catchable), all pointer casts through `uintptr_t`
2. **Declared in the header** — `lang_runtime.h` lines 69–76 with correct signatures
3. **Wired in the compiler** — Elaboration.fs has 8 elaboration arms (correct ordering: two-arg before one-arg) and 16 ExternalFuncDecl entries across both required lists
4. **Tested end-to-end** — 10 E2E tests covering all 8 builtins plus error handling for `read_lines` and `get_env` on missing targets

Notable implementation decisions verified in code:
- `lang_dir_files` allows `DT_UNKNOWN` in addition to `DT_REG` for filesystem compatibility
- `lang_path_combine` conditionally adds `/` separator (matches Path.Combine semantics)
- `print`/`println` arms in Elaboration.fs emit `LlvmIntToPtrOp` when `strVal.Type = I64` to handle strings extracted from list cons cells (head stored as int64 per ABI) — this fix enables `read_lines` result to be pattern-matched and printed

This is the final phase of the v6.0 milestone. All v6.0 requirements are implemented.

---

_Verified: 2026-03-27T23:55:00Z_
_Verifier: Claude (gsd-verifier)_
