# Hello World: 소스에서 바이너리까지

FunLang 소스 코드가 네이티브 바이너리로 변환되는 전체 과정을 "Hello, World!" 프로그램으로 추적한다.

## 1. 소스 코드

```fsharp
// hello.fun
let _ = println "Hello, World!"
```

한 줄. `println`은 builtin 함수로, 문자열을 stdout에 출력한다.

## 2. 컴파일

```bash
fnc hello.fun -o hello
```

또는:

```bash
dotnet run --project src/FunLangCompiler.Cli -- hello.fun -o hello
```

## 3. 내부 파이프라인

```
hello.fun
   │
   ▼
┌─────────────────────────────────────────────┐
│  FunLang Frontend (F# — deps/FunLang)       │
│                                             │
│  Lexer.fsl → Parser.fsy → AST              │
│  IndentFilter (들여쓰기 → 토큰)              │
│  Bidir.fs (타입 추론)                        │
│  AnnotationMap (Span → Type)                │
└─────────────────────────────────────────────┘
   │  AST + AnnotationMap
   ▼
┌─────────────────────────────────────────────┐
│  FunLangCompiler (F#)                       │
│                                             │
│  ElabProgram.fs  — 프로그램 구조 생성        │
│  Elaboration.fs  — AST → MlirIR 변환        │
│  Printer.fs      — MlirIR → .mlir 텍스트     │
└─────────────────────────────────────────────┘
   │  .mlir 파일
   ▼
┌─────────────────────────────────────────────┐
│  MLIR/LLVM Pipeline (외부 도구)              │
│                                             │
│  mlir-opt       — arith/cf/func → LLVM 변환  │
│  mlir-translate — LLVM dialect → LLVM IR     │
│  clang          — LLVM IR + C runtime → ELF  │
└─────────────────────────────────────────────┘
   │  네이티브 바이너리
   ▼
  hello
```

## 4. 각 단계의 출력

### 4.1 AST

파서가 생성하는 AST:

```
Module(
  [LetDecl("_",
    App(Var("println"),
        String("Hello, World!")),
    span)])
```

`let _ = expr` — 와일드카드 바인딩. `println "Hello, World!"`는 함수 적용.

### 4.2 MlirIR (내부 IR)

Elaboration이 생성하는 F# 데이터 구조:

```fsharp
{ Globals = [StringConstant("@__str_0", "Hello, World!")]
  ExternalFuncs = [
    { ExtName = "@printf"; ExtParams = [Ptr]; ExtReturn = Some I32; IsVarArg = true }
    { ExtName = "@GC_init"; ... }
    { ExtName = "@lang_init_args"; ... }
  ]
  Funcs = [
    { Name = "@_fnc_entry"
      InputTypes = [I64; Ptr]
      ReturnType = Some I64
      Body = { Blocks = [
        { Label = None; Args = []
          Body = [
            LlvmCallVoidOp("@lang_init_args", [argc; argv])
            LlvmCallVoidOp("@GC_init", [])
            // "Hello, World!" 문자열 구조체 생성
            ArithConstantOp(sizeVal, 24)        // 24 bytes: heap_tag + length + data_ptr
            LlvmCallOp(headerVal, "@GC_malloc", [sizeVal])
            // heap_tag = 1 (STRING)
            LlvmGEPStructOp(tagSlot, headerVal, 0)
            ArithConstantOp(tagVal, 1)
            LlvmStoreOp(tagVal, tagSlot)
            // length = 13
            LlvmGEPStructOp(lenSlot, headerVal, 1)
            ArithConstantOp(lenVal, 13)
            LlvmStoreOp(lenVal, lenSlot)
            // data pointer
            LlvmGEPStructOp(dataSlot, headerVal, 2)
            LlvmAddressOfOp(strAddr, "@__str_0")
            LlvmStoreOp(strAddr, dataSlot)
            // printf("%s\n", data)
            LlvmCallOp(_, "@printf", [strAddr])
            // return 0
            ArithConstantOp(exitVal, 0)
            ReturnOp [exitVal]
          ] }
      ] } }
  ] }
```

### 4.3 MLIR 텍스트 (.mlir)

Printer가 출력하는 MLIR:

```mlir
module {
  llvm.mlir.global internal constant @__str_0("Hello, World!\0A\00")

  llvm.func @printf(!llvm.ptr, ...) -> i32
  llvm.func @GC_init() -> ()
  llvm.func @GC_malloc(i64) -> !llvm.ptr
  llvm.func @lang_init_args(i64, !llvm.ptr) -> ()

  func.func @_fnc_entry(%arg0: i64, %arg1: !llvm.ptr) -> i64 {
      llvm.call @lang_init_args(%arg0, %arg1) : (i64, !llvm.ptr) -> ()
      llvm.call @GC_init() : () -> ()
      %t0 = arith.constant 24 : i64
      %t1 = llvm.call @GC_malloc(%t0) : (i64) -> !llvm.ptr
      // ... string struct 생성 ...
      %t8 = llvm.mlir.addressof @__str_0 : !llvm.ptr
      %t9 = llvm.call @printf(%t8) vararg(...) : (!llvm.ptr) -> i32
      %t10 = arith.constant 1 : i64
      %t11 = arith.constant 0 : i64
      %t12 = arith.shrsi %t10, %t11 : i64
      return %t12 : i64
  }
}
```

