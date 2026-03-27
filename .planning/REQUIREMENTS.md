# Requirements: LangBackend v6.0

**Defined:** 2026-03-28
**Core Value:** LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다

## v1 Requirements

Requirements for v6.0 milestone. Each maps to roadmap phases.

### Module System

- [ ] **MOD-01**: prePassDecls recursion — recurse into ModuleDecl inner decls to register types/records/exceptions in TypeEnv/RecordEnv/ExnTags
- [ ] **MOD-02**: extractMainExpr flattening — inline ModuleDecl inner bindings (LetDecl/LetRecDecl/LetMutDecl) into flat expression chain
- [ ] **MOD-03**: OpenDecl handling — no-op at backend level (type checker already resolves names)
- [ ] **MOD-04**: NamespaceDecl handling — no-op at backend level
- [ ] **MOD-05**: Qualified name desugar — FieldAccess(Constructor(modName), memberName) → resolve to module member's elaborated value
- [ ] **MOD-06**: LetPatDecl handling — add to extractMainExpr filter/build (currently silently dropped)

### File I/O

- [ ] **FIO-01**: read_file builtin — `string -> string` reads entire file contents
- [ ] **FIO-02**: write_file builtin — `string -> string -> unit` writes content to file (creates/overwrites)
- [ ] **FIO-03**: append_file builtin — `string -> string -> unit` appends content to file
- [ ] **FIO-04**: file_exists builtin — `string -> bool` checks file existence
- [ ] **FIO-05**: read_lines builtin — `string -> string list` reads file as list of lines
- [ ] **FIO-06**: write_lines builtin — `string -> string list -> unit` writes list of lines to file
- [ ] **FIO-07**: stdin_read_line builtin — `unit -> string` reads one line from stdin
- [ ] **FIO-08**: stdin_read_all builtin — `unit -> string` reads all stdin
- [ ] **FIO-09**: eprint/eprintln builtins — `string -> unit` prints to stderr
- [ ] **FIO-10**: get_env builtin — `string -> string` reads environment variable
- [ ] **FIO-11**: get_cwd builtin — `unit -> string` returns current working directory
- [ ] **FIO-12**: path_combine builtin — `string -> string -> string` joins path components
- [ ] **FIO-13**: dir_files builtin — `string -> string list` lists files in directory
- [ ] **FIO-14**: C runtime file I/O functions — lang_file_* functions in lang_runtime.c/h with GC_malloc'd LangString returns

### Regression

- [ ] **REG-01**: 기존 92개 E2E 테스트 전체 통과 유지

## v2 Requirements

Deferred to future release.

### Advanced Features

- **ADV-01**: FileImportDecl — multi-file import/compilation (requires recursive parse)
- **ADV-02**: get_args — command-line arguments (requires @main signature change)
- **ADV-03**: Separate compilation — compile modules independently and link
- **ADV-04**: String content-equality for hashtable keys

## Out of Scope

| Feature | Reason |
|---------|--------|
| FileImportDecl (multi-file import) | 재귀적 파서 호출 필요, v7.0 이후 |
| get_args (command-line arguments) | @main 시그니처 변경 필요 (argc/argv) |
| Separate module compilation | 링커 통합 필요, v7.0 이후 |
| printf/sprintf format strings | 복잡도 높음 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| MOD-01 | Phase 25 | Pending |
| MOD-02 | Phase 25 | Pending |
| MOD-03 | Phase 25 | Pending |
| MOD-04 | Phase 25 | Pending |
| MOD-05 | Phase 25 | Pending |
| MOD-06 | Phase 25 | Pending |
| FIO-01 | Phase 26 | Pending |
| FIO-02 | Phase 26 | Pending |
| FIO-03 | Phase 26 | Pending |
| FIO-04 | Phase 26 | Pending |
| FIO-05 | Phase 27 | Pending |
| FIO-06 | Phase 27 | Pending |
| FIO-07 | Phase 27 | Pending |
| FIO-08 | Phase 27 | Pending |
| FIO-09 | Phase 26 | Pending |
| FIO-10 | Phase 27 | Pending |
| FIO-11 | Phase 27 | Pending |
| FIO-12 | Phase 27 | Pending |
| FIO-13 | Phase 27 | Pending |
| FIO-14 | Phase 26 | Pending |
| REG-01 | All phases | Pending |

**Coverage:**
- v1 requirements: 21 total
- Mapped to phases: 21/21
- Unmapped: 0

---
*Requirements defined: 2026-03-28*
*Last updated: 2026-03-27 — traceability assigned after roadmap creation*
