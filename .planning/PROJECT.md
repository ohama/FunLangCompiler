# LangBackend — LangThree Compiler

## What This Is

LangThree의 AST/타입체커를 재사용하고 MLIR → LLVM 파이프라인을 통해 네이티브 실행 바이너리를 생성하는 컴파일러. F#으로 구현되며 ../LangThree 프로젝트를 project reference로 참조한다. v1-v3에서 기본 타입/연산자/빌트인, v4에서 ADT/Records/Exceptions, v5에서 mutable variables(let mut/assign), Array(create/get/set/length + HOFs), Hashtable(C runtime)을 추가하여 92개 E2E 테스트가 통과한다.

## Core Value

LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다. 이것이 동작하면 나머지는 부가적이다.

## Current Milestone: v6.0 Modules & I/O

**Goal:** Module system과 File I/O builtins를 컴파일하여 LangThree의 모듈화된 프로그램 구조와 파일 입출력을 네이티브 코드로 지원한다

**Target features:**
- Module system: module declarations, imports, qualified names
- File I/O: open/read/write/close/readLine builtins via C runtime

## Requirements

### Validated (v1.0)

- ✓ LangThree frontend(.fsproj) project reference로 AST, 타입체커 재사용
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

### Out of Scope

- REPL — 인터프리터가 이미 존재함
- Module 시스템 — v6.0에서 구현
- tail call optimization — LLVM 자동 처리에 의존
- MlirIR optimization passes — correctness 우선
- incremental/separate compilation — 모듈 시스템 선행 필요
- File I/O 빌트인 — v6.0에서 구현
- printf/sprintf 포맷 문자열 — 복잡도 높음, v6.0 이후

## Context

- LangThree: ../LangThree/src/LangThree/LangThree.fsproj
  - AST (Ast.fs): Expr, Pattern, Value, TypeExpr 정의
  - 타입 추론: Infer.fs (Hindley-Milner), Bidir.fs
  - 렉서/파서: Lexer.fsl, Parser.fsy (FsLexYacc)
- v1 완성: MlirIR DU + Elaboration + Printer + Pipeline + CLI (15 E2E tests)
- v2 완성: Boehm GC + String/Tuple/List + Pattern Matching (34 E2E tests)
- v3 완성: 누락 연산자 + 패턴 매칭 확장 + 빌트인 확장 + Range (45 E2E tests)
- v4 완성: ADT/Records/Exceptions + first-class ctors + nested patterns (67 E2E tests)
- v5 완성: Mutable variables + Array + Hashtable + Array HOFs (92 E2E tests)

## Constraints

- **Tech Stack**: F# (.NET 10), MLIR text format, LLVM 20, FsLexYacc
- **Dependencies**: mlir-opt, mlir-translate, clang (shell pipeline)
- **Reuse**: LangThree frontend 변경 없이 참조만 함
- **GC**: Boehm GC (libgc) — 보수적 GC, 가장 간단한 통합 경로
- **Platform**: Linux x86-64 / macOS arm64

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| LangThree .fsproj 참조 | 파서/타입체커 중복 구현 방지 | ✓ Good |
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

---
*Last updated: 2026-03-28 after v5.0 milestone shipped*
