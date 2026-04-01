# FunLangCompiler — FunLang Compiler

## What This Is

FunLang의 AST/타입체커를 재사용하고 MLIR → LLVM 파이프라인을 통해 네이티브 실행 바이너리를 생성하는 컴파일러. F#으로 구현되며 ../FunLang 프로젝트를 project reference로 참조한다. v1-v3에서 기본 타입/연산자/빌트인, v4에서 ADT/Records/Exceptions, v5에서 mutable variables/Array/Hashtable, v6에서 Module system/File I/O builtins(14종)를 추가하여 118개 E2E 테스트가 통과한다.

## Core Value

FunLang 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다. 이것이 동작하면 나머지는 부가적이다.

## Current State

v14.0 shipped. 230 E2E tests. 13 Prelude modules.
~4,300 lines F# (Elaboration.fs), ~1,450 lines C (lang_runtime.c), 13 Prelude .fun files.
String module: 14 functions. List module: 40+ functions.

## Current Milestone: v15.0 unknownSpan 제거

**Goal:** 에러 메시지에서 unknownSpan(0:0:0) 대신 실제 소스 위치를 표시하도록 전체 11곳 수정

**Target features:**
- Elaboration.fs 10곳의 unknownSpan을 호출부 AST 노드 Span으로 교체
- Program.fs 1곳의 unknownSpan을 적절한 Span으로 교체
- 에러 발생 시 정확한 file:line:col 표시 보장

## Requirements

### Validated (v1.0)

- ✓ FunLang frontend(.fsproj) project reference로 AST, 타입체커 재사용
- ✓ MLIR 텍스트 포맷 직접 생성 → mlir-opt → mlir-translate → clang
- ✓ AST → MlirIR Elaboration (int/bool, 산술/비교, let/let rec, lambda/app, if-else)
- ✓ MlirIR → .mlir → LLVM IR → 네이티브 바이너리
- ✓ CLI: `langbackend file.lt` → 실행 파일 출력
- ✓ 15개 FsLit E2E 테스트 (전 카테고리)

### Validated (v2.0)

