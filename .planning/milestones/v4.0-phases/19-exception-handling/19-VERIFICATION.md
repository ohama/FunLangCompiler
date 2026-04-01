---
phase: 19-exception-handling
verified: 2026-03-27T06:27:15Z
status: passed
score: 6/6 must-haves verified
---

# Phase 19: Exception Handling Verification Report

**Phase Goal:** 예외를 발생시키고 잡을 수 있다 — setjmp/longjmp 기반 C 런타임이 통합되고 Raise/TryWith가 네이티브 코드로 컴파일된다
**Verified:** 2026-03-27T06:27:15Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth                                                                                              | Status     | Evidence                                                                                         |
| --- | -------------------------------------------------------------------------------------------------- | ---------- | ------------------------------------------------------------------------------------------------ |
| 1   | `raise (Failure "boom")` calls `@lang_throw`, prints message and aborts; `llvm.unreachable` follows | ✓ VERIFIED | 19-01-raise-unhandled.flt passes; Elaboration.fs line 1491 emits LlvmCallVoidOp+LlvmUnreachableOp |
| 2   | `try raise (Failure "x") with | Failure msg -> string_length msg` catches and exits 1            | ✓ VERIFIED | 19-03-try-basic.flt passes; full TryWith 5-block CFG in Elaboration.fs lines 1500-1835           |
| 3   | Nested try-with correctly pushes/pops handler stack; inner handler catches without interfering     | ✓ VERIFIED | 19-04-try-nested.flt passes; blocksBeforeBind tracking + inner merge block patching               |
| 4   | `raise (ParseError "bad input")` passes payload through; handler extracts string field correctly   | ✓ VERIFIED | 19-05-try-payload.flt passes; resolveAccessorTyped2 uses Ptr type for exception payloads          |
| 5   | Unhandled exception (no matching handler) aborts with printed message, not undefined behavior      | ✓ VERIFIED | 19-06-try-fallthrough.flt passes; exn_fail block re-raises via @lang_throw; lang_throw exits 1    |
| 6   | All 63 E2E tests pass (REG-01 gate)                                                                | ✓ VERIFIED | Full suite run: 63/63 pass                                                                        |

**Score:** 6/6 truths verified

### Required Artifacts

| Artifact                                            | Expected                                          | Status      | Details                                                                      |
| --------------------------------------------------- | ------------------------------------------------- | ----------- | ---------------------------------------------------------------------------- |
| `src/FunLangCompiler.Compiler/lang_runtime.h`           | LangExnFrame struct, function declarations        | ✓ VERIFIED  | 28 lines; LangExnFrame with jmp_buf+prev; lang_try_push/exit/throw/current_exception declared |
| `src/FunLangCompiler.Compiler/lang_runtime.c`           | setjmp/longjmp handler stack implementation       | ✓ VERIFIED  | 157 lines; lang_exn_top global; lang_try_push (stack link), lang_try_exit (pop), lang_throw (_longjmp), lang_current_exception |
| `src/FunLangCompiler.Compiler/Elaboration.fs`           | Raise case, TryWith case, appendReturnIfNeeded    | ✓ VERIFIED  | 2025 lines; Raise at line 1483, TryWith at line 1500, appendReturnIfNeeded at line 1843, prePassDecls dual-write at line 1922 |
| `src/FunLangCompiler.Compiler/MlirIR.fs`               | Attrs field on ExternalFuncDecl                   | ✓ VERIFIED  | Attrs: string list at line 27; used for returns_twice on @_setjmp            |
| `src/FunLangCompiler.Compiler/Printer.fs`               | Emit attributes string for external decls         | ✓ VERIFIED  | Attrs emission at lines 25-26                                                 |
| `tests/compiler/19-01-raise-unhandled.flt`          | Unhandled raise aborts with "Fatal: unhandled exception" | ✓ VERIFIED | 7 lines; test passes                                                         |
| `tests/compiler/19-02-raise-payload.flt`            | User-defined exception raise aborts correctly     | ✓ VERIFIED  | 7 lines; test passes                                                          |
| `tests/compiler/19-03-try-basic.flt`                | Basic TryWith catch round-trip                    | ✓ VERIFIED  | 10 lines; test passes (outputs 1 = string_length "x")                        |
| `tests/compiler/19-04-try-nested.flt`               | Nested try-with scoping                           | ✓ VERIFIED  | 15 lines; test passes (outputs 15)                                            |
| `tests/compiler/19-05-try-payload.flt`              | Payload extraction in handler                     | ✓ VERIFIED  | 9 lines; test passes (outputs 9 = string_length "bad input")                  |
| `tests/compiler/19-06-try-fallthrough.flt`          | Unmatched exception re-raise                      | ✓ VERIFIED  | 11 lines; test passes (Fatal: unhandled exception, exit 1)                    |

