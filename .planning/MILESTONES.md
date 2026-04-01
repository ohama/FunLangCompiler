# Milestones: FunLangCompiler

## Completed

### v1.0 — Core Compiler (2026-03-26)

**Goal:** FunLang 소스 코드를 네이티브 실행 바이너리로 컴파일

**Phases:** 1–6 (11 plans, all verified)
**Requirements:** 21/21 complete
**Tests:** 15 FsLit E2E tests

**What shipped:**
- MlirIR typed internal IR (F# DU)
- Elaboration pass (AST → MlirIR translation)
- Scalar codegen (int, arith, let/var SSA)
- Booleans, comparisons, control flow (if-else, &&, ||)
- Known functions (let rec → FuncOp + DirectCallOp)
- Closures (lambda capture → flat struct + indirect call)
- CLI (`langbackend file.lt` → native binary)

**Key decisions validated:**
- MLIR text format direct generation (no P/Invoke) ✓
- MlirIR as typed internal IR (not thin wrapper) ✓
- Flat closure struct {fn_ptr, env_fields} ✓
- Caller-allocates closure pattern ✓

---

### v2.0 — Data Types & Pattern Matching (2026-03-26)

**Goal:** String, 튜플, 리스트 타입 지원 + 패턴 매칭 + Boehm GC 런타임

**Phases:** 7–11 (9 plans, all verified)
**Requirements:** 20/20 complete
**Tests:** 34 FsLit E2E tests
**LOC:** 1,861 (F# + C)

**What shipped:**
- Boehm GC runtime integration (GC_INIT + GC_malloc for all heap allocation)
- String compilation ({i64 length, ptr data} heap struct, strcmp equality, builtins)
- Tuple compilation (GC_malloc'd N-field structs, destructuring via GEP + load)
- List compilation (null-pointer nil, cons cells, literal desugaring)
- Pattern matching (Jacobs decision tree, cf.cond_br chain, all v2 pattern types)
- print/println builtins, string_length, string_concat, to_string

**Key decisions validated:**
- Boehm GC (conservative collector, `-lgc` link only) ✓
- Uniform boxed representation (all heap types as ptr) ✓
- Sequential cf.cond_br match compilation ✓
- @lang_match_failure fallback for non-exhaustive match ✓
- Jacobs decision tree for pattern matching ✓

---

### v3.0 — Language Completeness (2026-03-26)

**Goal:** 누락 연산자, 빌트인, 패턴 매칭 확장으로 대부분의 FunLang 프로그램 컴파일 가능

**Phases:** 12–15 (5 plans, all verified)
**Requirements:** 17/17 complete
**Tests:** 45 FsLit E2E tests

**What shipped:**
- Modulo (%), Char literal ('A'), PipeRight (|>), ComposeRight/Left (>>, <<) operators
- when guards, OrPat (| P1 | P2), ConstPat(CharConst) pattern matching extensions
- failwith runtime error, string_sub/string_contains/string_to_int string builtins
- char_to_int/int_to_char character builtins, variable print/println
- Range syntax ([start..stop], [start..step..stop]) list generation

**Key decisions validated:**
- PipeRight/Compose are elaboration-time desugar only, no new MLIR ops ✓
- OrPat expanded before MatchCompiler; char is already i64 (identity elaboration) ✓
- Range via C runtime (lang_range) returning Phase-10-compatible cons list ✓

---

### v4.0 — Type System & Error Handling (2026-03-27)

**Goal:** ADT/GADT, Records(mutable fields), Exception handling(setjmp/longjmp)으로 FunLang 타입 시스템 기능 대부분 네이티브 코드 지원

**Phases:** 16–20 (12 plans, all verified)
**Requirements:** 27/27 complete
**Tests:** 67 FsLit E2E tests (22 new)
**LOC:** 2,861 F# + 184 C

**What shipped:**
- ADT discriminated unions: nullary/unary/multi-arg constructors + full pattern matching round-trip
- Record types: field access, functional update, mutable field mutation, structural pattern matching
- Exception handling: setjmp/longjmp C runtime, nested try-with, payload extraction, handler-miss re-raise
- First-class constructors: unary+ ctors as higher-order function arguments via Lambda closure wrapping
- Nested ADT pattern matching: multi-level GEP chains with Ptr-retype resolution

**Key decisions validated:**
- ADT 16-byte block layout {tag, payload} with uniform boxed representation ✓
- Record flat layout GC_malloc(n*8), no tag prefix ✓
- setjmp/longjmp with inline _setjmp for ARM64 PAC compatibility ✓
- Uniform closure ABI (ptr, i64) -> i64 with ptrtoint/inttoptr bridging ✓

---

### v5.0 — Mutable & Collections (2026-03-28)

**Goal:** Mutable variable bindings, Array, Hashtable을 컴파일하여 FunLang imperative programming 패턴 네이티브 코드 지원

**Phases:** 21–24 (8 plans, all verified)
**Requirements:** 26/26 complete
**Tests:** 92 FsLit E2E tests (25 new)
**LOC:** 3,187 F# + 450 C

**What shipped:**
- Mutable variables: LetMut/Assign GC ref cell, transparent Var deref, closure capture of shared ref cell
- Array: one-block layout, dynamic GEP, bounds checking (catchable exception), list conversion
- Hashtable: C runtime chained buckets, murmurhash3 hashing, 6 builtins
- Array HOFs: iter/map/fold/init via C runtime closure callbacks (LangClosureFn)

**Key decisions validated:**
- GC ref cell (8-byte GC_malloc) for mutable variables ✓
- One-block array layout GC_malloc((n+1)*8) ✓
- C runtime for hashtable (chained buckets, GC_malloc throughout) ✓
- Closure callbacks from C runtime via LangClosureFn typedef ✓
- array_fold two-call curried pattern per iteration ✓

---

### v6.0 — Modules & I/O (2026-03-28)

**Goal:** Module system + File I/O builtins로 FunLang 모듈화 프로그램과 파일 입출력 네이티브 코드 지원

**Phases:** 25–27 (5 plans, all verified)
**Requirements:** 21/21 complete
**Tests:** 118 FsLit E2E tests (26 new)
**LOC:** 3,367 F# + 739 C

**What shipped:**
- Module system: prePassDecls recursion, flattenDecls, qualified names (M.x, M.Ctor, M.f)
- File I/O Core: read_file, write_file, append_file, file_exists, eprint, eprintln
- File I/O Extended: read_lines, write_lines, stdin_read_line/all, get_env, get_cwd, path_combine, dir_files

**Key decisions validated:**
- Module = compile-time AST flattening (no runtime module objects) ✓
- Shared exnCounter for recursive prePassDecls ✓
- File I/O: open-use-close internally (no exposed handles) ✓
- All file errors via lang_throw (catchable) ✓

---

### v7.0 — Imperative Syntax (2026-03-28)

**Goal:** Expression sequencing, indexing syntax, if-then, while/for loops 네이티브 코드 지원

**Phases:** 28–29 (4 plans, all verified)
**Requirements:** 14/14 complete
**Tests:** 138 FsLit E2E tests (20 new)

**What shipped:**
- Expression sequencing (e1; e2) via LetPat desugar
- If-then without else via If(cond, then, Tuple([])) desugar
- Array/hashtable indexing (arr.[i], ht.[key]) via runtime dispatch
- While loops (3-block header CFG)
- For loops ascending/descending (block-arg loop counter)

**Key decisions validated:**
- SEQ/ITE: parser-level desugar, zero new codegen ✓
- IDX: runtime dispatch via tag=-1 sentinel ✓
- While: header-block CFG with condition re-elaboration ✓
- For: block argument %i:i64 for SSA-correct counter ✓

---

### v8.0 — Final Parity (2026-03-28)

**Goal:** Type annotations + for-in collection loops로 FunLang 인터프리터와의 기능 차이 해소

**Phases:** 30 (2 plans, all verified)
**Requirements:** 8/8 complete
**Tests:** 144 FsLit E2E tests (6 new)

**What shipped:**
- Annot/LambdaAnnot type annotation pass-through
- ForInExpr collection iteration (list + array) via C runtime closure callbacks

**Key decisions validated:**
- Compile-time dispatch (ArrayVars) instead of runtime GC_size check ✓

---

### v9.0 — Collections & Builtins Parity (2026-03-30)

**Goal:** FunLang v7.0/v7.1 기능을 컴파일러에 구현하여 Phase 62까지 패리티 달성

**Phases:** 31–35 (14 plans, all verified)
**Requirements:** 33/33 complete
**Tests:** 183 FsLit E2E tests (39 new)
**LOC:** 3,564 F# + 1,100 C + 8 Prelude .fun files

**What shipped:**
- 16 new builtin functions (string, char, hashtable, list, array, io)
- 4 new collection types (StringBuilder, HashSet, Queue, MutableList) with C runtime
- 4 new language constructs (string slicing, list comprehension, TuplePat for-in, collection for-in)
- 8 Prelude modules (String, Hashtable, StringBuilder, Char, List, Array, Option, Result)
- CLI prelude auto-loading with module-qualified naming
- 11 compiler bug fixes (closures, accessor cache, LetRec, type coercions)

**Key decisions validated:**
- Struct typedefs in header only (prevents clang redefinition) ✓
- GC_malloc+memcpy for buffer growth (never realloc) ✓
- CollectionVars: Map<string, CollectionKind> for compile-time for-in dispatch ✓
- Module-qualified naming (Option_map) eliminates MLIR symbol collisions ✓
- CLI prelude loading via input-file directory walk-up ✓

---

### v10.0 — FunLexYacc 네이티브 컴파일 지원 (2026-03-30)

**Goal:** FunLexYacc 셀프호스팅을 위한 잔여 블로커 해소

**Phases:** 36–42 (9 plans, all verified)
**Requirements:** 19/19 complete (15 original + 4 added during execution)
**Tests:** 202 FsLit E2E tests (19 new)

**What shipped:**
- Bug fixes: for-in mutable capture, sequential if, bool I1 coercion, if-match nested empty block
- Hashtable string keys: C runtime hash/compare extension + Elaboration dispatch
- CLI arguments: @main (argc, argv) + get_args runtime helper
- Format strings: sprintf/printfn via C runtime snprintf delegation
- Multi-file import: AST flattening for `open "file.fun"` in Program.fs
- OpenDecl implementation: `open Module` brings members into scope via alias LetDecls
- Prelude sync: 11/12 files byte-identical to FunLang/Prelude/

**Key decisions validated:**
- IDX dispatch: LangHashtableStr tag=-2 ✓
- expandImports: HashSet push/pop for diamond imports ✓
- sprintf: format-specific C wrappers with elaboration dispatch ✓
- OpenDecl: two-pass collectModuleMembers + alias LetDecls ✓

---

### v11.0 — Compiler Error Messages (2026-03-31)

**Goal:** 컴파일러 에러 메시지에 소스 위치, 파서 에러 보존, MLIR 디버그, 컨텍스트 힌트, 에러 분류 추가

**Phases:** 44–46 (4 plans, all verified)
**Requirements:** 12/12 complete
**Tests:** 217 FsLit E2E tests (15 new)

**What shipped:**
- failWithSpan 인프라: [Elaboration] file:line:col: message 형태
- 파서 에러 보존: parseModule 실패 시 첫 번째 에러 유지
- MLIR 디버그: mlir-opt/translate 실패 시 .mlir 파일 보존
- 컨텍스트 힌트: Record/Field/Function 에러에 사용 가능한 항목 표시
- 에러 분류: [Parse]/[Elaboration]/[Compile] 카테고리 구분

---

### v12.0 — Error Message Accuracy (2026-04-01)

**Goal:** 에러 메시지의 줄 번호 정확성, 파서 에러 위치 추가, 테스트 안정성, 비교 연산 unboxing 버그 수정

**Phases:** 47–50 (4 plans, all verified)
**Tests:** 217 FsLit E2E tests

**What shipped:**
- Prelude 별도 파싱: 유저 코드 줄 번호가 1부터 시작 (was 174)
- Parse error position: [Parse] file:line:col: parse error 형태
- Error tests CHECK-RE: 7 에러 테스트를 정규식 매칭으로 전환
- Unboxing comparison bug: ordinal comparison에 coerceToI64 추가

**Key decisions validated:**
- Two-phase parsing (preludeDecls @ userDecls, "<prelude>" filename) ✓
- lastParsedPos mutable before try block (F# scoping rule) ✓
- fslit CHECK-RE per-line matching ✓
- coerceToI64 for ordinal comparisons, Equal/NotEqual unchanged ✓

---

### v13.0 — FunLang Typeclass Sync (2026-04-01)

**Goal:** FunLang v10.0-v12.0에서 추가된 AST 구조 변경과 Typeclass 컴파일 지원을 FunLangCompiler에 반영

**Phases:** 51–53 (3 plans, all verified)
**Tests:** 222+ FsLit E2E tests (5 new typeclass tests)

**What shipped:**
- AST 구조 동기화 (TypeDecl 5-field, TypeClassDecl superclasses, InstanceDecl constraints, DerivingDecl)
- elaborateTypeclasses: TypeClassDecl 제거, InstanceDecl→LetDecl, DerivingDecl→Show/Eq 자동 생성
- Prelude/Typeclass.fun: Show, Eq 빌트인 인스턴스 (int/bool/string/char)
- show/eq/deriving Show E2E 테스트

**Key decisions validated:**
- elaborateTypeclasses replicated from FunLang (not shared) ✓
- Two-pass ctorMap for DerivingDecl expansion ✓
- Instance methods use original names (no mangling) ✓
- Typeclass.fun first in Prelude load order ✓

---

## Current

### v14.0 — FunLang Standard Library Sync (In Progress)

**Goal:** FunLang Prelude/String.fun과 Prelude/List.fun에 추가된 함수들을 컴파일러에 반영

**Phases:** 54–56
**Requirements:** 16 requirements (STR-01~07, RT-01~06, LIST-01, TEST-01~02)

**Planned deliverables:**
- String: split, indexOf, replace, toUpper, toLower (신규 C 런타임 + Elaboration 디스패치), join/substring (별칭)
- List: 17개 순수 FunLang 함수 Prelude 동기화
- E2E 테스트: 모든 신규 String/List 함수 검증

