# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-03-26)

**Core value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다
**Current focus:** v4.0 Type System & Error Handling — Phase 20 in progress

## Current Position

Phase: 20 of 20 (Completeness) — Complete
Plan: 02 of 2 complete
Status: Phase complete — all 20 phases done
Last activity: 2026-03-27 — Completed 20-02-PLAN.md (nested ADT patterns + raise-in-handler, 66/66 tests)

Progress: [██████████] 100% (v4.0 complete)

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
- Exception runtime: out-of-line lang_try_enter with returns_twice validated by nm; handler stack works with MLIR external linkage (19-01)
- prePassDecls ExceptionDecl dual-write: both ExnTags and TypeEnv; exception ctors have tag=exnCounter++ and arity 0/1 (19-01)
- freeVars Raise/TryWith use inline patBoundVars — MatchCompiler.boundVarsOfPattern does not exist (19-01)
- TryWith uses inline _setjmp (not lang_try_enter wrapper) — ARM64 PAC: out-of-line setjmp wrapper freed stack before longjmp; _setjmp must be called in same function as try-with expression (19-03)
- Exception payload GEP uses Ptr type (not I64) — payloads are heap strings, resolveAccessorTyped2 must use Ptr (19-03)
- Let nested-terminator fix: track blocksBeforeBind, detect isTerminator (CfBrOp/CfCondBrOp/LlvmUnreachableOp), patch last side block with body ops when inner bind ends with terminator (19-03)
- ExternalFuncDecl.Attrs: string list for MLIR function attributes; returns_twice on @_setjmp declaration (19-03)
- [v4.0 pending] Pop handler stack before handler body executes, not after (C-16)
- Raise deadVal defined via ArithConstantOp(0L) before llvm.unreachable — MLIR SSA requires all referenced names be defined (19-02)
- appendReturnIfNeeded: suppress ReturnOp when last op is LlvmUnreachableOp in elaborateModule/elaborateProgram (19-02)
- AdtCtor real tag now supplied from TypeEnv in emitCtorTest (17-01); Phase 16 placeholder replaced
- RecordCtor identity = sorted field names list; canonical ordering enforced at desugarPattern site (16-02)
- parseModule fallback: parseProgram tries parseModule, falls back to parseExpr + synthetic Module for bare-expression inputs (16-01)
- ElabEnv gains TypeEnv/RecordEnv/ExnTags; elaborateProgram is new entry point; prePassDecls scans Decl list (16-01)
- Record flat layout: GC_malloc(n*8), field at slot RecordEnv[typeName][fieldName], no tag prefix (18-01)
- RecordExpr typeName=None resolved by field-set equality match in RecordEnv; field names must be unique across types (18-01)
- SetField returns unit (i64=0) via fresh ArithConstantOp — not the stored value (18-01)
- FieldAccess/SetField search all RecordEnv entries for fieldName slot (field-name uniqueness assumed) (18-01)
- RecordUpdate op order: srcOps → overrideOps → allocOps → copyOps (prevents SSA use-before-def) (18-01)
- RecordCtor structural match: unconditional i1=1 (no tag prefix, unlike ADT) (18-02)
- ensureRecordFieldTypes: resolve fieldMap via Set superset match in RecordEnv, remap alphabetical argAccs to declaration-order slot indices via Map.find fieldName fieldMap (18-02)
- RecordPat test syntax: `{ field = var }` explicit form only — grammar does not support shorthand `{ field }` variable binding (18-02)
- Constructor-as-value: check TypeEnv Arity; arity>=1 re-elaborates as Lambda wrapping constructor (20-01)
- Closure ABI always `(ptr, i64) -> i64`; Ptr-returning closure bodies emit ptrtoint before llvm.return (20-01)
- resolveAccessorTyped Root: emit inttoptr when I64 scrutinee needs Ptr for AdtCtor match GEP (20-01)
- LlvmIntToPtrOp (llvm.inttoptr i64->ptr) and LlvmPtrToIntOp (llvm.ptrtoint ptr->i64) added to MlirIR (20-01)
- Higher-order constructor passing (apply f x = f x with f=Some) blocked: lambda params typed I64, can't recognize as closures at call site — ADT-12 scope (20-01)
- ADT-12 root cause: resolveAccessorTyped false,_ and true,v Field branches used resolveAccessor parent (cached I64) as GEP base; fix is resolveAccessorTyped parent Ptr in all Field branches (20-02)
- EXN-08 fix 1: emitDecisionTree/2 Leaf+Guard cases now conditionally skip CfBrOp(merge) when body ends with LlvmUnreachableOp (20-02)
- EXN-08 fix 2: TryWith tryBodyBlock CfBrOp patch checks predecessor existence; emits LlvmUnreachableOp for dead inner merge blocks (all handler arms noreturn) (20-02)

### Pending Todos

None. v4.0 is complete.

### Blockers/Concerns

None.

## Session Continuity

Last session: 2026-03-27
Stopped at: Completed 20-02-PLAN.md — nested ADT patterns + raise-in-handler (66/66 tests)
Resume file: None