### Key Link Verification

| From                           | To                          | Via                                          | Status     | Details                                                                      |
| ------------------------------ | --------------------------- | -------------------------------------------- | ---------- | ---------------------------------------------------------------------------- |
| `Elaboration.fs` Raise case    | `@lang_throw`               | LlvmCallVoidOp + LlvmUnreachableOp           | ✓ WIRED    | Line 1491; exnVal (ADT ptr) passed directly to @lang_throw                   |
| `Elaboration.fs` TryWith entry | `@_setjmp` + `@lang_try_push` | Inline _setjmp in caller stack frame         | ✓ WIRED    | Lines 1518-1525; GC_malloc frame, lang_try_push links it, _setjmp saves jmp_buf |
| `Elaboration.fs` exn_caught    | `@lang_current_exception`   | LlvmCallOp to retrieve exception pointer     | ✓ WIRED    | Lines 1577-1578; exn_caught pops handler then calls lang_current_exception    |
| `Elaboration.fs` exn_fail      | `@lang_throw`               | Re-raise via LlvmCallVoidOp + LlvmUnreachableOp | ✓ WIRED | Lines 1827-1828; unmatched exception propagated to outer handler              |
| `lang_runtime.c` lang_throw    | `_longjmp`                  | _longjmp to top handler frame's buf          | ✓ WIRED    | Line 144; uses _longjmp to match inline _setjmp (no signal mask overhead)    |
| `prePassDecls` ExceptionDecl   | `TypeEnv`                   | Dual-write to ExnTags AND TypeEnv            | ✓ WIRED    | Lines 1922-1927; arity derived from dataTypeOpt; emitCtorTest can find exception tags |
| `appendReturnIfNeeded`         | `elaborateModule`           | Applied to entry block and last side block   | ✓ WIRED    | Lines 1854, 1858; prevents dead ReturnOp after llvm.unreachable               |
| `appendReturnIfNeeded`         | `elaborateProgram`          | Applied to entry block and last side block   | ✓ WIRED    | Lines 1985, 1989; same guard in program-level elaboration                     |
| `@_setjmp` ExternalFuncDecl    | `returns_twice` Attrs       | Attrs = ["returns_twice"] in externalFuncs   | ✓ WIRED    | Lines 1892, 2023; prevents LLVM misoptimization of _setjmp call site          |

### Requirements Coverage

| Requirement | Description                                       | Status      | Blocking Issue |
| ----------- | ------------------------------------------------- | ----------- | -------------- |
| EXN-01      | raise calls @lang_throw + llvm.unreachable        | ✓ SATISFIED | None           |
| EXN-02      | Unhandled exception aborts with error message     | ✓ SATISFIED | None           |
| EXN-03      | TryWith catches exception and executes handler    | ✓ SATISFIED | None           |
| EXN-04      | Exception payload extracted correctly in handler  | ✓ SATISFIED | None           |
| EXN-05      | Nested try-with push/pop handler stack correctly  | ✓ SATISFIED | None           |
| EXN-06      | All existing E2E tests continue to pass           | ✓ SATISFIED | None (63/63)   |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
| ---- | ---- | ------- | -------- | ------ |
| None | —    | —       | —        | —      |

No anti-patterns found. No TODO/FIXME, no stub returns, no placeholder content in any phase-19 files.

### Human Verification Required

None required. All success criteria are mechanically verifiable and verified.

### Gaps Summary

No gaps. All 6 success criteria pass against the actual codebase.

---

## Verification Notes

**Test runner methodology:** Python-based flt runner that mimics the `%S`/`%input` substitution from the .flt command format, with correct stderr handling (captured only when command contains `2>&1`). 63/63 pass.

**ARM64 PAC deviation caught in 19-03:** The plan originally used an out-of-line `lang_try_enter` wrapper for setjmp. This failed on macOS ARM64 because longjmp cannot return to a freed stack frame across pointer authentication. The fix (inline `_setjmp` + `lang_try_push` for stack management only) is implemented and verified: `_setjmp` is called directly in the generated MLIR function, keeping the jmp_buf in the live stack frame.

**Pre-existing test note:** `08-02-string-equality.flt` emits a spurious `mlir-opt failed` message to stderr during compilation; the binary still produces correct output and the test passes. This is a pre-phase-19 condition unrelated to exception handling.

---

_Verified: 2026-03-27T06:27:15Z_
_Verifier: Claude (gsd-verifier)_
