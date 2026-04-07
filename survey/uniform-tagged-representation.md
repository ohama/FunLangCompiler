# Uniform Tagged Representation Survey

OCaml 방식의 tagged value representation을 FunLang 컴파일러에 도입하는 방안 조사.

## 배경

현재 FunLang 컴파일러는 값을 두 가지 LLVM 타입으로 표현한다:

| 값 종류 | MlirType | LLVM 타입 |
|---------|----------|-----------|
| int, bool, char, unit | I64 | i64 |
| string, list, tuple, record, closure, hashtable | Ptr | !llvm.ptr |

Prelude module 함수(func.func)는 모든 파라미터를 i64로 전달하므로, 함수 body 안에서 I64/Ptr 구분이 불가능하다. 이것이 Hashtable `*Str` 중복의 근본 원인이다.

## OCaml의 접근: Uniform Representation

OCaml은 모든 값을 machine word 하나(`value` = `intptr_t`)로 표현한다:

```
정수:    |-------- 63-bit value --------|1|    LSB = 1, 값 = 2n+1
포인터:  |-------- 64-bit address ------|0|    LSB = 0 (힙 정렬 보장)
```

- 런타임에서 `val & 1`로 즉시 구분
- 힙 블록에는 header word (size + tag byte)가 있어서 string/tuple/record 등 세부 구분 가능

### Generic Hash / Equality

OCaml의 `Hashtbl.hash`와 `(=)`는 C 런타임에서 구현:

```c
value caml_hash(value v) {
    if (Is_long(v)) {           // LSB=1 → 정수
        return hash_int(Long_val(v));
    } else {                    // LSB=0 → 포인터
        switch (Tag_val(v)) {
        case String_tag: return hash_string(v);
        case Double_tag: return hash_double(v);
        default:         return hash_block(v);  // 필드 재귀 순회
        }
    }
}
```

→ **하나의 Hashtbl 구현**으로 모든 key 타입 처리. `*Str` 변종 불필요.

## FunLang에 적용 시 설계

### 값 인코딩

```
int 42    →  85   (42*2+1)
int 0     →  1    (0*2+1)
int -1    →  -1   ((-1)*2+1 = -1, 2의 보수)
true      →  3    (1*2+1)
false     →  1    (0*2+1)
unit      →  1    (0*2+1, false와 동일 — 구분 불필요)
char 'A'  →  131  (65*2+1)
"hello"   →  0x7f8...  (힙 포인터, LSB=0)
[1;2;3]   →  0x7f8...  (Cons cell 포인터)
```

### MlirType 단순화

```fsharp
// 현재
type MlirType = I64 | I32 | I1 | Ptr

// tagged 후
type MlirType = Val | I32 | I1
// Val = tagged value (i64). Ptr 구분 불필요.
// I1은 조건 분기에만 사용 (값으로 저장 시 Val로 변환).
// I32는 GEP index 등 내부용.
```

### 산술 연산 변환

| 연산 | 현재 | Tagged | 추가 비용 |
|------|------|--------|----------|
| `a + b` | `add a, b` | `add a, b` → `sub result, 1` | 1 op |
| `a - b` | `sub a, b` | `sub a, b` → `add result, 1` | 1 op |
| `a * b` | `mul a, b` | `ashr a, 1` → `mul _, b` → `or result, 1`* | 2 ops |
| `a / b` | `sdiv a, b` | `ashr a, 1` → `ashr b, 1` → `sdiv` → `shl, 1` → `or, 1` | 4 ops |
| `a % b` | `srem a, b` | `ashr a, 1` → `ashr b, 1` → `srem` → `shl, 1` → `or, 1` | 4 ops |
| `a < b` | `icmp slt a, b` | `icmp slt a, b` | **0 ops** |
| `a == b` | `icmp eq a, b` | `icmp eq a, b` | **0 ops** |
| `-a` | `sub 0, a` | `sub 2, a` | 0 ops |

(*) `a * b`의 최적화: `(a >> 1) * b` → `result | 1`. b의 LSB가 1이므로 `(a>>1)*b = (a>>1)*(2n+1)`, 실제로는 untag 양쪽 후 retag 필요.

정확한 `a * b`:
```
t1 = ashr a, 1      // untag a
t2 = ashr b, 1      // untag b
t3 = mul t1, t2
result = shl t3, 1
result = or result, 1  // retag
```

### 비교 연산이 그대로 동작하는 이유

```
tagged(a) = 2a + 1
tagged(b) = 2b + 1

2a+1 < 2b+1  ⟺  a < b     ✓ (부호 있는 비교도 성립)
2a+1 == 2b+1  ⟺  a == b    ✓
```

## 수정이 필요한 파일/영역

### 컴파일러 (FunLangCompiler)