### 4.4 mlir-opt (Lowering)

MLIR의 high-level ops를 LLVM dialect으로 변환:

```bash
mlir-opt --convert-arith-to-llvm --convert-cf-to-llvm \
         --convert-func-to-llvm --reconcile-unrealized-casts \
         hello.mlir -o hello.lowered.mlir
```

`arith.constant` → `llvm.mlir.constant`, `func.func` → `llvm.func`, `return` → `llvm.return`

### 4.5 mlir-translate (LLVM IR)

```bash
mlir-translate --mlir-to-llvmir hello.lowered.mlir -o hello.ll
```

생성된 LLVM IR:

```llvm
@__str_0 = internal constant [15 x i8] c"Hello, World!\0A\00"

declare i32 @printf(ptr, ...)
declare void @GC_init()
declare ptr @GC_malloc(i64)

define i64 @_fnc_entry(i64 %0, ptr %1) {
  call void @lang_init_args(i64 %0, ptr %1)
  call void @GC_init()
  ; ... string struct 생성 ...
  %9 = call i32 (ptr, ...) @printf(ptr @__str_0)
  ret i64 0
}
```

### 4.6 clang (링크)

```bash
# Step 1: C runtime 컴파일
clang -c lang_runtime.c -o runtime.o

# Step 2: LLVM IR + runtime + Boehm GC → 바이너리
clang hello.ll runtime.o -lgc -o hello
```

`runtime.o`에는:
- `main()` → `_fnc_entry()` 호출 (C entry point)
- `lang_init_args()`, `lang_to_string_int()` 등 60+ C 함수
- LANG_TAG_INT/UNTAG_INT 매크로, generic hash/equality

## 5. 실행

```bash
$ ./hello
Hello, World!
$ echo $?
0
```

실행 흐름:

```
OS → crt0 → main() [C runtime]
                → _fnc_entry(argc, argv) [컴파일러 생성]
                    → lang_init_args(argc, argv) [C runtime: argv 저장]
                    → GC_init() [Boehm GC 초기화]
                    → GC_malloc(24) [문자열 구조체 할당]
                    → printf("Hello, World!\n") [출력]
                    → return 0
                → return 0
```

## 6. 값 표현

FunLang 컴파일러는 OCaml 방식 **tagged representation**을 사용한다:

| 값 | 표현 | 예시 |
|---|---|---|
| 정수 42 | `2*42+1 = 85` (LSB=1) | `arith.constant 85 : i64` |
| 문자열 "Hello" | 힙 포인터 (LSB=0) | `{heap_tag=1, length=5, data="Hello"}` |
| true/false | `3` / `1` (tagged 1/0) | `arith.constant 3 : i64` |
| unit `()` | `1` (tagged 0) | `arith.constant 1 : i64` |

런타임에서 `val & 1`로 즉시 int/pointer 구분 가능 → generic hash/equality 지원.

## 7. 파일 구조

```
FunLangCompiler/
├── deps/FunLang/           # Frontend (파서/타입체커)
├── src/FunLangCompiler.Compiler/
│   ├── MlirIR.fs           # IR 타입 정의
│   ├── Elaboration.fs      # AST → MlirIR (~3500줄)
│   ├── ElabHelpers.fs      # 헬퍼 함수 (~800줄)
│   ├── ElabProgram.fs      # @_fnc_entry 생성
│   ├── Printer.fs          # MlirIR → .mlir 텍스트
│   ├── Pipeline.fs         # mlir-opt → mlir-translate → clang
│   ├── lang_runtime.c      # C runtime (main() + 60+ 함수)
│   └── lang_runtime.h
└── src/FunLangCompiler.Cli/
    └── Program.fs           # CLI entry point
```

## 8. 응용: 이름을 인자로 받기

커맨드라인 인자를 받아 `hello <name>` 형태로 출력하는 프로그램:

```fsharp
// hello.fun
let _ =
    match get_args () with
    | name :: _ -> println ("hello " ^^ name)
    | []        -> println "hello world"
```

```bash
$ fnc hello.fun -o hello

$ ./hello
hello world

$ ./hello Alice
hello Alice

$ ./hello 세계
hello 세계
```

**사용된 기능:**

| 기능 | 설명 |
|------|------|
| `get_args ()` | 커맨드라인 인자를 `string list`로 반환 (argv[0] 프로그램명 제외) |
| `match ... with` | 패턴 매칭으로 리스트 분해 |
| `name :: _` | 첫 번째 원소 추출 (나머지 무시) |
| `^^` | 문자열 연결 연산자 (`string_concat`) |
| `println` | 문자열 출력 + 개행 |
