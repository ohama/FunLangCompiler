# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v4.0 Type System & Error Handling — Phase 18: Records

## Current Position

Phase: 18 of 20 (Records)
Plan: 01 of 2 complete
Status: In progress
Last activity: 2026-03-27 — Completed 18-01-PLAN.md (RecordExpr/FieldAccess/RecordUpdate/SetField, 55/55 tests)

Progress: [████░░░░░░] 40% (v4.0, 2/5 phases complete; Phase 18 plan 1/2 done)

## Performance Metrics

**Velocity:**
- v1.0: 11 plans, 6 phases — ~0.38 hours
- v2.0: 9 plans, 5 phases — 34 FsLit tests, 1,861 LOC
- v3.0: 5 plans, 4 phases — 45 FsLit tests
- Average duration: ~2.3 min/plan

## Accumulated Context

### Key Decisions

- MLIR text format direct generation (no P/Invoke)
- MlirIR as typed internal IR (F# DU)
- Flat closure struct {fn_ptr, env_fields}, caller-allocates
- Boehm GC (libgc) — conservative collector, `-lgc` link
- Uniform boxed representation (all heap types as ptr)
- Sequential cf.cond_br match compilation (Jacobs decision tree)
- @lang_match_failure fallback for non-exhaustive match
- PipeRight/ComposeRight/ComposeLeft are elaboration-time desugar only, no new MLIR ops (12-02)
- OrPat expanded in Elaboration.fs before MatchCompiler; char is already i64 (13-01, 14-01)
- Range -> lang_range(start, stop, step): C runtime returns Phase-10-compatible cons list (15)
- FsLit test inputs must not have trailing newlines — indent-sensitive lexer (15)
- ADT layout: 16-byte GC_malloc block, slot 0 = i64 tag (LlvmGEPLinearOp), slot 1 = payload stored directly (I64 or Ptr) — live in Elaboration.fs (17-01)
- Nullary ctors allocate real 16-byte block with tag and null at slot 1 — null encoding reserved for NilCtor (17-01)
- ADT payload stored directly at slot 1 (no extra heap indirection) — I64 and Ptr both work; resolveAccessor default I64 load correct for int payloads (17-01)
- AdtCtor argAccessors offset by +1: slot 0 = tag, payload at slots 1..N; Field(selAcc, i+1) in MatchCompiler.splitClauses (17-02)
- Ptr-retype guard in resolveAccessor Field case: when parent cached as I64 but GEP needed, re-resolve as Ptr via resolveAccessorTyped (17-02)
- parseProgram uses LangThree.IndentFilter to produce INDENT/DEDENT tokens — raw NEWLINE tokens from Lexer.tokenize are not accepted by parser grammar (17-02)
- [v4.0 pending] lang_try_enter must call setjmp via static inline/macro — not out-of-line C function (C-15)
- [v4.0 pending] Pop handler stack before handler body executes, not after (C-16)
- AdtCtor real tag now supplied from TypeEnv in emitCtorTest (17-01); Phase 16 placeholder replaced
- RecordCtor identity = sorted field names list; canonical ordering enforced at desugarPattern site (16-02)
- parseModule fallback: parseProgram tries parseModule, falls back to parseExpr + synthetic Module for bare-expression inputs (16-01)
- ElabEnv gains TypeEnv/RecordEnv/ExnTags; elaborateProgram is new entry point; prePassDecls scans Decl list (16-01)
- Record flat layout: GC_malloc(n*8), field at slot RecordEnv[typeName][fieldName], no tag prefix (18-01)
- RecordExpr typeName=None resolved by field-set equality match in RecordEnv; field names must be unique across types (18-01)
- SetField returns unit (i64=0) via fresh ArithConstantOp — not the stored value (18-01)
- FieldAccess/SetField search all RecordEnv entries for fieldName slot (field-name uniqueness assumed) (18-01)
- RecordUpdate op order: srcOps → overrideOps → allocOps → copyOps (prevents SSA use-before-def) (18-01)

### Pending Todos

None.

### Blockers/Concerns

- Phase 19 (Exception Handling): validate `static inline setjmp` + clang `-O2` interaction with MLIR-emitted LLVM IR before full TryWith codegen. Write standalone proof-of-concept first.
- Phase 18-02 (RecordPat): `argAccessors.[i]` from MatchCompiler uses alphabetical sort order; RecordEnv uses declaration order — `ensureRecordFieldTypes` helper must remap via declaration-order slot lookup (RESEARCH.md Approach A).

## Session Continuity

Last session: 2026-03-27T04:15:14Z
Stopped at: Completed 18-01-PLAN.md — Phase 18 plan 1/2 done
Resume file: None