- ✓ Boehm GC 런타임 통합 (GC_INIT + GC_malloc, -lgc 링크)
- ✓ v1 클로저 alloca → GC_malloc 마이그레이션
- ✓ print/println 빌트인 (printf libc 호출)
- ✓ String 컴파일 ({i64 length, ptr data} 힙 구조체, strcmp 비교, 빌트인)
- ✓ 튜플 컴파일 (GC_malloc'd N-field 구조체, 디스트럭처링)
- ✓ 리스트 컴파일 (null nil, cons cell, 리터럴 디슈가)
- ✓ 패턴 매칭 (Jacobs decision tree, cf.cond_br 체인, 전 패턴 타입)
- ✓ 34개 FsLit E2E 테스트 (전 카테고리)

### Validated (v3.0)

- ✓ Modulo 연산자 (%) → arith.remsi
- ✓ Char 리터럴 → i64 변환
- ✓ PipeRight (|>) → App 디슈가
- ✓ ComposeRight/Left (>>, <<) → 클로저 생성
- ✓ when 가드 (패턴 매칭 조건)
- ✓ OrPat (| P1 | P2 대안 패턴)
- ✓ ConstPat(CharConst) 패턴 매칭
- ✓ failwith 빌트인 (런타임 에러)
- ✓ string_sub, string_contains, string_to_int 빌트인
- ✓ char_to_int, int_to_char 빌트인
- ✓ Range 문법 ([start..stop], [start..step..stop])
- ✓ 45개 FsLit E2E 테스트 (전 카테고리)

### Validated (v4.0)

- ✓ ADT/GADT discriminated unions — nullary/unary/multi-arg constructors + pattern matching round-trip
- ✓ Records — field access, functional update, mutable mutation, structural pattern matching
- ✓ Exception handling — setjmp/longjmp C runtime, Raise/TryWith, nested handlers, payload extraction
- ✓ First-class constructors — unary+ ctors as HOF arguments via Lambda closure wrapping
- ✓ Nested ADT pattern matching — multi-level GEP chains with Ptr-retype resolution
- ✓ 67개 FsLit E2E 테스트 (전 카테고리)

### Validated (v5.0)

- ✓ Mutable variables — let mut/assign GC ref cell, transparent Var deref, closure capture of shared ref cell
- ✓ Array — one-block layout, dynamic GEP, bounds checking, list conversion, HOFs (iter/map/fold/init)
- ✓ Hashtable — C runtime chained buckets, murmurhash3, create/get/set/containsKey/remove/keys
- ✓ 92개 FsLit E2E 테스트 (전 카테고리)

### Validated (v6.0)

- ✓ Module system — prePassDecls recursion, flattenDecls, qualified names (M.x/M.Ctor/M.f)
- ✓ File I/O Core — read_file/write_file/append_file/file_exists/eprint/eprintln
- ✓ File I/O Extended — read_lines/write_lines/stdin_read_line/stdin_read_all/get_env/get_cwd/path_combine/dir_files
- ✓ 118개 FsLit E2E 테스트 (전 카테고리)

### Validated (v7.0)

- ✓ Expression sequencing (e1; e2) — LetPat desugar
- ✓ If-then without else — If(cond, then, Tuple([])) desugar
- ✓ Array/hashtable indexing (arr.[i], ht.[key]) — runtime dispatch
- ✓ While loops — 3-block header CFG
- ✓ For loops ascending/descending — block-arg loop counter
- ✓ 138개 FsLit E2E 테스트 (전 카테고리)

### Validated (v8.0)

- ✓ Type annotations — Annot/LambdaAnnot pass-through
- ✓ For-in collection loops — list/array iteration via C runtime closure callback
- ✓ 144개 FsLit E2E 테스트 (전 카테고리)

### Validated (v9.0)

- ✓ String/Char/IO builtins (16종) — v9.0
- ✓ Hashtable/List/Array builtins (6종) — v9.0
- ✓ Collection types: StringBuilder, HashSet, Queue, MutableList — v9.0
- ✓ Language constructs: string slicing, list comprehension, TuplePat for-in, collection for-in — v9.0
- ✓ Prelude modules: String, Hashtable, StringBuilder, Char, List, Array, Option, Result — v9.0
- ✓ CLI prelude auto-loading — v9.0
- ✓ 183개 FsLit E2E 테스트 (전 카테고리)

### Validated (v10.0)

- ✓ Hashtable 문자열 키 지원 (RT-01, RT-02) — v10.0
- ✓ CLI 인자 접근 get_args + @main(argc,argv) (RT-03, RT-04) — v10.0
- ✓ sprintf/printfn 포맷 문자열 (RT-05~RT-08) — v10.0
- ✓ 멀티파일 import open "file.fun" (COMP-01~COMP-04) — v10.0
- ✓ OpenDecl open Module 스코프 바인딩 (OPEN-01~OPEN-03) — v10.0
- ✓ 컴파일러 버그 수정 FIX-01~FIX-04 — v10.0
- ✓ Prelude 12개 모듈 FunLang 동기화 — v10.0
- ✓ 202개 FsLit E2E 테스트 (전 카테고리)

### Validated (v11.0)

- ✓ failWithSpan 에러 위치 인프라 (LOC-01~03) — v11.0
- ✓ 파서 에러 보존 + MLIR 디버그 파일 보존 (PARSE-01~02, MLIR-01~02) — v11.0
- ✓ 컨텍스트 힌트: Record/Field/Function 에러 (CTX-01~03) — v11.0
- ✓ 에러 분류 [Parse]/[Elaboration]/[Compile] (CAT-01~02) — v11.0

### Validated (v12.0)

- ✓ Prelude 별도 파싱으로 유저 코드 줄 번호 정확 표시 (LINE-01, LINE-02) — v12.0
- ✓ 파서 에러에 file:line:col 위치 포함 (PARSE-POS-01) — v12.0
- ✓ 에러 테스트 CHECK-RE 전환 (TEST-01, TEST-02) — v12.0
- ✓ boxed ptr 비교 연산 unboxing 버그 수정 (UNBOX-01, UNBOX-02) — v12.0
- ✓ 217개 FsLit E2E 테스트 (전 카테고리)

### Validated (v13.0)

- ✓ AST 구조 동기화 — TypeDecl 5-field, TypeClassDecl superclasses, InstanceDecl constraints, DerivingDecl (AST-01~05) — v13.0
- ✓ elaborateTypeclasses — TypeClassDecl 제거, InstanceDecl→LetDecl, DerivingDecl→Show/Eq 자동 생성 (TC-01~05) — v13.0
- ✓ Prelude/Typeclass.fun — Show/Eq 빌트인 인스턴스 (PRE-01, PRE-02) — v13.0
- ✓ Typeclass E2E 테스트 — show/eq/deriving Show (TEST-01~03) — v13.0
- ✓ 222+ FsLit E2E 테스트

### Validated (v14.0)

- ✓ String 모듈 확장: split, indexOf, replace, toUpper, toLower, join, substring (STR-01~07) — v14.0
- ✓ C 런타임: string_split, string_indexof, string_replace, string_toupper, string_tolower (RT-01~06) — v14.0
- ✓ List 모듈 확장: 17개 함수 (init, find, findIndex, partition, groupBy, scan, replicate, collect, pairwise, sumBy, sum, minBy, maxBy, contains, unzip, forall, iter) (LIST-01) — v14.0
- ✓ 230 FsLit E2E 테스트 (TEST-01, TEST-02) — v14.0

### Out of Scope

- REPL — 인터프리터가 이미 존재함
- tail call optimization — LLVM 자동 처리에 의존
- MlirIR optimization passes — correctness 우선
- incremental/separate compilation — 별도 링커 필요
- ~~printf/sprintf 포맷 문자열~~ — v10.0에서 구현
- ~~FileImportDecl (multi-file import)~~ — v10.0에서 구현 ✓
- ~~get_args~~ — v10.0에서 구현 ✓
- ~~printf/sprintf 포맷 문자열~~ — v10.0에서 구현 ✓

## Context

- FunLang: ../FunLang/src/FunLang/FunLang.fsproj
  - AST (Ast.fs): Expr, Pattern, Value, TypeExpr 정의
  - 타입 추론: Infer.fs (Hindley-Milner), Bidir.fs
  - 렉서/파서: Lexer.fsl, Parser.fsy (FsLexYacc)
- v1 완성: MlirIR DU + Elaboration + Printer + Pipeline + CLI (15 E2E tests)
- v2 완성: Boehm GC + String/Tuple/List + Pattern Matching (34 E2E tests)
- v3 완성: 누락 연산자 + 패턴 매칭 확장 + 빌트인 확장 + Range (45 E2E tests)
- v4 완성: ADT/Records/Exceptions + first-class ctors + nested patterns (67 E2E tests)
- v5 완성: Mutable variables + Array + Hashtable + Array HOFs (92 E2E tests)
- v6 완성: Module system + File I/O builtins 14종 (118 E2E tests)
- v7 완성: Expression sequencing + indexing + if-then + while/for loops (138 E2E tests)
- v8 완성: Type annotations + for-in collection loops (144 E2E tests) — FunLang parity
- v9 완성: Collections + Builtins + Language Constructs + Prelude Modules (183 E2E tests)
- v10 완성: Bug Fixes + Hashtable String Keys + CLI Args + sprintf + Multi-file Import + OpenDecl + Prelude Sync (202 E2E tests)
- v11 완성: failWithSpan + Parse error preservation + MLIR debug + Context hints + Error categories (217 E2E tests)
- v12 완성: Prelude separate parsing + Parse error position + CHECK-RE tests + Unboxing comparison fix (217 E2E tests)
- v13 완성: AST sync + elaborateTypeclasses + Prelude/Typeclass.fun + show/eq/deriving E2E (222+ E2E tests)
- v14 완성: FunLang Standard Library Sync — String 7함수 + List 17함수 (230 E2E tests)
- 참고: survey/funlexyacc-gap-status-v9.md (FunLexYacc 컴파일 갭 분석)

## Constraints

- **Tech Stack**: F# (.NET 10), MLIR text format, LLVM 20, FsLexYacc
- **Dependencies**: mlir-opt, mlir-translate, clang (shell pipeline)
- **Reuse**: FunLang frontend 변경 없이 참조만 함
- **GC**: Boehm GC (libgc) — 보수적 GC, 가장 간단한 통합 경로
- **Platform**: Linux x86-64 / macOS arm64

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| FunLang .fsproj 참조 | 파서/타입체커 중복 구현 방지 | ✓ Good |
| MLIR 텍스트 포맷 직접 생성 | 소유권/메모리 문제 없음, 코드 단순 | ✓ Good |
| MlirIR as typed internal IR | Printer 분리로 테스트 용이, 미래 최적화 패스 가능 | ✓ Good |
| Flat closure struct {fn_ptr, env} | 단순하고 정확, 모든 클로저 테스트 통과 | ✓ Good |
| Caller-allocates closure | Callee-allocates는 stack-use-after-return UB 발생 | ✓ Good |
| Boehm GC for v2 | 보수적 GC, C 라이브러리 링크만으로 통합 가능 | ✓ Good |
| Uniform boxed representation | 균일 ptr 표현으로 GC + 다형성 단순화 | ✓ Good |
| Sequential cf.cond_br match | if-else와 동일 메커니즘, 구현 단순 | ✓ Good |
| @lang_match_failure fallback | UB 방지, 비소진 매치에 런타임 에러 보장 | ✓ Good |

| setjmp/longjmp for exceptions | 가장 단순한 C-level 언와인딩, Boehm GC와 호환 | ✓ Good |
| 태그 기반 ADT 힙 구조체 | {i64 tag, ptr payload} — 균일 표현과 일관 | ✓ Good |
| Record as named N-field struct | 튜플과 유사하나 필드 이름 → 인덱스 매핑 추가 | ✓ Good |
| inline _setjmp (ARM64 PAC) | out-of-line wrapper freed stack before longjmp | ✓ Good |
| Uniform closure ABI (ptr, i64)->i64 | ptrtoint/inttoptr bridging for type-erased values | ✓ Good |

---
| GC ref cell for mutable variables | 8-byte GC_malloc'd box, transparent deref | ✓ Good |
| One-block array layout | GC_malloc((n+1)*8), slot 0 = length | ✓ Good |
| C runtime for hashtable | Chained buckets, GC_malloc, murmurhash3 | ✓ Good |
| LangClosureFn callback ABI | C runtime calls MLIR-generated closures via fn_ptr | ✓ Good |
| array_fold two-call curried | partial = fn(closure, acc), then fn2(partial, elem) | ✓ Good |

| Module = AST flattening | Compile-time only, no runtime module objects | ✓ Good |
| Shared exnCounter in prePassDecls | Recursive calls share ref for unique tags | ✓ Good |
| File I/O: open-use-close internally | No exposed handles, GC won't fclose | ✓ Good |

---
| IDX runtime dispatch via tag sentinel | LangHashtable tag=-1, array length>=0 | ✓ Good |
| While: header-block CFG | 3-block pattern, condition re-elaboration | ✓ Good |
| For: block-arg loop counter | %i:i64 SSA-correct, no ref cell | ✓ Good |

| Two-phase Prelude parsing | preludeDecls @ userDecls, "<prelude>" filename | ✓ Good |
| lastParsedPos before try block | F# with-handler can't access try-block vars | ✓ Good |
| coerceToI64 for ordinal comparison | Ptr→I64 before arith.cmpi, Equal/NotEqual unchanged | ✓ Good |

| elaborateTypeclasses replicated | FunLang Elaborate.fs 포트, 공유 안함 | ✓ Good |
| Two-pass ctorMap for DerivingDecl | TypeDecl ctors 수집 → Show/Eq LetDecl 생성 | ✓ Good |
| Instance methods no mangling | show/eq 원래 이름 유지, 마지막 정의 wins | ✓ Good |

---
*Last updated: 2026-04-01 after v14.0 milestone completed*
