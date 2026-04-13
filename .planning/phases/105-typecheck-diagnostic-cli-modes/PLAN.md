# Phase 105 Plan: Type Check Diagnostic CLI Modes

## Goal
Issue #25 — fnc에 typeCheck 결과를 제어/노출하는 4개 CLI 옵션 추가.

## Tasks

### T1: typeCheck 호출 리팩터 (Program.fs)
현재 inline 블록을 헬퍼 함수로 추출:
```
runTypeCheck (suppressStderr: bool) (inputPath: string) : Result<Map<Span,Type>, string>
  - typeCheckFile 호출
  - suppressStderr=false 면 에러 메시지 stderr로 통과
  - 성공: Ok annotationMap
  - 실패: Error message
```

### T2: CLI 플래그 4개 추가
```
--check                    typecheck-only mode
--show-typecheck           emit type errors as warnings, continue compile
--strict-typecheck         halt on type errors
--diagnostic-annotations   emit annotationMap entry count line
```

`parseArgs` 확장. record/tuple로 모드 묶어서 전달.

### T3: 모드별 분기 (mainImpl)

**`--check` 모드:**
```
runTypeCheck suppress=false → 
  Ok _ → printfn "OK" + exit 0
  Error msg → eprintfn msg + exit 1
codegen 스킵.
```

**`--strict-typecheck`:**
```
runTypeCheck suppress=false →
  Ok m → 기존 컴파일 계속 (m 사용)
  Error msg → eprintfn msg + exit 1 (codegen 스킵)
```

**`--show-typecheck`:**
```
runTypeCheck suppress=false →
  Ok m → 기존 컴파일 (m 사용)
  Error msg → eprintfn "[Warning] " + msg + 기존 컴파일 (Map.empty)
```

**`--diagnostic-annotations`:**
```
runTypeCheck 후 annotationMap.Count + status를 stderr로 1줄.
다른 모드와 직교 (조합 가능).
```

기본(no flag): 현재 동작 유지 (silent fallback).

### T4: --help 메시지 확장
DIAGNOSTICS 섹션 추가:
```
DIAGNOSTICS — TYPE CHECK CONTROL (default: silent fallback)
  --check                    Type-check only; skip codegen and linking
  --show-typecheck           Show type errors as warnings, continue compile
  --strict-typecheck         Halt compilation on any type error
  --diagnostic-annotations   Emit annotationMap entry count to stderr
```

### T5: E2E 테스트 추가
- `40-01-check-clean.flt` — `fnc --check` clean file → exit 0
- `40-02-check-error.flt` — `fnc --check` 타입 에러 파일 → exit 1, stderr에 메시지
- `40-03-strict-clean.flt` — `--strict-typecheck` clean → 정상 컴파일
- `40-04-strict-error.flt` — `--strict-typecheck` 타입 에러 → exit 1, 출력 파일 미생성
- `40-05-diagnostic-annotations.flt` — entry count 출력 검증

### T6: 회귀 검증
- `dotnet build src/FunLangCompiler.Cli` 성공
- `dotnet run --project tests/projfile` 통과
- 전체 E2E 267+5=272개 통과

## Verification
모든 새 플래그 동작 + 기존 테스트 회귀 없음.

## Risk
- typeCheckFile이 stderr에 직접 출력하는 것이 아니라 예외 메시지로만 반환할 수도 있음 — 확인 필요
- `--check` 모드에서 출력 파일 생성 안 하는 것이 자연스러우나 `-o` 파라미터 처리 방식 검토
