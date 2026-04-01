# CLAUDE.md

## Build & Test

```bash
# Build
dotnet build src/FunLangCompiler.Cli

# Run all E2E tests (234 tests)
dotnet run --project deps/fslit/FsLit/FsLit.fsproj -- tests/compiler/

# Run a specific test
dotnet run --project deps/fslit/FsLit/FsLit.fsproj -- tests/compiler/04-01-fact.flt

# Compile a FunLang source file
dotnet run --project src/FunLangCompiler.Cli -- hello.fun -o hello
# Or after install: fnc hello.fun -o hello
```

## Binary Names

| Binary | Repo | Description |
|--------|------|-------------|
| `fn` | FunLang | 인터프리터 |
| `fnc` | FunLangCompiler | 네이티브 컴파일러 |

## Telegram

Telegram 메시지가 오면 간단히 답장하고 현재 작업을 계속 진행한다. 작업을 중단하지 않는다.
작업 결과는 반드시 Telegram으로 보고한다.
