# Roadmap: LangBackend v4.0 — Type System & Error Handling

## Milestones

- ✅ **v1.0 Core Compiler** — Phases 1-6 (shipped 2026-03-26)
- ✅ **v2.0 Data Types & Pattern Matching** — Phases 7-11 (shipped 2026-03-26)
- ✅ **v3.0 Language Completeness** — Phases 12-15 (shipped 2026-03-26)
- ✅ **v4.0 Type System & Error Handling** — Phases 16-20 (shipped 2026-03-27)

## Phases

<details>
<summary>✅ v1.0 Core Compiler (Phases 1-6) — SHIPPED 2026-03-26</summary>

See: `.planning/milestones/v1.0-phases/` for phase plans and summaries.

Delivered: MlirIR DU, Elaboration pass, scalar codegen, booleans/control flow, known functions, closures, CLI. 15 E2E tests.

</details>

<details>
<summary>✅ v2.0 Data Types & Pattern Matching (Phases 7-11) — SHIPPED 2026-03-26</summary>

See: `.planning/milestones/v2.0-phases/` for phase plans and summaries.

Delivered: Boehm GC, String/Tuple/List heap types, Jacobs pattern matching decision tree. 34 E2E tests.

</details>

<details>
<summary>✅ v3.0 Language Completeness (Phases 12-15) — SHIPPED 2026-03-26</summary>

See: `.planning/milestones/v3.0-ROADMAP.md` for full phase details.

Delivered: Modulo/Char/Pipe/Compose operators, when guards, OrPat, CharConst patterns, failwith/string/char builtins, Range syntax. 45 E2E tests.

</details>

---

### v4.0 Type System & Error Handling (In Progress)

**Milestone Goal:** ADT/GADT(discriminated unions), Records(mutable fields), Exception handling(setjmp/longjmp)을 컴파일하여 LangThree의 타입 시스템 기능 대부분을 네이티브 코드로 지원한다.

- [x] **Phase 16: Environment Infrastructure** — TypeEnv/RecordEnv/ExnTags 구성 + MatchCompiler CtorTag 확장
- [x] **Phase 17: ADT Construction & Pattern Matching** — Constructor elaboration + ConstructorPat round-trip ✓
- [x] **Phase 18: Records** — RecordExpr/FieldAccess/RecordUpdate/SetField/RecordPat elaboration ✓
- [x] **Phase 19: Exception Handling** — setjmp/longjmp C runtime + Raise/TryWith elaboration ✓
- [x] **Phase 20: Completeness** — First-class constructors, nested ADT patterns, exception re-raise/in-handler ✓

---

### Phase 16: Environment Infrastructure

**Goal**: ElabEnv가 TypeDecl/RecordDecl/ExceptionDecl 선행 처리로 생성된 TypeEnv/RecordEnv/ExnTags를 보유하고, MatchCompiler가 ADT/Record 패턴을 인식한다 — IR은 아직 생성하지 않는다
**Depends on**: Phase 15
**Requirements**: ADT-01, ADT-02, ADT-03, ADT-04, REC-01
**Success Criteria** (what must be TRUE):
  1. `elaborateProgram` entry point processes all TypeDecl/RecordDecl/ExceptionDecl before any expression elaboration; constructor names map to tag indices in TypeEnv
  2. ExceptionDecl constructors are registered in TypeEnv using the same tag-index mechanism as ADT constructors
  3. MatchCompiler.CtorTag includes `AdtCtor` and `RecordCtor` variants; `desugarPattern` dispatches ConstructorPat and RecordPat without hitting `failwith`
  4. All 45 existing E2E tests continue to pass (REG-01 gate)
**Plans**: 2 plans

Plans:
- [x] 16-01-PLAN.md — elaborateProgram pre-pass: ElabEnv extension + prePassDecls + Program.fs entry point switch (ADT-01, ADT-02, ADT-04, REC-01)
- [x] 16-02-PLAN.md — MatchCompiler CtorTag extension: AdtCtor/RecordCtor variants + desugarPattern dispatch (ADT-03)

---

### Phase 17: ADT Construction & Pattern Matching

**Goal**: ADT 값을 생성하고 패턴 매칭으로 소비하는 완전한 라운드트립이 가능하다 — nullary/unary/multi-arg constructors와 ConstructorPat이 네이티브 코드로 컴파일된다
**Depends on**: Phase 16
**Requirements**: ADT-05, ADT-06, ADT-07, ADT-08, ADT-09, ADT-10
**Success Criteria** (what must be TRUE):
  1. `type Color = Red | Green | Blue` — nullary constructor `Red` compiles to a 16-byte `{tag=0, null}` heap block; `match c with | Red -> 1 | _ -> 0` exits with 1
  2. `type Option = None | Some of int` — `Some 42` compiles to `{tag=1, ptr->42}`; `match (Some 42) with | Some n -> n | None -> 0` exits with 42
  3. `type Pair = Pair of int * int` — multi-arg `Pair(3, 4)` wraps args as tuple payload; pattern match extracts both fields correctly
  4. GADT constructor compiles identically to regular ADT constructor after frontend type-checking (backend treats as plain ADT)
  5. All 45 existing E2E tests continue to pass (REG-01 gate)
**Plans**: 2 plans

Plans:
- [x] 17-01-PLAN.md — Constructor elaboration: nullary/unary/multi-arg ADT value construction + 3 E2E tests (ADT-05, ADT-06, ADT-07)
- [x] 17-02-PLAN.md — ConstructorPat elaboration: tag comparison + payload GEP + sub-pattern dispatch + resolveAccessor Ptr-retype guard + 3 E2E tests (ADT-08, ADT-09, ADT-10)

---

### Phase 18: Records

