# FunLangCompiler Project File (funproj.toml)

`fn`과 `fnc`가 공유하는 Cargo 스타일 프로젝트 빌드 시스템. `funproj.toml`로 프로젝트 구성, 빌드/테스트 타겟을 관리한다.

---

## Quick Start

```toml
# funproj.toml
[project]
name = "myproject"

[[executable]]
name = "myapp"
main = "src/main.fun"

[[test]]
name = "basic"
main = "tests/basic.fun"
```

```bash
fnc build          # 모든 executable → build/ 네이티브 바이너리
fnc test           # 모든 test 컴파일 + 실행
```

---

## File Format

### [project] Section

프로젝트 메타데이터.

```toml
[project]
name = "myproject"
```

| 필드 | 타입 | 필수 | 설명 |
|------|------|------|------|
| `name` | string | 선택 | 프로젝트 이름 |

**Prelude 로딩** (v0.1.5+): Prelude는 컴파일러 바이너리에 내장되어 있으므로 별도 설정 불필요. 개발 중 Prelude 수정 시, 입력 파일 디렉토리에서 상위 방향으로 `Prelude/` 디렉토리를 찾으면 우선 사용됨 (hot-edit 지원).

### [[executable]] Section

빌드(컴파일) 타겟 정의. 여러 개 선언 가능.

```toml
[[executable]]
name = "myapp"
main = "src/main.fun"

[[executable]]
name = "tool"
main = "src/tool.fun"
```

| 필드 | 타입 | 필수 | 설명 |
|------|------|------|------|
| `name` | string | 필수 | 타겟 이름 (`fnc build <name>`으로 지정) |
| `main` | string | 필수 | 엔트리 포인트 파일 (`funproj.toml` 기준 상대 경로) |

### [[test]] Section

테스트 타겟 정의. 여러 개 선언 가능.

```toml
[[test]]
name = "unit"
main = "tests/unit.fun"

[[test]]
name = "integration"
main = "tests/integration.fun"
```

| 필드 | 타입 | 필수 | 설명 |
|------|------|------|------|
| `name` | string | 필수 | 테스트 이름 (`fnc test <name>`으로 지정) |
| `main` | string | 필수 | 테스트 파일 (`funproj.toml` 기준 상대 경로) |

---

## CLI Commands

### fnc build

```bash
fnc build              # 모든 [[executable]] 타겟을 네이티브 바이너리로 컴파일
fnc build myapp        # 'myapp' 타겟만 컴파일
fnc build -O3          # 공격적 최적화로 컴파일
```

**동작:**
1. CWD에서 `funproj.toml` 탐색 (상위 디렉토리까지)
2. TOML 파싱, `[[executable]]` 타겟 추출
3. 각 타겟의 `main` 파일을 네이티브 바이너리로 컴파일
4. 결과를 `build/` 디렉토리에 출력

**출력 예시:**

```
$ fnc build
OK: myapp -> build/myapp (0.5s)
OK: tool -> build/tool (0.3s)
```

**종료 코드:**
- `0`: 모든 타겟 성공
- `1`: 에러 발생

### fnc test

```bash
fnc test               # 모든 [[test]] 타겟 컴파일 + 실행
fnc test unit          # 'unit' 테스트만
```

**동작:**
1. CWD에서 `funproj.toml` 탐색
2. TOML 파싱, `[[test]]` 타겟 추출
3. 각 타겟을 **네이티브 바이너리로 컴파일 + 실행**
4. 종료 코드로 PASS/FAIL 판정 (0 = PASS, 그 외 = FAIL)
5. 실패한 테스트가 있어도 나머지 계속 실행

**출력 예시:**

```
$ fnc test
all good
PASS: unit (0.4s)
FAIL: integration (exit 1, 0.3s)
1/2 tests passed
```

**종료 코드:**
- `0`: 모든 테스트 통과
- `1`: 실패한 테스트 있음

### fnc (단일 파일)

```bash
fnc hello.fun              # hello 바이너리 생성
fnc hello.fun -o myapp     # 출력 이름 지정
fnc hello.fun -O3          # 최적화 레벨 지정
```

기존 단일 파일 컴파일 모드. `funproj.toml` 불필요.

---

## fn과 fnc 비교

