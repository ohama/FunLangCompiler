# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.1.10] - 2026-04-14

### Fixed
- **Issue #27** — Import 체인에서 imported library 함수의 FieldAccess가 annotation 을 잃어 strict disambig 가 spurious Ambiguous 에러를 내던 문제. FunLang#26 (`typeCheckFile` 이 imported file span 을 AnnotationMap 에 포함하도록) 해결로 자동 해소. Library 레이어를 사용하는 모든 multi-file 프로젝트에서 strict disambig 가 정상 동작.

### Changed
- `deps/FunLang` submodule: `7b9d252` (v0.1.5) → `df965f4` (v0.1.6) — FunLang#26 (`fix(#26): populate imported file spans in AnnotationMap`) 포함.

### Improved
- Imported file 의 에러 위치 정확도 개선 — 이전에는 `:0:0-1:0` 로 표시되던 imported file 타입 에러가 이제 정확한 `file.fun:line:col` 로 표시됨. 진단 UX 크게 개선.

## [0.1.9] - 2026-04-14

### Fixed
- **Issue #26** — v0.1.8의 strict field disambiguation이 동일 필드 집합을 가진 여러 record 타입 사용 시 spurious Ambiguous 에러를 냈던 문제. 근본 원인은 FunLang의 RecordExpr 처리 버그 (outer type annotation 무시, `DuplicateFieldName` 오발화)였음. FunLang#25 해결로 자동 해소.

### Changed
- `deps/FunLang` submodule: `d62b566` (v0.1.3) → `7b9d252` (v0.1.5) — 다음 수정 포함:
  - FunLang#25: RecordExpr disambiguation via outer expected type
  - FunLang#23 revert: `s.[i]` 를 다시 `char` 타입으로 (char 리터럴과 통일)

### Added
- Ambiguous field access 에러에 annotationMap 비어있을 때 root cause 힌트 추가:
  > `[NOTE] annotationMap is empty — FunLang type check likely failed. Run \`fnc --check <file>\` to see the underlying type error.`
  
  타입 체크 실패로 disambig fallback이 불가능한 상황을 즉시 진단 가능.

## [0.1.8] - 2026-04-14

### Changed
- **Strict Field Disambiguation** (Phase 107) — `FieldAccess` 경로에서 last-wins fallback 제거. 여러 record 타입이 동일 필드명을 공유할 때 record expression의 타입이 annotationMap에 없으면 **컴파일 에러**로 처리 (이전: 가장 늦게 선언된 record 타입으로 silently fallback).
- 에러 메시지에 친절한 수정 예시 포함:
  ```
  Ambiguous field access: 'start' is defined in multiple record types [Bar, Foo].
  Add a type annotation to the record expression (e.g. `(x : Bar).start`)
  so the compiler can select the correct field.
  ```
- annotation이 있으나 candidates에 없는 타입을 가리키는 경우도 명확한 에러로 구분.

### Added
- E2E 테스트 `102-01-ambiguous-field-with-annot.flt` — 명시 type annotation으로 정상 disambiguation 검증.
- E2E 테스트 `102-02-ambiguous-field-without-annot.flt` — annotation 없는 경우 에러 메시지 방출 검증.

### Notes
- 기존 271개 E2E 테스트 모두 통과 (fallback에 의존한 테스트 없음 확인).
- 신규 테스트 2개 추가 → 총 **273/273 통과**.
- FunLang 수정 없이 FunLangCompiler 단독 구현 완료. 구현 중 annotation gap 발견되지 않음.

## [0.1.7] - 2026-04-13

### Added
- **Prelude Full Type Annotations** (Phase 106) — Phase 102에서 누락된 11개 Prelude 파일에 명시적 타입 어노테이션 일괄 추가:
  - `Core.fun` — 17개 함수 (id, const, compose, flip, apply, ^^, |>, >>, <<, <|, not, min, max, abs, fst, snd, ignore)
  - `Int.fun` — parse, toString
  - `Array.fun` — 12개 함수 (`'a array` postfix 형식)
  - `HashSet.fun` — 6개 함수 (`'a hashset` postfix 형식)
  - `Hashtable.fun` — 8개 함수 (`hashtable<'k, 'v>` 형식; tryGetValue는 컴파일러 transform 차이로 untyped 유지)
  - `MutableList.fun` — 6개 함수 (`'a mutablelist`)
  - `Queue.fun` — 4개 함수 (`'a queue`)
  - `StringBuilder.fun` — 3개 함수 (`stringbuilder`)
  - `List.fun` — 50+ 함수 (모두 `'a list`/`'a option` 등)
  - `Option.fun` — 12개 함수 (`'a option` postfix)
  - `Result.fun` — 10개 함수 (`result<'a, 'b>` 형식)

