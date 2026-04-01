---
phase: 07-gc-runtime-integration
verified: 2026-03-26T05:22:39Z
status: passed
score: 4/4 must-haves verified
gaps: []
---

# Phase 7: GC Runtime Integration Verification Report

**Phase Goal:** Every emitted binary links Boehm GC, calls GC_INIT() on startup, and allocates all heap memory through GC_malloc — v1 closure environments are migrated off the stack so closures can safely escape into heap containers; print and println builtins are available for all subsequent test programs
**Verified:** 2026-03-26T05:22:39Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #  | Truth                                                                                              | Status     | Evidence                                                                                                                          |
|----|----------------------------------------------------------------------------------------------------|------------|-----------------------------------------------------------------------------------------------------------------------------------|
| 1  | Compiled binary links Boehm GC and GC_PRINT_STATS=1 shows GC initialized before first allocation  | ✓ VERIFIED | `GC_PRINT_STATS=1 /tmp/test_gc` emits GC stats and exits 8 (correct add5(3)); `Initiating full world-stop collection` present    |
| 2  | All 15 original v1 FsLit tests pass after closure alloca-to-GC_malloc migration                   | ✓ VERIFIED | `fslit tests/compiler/`: 18/18 pass; 15 original tests included with zero regressions                                            |
| 3  | A closure escaped from its defining stack frame executes correctly                                 | ✓ VERIFIED | `add_n 5` applied to 10 returns exit code 15; `07-01-gc-closure-escape.flt` PASS                                                 |
| 4  | `print "hello"` and `println "world"` compile and write expected strings to stdout                 | ✓ VERIFIED | Binary from `let _ = print "hello" in let _ = println " world" in 0` outputs `hello world`; `07-02-print-basic.flt` + `07-03-println-basic.flt` both PASS |

**Score:** 4/4 truths verified

### Required Artifacts

| Artifact                                          | Expected                                                         | Status     | Details                                                                                   |
|---------------------------------------------------|------------------------------------------------------------------|------------|-------------------------------------------------------------------------------------------|
| `src/FunLangCompiler.Compiler/MlirIR.fs`              | MlirGlobal, ExternalFuncDecl, LlvmCallOp, LlvmCallVoidOp        | ✓ VERIFIED | All four types/cases present; MlirModule has Globals + ExternalFuncs fields (lines 18-86) |
| `src/FunLangCompiler.Compiler/Printer.fs`             | printGlobal, printExternalDecl, LlvmCallOp/Void cases, printModule order | ✓ VERIFIED | All functions present (lines 11-161); printModule emits globals → extern decls → funcs    |
| `src/FunLangCompiler.Compiler/Pipeline.fs`            | Platform-aware gcLinkFlags, -lgc threaded into clang args        | ✓ VERIFIED | `gcLinkFlags` at line 32; macOS path `-L/opt/homebrew/opt/bdw-gc/lib -lgc`; clang invocation at line 86 |
| `src/FunLangCompiler.Compiler/Elaboration.fs`         | GC_init in @main, GC_malloc for closures, print/println handlers, Globals/ExternalFuncs population | ✓ VERIFIED | `LlvmCallVoidOp("@GC_init")` prepended to @main (line 441-446); `LlvmCallOp(@GC_malloc)` in App dispatch (line 406); print/println cases at lines 363-386; externalFuncs at lines 454-458 |
| `tests/compiler/07-01-gc-closure-escape.flt`      | Escaped closure E2E test: add_n 5 applied to 10 = 15            | ✓ VERIFIED | File exists with correct test; PASS at runtime                                            |
| `tests/compiler/07-02-print-basic.flt`            | print + println combined output test                             | ✓ VERIFIED | File exists with correct test; PASS at runtime                                            |
| `tests/compiler/07-03-println-basic.flt`          | Multiple println producing separate lines                        | ✓ VERIFIED | File exists with correct test; PASS at runtime                                            |

### Key Link Verification

