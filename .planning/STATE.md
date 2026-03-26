# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v2.0 — Phase 11: Full Pattern Matching (next)

## Current Position

Phase: 10 of 11 (Lists) — Complete
Plan: 1 of 1 in phase 10
Status: Plan 10-01 complete
Last activity: 2026-03-26 — Completed 10-01-PLAN.md (lists: null-ptr EmptyList, GC_malloc cons cells, foldBack desugaring, null-check match, 27 tests passing)

Progress: [████████████████████] 10/11 phases started (Phase 10 Lists plan 01 complete)

## Performance Metrics

**Velocity:**
- Total plans completed: 11 (v1.0 ALL COMPLETE)
- Average duration: ~2.3 min
- Total execution time: ~0.38 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-mlirir-foundation | 3 | ~5 min | ~1.7 min |
| 02-scalar-codegen-via-mlirir | 2 | ~4 min | ~2 min |
| 03-booleans-comparisons-control-flow | 2 | ~4 min | ~2 min |
| 04-known-functions-via-elaboration | 1 | ~3 min | ~3 min |
| 05-closures-via-elaboration | 2 | ~9 min | ~4.5 min |
| 06-cli | 1 | ~2 min | ~2 min |

**Recent Trend:**
- Last 11 plans: 01-01 (2 min), 01-02 (1 min), 01-03 (2 min), 02-01 (2 min), 02-02 (2 min), 03-01 (2 min), 03-02 (2 min), 04-01 (3 min), 05-01 (6 min), 05-02 (3 min), 06-01 (2 min)
- Trend: Stable ~2-3 min/plan; v2 plans expected slightly longer (GC integration, pattern matching)

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- [v2.0 GC]: Use Boehm GC (libgc) — conservative collector, no IR changes, `-lgc` link only
- [v2.0 GC]: GC_INIT() emitted unconditionally as first op in @main (C-8 prevention)
- [v2.0 GC]: All heap allocation through GC_malloc without exception (C-9 prevention)
- [v2.0 GC]: Migrate closure environments from llvm.alloca to GC_malloc in Phase 7 BEFORE any heap type codegen (C-7 prevention)
- [v2.0 Strings]: String layout is {i64 length, ptr data} two-field heap struct; byte array in llvm.mlir.global (static) + GC_malloc'd header
- [v2.0 Lists]: EmptyList = llvm.mlir.zero (null ptr); Cons = GC_malloc(16) two-pointer cons cell
- [v2.0 PatMatch]: Match compiles to sequential cf.cond_br chain (same mechanism as if-else); always emit @lang_match_failure terminal (C-10 prevention)
- [06-01]: -o flag is optional; Path.GetFileNameWithoutExtension(inputPath) auto-derives output binary name
- [07-01]: GC symbol is @GC_init (lowercase i) — GC_INIT is a C macro, not a linkable symbol
- [07-01]: LlvmCallVoidOp has no result field — void calls must not consume an SSA name counter slot
- [07-01]: printModule order: globals -> extern decls -> funcs (required by MLIR 20)
- [07-02]: Closure env byte count = (numCaptures + 1) * 8 — slot 0 is fn ptr, slots 1..N are captures
- [07-02]: GC_init and GC_malloc always declared in ExternalFuncs unconditionally
- [07-03]: print/println special-cased as App(Var("print"|"println"), String(s)) before general App branch
- [07-03]: LetPat(WildcardPat) added to support "let _ = expr in body" sequencing idiom
- [07-03]: @printf always declared in ExternalFuncs unconditionally alongside GC_init and GC_malloc
- [08-01]: LlvmGEPStructOp hardcodes !llvm.struct<(i64, ptr)> — generic StructType deferred to Phase 9 tuples
- [08-01]: lang_runtime.c compiled per-invocation to temp .runtime.o — consistent with temp-file pipeline pattern
- [08-01]: @strcmp, @lang_string_concat, @lang_to_string_int, @lang_to_string_bool always declared in ExternalFuncs unconditionally
- [08-01]: gcIncludeFlag added to Pipeline for macOS bdw-gc header path when compiling lang_runtime.c
- [08-02]: ArithExtuIOp (arith.extui) added to promote I1 to I64 before C ABI calls — @lang_to_string_bool takes int64_t, not i1
- [08-02]: to_string bool path: elaborate arg (I1), ArithExtuIOp to I64, call @lang_to_string_bool with I64
- [08-02]: FsLit tests must be single-line — LangThree parser does not accept newlines between sub-expressions
- [08-02]: string_concat nested App matched before general App: App(App(Var("string_concat"),...),...)  ordered before general App dispatch
- [09-01]: typeOfPat heuristic for tuple destructuring: TuplePat sub-pattern → load as Ptr; VarPat/WildcardPat → load as I64
- [09-01]: Sequential let (x,inner) = ... in let (y,z) = inner requires type info to load inner as Ptr; use inline nested TuplePat form instead
- [09-01]: Match(single TuplePat arm) desugars to LetPat(TuplePat) at elaboration time — no full match compiler needed for TUP-03
- [10-01]: LlvmNullOp result.Type = Ptr, prints as llvm.mlir.zero : !llvm.ptr (not arith.constant 0 + cast)
- [10-01]: LlvmIcmpOp result.Type = I1, prints as llvm.icmp "pred" %lhs, %rhs : !llvm.ptr (llvm dialect, not arith.cmpi)
- [10-01]: isListParamBody: AST pre-scan for LetRec list param detection — Match scrutinee=param with EmptyListPat/ConsPat arms → paramType = Ptr
- [10-01]: Phase 10 Match limited to two-arm EmptyListPat+ConsPat pattern; other match patterns failwithf until Phase 11
- [10-01]: Head type hardcoded I64 for integer-list scope; tail type always Ptr; head stored at cellPtr base (no GEP needed for slot 0)

### Pending Todos

None.

### Blockers/Concerns

- [Phase 7, RESOLVED]: LlvmAllocaOp migration — only one usage site in App dispatch; fully migrated in 07-02
- [Phase 7, RESOLVED]: macOS -L flag for bdw-gc added in 07-01 via RuntimeInformation.IsOSPlatform

## Session Continuity

Last session: 2026-03-26
Stopped at: Completed 10-01-PLAN.md (lists: null-ptr EmptyList, GC_malloc cons cells, null-check match, 27 tests passing)
Resume file: None
