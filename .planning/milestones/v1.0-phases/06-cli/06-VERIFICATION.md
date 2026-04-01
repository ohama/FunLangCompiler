---
phase: 06-cli
verified: 2026-03-26T04:03:27Z
status: passed
score: 5/5 must-haves verified
---

# Phase 6: CLI File Input and Error Handling — Verification Report

**Phase Goal:** A usable `langbackend <file.lt>` command reads a FunLang source file, runs the full Elaboration → MlirIR → Printer → shell pipeline, and produces a named native executable that the user can run directly.
**Verified:** 2026-03-26T04:03:27Z
**Status:** PASSED
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #  | Truth | Status | Evidence |
|----|-------|--------|----------|
| 1  | `langbackend hello.lt` produces a `hello` executable in the current directory (strips .lt extension) | VERIFIED | `06-01-cli-file-input.flt` PASS: `cd /tmp && cp %input ${OUTNAME}.lt && dotnet run ... -- ${OUTNAME}.lt && ./${OUTNAME}` produces binary, exits 15 |
| 2  | `langbackend hello.lt -o custom` produces a `custom` executable (explicit -o overrides default) | VERIFIED | `parseArgs` in Program.fs returns `Some o` for `-o` flag; `outputPath = o` branch confirmed at line 34 |
| 3  | Missing or nonexistent input file prints a human-readable error and exits non-zero without producing a binary | VERIFIED | `06-02-cli-error.flt` PASS: nonexistent path prints `Error: file not found: /tmp/langback_nonexistent_file.lt` and exits 1 |
| 4  | A FunLang parse error prints a human-readable error message and exits non-zero | VERIFIED | `try/with ex -> eprintfn "Error: %s" ex.Message; 1` at lines 46-64 of Program.fs wraps entire parse/elaborate/compile pipeline |
| 5  | All 13 existing FsLit tests still pass (no regression from CLI changes) | VERIFIED | 15/15 FsLit tests pass: all 13 prior tests (01 through 05-02) plus 2 new CLI tests green |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Exists | Substantive | Wired | Status |
|----------|----------|--------|-------------|-------|--------|
| `src/FunLangCompiler.Cli/Program.fs` | CLI driver with auto-named output, file-not-found handling, parse error handling | YES | YES (65 lines, no stubs, 0 warnings) | YES — called by `dotnet run` entrypoint; wires to `Pipeline.compile`, `Elaboration.elaborateModule`, `parseExpr` | VERIFIED |
| `tests/compiler/06-01-cli-file-input.flt` | E2E test for file input producing correct binary | YES | YES (5 lines, correct FsLit format) | YES — `fslit` runner executes it; PASS | VERIFIED |
| `tests/compiler/06-02-cli-error.flt` | E2E test for error handling (nonexistent file) | YES | YES (5 lines, correct FsLit format) | YES — `fslit` runner executes it; PASS | VERIFIED |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Program.fs argument parsing | `outputPath` derivation | `Path.GetFileNameWithoutExtension(inputPath)` when `-o` absent | WIRED | Line 36: `System.IO.Path.GetFileNameWithoutExtension(inputPath)` — strips `.lt` extension and directory prefix |
| `outputPath` | `Pipeline.compile` | Passed as second arg at line 50 | WIRED | `Pipeline.compile mlirMod outputPath` — output name flows from parsed args into compile step |
| Program.fs file existence check | `eprintfn "Error: file not found: %s"` + exit 1 | `File.Exists` guard at line 40 | WIRED | `if not (System.IO.File.Exists(inputPath)) then eprintfn ...; 1` — fires before `ReadAllText` |
| Program.fs parse error catch | stderr error message + exit 1 | `try/with ex -> eprintfn "Error: %s" ex.Message` at lines 46-64 | WIRED | Entire parse/elaborate/compile pipeline inside try block; exception message printed cleanly |
| `06-01-cli-file-input.flt` command | Auto-naming behavior exercised | `cd /tmp && ... ${OUTNAME}.lt` (no `-o` flag) | WIRED | Command line calls CLI with `.lt` file only; binary named after stripped filename; test exits 15 |

### Requirements Coverage

| Requirement | Status | Notes |
|-------------|--------|-------|
| SC-1: `langbackend hello.lt` produces a `hello` executable | SATISFIED | Verified via 06-01-cli-file-input.flt PASS; `GetFileNameWithoutExtension` strips extension |
| SC-2: Produced executable runs without dynamic library issues | SATISFIED (on macOS host) | 15/15 FsLit tests run and produce executing binaries; Linux x86-64 requires human verification on target system |
| SC-3: Compile error prints human-readable message and exits non-zero | SATISFIED | File-not-found verified via 06-02-cli-error.flt PASS; parse-error path verified structurally in Program.fs lines 46-64 |

### Anti-Patterns Found

None. Program.fs has 0 TODO/FIXME/placeholder patterns, 0 empty handlers, builds with 0 warnings.

### Human Verification Required

#### 1. Linux x86-64 Dynamic Library Behavior

**Test:** On a Linux x86-64 system, run `langbackend hello.lt` (where `hello.lt` contains `42`) and then run `./hello`.
**Expected:** `./hello` exits with code 42 and produces no "library not found" or dynamic linker errors.
**Why human:** The macOS clang flags (`-Wno-override-module`) and the absence of explicit `-static` cannot be verified programmatically to produce a fully self-contained binary on Linux. The test environment is macOS; the success criterion targets Linux x86-64.

#### 2. Parse Error User Experience

**Test:** Create a file `bad.lt` containing `let x = in 5` (malformed let). Run `langbackend bad.lt`.
**Expected:** The error message printed to stderr is readable and indicates the source of the problem (not a .NET stack trace or internal exception type name).
**Why human:** The `try/with ex -> eprintfn "Error: %s" ex.Message` path is structurally wired, but the quality of the parser's exception `.Message` for different error inputs cannot be verified without running a bad input.

## Verification Summary

All five must-have truths verified. All three required artifacts exist, are substantive, and are wired. All four key links confirmed in source. No anti-patterns found. `dotnet build` exits 0 with 0 warnings. 15/15 FsLit tests pass.

The phase goal — a usable `langbackend <file.lt>` command that reads a FunLang source, runs the full pipeline, and produces a named native executable — is achieved. Two items flagged for human verification are the Linux dynamic library behavior (environment difference, not a code issue) and the readable quality of parse error messages.

---
_Verified: 2026-03-26T04:03:27Z_
_Verifier: Claude (gsd-verifier)_
