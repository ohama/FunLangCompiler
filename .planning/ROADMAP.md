# Roadmap: FunLangCompiler — v23.0 FunLang v14.0 Sync + Prelude Unification

## Overview

v23.0 fixes a critical string indexing bug (Issue #22) blocking FunLexYacc, then synchronizes the FunLang submodule to v14.0 and unifies the Prelude with FunLang's canonical sources. The result: 12 Prelude files share source with FunLang, compiler-only extensions are preserved, and annotated multi-param style compiles correctly.

## Milestones

- ✅ **v1.0–v22.0** — Phases 1–93 (shipped)
- 🚧 **v23.0 FunLang v14.0 Sync + Prelude Unification** — Phases 94–97 (in progress)

## Phases

<details>
<summary>✅ v1.0–v22.0 (Phases 1–93) — SHIPPED</summary>

See MILESTONES.md for full history. Last phase: 93 (v13.1).

</details>

### 🚧 v23.0 FunLang v14.0 Sync + Prelude Unification (In Progress)

**Milestone Goal:** Fix string parameter indexing bug, sync FunLang submodule to v14.0, and unify all 12 Prelude files with FunLang's canonical sources while preserving compiler-only extensions.

---

#### Phase 94: String Parameter Indexing Bug Fix

**Goal**: `s.[i]` returns the correct character value when `s` is a function parameter (Issue #22)
**Depends on**: Nothing (standalone bug fix)
**Requirements**: BUG-01
**Success Criteria** (what must be TRUE):
  1. `s.[i]` on a string received as a function parameter returns the same value as on a string bound by `let`
  2. FunLexYacc programs that use string parameter indexing compile and produce correct output
  3. An E2E test exercises the bug scenario and passes

**Plans**: 1 plan

Plans:
- [ ] 94-01-PLAN.md — Fix isPtrParamBody + StringVars heuristics, add E2E test

---

#### Phase 95: FunLang v14.0 Type System Sync

**Goal**: ElabHelpers.fs recognizes all FunLang v14.0 collection types with no build warnings or silent bugs; FunLang submodule committed at v14.0 (8da0af2)
**Depends on**: Phase 94
**Requirements**: TYPE-01, TYPE-02, SUB-01
**Success Criteria** (what must be TRUE):
  1. `dotnet build` produces zero incomplete-pattern-match warnings for THashSet/TQueue/TMutableList/TStringBuilder
  2. `detectCollectionKind` handles all v14.0 collection union cases without falling through to default
  3. `git submodule status` shows FunLang pinned at 8da0af2

**Plans**: 1 plan

Plans:
- [ ] 95-01-PLAN.md — Patch ElabHelpers.fs typeNeedsPtr + detectCollectionKind; commit submodule

---

#### Phase 96: Prelude Trivial Sync (9 files)

**Goal**: 7 copy-only Prelude files and 2 copy+append files are byte-for-byte identical to FunLang v14.0 Prelude (plus compiler-only additions)
**Depends on**: Phase 95
**Requirements**: PRE-01, PRE-02, PRE-03
**Success Criteria** (what must be TRUE):
  1. Array, Char, Hashtable, Int, Queue, String, StringBuilder Prelude files are identical to FunLang v14.0 originals
  2. HashSet.fun contains all FunLang content plus `keys` and `toList` compiler-only functions
  3. MutableList.fun contains all FunLang content plus `toList` compiler-only function
  4. All existing E2E tests pass after the file replacements

**Plans**: TBD

Plans:
- [ ] 96-01: Copy 7 trivial files; copy+append HashSet and MutableList

---

#### Phase 97: Prelude Manual Merge (5 files)

**Goal**: Core, List, Option, Result, and Typeclass Prelude files use FunLang v14.0 multi-param style while preserving compiler-only functions; all 260+ E2E tests pass
**Depends on**: Phase 96
**Requirements**: PRE-04, PRE-05, PRE-06, PRE-07, PRE-08
**Success Criteria** (what must be TRUE):
  1. Core.fun includes `char_to_int` and `int_to_char` compiler-only functions alongside the FunLang v14.0 content
  2. Option.fun compiles with multi-param style (`let f (x:T) (y:U) = ...`) and all existing Option E2E tests pass
  3. List.fun, Result.fun, and Typeclass.fun compile with multi-param style; all related E2E tests pass
  4. `dotnet run -- tests/compiler/` reports 260+ tests passing with no regressions

**Plans**: TBD

Plans:
- [ ] 97-01: Merge Core.fun + Option.fun (validate multi-param style first)
- [ ] 97-02: Merge List.fun + Result.fun + Typeclass.fun; full E2E pass

---

#### Phase 98: --trace Compiler Flag for Function Entry Tracing

**Goal**: `fnc --trace` 플래그를 켜면 생성된 바이너리의 모든 func.func 함수 진입 시 함수명을 stderr에 자동 출력; 플래그 없이 컴파일하면 트레이스 코드 없음 (zero overhead)
**Depends on**: Phase 94
**Requirements**: DEBUG-01
**Success Criteria** (what must be TRUE):
  1. `fnc --trace hello.fun -o hello` 로 컴파일한 바이너리 실행 시 stderr에 각 함수 진입이 `[TRACE] @funcName` 형태로 출력됨
  2. `fnc hello.fun -o hello` (플래그 없음)로 컴파일한 바이너리는 트레이스 출력 없음
  3. 기존 263+ E2E 테스트가 --trace 없이 컴파일 시 모두 통과 (regression 없음)
  4. FunLexYacc를 --trace로 컴파일하여 SIGSEGV 크래시 직전 마지막 함수를 식별할 수 있음

**Plans**: 0 plans

Plans:
- [ ] TBD (run /gsd:plan-phase 98 to break down)

**Details:**
런타임 SIGSEGV 등 디버깅 시 크래시 지점을 좁히기 위한 컴파일러 기능.
Elaboration 단계에서 각 func.func/llvm.func 의 entry block 앞에 stderr 출력 코드를 삽입.
CLI에서 --trace 플래그 파싱 → ElabEnv에 traceEnabled 전달 → func emit 시 조건부 삽입.

---

#### Phase 99: Match Failure Diagnostics — 소스 위치, 값, 콜 스택

**Goal**: non-exhaustive match 에러 발생 시 소스 위치(file:line), 매치 대상 값의 태그/표현, 콜 스택 backtrace를 stderr에 출력하여 디버깅을 즉시 가능하게 함
**Depends on**: Phase 98
**Requirements**: DEBUG-02
**Success Criteria** (what must be TRUE):
  1. match 실패 시 `Fatal: non-exhaustive match at file.fun:42` 형태로 소스 위치가 출력됨
  2. match 실패 시 매치 대상 값의 태그 또는 정수 표현이 함께 출력됨 (예: `value=3`, `tag=Cons`)
  3. 런타임 콜 스택이 유지되어 match 실패 시 `Backtrace:` 섹션에 호출 경로가 출력됨
  4. 기존 263+ E2E 테스트 모두 통과 (regression 없음)
  5. FunLexYacc의 "non-exhaustive match" 에러에서 정확한 소스 위치와 콜 스택 확인 가능

**Plans**: 0 plans

Plans:
- [ ] TBD (run /gsd:plan-phase 99 to break down)

**Details:**
현재 `lang_match_failure()`는 인자 없이 "Fatal: non-exhaustive match"만 출력.
개선: (1) Elaboration에서 match 실패 분기에 소스 span 정보를 string global로 전달,
(2) 매치 대상 값을 i64로 전달하여 태그/값 출력,
(3) 런타임에 간단한 콜 스택 push/pop (`lang_trace_push`/`lang_trace_pop`) 유지 + 에러 시 출력.

---

#### Phase 100: Hashtable.tryGetValue Option Tag Fix (Issue #23)

**Goal**: `Hashtable.tryGetValue`가 FunLang의 option ADT 형식(`{heap_tag=5, constructor_tag, payload}`)으로 반환하여 `match r with Some v -> ... | None -> ...` 패턴이 정상 동작하게 함
**Depends on**: Phase 99
**Requirements**: BUG-02
**Success Criteria** (what must be TRUE):
  1. `Hashtable.tryGetValue ht key`의 반환값이 `match` 문에서 `Some v` / `None`으로 올바르게 매칭됨
  2. C 런타임의 `lang_hashtable_trygetvalue`가 ADT option 형식 (`heap_tag=5`, `tag=Some_tag`/`tag=None_tag`) 으로 반환
  3. FunLexYacc의 `Dfa.fun:246` match 실패가 해결됨
  4. 기존 263+ E2E 테스트 모두 통과 (regression 없음)
  5. option match를 사용하는 E2E 테스트 추가

**Plans**: 0 plans

Plans:
- [ ] TBD (run /gsd:plan-phase 100 to break down)

**Details:**
근본 원인: `lang_hashtable_trygetvalue`가 `{heap_tag=TUPLE(2), count=2, bool, value}` 형태의 tuple을 반환하지만, 컴파일러의 option match는 `{heap_tag=ADT(5), constructor_tag, payload}` 형태의 ADT를 기대.
수정: C 런타임에서 Some/None의 constructor tag를 알아야 함. Option의 TypeDecl 순서에서 None=tag0, Some=tag1 (Prelude/Option.fun의 `type 'a option = None | Some of 'a` 선언 순서). `lang_hashtable_trygetvalue`가 이 형식으로 반환하도록 수정.

---

#### Phase 101: failwith/unhandled exception Backtrace

**Goal**: `failwith "msg"` 및 unhandled exception (`lang_throw`) 발생 시에도 콜 스택 backtrace를 stderr에 출력
**Depends on**: Phase 99
**Requirements**: DEBUG-03
**Success Criteria** (what must be TRUE):
  1. `failwith "error"` 호출 시 에러 메시지와 함께 `Backtrace:` 출력
  2. unhandled exception (try-with 없이 raise) 시에도 `Backtrace:` 출력
  3. 기존 264+ E2E 테스트 모두 통과

**Plans**: 0 plans

Plans:
- [ ] TBD (run /gsd:plan-phase 101 to break down)

**Details:**
Phase 99에서 추가한 `lang_trace_push`/`lang_trace_pop` + `lang_print_backtrace()` 인프라 재활용.
`lang_failwith()`와 `lang_throw()` (unhandled 경로)에 `lang_print_backtrace()` 호출 1줄씩 추가.

---

## Progress

**Execution Order:** 94 → 95 → 96 → 97 → 98 → 99 → 100 → 101

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 94. String Parameter Indexing Bug Fix | v23.0 | 1/1 | ✓ Complete | 2026-04-09 |
| 95. FunLang v14.0 Type System Sync | v23.0 | 1/1 | ✓ Complete | 2026-04-09 |
| 96. Prelude Trivial Sync (9 files) | v23.0 | 0/TBD | Not started | - |
| 97. Prelude Manual Merge (5 files) | v23.0 | 0/TBD | Not started | - |
| 98. --trace Compiler Flag | v23.0 | 0/TBD | Not started | - |
| 99. Match Failure Diagnostics | v23.0 | 0/TBD | Not started | - |
| 100. Hashtable.tryGetValue Option Fix | v23.0 | 0/TBD | Not started | - |
| 101. failwith/exception Backtrace | v23.0 | 0/TBD | Not started | - |
