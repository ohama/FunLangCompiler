# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-04-10

### Added
- `--trace` compiler flag for function entry tracing — stderr에 `[TRACE] @funcName` 출력, zero overhead when disabled (Phase 98)
- Match failure diagnostics — 소스 위치(file:line), 매치 대상 값, 콜 스택 backtrace 출력 (Phase 99)
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
