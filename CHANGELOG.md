# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

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