| From                                              | To                           | Via                                                      | Status     | Details                                                                                                      |
|---------------------------------------------------|------------------------------|----------------------------------------------------------|------------|--------------------------------------------------------------------------------------------------------------|
| `Printer.fs printModule`                          | `MlirModule.Globals + ExternalFuncs` | emit globals then extern decls before func definitions  | ✓ WIRED    | `printModule` at line 153: globalTexts → externTexts → funcTexts; conditional section emission              |
| `Pipeline.fs compile`                             | clang args                   | `gcLinkFlags` injected as `-Wno-override-module %s %s -o %s` | ✓ WIRED | Line 86; `RuntimeInformation.IsOSPlatform(OSPlatform.OSX)` for macOS path detection                         |
| `Elaboration.fs elaborateModule`                  | `MlirModule.ExternalFuncs`   | always includes GC_init, GC_malloc, printf declarations  | ✓ WIRED    | Lines 454-458; unconditional population in elaborateModule                                                   |
| `Elaboration.fs App dispatch (closure-making)`    | `LlvmCallOp(@GC_malloc)`     | replaces LlvmAllocaOp with ArithConstantOp(bytes) + LlvmCallOp | ✓ WIRED | Lines 399-409; `(ci.NumCaptures + 1) * 8` byte count calculation present                                    |
| `Elaboration.fs elaborateModule`                  | `@main entry block`          | `LlvmCallVoidOp(@GC_init)` prepended as first op        | ✓ WIRED    | Lines 441-446; `gcInitOp :: entryBlock.Body` prepend pattern                                                 |
| `Elaboration.fs App(Var("print"/"println"), String(s))` | `LlvmAddressOfOp + LlvmCallOp(@printf)` | special-case match before general App branch | ✓ WIRED | Lines 363-386; `addStringGlobal` → `LlvmAddressOfOp` → `LlvmCallOp(@printf)` → `ArithConstantOp(0)` unit return |
| `Elaboration.fs ElabEnv.Globals`                  | `MlirModule.Globals`         | accumulated string globals transferred in elaborateModule | ✓ WIRED   | Line 453: `env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value))`                 |

### Requirements Coverage

| Requirement   | Status      | Notes                                                                                        |
|---------------|-------------|----------------------------------------------------------------------------------------------|
| SC-1: Binary allocs closures + print runs without crash; GC_PRINT_STATS=1 shows GC init | ✓ SATISFIED | Verified live: `GC_PRINT_STATS=1 /tmp/test_gc` shows full GC stats output                 |
| SC-2: All 15 v1 closure FsLit tests pass after GC_malloc migration                      | ✓ SATISFIED | 18/18 total pass (includes all 15 original); no regressions                                |
| SC-3: Closure returned from function and applied after stack frame return executes correctly | ✓ SATISFIED | `add5 10 = 15` via `07-01-gc-closure-escape.flt` PASS; confirmed live execution exit code 15 |
| SC-4: `print "hello"` and `println "world"` write expected strings to stdout            | ✓ SATISFIED | `07-02-print-basic.flt` + `07-03-println-basic.flt` both PASS; live binary outputs `hello world` |

### Anti-Patterns Found

No anti-patterns found. Zero TODO/FIXME/placeholder markers in compiler source files. Build produces zero warnings.

### Human Verification Required

None. All success criteria were verified programmatically and through live binary execution.

## Summary

Phase 7 goal fully achieved. All three plans (07-01, 07-02, 07-03) delivered working implementations:

- **07-01 (IR Infrastructure):** MlirGlobal, ExternalFuncDecl, LlvmCallOp, LlvmCallVoidOp added to MlirIR.fs; Printer.fs handles all new constructs; Pipeline.fs links -lgc with macOS Homebrew path detection.
- **07-02 (GC Closure Migration):** GC_init emitted as first op in @main unconditionally; all closure environment allocations use GC_malloc (byte count = (numCaptures + 1) * 8); 16/16 tests passing including new escaped-closure test.
- **07-03 (print/println Builtins):** print and println elaborate to @printf with module-level string globals; content-based deduplication via addStringGlobal; LetPat(WildcardPat) sequencing added; 18/18 tests passing.

Live binary execution confirms: GC initializes (GC_PRINT_STATS=1 output), closures escape correctly (exit code 15), and print/println write to stdout correctly.

---
_Verified: 2026-03-26T05:22:39Z_
_Verifier: Claude (gsd-verifier)_
