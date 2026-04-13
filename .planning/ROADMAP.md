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

#### Phase 102: Prelude Type Annotations

**Goal**: Prelude의 모든 wrapper/identity 함수에 명시적 타입 어노테이션(char/int/string 등)을 부여하여, FunLang 타입 체커가 multi-file import 프로젝트에서 annotationMap을 올바르게 채울 수 있게 함
**Depends on**: Phase 101
**Requirements**: PRE-TYPE-01
**Success Criteria** (what must be TRUE):
  1. `Prelude/Core.fun`의 `char_to_int : char -> int`, `int_to_char : int -> char` 가 명시적 어노테이션으로 선언됨
  2. `Prelude/Char.fun`의 모든 wrapper (isDigit, toUpper, isLetter, isUpper, isLower, toLower, toInt, ofInt) 가 `char -> bool` 또는 `char -> char` 형태로 명시 어노테이션
  3. `Prelude/String.fun`의 모든 wrapper (concat, join, endsWith, startsWith, trim, length, contains, split, indexOf, replace, toUpper, toLower, substring) 가 명시 어노테이션
  4. 기존 264+ E2E 테스트가 어노테이션 추가 후에도 모두 통과 (regression 없음)
  5. FunLang `typeCheckFile`이 `s.[i] : char` semantic을 사용하는 minimal multi-file import 예제에서 성공하여 annotationMap에 entries를 생성
  6. `char_to_int`, `int_to_char` 호출 시 FunLang 타입 체커가 `TArrow(TChar, TInt)` / `TArrow(TInt, TChar)` 로 올바르게 해석

**Plans**: 0 plans

Plans:
- [ ] TBD (run /gsd:plan-phase 102 to break down)

**Details:**
배경:
- FunLang#15: `s.[i] : char` 로 결정 (FunLexYacc 검증 중 확정)
- FunLang#22: import 체인 타입 해소 수정 (해결)
- FunLang#23: char 리터럴과 int 비교 — 올바른 방향은 `char_to_int` 명시 변환

현재 `Prelude/Core.fun`의 `char_to_int`, `int_to_char`는 `let char_to_int c = c` 같이 어노테이션 없이 identity로 정의됨. FunLang 타입 체커가 `'a -> 'a` 로 추론하여 builtin scheme `TArrow(TChar, TInt)` 과 충돌 가능.

작업:
- Prelude/Core.fun: char_to_int, int_to_char 에 명시적 타입
- Prelude/Char.fun: 모든 함수 `(c : char)` 파라미터 및 반환 타입 명시
- Prelude/String.fun: 모든 string 함수 파라미터/반환 타입 명시
- 필요 시 Prelude/Int.fun, Prelude/Option.fun 등 다른 파일도 검토

Issue #24 재발 완전 해결에 필요 (FunLexYacc의 타입 체크가 실제로 성공해야 annotationMap이 채워져 record field disambiguation이 동작).

---

#### Phase 103: Embed Prelude into Compiler Binary

**Goal**: Prelude 소스 파일을 컴파일러 바이너리에 embedded resource로 포함하여, Prelude 디렉토리 없는 환경에서도 `char_to_int`, `List.map`, `String.trim` 등 모든 Prelude 함수를 자유롭게 사용 가능하게 함
**Depends on**: Phase 101
**Requirements**: PRE-EMBED-01
**Success Criteria** (what must be TRUE):
  1. `/tmp/` 같이 Prelude 디렉토리가 없는 위치에서 `.fun` 파일을 컴파일해도 Prelude 함수(`char_to_int`, `List.map` 등)가 정상 해석됨
  2. 컴파일러 바이너리(dotnet DLL)에 14개 Prelude 파일(Typeclass/Core/Option/Result/String/Char/Int/Hashtable/HashSet/MutableList/Queue/StringBuilder/List/Array)이 embedded resource로 포함됨
  3. 파일시스템 `Prelude/` 디렉토리가 존재하면 여전히 우선 사용됨 (override 가능) — 개발 중 Prelude 수정 시 재빌드 없이 테스트 가능
  4. 기존 264+ E2E 테스트 모두 통과 (regression 없음)
  5. `funproj.toml`의 explicit prelude path 역시 여전히 동작

