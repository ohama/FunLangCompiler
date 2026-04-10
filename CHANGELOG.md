# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

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