| 명령 | fn (인터프리터) | fnc (컴파일러) |
|------|----------------|----------------|
| `build` | 타입 체크 | **네이티브 바이너리 생성** |
| `test` | 타입 체크 + 인터프리트 실행 | **컴파일 + 바이너리 실행** |
| 단일 파일 | 인터프리트 실행 | 네이티브 컴파일 |
| 출력 위치 | (없음) | `build/` 디렉토리 |
| funproj.toml | 동일 형식 | 동일 형식 |

같은 `funproj.toml`, 같은 소스, 같은 `import "..."` 의존성 — 도구만 바꾸면 된다.

```bash
# 개발 중: fn으로 빠른 반복
fn test                   # 인터프리터로 즉시 실행

# 배포 시: fnc로 네이티브 빌드
fnc build -O2             # 최적화된 바이너리 생성
./build/myapp             # 네이티브 속도로 실행
```

---

## Path Resolution

모든 경로는 `funproj.toml` 파일이 위치한 디렉토리 기준 상대 경로로 해석된다.

```
myproject/
├── funproj.toml
├── Prelude/              # 선택 — hot-edit 시 우선 사용 (없어도 내장 Prelude로 동작)
│   └── MyLib.fun
├── src/
│   ├── main.fun          # main = "src/main.fun"
│   └── lib/              #   → /abs/path/myproject/src/main.fun
│       └── utils.fun     # open "lib/utils.fun" (main.fun 기준 상대 경로)
├── tests/
│   └── test.fun          # main = "tests/test.fun"
└── build/                # fnc build가 자동 생성
    ├── myapp
    └── tool
```

멀티파일 의존성은 소스 코드의 `import "file.fun"` 문이 해결한다. `fnc`가 재귀적으로 인라인.

---

## Error Messages

| 상황 | 메시지 |
|------|--------|
| `funproj.toml` 없음 | `Error: funproj.toml not found (searched from current directory upward)` |
| 타겟 파일 없음 | `Error: target file not found: path/to/file.fun` |
| 존재하지 않는 타겟 이름 | `Error: no executable target named 'foo'` |
| 타겟 미정의 | `No executable targets defined in funproj.toml` |

---

## Complete Example

### Project Structure

```
calculator/
├── funproj.toml
├── Prelude/
│   └── MathLib.fun
├── src/
│   └── calc.fun
└── tests/
    └── test-calc.fun
```

### funproj.toml

```toml
[project]
name = "calculator"

[[executable]]
name = "calc"
main = "src/calc.fun"

[[test]]
name = "test-calc"
main = "tests/test-calc.fun"
```

### Prelude/MathLib.fun

```fsharp
module MathLib =
    let square x = x * x
    let cube x = x * x * x
```

### src/calc.fun

```fsharp
open MathLib
let result = square 5 + cube 2
let _ = printfn "result = %d" result
```

### tests/test-calc.fun

```fsharp
open MathLib

let assert_eq name expected actual =
    if expected = actual then
        printfn "PASS: %s" name
    else
        printfn "FAIL: %s (expected %d, got %d)" name expected actual

let _ = assert_eq "square 5" 25 (square 5)
let _ = assert_eq "cube 3" 27 (cube 3)
```

### 실행

```bash
$ cd calculator

$ fnc build
OK: calc -> build/calc (0.5s)

$ ./build/calc
result = 33

$ fnc test
PASS: square 5
PASS: cube 3
PASS: test-calc (0.4s)
1/1 tests passed

$ fnc src/calc.fun
# 단일 파일 모드도 여전히 동작
```

---

## Implementation Notes

- **TOML 파서:** 수동 구현 (외부 의존성 없음). `[project]`, `[[executable]]`, `[[test]]` 서브셋만 지원.
- **멀티파일:** `import "file.fun"` 임포트를 `expandImports`가 재귀적으로 AST에 인라인.
- **compileFile 헬퍼:** 단일 파일 모드와 프로젝트 모드가 동일한 컴파일 파이프라인 공유.

### Source Files

| 파일 | 역할 |
|------|------|
| `src/FunLangCompiler.Compiler/ProjectFile.fs` | TOML 파싱, 경로 해석, `FunProjConfig` 생성 |
| `src/FunLangCompiler.Cli/Program.fs` | CLI 라우팅 (build/test/단일 파일), compileFile 헬퍼 |

---

*Source: `src/FunLangCompiler.Compiler/ProjectFile.fs`, `src/FunLangCompiler.Cli/Program.fs`*
*Last updated: 2026-04-07*