| 파일 | 변경 내용 | 규모 |
|------|----------|------|
| **MlirIR.fs** | `Ptr` 제거, `Val` 도입 | 소 |
| **Elaboration.fs** — 산술 | +,-,*,/,% 에 tag/untag 추가 | 중 (~6��) |
| **Elaboration.fs** — 리터럴 | int/bool/char 상수에 `2n+1` 적용 | 소 (~3곳) |
| **Elaboration.fs** — hashtable | `*_str` 패턴 전부 삭제 | 소 (삭제만) |
| **Elaboration.fs** — MlirType dispatch | `v.Type = Ptr` 기반 분기 전부 제거 | 중 (~20곳) |
| **ElabHelpers.fs** — coerce | `coerceToI64`/`coerceToPtrArg` 단순화 | 소 |
| **Printer.fs** | LLVM IR 출력에서 타입 구분 조정 | 중 |
| **Hashtable.fun** | `*Str` 변종 전부 삭제 | 소 |

### C 런타임 (deps/FunLang)

| 함수 그룹 | 변경 내용 |
|-----------|----------|
| **Hashtable** | int/str 변종 통합. `key & 1` 체크로 런타임 dispatch |
| **int 입출력** | `int_to_string`, `print_int` 등에 untag (`>> 1`) 추가 |
| **char 변환** | `char_to_int`, `int_to_char`에 tag/untag 추가 |
| **array 인덱스** | `array_get(arr, idx)` — idx untag 필요 |
| **to_string** | LSB 체크로 generic dispatch 가능 (bonus) |
| **equality** | generic `(=)` 구현 가능 (bonus) |

### 변경 불필요

| 항목 | 이유 |
|------|------|
| string 연산 | 이미 포인터, LSB=0, 변경 없음 |
| list 연산 | Cons cell 포인터, 변경 없음 |
| tuple/record | 힙 할당 포인터, 변경 없음 |
| closure 호출 | 이미 i64 uniform ABI |
| 비교 연산 (`<`, `==`) | tagged 상태에서 직접 비교 성립 |
| GEP/load/store | 포인터 연산은 inttoptr 후 사용 (현재와 동일) |

## 트레이드오프

### 장점

- **`*Str` 중복 완전 제거** — Hashtable뿐 아니라 향후 모든 polymorphic 구조체
- **Generic equality** — `(=)` 하나로 int, string, record 비교
- **Generic hash** — Hashtable의 key 타입 제약 해소
- **Generic to_string** — 런타임 타입 판별로 polymorphic 출력
- **Elaboration 단순화** — MlirType 기반 분기 대폭 축소
- **Prelude 자유도** — wrapper 함수가 타입 정보 소실 걱정 없음

### 단점

- **정수 범위 축소** — 64-bit → 63-bit (±4.6×10^18 → ±2.3×10^18)
- **곱셈/나눗셈 오버헤드** — 연산당 shift 2~4회 추가
- **C 런타임 전면 수정** — deps/FunLang에 대한 대규모 변경 (또는 issue 등록)
- **대규모 리팩토링** — 별도 milestone 규모 (1~2 phase)

### 덧셈/뺄셈은 거의 무비용

FunLang 코드에서 가장 빈번한 산술은 `+`/`-`/비교이다. 이들은 tagged 상태에서 1 op 이하의 추가 비용으로 동작한다. 곱셈/나눗셈은 상대적으로 드물어 전체 성능 영향은 미미할 것으로 예상된다.

## 구현 순서 제안

```
Phase 1: MlirType에 Val 추가, Ptr와 공존 (backward compatible)
Phase 2: 리터럴 tagging (int/bool/char)
Phase 3: 산술 연산 tag/untag
Phase 4: C 런타임 수정 (int 입출력, array 인덱스)
Phase 5: Hashtable 통합 (int/str 변종 합치기)
Phase 6: coerce 함수 정리, Ptr 타입 제거
Phase 7: Generic equality/hash/to_string (optional bonus)
```

## OCaml과의 비교

| | OCaml | FunLang (tagged 도입 후) |
|---|---|---|
| 즉시값 | int: `2n+1` | int: `2n+1` |
| 힙 블록 header | size + tag byte (string=252, tuple=0, ...) | **없음** (tag byte 미도입 시) |
| Generic hash/equal | header tag로 재귀 순회 | LSB로 int/ptr 구분만 (깊은 구조 비교는 미지원) |
| 다형성 수준 | 완전 (어떤 값이든 hash key 가능) | int/string만 (tuple, record key는 미지원) |
| Float boxing | 힙 할당 (Double_tag) | 현재 미지원 |

FunLang은 OCaml만큼의 완전한 polymorphism은 필요 없다. **LSB 1-bit tagging만으로 int/string 구분이 되면** 현재의 `*Str` 문제는 해결된다. 힙 블록 header까지 도입하면 tuple/record key 등 확장 가능하지만, 별도 단계로 분리할 수 있다.

---
*2026-04-07 — OCaml 방식 tagged representation 조사*
