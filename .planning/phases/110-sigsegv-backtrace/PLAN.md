# Phase 110: Runtime SIGSEGV/SIGBUS/SIGFPE/SIGILL Backtrace (Issue #29)

## Goal
Hardware fault (stack overflow, null deref, division by zero 등) 발생 시 native binary 가 silent exit 139 가 아니라 **fatal 메시지 + FunLang backtrace** 출력.

## 구현

`src/FunLangCompiler.Compiler/lang_runtime.c`:

1. `signal.h` include
2. `lang_signal_handler(int sig)` — sig 이름 출력 + `lang_print_backtrace()` + SIG_DFL 복원 + raise (core dump / exit code 139 보존)
3. `sigaltstack` 로 128KB alternate stack 확보 — stack overflow 상황에서도 핸들러 실행 가능 (중요!)
4. `sigaction` with `SA_ONSTACK | SA_RESETHAND` 로 등록 — SIGSEGV, SIGBUS, SIGFPE, SIGILL
5. `main()` 에서 `lang_install_signal_handlers()` 호출

## 검증

### 무한 재귀 → stack overflow
```fun
let rec recurseForever (n : int) : int = recurseForever (n + 1) + 1
let _ = printfn "%d" (recurseForever 0)
```

이전 동작 (v0.1.11):
```
exit=139  (stderr 비어있음)
```

새 동작 (v0.1.12):
```
Fatal: runtime signal 11: SIGSEGV (segmentation fault — likely stack overflow, null deref, or invalid memory access)
Backtrace (most recent call last):
  0: @_fnc_entry
  1: @recurseForever
  2: @recurseForever
  ... (256 depth)
exit=139
```

## 관련
- Issue #29 (FunLangCompiler)
- Phase 99 (Match failure diagnostics) — 동일 `lang_print_backtrace()` 재사용
- Phase 101 (failwith backtrace) — 동일 infrastructure 재사용

## 남은 작업 (추후)
- Issue #29 의 기타 개선 제안 (circular-buffer trace, DWARF 통합) — 별도 phase 로 추적