**Plans**: 0 plans

Plans:
- [ ] TBD (run /gsd:plan-phase 103 to break down)

**Details:**
현재 동작 (`src/FunLangCompiler.Cli/Program.fs:123-164`):
- `findPreludeDir()`가 파일시스템 검색
  1. 명시적 `preludeDir` (funproj.toml)
  2. input 파일 디렉토리부터 walkUp하며 `Prelude/` 찾기
  3. assembly 디렉토리의 `Prelude/` fallback
- 찾지 못하면 `preludeSrc = ""` → Prelude 함수 사용 불가

문제:
- `/tmp/` 등 임시 위치에서 테스트 시 Prelude 미로딩 → `char_to_int`, `List.map` undefined
- 사용자가 FunLangCompiler 바이너리만 설치한 경우 Prelude 디렉토리 별도 배포 필요
- FunLexYacc는 `Prelude@` 심볼릭 링크로 우회하지만 부적절한 해결책

작업:
- F# 프로젝트에 `<EmbeddedResource>` 로 Prelude/*.fun 파일 포함
- `findPreludeDir()` 실패 시 embedded resource에서 로드
- override 순서: funproj.toml > walkUp > embedded resource (fallback 제거)
- `dotnet publish` 시 single-file 배포 가능하도록 보장

**실행 순서 주의:** Phase 102 (Prelude 타입 어노테이션) 검증을 임시 파일에서 하려면 Phase 103이 먼저 필요. 권장 실행: 103 → 102.

---

#### Phase 104: printf / eprintf / log / logf Builtins + CLI Flags

**Goal**: 출력 빌트인 패밀리 완성 + 조건부 디버그 로그 함수 추가
**Depends on**: Phase 103
**Requirements**: IO-01, DEBUG-04
**Success Criteria** (what must be TRUE):
  1. `printf` (stdout, no newline), `eprintf` (stderr, no newline) 빌트인 동작
  2. `eprintfn` 의 N-arg sprintf 경로가 추가되어 `printfn` 과 대칭
  3. `log` (string -> unit) / `logf` (formatted) 빌트인 — `--log` 플래그 미지정 시 no-op (argument 평가 없음)
  4. `--log` CLI 플래그로 `log`/`logf` 출력 활성화
  5. `-h` / `--help` CLI 플래그로 사용법, 모든 CLI 옵션, 빌트인 함수 목록 출력
  6. 신규 E2E 테스트 (`39-04-printf-eprintf`, `39-05-log-disabled`, `39-06-log-enabled`) 통과
  7. 기존 264+ E2E 테스트 모두 통과

**Plans**: 1 plan

Plans:
- [x] 104-01: printf/eprintf elaboration arms, log/logf gated by env.LogEnabled, CLI flag/help wiring

**Details:**
완료 시 v0.1.4 릴리스. 자세한 변경 사항은 CHANGELOG.md 참조.

---

#### Phase 105: Type Check Diagnostic CLI Modes (Issue #25)

**Goal**: fnc에 4가지 CLI 옵션을 추가하여 FunLang 타입 체크 결과를 제어/노출하고, 사용자가 type error 발생 시 컴파일 중단을 선택할 수 있게 함
**Depends on**: Phase 104
**Requirements**: TC-DIAG-01..04
**Success Criteria** (what must be TRUE):
  1. `--check` : codegen/링크 스킵하고 typeCheckFile만 실행. 타입 에러 stderr 출력 + exit 1, clean이면 exit 0. 출력 파일 미생성.
  2. `--show-typecheck` : 컴파일은 진행하되 typeCheck 에러도 stderr에 warning으로 출력. annotationMap fallback 유지. exit code는 컴파일 성공 여부.
  3. `--strict-typecheck` : 타입 에러 1개라도 있으면 codegen 없이 exit 1 (사용자 명시 요청 — type error 시 컴파일 중단).
  4. `--diagnostic-annotations` : annotationMap entry 수 + typecheck 성공/실패 상태를 stderr 1줄로 출력.
  5. 4개 플래그 모두 default OFF — 기존 silent fallback 동작 backward-compat 보존.
  6. `--help` 메시지에 4개 신규 플래그 명시 + 사용 시나리오 짧게 안내.
  7. 각 플래그별 E2E 테스트 추가 (clean / type error 두 케이스).
  8. 기존 267+ E2E 테스트 모두 통과.

