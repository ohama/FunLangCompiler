---
phase: 08-strings
plan: 01
subsystem: compiler-ir
tags: [mlir, strings, gc, string-struct, getelementptr, lang_runtime, c-runtime, fsharp, builtins]

# Dependency graph
requires:
  - phase: 07-gc-runtime-integration/07-03
    provides: print/println builtins, string globals, @printf ExternalFuncs, LetPat elaboration
  - phase: 07-gc-runtime-integration/07-01
    provides: GC_malloc, LlvmCallOp, LlvmCallVoidOp, ExternalFuncDecl pattern

provides:
  - LlvmGEPStructOp MlirOp case for typed struct field GEP (llvm.getelementptr inbounds with !llvm.struct<(i64, ptr)>)
  - String(s) elaboration to GC_malloc'd {i64 length, ptr data} header struct
  - string_length builtin: GEP field 0 + llvm.load → i64
  - General print/println accepting string struct Ptr variables (GEP field 1 + printf)
  - lang_runtime.c with lang_string_concat, lang_to_string_int, lang_to_string_bool
  - Pipeline compiles lang_runtime.c to .runtime.o and links it into every binary
  - @strcmp, @lang_string_concat, @lang_to_string_int, @lang_to_string_bool declared in ExternalFuncs
affects:
  - 08-02 (string_concat, to_string, string equality — all build on this infrastructure)
  - 09+ (string printing, struct GEP pattern reused for tuples)

# Tech tracking
tech-stack:
  added:
    - lang_runtime.c (C runtime helper file compiled alongside every binary)
  patterns:
    - String struct layout: {i64 length at field 0, ptr data at field 1} — GC_malloc(16)
    - LlvmGEPStructOp always emits !llvm.struct<(i64, ptr)> type annotation (hardcoded for Phase 8)
    - C runtime file compiled to temp .runtime.o via clang -c before final link step
    - Print/println: static literal fast path takes priority; general Ptr case follows
    - elaborateStringLiteral: 8-op sequence (arith.constant + GC_malloc + 2x GEP + 2x constant + 2x store)

key-files:
  created:
    - src/FunLangCompiler.Compiler/lang_runtime.c
    - tests/compiler/08-01-string-literal.flt
  modified:
    - src/FunLangCompiler.Compiler/MlirIR.fs
    - src/FunLangCompiler.Compiler/Printer.fs
    - src/FunLangCompiler.Compiler/Elaboration.fs
    - src/FunLangCompiler.Compiler/Pipeline.fs

key-decisions:
  - "LlvmGEPStructOp hardcodes !llvm.struct<(i64, ptr)> — only string structs use struct GEP in Phase 8; generic StructType deferred to Phase 9 tuples"
  - "lang_runtime.c compiled per-invocation to temp .runtime.o (not pre-compiled) — avoids binary distribution issue, consistent with temp-file pipeline pattern"
  - "elaborateStringLiteral placed after freshName/freshLabel to avoid forward-reference — F# requires top-down binding order"
  - "gcIncludeFlag added for macOS bdw-gc header path (-I/opt/homebrew/opt/bdw-gc/include)"

patterns-established:
  - "String struct GEP pattern: LlvmGEPStructOp(result, ptr, 0) for length, LlvmGEPStructOp(result, ptr, 1) for data"
  - "Runtime C helpers: operations requiring dynamic pointer arithmetic implemented in lang_runtime.c, declared as ExternalFuncs"

# Metrics
duration: 6min
completed: 2026-03-26
---

# Phase 8 Plan 01: String Literal Infrastructure Summary

**GC_malloc'd {i64 length, ptr data} string structs with LlvmGEPStructOp, lang_runtime.c C helpers, and string_length builtin — `string_length "hello"` exits with 5**

## Performance

- **Duration:** 6 min
- **Started:** 2026-03-26T06:19:46Z
- **Completed:** 2026-03-26T06:25:51Z
- **Tasks:** 5
- **Files modified:** 6

## Accomplishments
- Added LlvmGEPStructOp to MlirIR and Printer for typed struct field GEP (`llvm.getelementptr inbounds %ptr[0, N] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>`)
- Created lang_runtime.c with `lang_string_concat`, `lang_to_string_int`, `lang_to_string_bool` using Boehm GC
- Updated Pipeline to compile lang_runtime.c to a temp `.runtime.o` and link it into every binary
- Elaborated `String(s)` AST nodes to 8-op sequences allocating a GC_malloc'd header struct with length at field 0 and static data ptr at field 1
- Implemented `string_length` builtin via GEP field 0 + llvm.load; all 19 tests pass

## Task Commits

1. **Task 1: Add LlvmGEPStructOp to MlirIR and Printer** - `6d89f4b` (feat)
2. **Task 2: Create lang_runtime.c with string builtins** - `e02d9ec` (feat)
3. **Task 3: Update Pipeline.fs to compile and link lang_runtime.c** - `04e4b73` (feat)
4. **Task 4: Elaborate String node and string_length builtin** - `a04c50a` (feat)
5. **Task 5: Write STR-01 + STR-03 FsLit test** - `bb1539a` (test)

## Files Created/Modified
- `src/FunLangCompiler.Compiler/MlirIR.fs` - Added LlvmGEPStructOp case to MlirOp DU
- `src/FunLangCompiler.Compiler/Printer.fs` - Added printOp case for LlvmGEPStructOp
- `src/FunLangCompiler.Compiler/lang_runtime.c` - New C runtime with string concat/to_string helpers
- `src/FunLangCompiler.Compiler/Pipeline.fs` - Compile runtime C file to temp .o, include in link
- `src/FunLangCompiler.Compiler/Elaboration.fs` - String elaboration, string_length, general print/println, new ExternalFuncs
- `tests/compiler/08-01-string-literal.flt` - E2E test: `string_length "hello"` exits with 5

## Decisions Made
- LlvmGEPStructOp hardcodes `!llvm.struct<(i64, ptr)>` in the Printer — only string structs need typed struct GEP in Phase 8; a generic StructType to MlirType can wait until Phase 9 tuples need it
- lang_runtime.c compiled fresh each time to a temp `.runtime.o` alongside the other temp files — avoids pre-compilation and stays consistent with the existing temp-file pipeline
- `elaborateStringLiteral` placed after `freshLabel` (not before `freshName`) to respect F# top-down binding order

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- String struct foundation complete; plan 08-02 can implement string_concat, to_string, and string equality (strcmp)
- lang_runtime.c already has all three runtime functions; 08-02 only needs Elaboration cases to call them
- General print/println now accepts string struct variables — enables `print (to_string 42)` pattern in 08-02

---
*Phase: 08-strings*
*Completed: 2026-03-26*