**Goal**: Record 값을 생성, 접근, 갱신, 변이하고 패턴 매칭으로 소비할 수 있다
**Depends on**: Phase 16 (RecordEnv must be populated)
**Requirements**: REC-02, REC-03, REC-04, REC-05, REC-06
**Success Criteria** (what must be TRUE):
  1. `type Point = { x: int; y: int }` + `let p = { x = 3; y = 4 }` compiles to a GC_malloc'd 2-field struct; `p.x` exits with 3
  2. `let p2 = { p with y = 10 }` allocates a new block, copies `x`, writes `y = 10`; `p.x` and `p2.x` are independent (not aliased)
  3. `let r = { mutable v = 0 }` + `r.v <- 42` sets the field in-place; subsequent read exits with 42; `r.v <- ...` returns unit
  4. `match p with | { x = 3; y } -> y | _ -> 0` exits with 4 (RecordPat field extraction)
  5. All 45 existing E2E tests continue to pass (REG-01 gate)
**Plans**: 2 plans

Plans:
- [x] 18-01-PLAN.md — RecordExpr/FieldAccess/RecordUpdate/SetField elaboration + freeVars + 4 E2E tests (REC-02, REC-03, REC-04, REC-05)
- [x] 18-02-PLAN.md — RecordPat elaboration: fill RecordCtor stubs + ensureRecordFieldTypes slot remapping + 2 E2E tests (REC-06)

---

### Phase 19: Exception Handling

**Goal**: 예외를 발생시키고 잡을 수 있다 — setjmp/longjmp 기반 C 런타임이 통합되고 Raise/TryWith가 네이티브 코드로 컴파일된다
**Depends on**: Phase 17 (exception values are ADT DataValues), Phase 18 (handlers may match record payloads)
**Requirements**: EXN-01, EXN-02, EXN-03, EXN-04, EXN-05, EXN-06
**Success Criteria** (what must be TRUE):
  1. `raise (Failure "boom")` calls `@lang_throw`, prints the exception message and aborts; `llvm.unreachable` follows the call
  2. `try raise (Failure "x") with | Failure msg -> string_length msg` catches the exception and exits with 1 (basic TryWith round-trip)
  3. Nested `try-with` blocks correctly push/pop the handler stack; the inner handler catches inner exceptions without interfering with the outer handler
  4. `raise (ParseError "bad input")` passes the payload through `lang_throw`/`lang_current_exception`; the handler extracts the string field correctly
  5. An unhandled exception (no matching handler) aborts with a printed message rather than undefined behavior
  6. All 57 existing E2E tests continue to pass (REG-01 gate)
**Plans**: 3 plans

Plans:
- [x] 19-01-PLAN.md — C runtime extension + Elaboration.fs scaffolding: lang_runtime.h, lang_try_enter/exit/throw, external func decls, prePassDecls fix, freeVars (EXN-01)
- [x] 19-02-PLAN.md — Raise elaboration: @lang_throw call + llvm.unreachable + 2 E2E tests (EXN-02, EXN-06 partial)
- [x] 19-03-PLAN.md — TryWith elaboration: setjmp branch + handler decision tree + merge block + 4 E2E tests (EXN-03, EXN-04, EXN-05, EXN-06)

---

### Phase 20: Completeness

**Goal**: First-class constructors, nested ADT pattern matching, exception re-raise, and handler-internal exceptions all work correctly, closing the remaining edge cases
**Depends on**: Phase 17 (ADT), Phase 19 (exceptions)
**Requirements**: ADT-11, ADT-12, EXN-07, EXN-08
**Success Criteria** (what must be TRUE):
  1. `List.map Some [1; 2; 3]` compiles — unary constructor `Some` is wrapped as a lambda and passed as a first-class function
  2. `match tree with | Node(Node(Leaf, v, Leaf), root, _) -> root + v | _ -> 0` — nested constructor pattern (multi-level GEP chains) extracts values at depth 2+
  3. Handler miss propagates the exception: when no arm matches, `lang_throw` is called from the `Fail` branch, re-raising to the outer handler or aborting
  4. An exception raised inside a handler body is handled correctly — the inner `try-with` frame does not re-enter the current handler
  5. All 63 existing E2E tests continue to pass (REG-01 gate)
**Plans**: 3 plans

Plans:
- [x] 20-01-PLAN.md — First-class constructors: arity-aware Constructor(name, None, _) wraps unary+ as Lambda closure (ADT-11)
- [x] 20-02-PLAN.md — Nested ADT pattern Ptr pre-load + raise-in-handler terminator fix (ADT-12, EXN-07 verified, EXN-08)
- [x] 20-03-PLAN.md — Gap closure: I64 closure dispatch + general App case for HOF constructor passing (ADT-11 complete)

---

## Progress

**Execution Order:**
16 -> 17 -> 18 -> 19 -> 20 (17 and 18 are independent after 16; 19 requires both 17 and 18; 20 requires 17 and 19)

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1-6. Core Compiler | v1.0 | 11/11 | Complete | 2026-03-26 |
| 7-11. Data Types & Pattern Matching | v2.0 | 9/9 | Complete | 2026-03-26 |
| 12-15. Language Completeness | v3.0 | 5/5 | Complete | 2026-03-26 |
| 16. Environment Infrastructure | v4.0 | 2/2 | Complete | 2026-03-27 |
| 17. ADT Construction & Pattern Matching | v4.0 | 2/2 | Complete | 2026-03-27 |
| 18. Records | v4.0 | 2/2 | Complete | 2026-03-27 |
| 19. Exception Handling | v4.0 | 3/3 | Complete | 2026-03-27 |
| 20. Completeness | v4.0 | 3/3 | Complete | 2026-03-27 |

---
*Created: 2026-03-26 for v4.0 milestone*
*Last updated: 2026-03-27*
