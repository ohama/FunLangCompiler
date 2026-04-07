# CLAUDE.md

## Build & Test

```bash
# Build
dotnet build src/FunLangCompiler.Cli

# Run all E2E tests (260+ tests)
dotnet run --project deps/fslit/FsLit/FsLit.fsproj -- tests/compiler/

# Run a specific test
dotnet run --project deps/fslit/FsLit/FsLit.fsproj -- tests/compiler/04-01-fact.flt

# Run ProjectFile parser tests
dotnet run --project tests/projfile

# Compile a single file (default -O2)
fnc hello.fun -o hello
# Or: dotnet run --project src/FunLangCompiler.Cli -- hello.fun -o hello

# Optimization: -O0 (none) / -O2 (default) / -O3 (aggressive)
fnc hello.fun -O3
```

## Project Build (funproj.toml)

```bash
fnc build              # 모든 [[executable]] → build/ 네이티브 바이너리
fnc build myapp        # 특정 타겟만
fnc test               # 모든 [[test]] 컴파일 + 실행
fnc test unit          # 특정 테스트만
```

See [PROJECTFILE.md](PROJECTFILE.md) for funproj.toml format.

## Debugging

`dbg expr` — prints `[file:line] value` to stderr, returns value unchanged (pass-through).

```fsharp
let x = dbg (expensive_computation 42)
// stderr: [file.fun:1] 1764
// x = 1764
```

## Binary Names

| Binary | Repo | Description |
|--------|------|-------------|
| `fn` | FunLang | 인터프리터 |
| `fnc` | FunLangCompiler | 네이티브 컴파일러 |

## Telegram

Telegram 메시지가 오면 간단히 답장하고 현재 작업을 계속 진행한다. 작업을 중단하지 않는다.
작업 결과는 반드시 Telegram으로 보고한다.
