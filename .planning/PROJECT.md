# LangBackend — LangThree Compiler

## What This Is

LangThree의 AST/타입체커를 재사용하고 MLIR → LLVM 파이프라인을 통해 네이티브 실행 바이너리를 생성하는 컴파일러. F#으로 구현되며 ../LangThree 프로젝트를 project reference로 참조한다. 기존 인터프리터(Eval.fs)를 대체하는 코드 생성 백엔드를 구축하는 것이 목표다.

## Core Value

LangThree 소스 코드를 입력받아 네이티브 실행 바이너리를 출력한다. 이것이 동작하면 나머지는 부가적이다.

## Requirements

### Validated

(None yet — ship to validate)

### Active

- [ ] LangThree frontend(.fsproj) project reference로 AST, 타입체커 재사용
- [ ] MLIR C API P/Invoke 바인딩 (libMLIR.so)
- [ ] AST → MLIR dialect codegen (int/bool, 산술/비교, let/let rec, lambda/app, if-else)
- [ ] MLIR → LLVM IR lowering pass
- [ ] LLVM → 네이티브 바이너리 (object file → 링크)
- [ ] CLI: `.lt` 파일을 받아 실행 파일 출력
- [ ] 핵심 기능 E2E 테스트 (파일 컴파일 → 실행 → 출력 검증)

### Out of Scope

- string 컴파일 — 메모리 관리 복잡도, v2로 미룸
- 튜플/리스트 컴파일 — GC/박싱 필요, v2로 미룸
- 패턴 매칭 컴파일 — 튜플/리스트 선행 필요, v2로 미룸
- REPL — 인터프리터가 이미 존재함
- ADT/GADT — LangThree v1에서 미구현

## Context

- LangThree: ../LangThree/src/LangThree/LangThree.fsproj
  - AST (Ast.fs): Expr, Pattern, Value, TypeExpr 정의
  - 타입 추론: Infer.fs (Hindley-Milner), Bidir.fs
  - 렉서/파서: Lexer.fsl, Parser.fsy (FsLexYacc)
- 이전 LangBackend: 이미 MLIR P/Invoke 바인딩 경험 있음 (git history 참조)
- MLIR C API를 F# P/Invoke로 래핑하는 패턴 사용
- 타겟: Linux x86-64 (WSL2 환경)

## Constraints

- **Tech Stack**: F# (.NET 10), MLIR C API, FsLexYacc (LangThree 재사용)
- **Dependencies**: libMLIR.so, clang/lld (링킹용)
- **Reuse**: LangThree frontend 변경 없이 참조만 함
- **Platform**: Linux x86-64 (WSL2)

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| LangThree .fsproj 참조 | 파서/타입체커 중복 구현 방지 | — Pending |
| MLIR → LLVM (MLIR 경유) | 직접 LLVM IR보다 추상화 레벨 높음, dialect 확장 가능 | — Pending |
| v1 커어 기능만 | 빠른 E2E 검증 후 확장, 복잡한 런타임(GC) 미루기 | — Pending |
| F# 구현 | LangThree와 동일 언어, 코드 재사용 최대화 | — Pending |

---
*Last updated: 2026-03-26 after initialization*
