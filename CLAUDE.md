# CLAUDE.md

## Build & Test

```bash
# Build
dotnet build src/FunLangCompiler.Cli

# Run all E2E tests (267 tests)
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

`--trace` — 함수 진입 트레이싱. stderr에 `[TRACE] @funcName` 출력.

```bash
fnc --trace myapp.fun -o myapp
./myapp 2>trace.log   # 모든 함수 진입 기록
tail -20 trace.log    # 마지막 20줄 확인
```

`log` / `logf` — 조건부 디버그 로그 (stderr + 개행). 기본 비활성화. `--log` 컴파일 플래그로 활성화.

```fun
let _ = log "init complete"           // 비활성화 시: no-op
let _ = logf "count=%d" n             // 활성화 시: stderr 출력
```

```bash
fnc app.fun -o app                    # log/logf 모두 컴파일러가 제거
fnc --log app.fun -o app              # log/logf가 stderr로 출력
```

`fnc --help` — 모든 CLI 옵션 및 빌트인 함수 목록 표시.

Match failure diagnostics — non-exhaustive match 시 소스 위치 + 콜 스택 출력.

```
Fatal: non-exhaustive match at file.fun:42 (value=3)
Backtrace (most recent call last):
  0: @_fnc_entry
  1: @myFunc
```

상세: [documentation/match-failure-diagnostics.md](documentation/match-failure-diagnostics.md)

## Binary Names

| Binary | Repo | Description |
|--------|------|-------------|
| `fn` | FunLang | 인터프리터 |
| `fnc` | FunLangCompiler | 네이티브 컴파일러 |

## Language

일본어 사용 금지. 한글로만 대화한다.

## Telegram

Telegram 메시지가 오면 간단히 답장하고 현재 작업을 계속 진행한다. 작업을 중단하지 않는다.
작업 결과는 반드시 Telegram으로 보고한다.
