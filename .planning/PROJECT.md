# LangBackend — LangThree Compiler

## What This Is

LangThree의 AST/타입체커를 재사용하고 MLIR → LLVM 파이프라인을 통해 네이티브 실행 바이너리를 생성하는 컴파일러. F#으로 구현되며 ../LangThree 프로젝트를 project reference로 참조한다. v1에서 int/bool/함수/클로저 코드 생성을 완성했고, v2에서 Boehm GC 런타임, string/tuple/list 힙 타입, 패턴 매칭(Jacobs decision tree)을 추가하여 34개 E2E 테스트가 통과한다.

## Core Value

LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다. 이것이 동작하면 나머지는 부가적이다.

## Current Milestone

Planning next milestone.

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

### Out of Scope

- REPL — 인터프리터가 이미 존재함
- ADT/GADT — LangThree에서 미구현
- tail call optimization — LLVM이 자동 처리 기대, 명시적 보장 안 함
- Windows/macOS 네이티브 지원 — Linux x86-64 (WSL2/macOS) 우선
- MlirIR optimization passes — correctness 우선, 최적화는 v3 이후
- incremental/separate compilation — 모듈 시스템 필요, v3 이후

## Context

- LangThree: ../LangThree/src/LangThree/LangThree.fsproj
  - AST (Ast.fs): Expr, Pattern, Value, TypeExpr 정의
  - 타입 추론: Infer.fs (Hindley-Milner), Bidir.fs
  - 렉서/파서: Lexer.fsl, Parser.fsy (FsLexYacc)
- v1 완성: MlirIR DU + Elaboration + Printer + Pipeline + CLI (15 E2E tests)
- v2 완성: Boehm GC + String/Tuple/List + Pattern Matching (34 E2E tests)

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

---
*Last updated: 2026-03-26 after v2.0 completion*