### Changed
- 모든 Prelude 어노테이션 syntax를 FunLang 캐노니컬 형식으로 통일:
  - postfix lowercase: `'a hashset`, `'a queue`, `'a mutablelist`, `'a array`, `'a option`
  - lowercase angle brackets: `hashtable<'k, 'v>`, `result<'a, 'b>`
  - generic 없음: `stringbuilder`
  - unit 인자: `let f () = ...` (어노테이션 없이)

### Notes
- 시행착오: 처음 `HashSet<'a>` (PascalCase 각괄호) 형식 사용 → FunLang 타입 체커 reject. FunLang의 `deps/FunLang/Prelude/` 참조하여 lowercase로 통일.
- `fnc --check`로 Prelude 타입 체크 clean 확인.
- 전체 E2E 271/271 통과.

## [0.1.6] - 2026-04-13

### Added
- **Type Check Diagnostic CLI Modes** (Phase 105, Issue #25) — 4가지 진단 옵션 추가:
  - `--check` : type-check 전용 모드. codegen/링크 스킵. clean이면 exit 0 + annotation 수 출력, type error 시 stderr에 메시지 + exit 1.
  - `--show-typecheck` : 컴파일은 진행하되 typeCheck 에러를 stderr에 warning으로 노출. annotationMap fallback 유지.
  - `--strict-typecheck` (별칭 `--strict`) : 타입 에러 1개라도 있으면 codegen 중단 + exit 1. 출력 파일 미생성. 사용자 명시 요청 — type error 시 컴파일 중단.
  - `--diagnostic-annotations` : annotationMap entry 수 + typecheck 성공/실패 상태를 stderr 1줄로 출력. Issue #24 류 진단 보조.
- 4개 신규 E2E 테스트 (`101-01-check-clean`, `101-02-check-error`, `101-03-strict-typecheck-error`, `101-04-diagnostic-annotations`).
- `--help` 메시지에 "DIAGNOSTICS — TYPE CHECK CONTROL" 섹션 추가, 각 옵션 사용법 명시.

### Changed
- `compileFile` 시그니처 확장: `TypeCheckOptions` record 파라미터 추가 (default = 모두 false → 기존 silent fallback 동작 유지).
- typeCheck 호출을 `runTypeCheck` 헬퍼로 추출. stderr suppression 여부를 인자로 받음.
- CLI parseArgs를 mutable record + `consume` 함수로 리팩터 (이전 6-tuple → 더 확장 가능).

### Notes
- 4개 신규 플래그 모두 default OFF — backward compatibility 보존.
- 사용자 요청한 "type error 시 컴파일 중단"은 `--strict-typecheck` 로 opt-in 가능.
- 향후 FunLang이 모든 컴파일러 builtin을 등록하면 default를 strict로 전환 검토 가능.

## [0.1.5] - 2026-04-13

### Removed
- **`funproj.toml`의 `prelude` 키 지원 제거** — Phase 103 embedded Prelude + walkUp 검색으로 대체됨. `ProjectFile.fs`의 `PreludePath` 필드, `compileFile`의 `preludeDir` 파라미터 모두 제거. 기존 `funproj.toml`의 `prelude = "..."` 라인은 이제 무시되며 경고 없음 (backward-compatible).

### Changed
- `PROJECTFILE.md` 문서 업데이트 — `prelude` 키 관련 설명 제거, Prelude 로딩 메커니즘(embedded + walkUp) 설명 추가.

## [0.1.4] - 2026-04-13

### Added
- **Prelude embedded in compiler binary** (Phase 103) — 14개 Prelude 파일을 `<EmbeddedResource>` 로 내장. `/tmp/` 등 Prelude 디렉토리가 없는 위치에서도 Prelude 함수(`char_to_int`, `List.map`, `String.trim` 등) 사용 가능. 파일시스템 `Prelude/` 디렉토리가 있으면 우선 사용 (개발 중 핫 에디팅 지원).
- **`printf` / `eprintf` 빌트인** (Phase 104) — stdout/stderr 포맷 출력 (개행 없음). `printfn`/`eprintfn`과 대칭.
- **`log` / `logf` 빌트인** (Phase 104) — 디버그 로그 함수. 기본 비활성화, `--log` CLI 플래그로 활성화 시 stderr + 개행. 비활성화 시 argument 평가 없이 unit 반환 (no-op).
- **`--log` CLI 플래그** (Phase 104) — `log`/`logf` 출력 활성화.
- **`-h` / `--help` CLI 플래그** (Phase 104) — 상세 사용법, CLI 옵션, 빌트인 함수 목록 표시.
- **`eprintfn` N-arg 지원 확장** (Phase 104) — 이전: `%s` 1개 인자만 지원. 이제 `printfn`과 동일하게 2-arg까지 sprintf 경유하여 지원.
- E2E 테스트 추가: `39-04-printf-eprintf`, `39-05-log-disabled`, `39-06-log-enabled`.

### Changed
- **`char_to_int` / `int_to_char` 를 컴파일러 빌트인으로 승격** (Phase 102) — Prelude의 identity 정의가 FunLang builtin scheme `TArrow(TChar, TInt)` / `TArrow(TInt, TChar)` 를 shadow하지 않음. Elaboration.fs에서 identity pass-through로 처리.
- **Prelude/Char.fun, Prelude/String.fun 타입 어노테이션 추가** (Phase 102) — 모든 wrapper에 `(c : char) : bool`, `(s : string) : int` 등 명시적 타입.
- **annotationMap 경로 정규화** — FunLang `typeCheckFile` 이 absolute path로 기록한 span FileName을 FunLangCompiler의 inputPath로 변환. Multi-file 프로젝트에서 FieldAccess span 조회 성공 → Issue #24 record field disambiguation 동작.
- **`deps/FunLang` submodule**: v0.1.2 (335013c) → v0.1.3 (d62b566) — FunLang#22 type alias 해소 포함.
- 테스트 `35-05-option-module`, `35-06-result-module`: Prelude 내장으로 Option/Result 모듈 충돌 발생. 테스트 모듈명을 MyOption/MyResult로 변경.

### Fixed
- **Issue #24 multi-file record field disambiguation** — FunLang 타입 체커가 absolute path로 annotationMap을 기록하여 FunLangCompiler의 relative path span과 매칭 실패하던 버그 수정. 별도 파일에 선언된 동일 필드명의 record 타입들이 올바르게 구분됨.

## [0.1.3] - 2026-04-13

### Fixed
- Record field disambiguation across same-named fields in different record types (Issue #24) — FunLang submodule bump to v0.1.2 which includes FunLang#20 (FieldAccess TData annotation) and FunLang#21 (remove E0311 DuplicateRecordField check). Compiler의 disambiguation 로직은 이미 존재했으며 annotationMap이 채워지면서 자동으로 동작.

### Changed
- `deps/FunLang` submodule: `8654e64` → `335013c` (v0.1.2)

## [0.1.2] - 2026-04-10

### Added
- `failwith "msg"` 호출 시 소스 위치(`at file.fun:42`)와 콜 스택 backtrace 출력 (Phase 101)
- Unhandled exception (`raise` without `try-with`) 시에도 콜 스택 backtrace 출력

## [0.1.1] - 2026-04-10

### Fixed
- `Hashtable.tryGetValue` returns proper ADT option (`Some v` / `None`) instead of tuple — fixes `match` failure at runtime (Issue #23, Phase 100)
- FunLexYacc DFA subset construction `non-exhaustive match` 해결 (root cause of Issue #23)

### Changed
- Updated `32-01-hashtable-trygetvalue` and `35-02-hashtable-module` E2E tests to use option match pattern

## [0.1.0] - 2026-04-10

### Added
- `--trace` compiler flag for function entry tracing — stderr에 `[TRACE] @funcName` 출력, zero overhead when disabled (Phase 98)
- Match failure diagnostics — 소스 위치(file:line), 매치 대상 값, 콜 스택 backtrace 출력 (Phase 99). 상세: [documentation/match-failure-diagnostics.md](documentation/match-failure-diagnostics.md)
- Runtime call stack (`lang_trace_push`/`lang_trace_pop`) for backtrace on errors
- FunLang v14.0 collection type support — THashSet, TQueue, TMutableList, TStringBuilder (Phase 95)
- E2E test for string parameter indexing (Issue #22)

### Fixed
- String character indexing returns correct value when string is a function parameter (Issue #22)
- LambdaAnnot per-parameter span collision — FunLang #19 fix integrated, 6-tuple LetRec adaptation
- LambdaAnnot handler uses annotationMap when available, synthetic fallback otherwise
- LetRec paramTypeAnnot-based type determination for first param
- FieldAccess disambiguation for multi-record environments
- Coerce string index to I64 in closure param context
- Lambda-lifted local let rec string capture uses `lang_string_char_at`
- TEName added to `typeExprNeedsPtr` + StringVars propagation fix
- Revert type error fatal — blocked by FunLang parser regression (#14)

### Changed
- FunLang submodule updated to Phase 103 (`8654e64`) with annotationMap per-parameter span fix
- `lang_match_failure` now accepts source location and scrutinee value parameters
- ElabHelpers `typeNeedsPtr` and `detectCollectionKind` handle all v14.0 union cases