**Plans**: 0 plans

Plans:
- [ ] TBD (run /gsd:plan-phase 105 to break down)

**Details:**
배경:
- Issue #25: typeCheck 결과 제어 도구 부재 — 진단 시 fnc 소스 임시 수정 또는 외부 `fn --check` 사용해야 함
- Issue #21: type errors silently ignored — runtime crash로 나타남
- Issue #24: annotationMap empty fallback이 record disambiguation 폴백 오동작 유발

현재 코드 (`Program.fs:209-218`):
```fsharp
let annotationMap =
    try
        let savedErr = System.Console.Error
        System.Console.SetError(System.IO.TextWriter.Null)
        try
            let typedModule = ExportApi.typeCheckFile (Path.GetFullPath(inputPath))
            typedModule.AnnotationMap
        finally
            System.Console.SetError(savedErr)
    with _ -> Map.empty
```

문제:
- stderr 리다이렉트 → 타입 에러 메시지 사용자에게 보이지 않음
- `with _ -> Map.empty` → 모든 예외 무시 → 타입 에러로 인한 annotationMap 부재가 silent
- 진단/CI/strict 빌드 모두 불가능

작업:
- CLI 파싱: `--check`, `--show-typecheck`, `--strict-typecheck`, `--diagnostic-annotations` 4개 플래그 추가
- typeCheckFile 호출을 함수로 추출 (성공 시 annotationMap, 실패 시 ex 메시지 반환)
- 모드별 분기 처리:
  - `--check`: typeCheckFile만 실행, 결과로 exit (codegen 스킵)
  - `--show-typecheck`: 에러 메시지 stderr로 노출 + 기존 컴파일 계속
  - `--strict-typecheck`: 에러 시 codegen 중단 + exit 1
  - `--diagnostic-annotations`: annotationMap entry 수 출력
- `--help` 업데이트 (DIAGNOSTICS 섹션 추가)
- E2E 테스트 추가:
  - `40-01-check-clean.flt` (clean 파일 → exit 0)
  - `40-02-check-error.flt` (type error 파일 → exit 1, stderr 메시지)
  - `40-03-strict-typecheck.flt` (strict 모드 + clean → 정상 컴파일)
  - `40-04-diagnostic-annotations.flt` (entry count 출력 검증)

추가 메모:
- 향후 Phase에서 default를 strict로 전환 가능 (FunLang이 모든 컴파일러 builtin을 등록하면)
- 현재는 backward-compat 우선 — 모든 신규 동작은 opt-in
- Issue #24 진단 자동화 가능: `fnc --diagnostic-annotations file.fun` 로 annotationMap 빈 상태 즉시 감지

---

## Progress

**Execution Order:** 94 → 95 → 96 → 97 → 98 → 99 → 100 → 101 → 103 → 102 → 104 → 105

(Phase 103 먼저: Prelude embed으로 임시 위치 테스트 가능. Phase 102: 타입 어노테이션 추가 및 검증. Phase 104: 출력/디버그 빌트인 + CLI 확장. Phase 105: 타입 체크 진단 옵션.)

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
| 102. Prelude Type Annotations | v23.0 | 1/1 | ✓ Complete | 2026-04-13 |
| 103. Embed Prelude into Binary | v23.0 | 1/1 | ✓ Complete | 2026-04-13 |
| 104. printf/eprintf/log/logf + CLI flags | v23.0 | 1/1 | ✓ Complete | 2026-04-13 |
| 105. Type Check Diagnostic CLI Modes | v23.0 | 1/1 | ✓ Complete | 2026-04-13 |
| 106. Prelude Full Type Annotations | v23.0 | 1/1 | ✓ Complete | 2026-04-13 |
