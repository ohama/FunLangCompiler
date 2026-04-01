# FunLangCompiler Abstract Grammar

## 소개

이 문서는 FunLangCompiler(`fnc`) 컴파일러가 지원하는 추상 문법을 정의한다.
FunLangCompiler는 FunLang의 파서/AST를 재사용하며, `Elaboration.fs`에서 AST를 MLIR IR로 변환한다.
따라서 **파싱 가능한 문법은 FunLang와 동일**하며, 이 문서는 컴파일러가 실제로 코드 생성하는 범위를 기술한다.

**CLI:** `fnc <file.fun> [-o <output>]` — FunLang 소스를 네이티브 바이너리로 컴파일

---

## 표기법 (Notation)

| 표기 | 의미 |
|------|------|
| `::=` | 정의 |
| `\|` | 선택 (alternatives) |
| `*` | 0회 이상 반복 |
| `+` | 1회 이상 반복 |
| `?` | 0 또는 1회 (optional) |
| `( ... )` | 그룹핑 |
| `'token'` | 리터럴 토큰 (따옴표 포함) |
| `IDENT` | 소문자 시작 식별자 |
| `UPPER` | 대문자 시작 식별자 (생성자) |
| `INT` | 정수 리터럴 |
| `BOOL` | `true` 또는 `false` |
| `STRING` | 문자열 리터럴 |
| `CHAR` | 문자 리터럴 |
| `TYPE_VAR` | 타입 변수 (`'a`, `'b`, ...) |
| `INFIXOP` | 사용자 정의 중위 연산자 |

---

## 1. 프로그램과 모듈 (Programs and Modules)

```
program     ::= 'module' qualified_ident decl* EOF
             |  decl* EOF

qualified_ident ::= IDENT ('.' IDENT)*
```

최상위 프로그램은 빈 파일이거나(`EmptyModule`), 선언 목록이거나,
`module` 헤더를 가진 명명된 모듈이다.

Prelude 파일(`Prelude/*.fun`)은 컴파일 전에 별도 파싱되어 AST가 합쳐진다.
`open "file.fun"` 파일 임포트는 `Program.fs`의 `expandImports`에서 AST 수준으로 인라인된다.

---

## 2. 선언 (Declarations)

```
decl ::= 'let' IDENT '=' expr
       | 'let' IDENT param+ '=' expr
       | 'let' IDENT '(' ')' '=' expr
       | 'let' '(' op_name ')' param+ '=' expr
       | 'let' tuple_pattern '=' expr
       | 'let' '_' '=' expr
       | 'let' IDENT '=' expr 'in' expr

       | 'let' 'rec' IDENT param+ '=' expr
           ('and' IDENT param+ '=' expr)*
       | 'let' 'rec' '(' op_name ')' param+ '=' expr

       | 'let' ('mut' | 'mutable') IDENT '=' expr

       | 'type' IDENT type_var* '=' constructor ('|' constructor)*
       | 'type' IDENT type_var* '=' '{' field_decl (';' field_decl)* ';'? '}'
       | 'type' IDENT type_var* '=' type_alias_expr

       | 'exception' IDENT ('of' type_expr)?

       | 'module' IDENT '=' decl+

       | 'open' qualified_ident
       | 'open' STRING

       -- 타입 클래스 (v10.0, v12.0 확장)
       | 'typeclass' IDENT type_var '='
             ('|' IDENT ':' type_expr)+
       | 'typeclass' constraint_list '=>' IDENT type_var '='     -- 슈퍼클래스 (v12.0)
             ('|' IDENT ':' type_expr)+
       | 'instance' IDENT atomic_type '='
             ('let' IDENT param+ '=' expr)+
       | 'instance' constraint_list '=>' IDENT atomic_type '='   -- 조건부 인스턴스 (v12.0)
             ('let' IDENT param+ '=' expr)+
       | 'deriving' IDENT IDENT                                  -- 자동 도출 (v12.0)

param    ::= IDENT
op_name  ::= INFIXOP0 | INFIXOP1 | INFIXOP2 | INFIXOP3 | INFIXOP4
type_var ::= TYPE_VAR
```

**컴파일러 노트:**
- 타입 별칭(`type_alias_expr`): 파싱되지만 코드 생성 시 무시 (균일 표현).
- `open qualified_ident`: 다중 세그먼트 `open A.B`를 마지막 세그먼트로 해석하여 모듈 멤버를 가져옴.
- Typeclass/Instance/Deriving: `elaborateTypeclasses`가 TypeClassDecl 제거, InstanceDecl→LetDecl, DerivingDecl→Show/Eq 자동 생성으로 변환. 슈퍼클래스 제약은 파싱되지만 강제되지 않음 (타입 시스템 없음).

### 2.1 모듈 시스템 (Module System)

FunLangCompiler의 모듈 시스템은 **컴파일 타임 AST 평탄화**로 구현된다. 런타임 모듈 객체는 없다.

#### 모듈 선언 (Module Declaration)

```
module List =
    let rec map f = fun xs -> match xs with | [] -> [] | h :: t -> f h :: map f t
    let hd xs = match xs with | h :: _ -> h
```

`flattenDecls`가 모듈 멤버에 접두사를 붙여 최상위로 끌어올린다:
```
let List_map f = ...     -- module-qualified name
let List_hd xs = ...
```

#### 정규화된 접근 (Qualified Access)

```
List.map f xs            -- FieldAccess(Constructor("List"), "map")
                         -- → App(Var("List_map"), ...)
```

#### open 선언 (Open Declaration)

```
open List                -- 모듈 멤버를 현재 스코프에 가져옴
open Outer.Inner         -- 다중 세그먼트: 마지막 세그먼트(Inner)의 멤버를 가져옴
```

`open`은 2단계로 구현된다:

1. **`collectModuleMembers`** — 전체 선언 목록을 스캔하여 각 모듈의 멤버 이름 레지스트리를 구축
   ```
   Map<string, string list>
   -- "List" → ["List_map"; "List_hd"; ...]
   ```

2. **`flattenDecls`의 `OpenDecl` 처리** — 모듈의 각 멤버에 대해 별칭 `LetDecl`을 생성
   ```
   open List
   -- 생성되는 별칭:
   -- let map = List_map    (Var("List_map"))
   -- let hd = List_hd      (Var("List_hd"))
   -- ...
   ```

정교화(elaboration) 시 이 별칭들은:
- **1-lambda 함수**: `Vars`에서 클로저 포인터로 해결
- **2-lambda 함수**: `KnownFuncs`에서 직접 호출 서명으로 해결 (클로저 래핑 불필요)

#### open의 섀도잉 (Shadowing)

여러 `open` 문이 있을 때 마지막 것이 우선한다:
```
open Option              -- optionMap → Option_optionMap
open List                -- map → List_map (Option_map이 있었다면 덮어씀)
```

#### 사용자 정의 연산자와 모듈 (Custom Operators in Modules)

```
module Core =
    let (^^) a b = string_concat a b

open Core                -- (^^) 사용 가능
```

모듈 내 연산자(`^^`, `++`, `<|>` 등)는 AST에서는 원래 이름을 유지하고,
MLIR 출력 시 `sanitizeMlirName`이 유효한 심볼명으로 변환한다:
```
Core_^^ → @Core__caret__caret_   (MLIR에서 유효)
```

#### 파일 임포트 (File Import)

```
open "utils.fun"         -- 파일의 모든 선언을 현재 스코프에 인라인
```

`Program.fs`의 `expandImports`가 컴파일 전에 재귀적으로 해결한다:
- 상대 경로는 임포트하는 파일의 디렉토리 기준
- 순환 임포트 감지 (HashSet push/pop)
- 다이아몬드 임포트 허용 (같은 파일을 여러 경로로 임포트)

### 2.2 생성자 선언 (Constructor Declarations)

```
constructor ::= UPPER                              -- 인자 없는 ADT 생성자
              | UPPER 'of' type_expr               -- 인자 있는 ADT 생성자
              | UPPER ':' type_expr '->' type_expr  -- GADT 생성자
```

GADT 생성자는 파싱되지만 정교화에서 일반 생성자로 처리된다.

### 2.3 레코드 필드 선언 (Record Field Declarations)

```
field_decl ::= IDENT ':' type_expr
             | 'mutable' IDENT ':' type_expr
```

---

## 3. 표현식 (Expressions)

우선순위 낮은 순서부터 높은 순서로 기술한다.

```
expr ::= -- 바인딩 형식
         'let' IDENT '=' expr 'in' expr
       | 'let' IDENT param+ '=' expr 'in' expr
       | 'let' IDENT '(' ')' '=' expr 'in' expr
       | 'let' tuple_pattern '=' expr 'in' expr
       | 'let' '_' '=' expr 'in' expr
       | 'let' 'rec' IDENT IDENT '=' expr 'in' expr
       | 'let' ('mut' | 'mutable') IDENT '=' expr 'in' expr
       | IDENT '<-' expr                               -- 가변 변수 재할당

         -- 패턴 매칭 / 예외 처리
       | 'match' expr 'with' match_clause+
       | 'try' expr 'with' match_clause+
       | 'try' expr 'with' IDENT '->' expr          -- 단일 핸들러 (인라인)

         -- 람다
       | 'fun' IDENT '->' expr
       | 'fun' IDENT IDENT+ '->' expr                   -- 다중 파라미터 (v13.0)
       | 'fun' '(' IDENT ':' type_expr ')' '->' expr
       | 'fun' '(' IDENT ':' type_expr ')'+  '->' expr
       | 'fun' '(' ')' '->' expr
       | 'fun' tuple_pattern '->' expr

         -- 제어 흐름
       | 'if' expr 'then' expr 'else' expr
       | 'if' expr 'then' expr                        -- else 없는 if (unit 반환)

         -- 시퀀싱
       | expr ';' expr                                 -- let _ = e1 in e2로 디슈거

         -- 루프
       | 'while' expr 'do' expr                        -- while 루프 (unit 반환)
       | 'for' IDENT '=' expr 'to' expr 'do' expr     -- 오름차순 for 루프
       | 'for' IDENT '=' expr 'downto' expr 'do' expr -- 내림차순 for 루프

         -- 컬렉션 for-in 루프
       | 'for' IDENT 'in' expr 'do' expr               -- 컬렉션 순회
       | 'for' tuple_pattern 'in' expr 'do' expr       -- 튜플 분해 순회

         -- 파이프 및 합성
       | expr '|>' expr                             -- 파이프 오른쪽
       | expr '>>' expr                             -- 함수 합성 오른쪽
       | expr '<<' expr                             -- 함수 합성 왼쪽

         -- 논리 연산
       | expr '||' expr
       | expr '&&' expr

         -- 비교 연산 (비결합, non-associative)
       | expr '=' expr | expr '<>' expr
       | expr '<' expr | expr '>' expr
       | expr '<=' expr | expr '>=' expr

         -- 사용자 정의 연산자
       | expr INFIXOP0 expr                         -- 비교 수준
       | expr INFIXOP1 expr                         -- 연결 수준 (우결합)
       | expr '::' expr                             -- cons (우결합)
       | expr INFIXOP2 expr                         -- 덧셈 수준

         -- 산술 연산
       | expr '+' expr | expr '-' expr

         -- 곱셈 수준
       | expr '*' expr | expr '/' expr | expr '%' expr
       | expr INFIXOP3 expr

         -- 지수 수준 (우결합)
       | expr INFIXOP4 expr

         -- 단항 / raise
       | '-' expr
       | 'raise' atom

         -- 뮤터블 필드 대입
       | atom '.' IDENT '<-' expr

         -- 인덱싱
       | atom '.[' expr ']' '<-' expr                  -- 인덱스 쓰기 (배열/해시테이블)

         -- 함수 적용 (좌결합, 최고 우선순위)
       | expr atom+

         -- 원자 표현식
       | atom
```

**컴파일러 노트:**
- 다중 파라미터 람다 `fun x y z -> e`는 파서가 `fun x -> fun y -> fun z -> e`로 디슈거.
- 인라인 try 핸들러 `try e with x -> h`는 파서가 `TryWith(e, [VarPat(x) -> h])`로 디슈거.
- 파이프/합성은 Elaboration에서 `App(f, x)` / 클로저 합성으로 디슈거.
- 논리 연산은 단락 평가, CFG 블록 생성.

### 3.1 원자 표현식 (Atomic Expressions)

```
atom ::= '(' ')'                                   -- unit
       | INT                                        -- 정수 리터럴
       | BOOL                                       -- 불리언 리터럴
       | STRING                                     -- 문자열 리터럴
       | CHAR                                       -- 문자 리터럴
       | IDENT                                      -- 변수 (소문자 시작)
       | UPPER                                      -- 인자 없는 생성자 (대문자 시작)
       | '(' expr ')'                               -- 괄호 묶음
       | '(' expr ':' type_expr ')'                 -- 타입 어노테이션
       | '(' expr ',' expr (',' expr)* ')'          -- 튜플
       | '[' ']'                                    -- 빈 리스트
       | '[' expr (';' expr)* ';'? ']'             -- 리스트 리터럴
       | '[' expr '..' expr ']'                     -- 범위 [start..stop]
       | '[' expr '..' expr '..' expr ']'           -- 스텝 범위 [start..step..stop]
       | '[' 'for' IDENT 'in' expr '->' expr ']'   -- 리스트 컴프리헨션
       | '[' 'for' IDENT 'in' expr '..' expr '->' expr ']'
       | '(' INFIXOP ')'                            -- 연산자를 값으로 사용
       | atom '.' IDENT                             -- 필드 접근 / 모듈 정규화 접근
       | atom '.[' expr ']'                          -- 인덱스 읽기 (좌결합)
       | atom '.[' expr '..' expr ']'               -- 문자열 슬라이싱 s.[start..stop]
       | atom '.[' expr '..' ']'                    -- 문자열 슬라이싱 s.[start..]
       | '{' field_binding (';' field_binding)* ';'? '}'          -- 레코드 생성
       | '{' expr 'with' field_binding (';' field_binding)* ';'? '}'  -- 레코드 갱신
```

### 3.2 매치 절 (Match Clauses)

```
match_clause ::= '|' or_pattern ('when' expr)? '->' expr

or_pattern   ::= pattern ('|' pattern)*
```

---

## 4. 패턴 (Patterns)

```
pattern ::= '_'                                     -- 와일드카드
          | IDENT                                   -- 변수 패턴 (소문자)
          | UPPER                                   -- 인자 없는 생성자 패턴 (대문자)
          | UPPER pattern                           -- 생성자 + 인자 패턴
          | INT                                     -- 정수 상수 패턴
          | '-' INT                                 -- 음수 정수 패턴
          | BOOL                                    -- 불리언 상수 패턴
          | STRING                                  -- 문자열 상수 패턴
          | CHAR                                    -- 문자 상수 패턴
          | '(' pattern ',' pattern (',' pattern)* ')' -- 튜플 패턴
          | pattern '::' pattern                    -- cons 패턴 (우결합)
          | '[' ']'                                 -- 빈 리스트 패턴
          | '[' pattern (';' pattern)* ';'? ']'    -- 리스트 리터럴 패턴 (cons 사슬로 변환)
          | '{' IDENT '=' pattern (';' IDENT '=' pattern)* ';'? '}'  -- 레코드 패턴
          | '(' pattern ')'                         -- 괄호 묶음

or_pattern    ::= pattern ('|' pattern)*            -- or-패턴 (한 절에 여러 대안)
tuple_pattern ::= '(' pattern ',' pattern (',' pattern)* ')'
```

`or_pattern`은 매치 절의 최상위에서만 허용된다.
각 패턴 대안은 동일한 바인딩 변수 집합을 가져야 한다.

매치 컴파일은 Jules Jacobs의 결정 트리 알고리즘을 사용한다.
`when` 가드는 결정 트리의 리프에서 조건 분기로 처리된다.

---

## 5. 타입 표현식 (Type Expressions)

```
type_expr ::= constraint_list '=>' arrow_type        -- 제약 타입 (v10.0)
            | tuple_type '->' type_expr             -- 함수 타입 (우결합)
            | tuple_type

constraint_list ::= constraint (',' constraint)*
constraint      ::= IDENT TYPE_VAR                   -- e.g., Show 'a

arrow_type ::= tuple_type '->' type_expr
             | tuple_type

tuple_type ::= atomic_type ('*' atomic_type)+       -- 튜플 타입 (2개 이상)
             | atomic_type

atomic_type ::= 'int'
              | 'bool'
              | 'string'
              | 'char'
              | 'unit'
              | TYPE_VAR                             -- 'a, 'b, ...
              | IDENT                               -- 명명된 타입 (Tree, Option, ...)
              | atomic_type 'list'                  -- 리스트 타입 (후위)
              | atomic_type IDENT                   -- 타입 적용 (e.g., int expr, 'a option)
              | '(' type_expr ')'
```

타입 별칭 선언(type alias)에서 RHS는 `type_alias_expr`로 제한된다.
이는 `IDENT`로 시작하는 bare named type을 제외하여 ADT 생성자와의 LALR(1) 충돌을 회피한다.

**컴파일러 노트:** 타입 표현식은 파싱되지만 코드 생성 시 무시된다. FunLangCompiler는 균일 표현(uniform representation: I64/I1/Ptr)을 사용하며 런타임 타입 검사를 수행하지 않는다. 제약 타입(`constraint_list =>`)은 `elaborateTypeclasses`에서 처리되어 런타임에 영향 없다.

---

## 6. 내장 타입 (Built-in Types)

```
-- 기본 스칼라 타입
int                          -- 정수 (LLVM i64)
bool                         -- true | false (LLVM i1/i64)
string                       -- UTF-8 문자열 ({i64 length, ptr data} 힙 구조체)
char                         -- 단일 문자 (i64, ASCII 코드)
unit                         -- 단위 타입; 유일한 값은 () (i64 0)

-- 복합 타입
'a list                      -- 불변 단일 연결 리스트 (cons cell: {ptr head, ptr tail})
'a array                     -- 가변 고정 크기 배열 (GC_malloc((n+1)*8), slot 0 = length)
('k, 'v) hashtable           -- 가변 해시 테이블 (C 런타임 chained buckets)
stringbuilder                -- 가변 문자열 빌더
hashset                      -- 가변 유일 원소 집합
queue                        -- 가변 FIFO 큐
mutablelist                  -- 가변 동적 리스트

-- 함수 타입
'a -> 'b                     -- 클로저 {fn_ptr, env} (균일 ABI: (ptr, i64) -> i64)

-- 튜플 타입
'a * 'b                      -- 2-튜플 (GC_malloc'd N-field 구조체)
'a * 'b * 'c                 -- 3-튜플
                             -- (n >= 2)

-- 예외 타입
exn                          -- 예외 (setjmp/longjmp C 런타임)
```

### 6.1 Prelude 타입

Prelude가 자동으로 정의하는 주요 타입:

```
type 'a Option = None | Some of 'a
type ('a, 'b) Result = Ok of 'a | Error of 'b
```

---

## 7. 내장 함수 (Built-in Functions)

FunLangCompiler(`fnc`)는 99개의 내장 함수를 `elaborateExpr`에서 직접 패턴 매치하여 컴파일한다.

### 7.1 문자열 연산 (String Operations)

| 함수 | 타입 | 설명 |
|------|------|------|
| `string_length` | `string -> int` | 문자열 길이 |
| `string_concat` | `string -> string -> string` | 두 문자열 연결 |
| `string_sub` | `string -> int -> int -> string` | 부분 문자열 (시작 인덱스, 길이) |
| `string_contains` | `string -> string -> bool` | 부분 문자열 포함 여부 |
| `to_string` | `'a -> string` | 임의 값을 문자열로 변환 |
| `string_to_int` | `string -> int` | 문자열을 정수로 파싱 (실패 시 예외) |
| `sprintf` | `string -> ... -> string` | 형식 문자열로 문자열 생성 (`%d`, `%s`, `%b`, `%%`) |
| `string_endswith` | `string -> string -> bool` | suffix 검사 |
| `string_startswith` | `string -> string -> bool` | prefix 검사 |
| `string_trim` | `string -> string` | 양쪽 공백 제거 |
| `string_concat_list` | `string -> string list -> string` | 구분자로 문자열 리스트 연결 |
| `string_split` | `string -> string -> string list` | 구분자로 분리 |
| `string_indexof` | `string -> string -> int` | 부분 문자열 위치 |
| `string_replace` | `string -> string -> string -> string` | 부분 문자열 치환 |
| `string_toupper` | `string -> string` | 대문자 변환 |
| `string_tolower` | `string -> string` | 소문자 변환 |

### 7.2 문자 연산 (Char Operations)

| 함수 | 타입 | 설명 |
|------|------|------|
| `char_to_int` | `char -> int` | 문자를 ASCII 코드로 변환 |
| `int_to_char` | `int -> char` | ASCII 코드를 문자로 변환 |
| `char_is_digit` | `char -> bool` | 숫자 문자 여부 |
| `char_is_letter` | `char -> bool` | 알파벳 문자 여부 |
| `char_is_upper` | `char -> bool` | 대문자 여부 |
| `char_is_lower` | `char -> bool` | 소문자 여부 |
| `char_to_upper` | `char -> char` | 대문자로 변환 |
| `char_to_lower` | `char -> char` | 소문자로 변환 |

### 7.3 출력 연산 (I/O Output)

| 함수 | 타입 | 구현 |
|------|------|------|
| `print` | `string -> unit` | `@lang_print` |
| `println` | `string -> unit` | `printf("%s\n")` |
| `printfn` | `string -> ... -> unit` | `println(sprintf(...))` 디슈거 |
| `eprint` / `eprintln` / `eprintfn` | stderr 변형 | 각각 `@lang_eprint*` |
| `sprintf` | `string -> ... -> string` | `@lang_sprintf_*` (포맷별 디스패치) |
| `stdin_read_line` / `stdin_read_all` | `unit -> string` | `@lang_stdin_*` |
| `get_args` | `unit -> string list` | `@lang_get_args` |
| `failwith` | `string -> 'a` | 예외 발생 |

### 7.4 파일/시스템

| 함수 | 설명 |
|------|------|
| `read_file`, `write_file`, `append_file`, `file_exists` | 파일 I/O |
| `read_lines`, `write_lines` | 줄 단위 I/O |
| `get_env`, `get_cwd`, `path_combine`, `dir_files` | 시스템 |

### 7.5 컬렉션

| 카테고리 | 함수들 |
|----------|--------|
| Array (12) | `create`, `init`, `get`, `set`, `length`, `of_list`, `to_list`, `iter`, `map`, `fold`, `sort`, `of_seq` |
| Hashtable (13) | `create`/`create_str`, `get`/`get_str`, `set`/`set_str`, `containsKey`/`_str`, `remove`/`_str`, `keys`/`keys_str`, `trygetvalue`, `count` |
| List (2) | `list_sort_by`, `list_of_seq` |
| StringBuilder (3) | `create`, `append`, `tostring` |
| HashSet (4) | `create`, `add`, `contains`, `count` |
| Queue (4) | `create`, `enqueue`, `dequeue`, `count` |
| MutableList (5) | `create`, `add`, `get`, `set`, `count` |

---

## 8. 연산자 우선순위 (Operator Precedence)

낮은 우선순위에서 높은 우선순위 순서로 나열한다.

| 레벨 | 연산자 | 결합성 | 설명 |
|------|--------|--------|------|
| 1 | `\|>` | 좌결합 | 파이프 오른쪽 |
| 2 | `>>` | 좌결합 | 함수 합성 오른쪽 |
| 3 | `<<` | 우결합 | 함수 합성 왼쪽 |
| 4 | `\|\|` | 좌결합 | 논리 OR (단락 평가) |
| 5 | `&&` | 좌결합 | 논리 AND (단락 평가) |
| 6 | `=` `<>` `<` `>` `<=` `>=` | 비결합 | 비교 연산 |
| 7 | `INFIXOP0` | 좌결합 | 사용자 정의 — `=` `<` `>` `\|` `&` `$` `!` 시작 |
| 8 | `INFIXOP1` `::` | 우결합 | 사용자 정의 — `@` `^` 시작; cons |
| 9 | `+` `-` `INFIXOP2` | 좌결합 | 덧셈; 사용자 정의 `+` `-` 시작 |
| 10 | `*` `/` `%` `INFIXOP3` | 좌결합 | 곱셈; 사용자 정의 `*` `/` `%` 시작 |
| 11 | `INFIXOP4` | 우결합 | 사용자 정의 — `**` 시작 |
| 12 | 함수 적용 | 좌결합 | `f x` |
| 13 | 단항 `-`, `raise` | 전위 | |

단일 문자 연산자(`+`, `-`, `*`, `/`, `%`, `<`, `>`, `=`)는 항상 내장 토큰으로 렉싱되며
사용자 정의 `INFIXOP`으로 처리되지 않는다.

---

## 9. Prelude 모듈 (Prelude Modules)

13개 Prelude 파일이 컴파일 전에 별도 파싱되어 자동 로드된다:

| 모듈 | 주요 함수 | 비고 |
|------|----------|------|
| Typeclass | `show`, `eq` | 빌트인 인스턴스 (int/bool/string/char) |
| Core | `id`, `const`, `compose`, `flip`, `apply`, `(^^)`, `not`, `min`, `max`, `abs`, `fst`, `snd`, `ignore` | `open Core` |
| Option | `optionMap`, `optionBind`, `optionDefault`, `isSome`, `isNone`, `(<\|>)`, `optionIter`, `optionFilter`, `optionDefaultValue` | `open Option` |
| Result | `resultMap`, `resultBind`, `resultMapError`, `resultDefault`, `isOk`, `isError`, `resultIter`, `resultToOption`, `resultDefaultValue` | `open Result` |
| List | `map`, `filter`, `fold`, `length`, `reverse`, `append`, `hd`/`head`, `tl`/`tail`, `zip`, `take`, `drop`, `any`/`exists`, `all`, `flatten`, `nth`, `(++)`, `isEmpty`, `sort`, `sortBy`, `mapi`, `tryFind`, `choose`, `distinctBy`, `ofSeq`, `init`, `find`, `findIndex`, `partition`, `groupBy`, `scan`, `replicate`, `collect`, `pairwise`, `sumBy`, `sum`, `minBy`, `maxBy`, `contains`, `unzip`, `forall`, `iter` | `open List` |
| Array | `create`, `get`, `set`, `length`, `ofList`, `toList`, `iter`, `map`, `fold`, `init`, `sort`, `ofSeq` | |
| String | `concat`, `endsWith`, `startsWith`, `trim`, `length`, `contains`, `split`, `indexOf`, `replace`, `toUpper`, `toLower`, `join`, `substring` | |
| Char | `IsDigit`, `ToUpper`, `IsLetter`, `IsUpper`, `IsLower`, `ToLower` | |
| Hashtable | `create`, `createStr`, `get`, `set`, `containsKey`, `keys`, `keysStr`, `remove`, `tryGetValue`, `count` | Backend 전용 `createStr`/`keysStr` 포함 |
| HashSet | `create`, `add`, `contains`, `count` | |
| MutableList | `create`, `add`, `get`, `set`, `count` | |
| Queue | `create`, `enqueue`, `dequeue`, `count` | |
| StringBuilder | `create`, `add`, `toString` | |

---

## 10. FunLang와의 비교 (Comparison with FunLang)

### 동일한 부분 (Identical)

| 영역 | 상세 |
|------|------|
| 파서/렉서 | FunLang의 `Parser.fsy` / `Lexer.fsl` / `IndentFilter.fs`를 그대로 재사용 |
| AST | FunLang의 `Ast.fs` 타입 정의를 그대로 사용 |
| 모든 표현식 문법 | 47개 Expr 변형 전부 지원 |
| 모든 패턴 문법 | 9개 Pattern 타입 전부 지원 |
| 모든 선언 문법 | `module`, `open`, `type`, `exception`, `let rec`, `let mut`, `typeclass`, `instance`, `deriving` 등 |
| 중첩 모듈 | `Outer.Inner.value` qualified access, `open Outer.Inner` 지원 |
| 연산자 우선순위 | 13단계 전부 동일 |

### 다른 부분 (Differences)

| 영역 | FunLang | FunLangCompiler | 이유 |
|------|-----------|-------------|------|
| 실행 방식 | 트리-워킹 인터프리터 | MLIR → LLVM → 네이티브 바이너리 | 핵심 아키텍처 차이 |
| 타입 검사 | HM 타입 추론 (`Infer.fs`, `Bidir.fs`) | 없음 (균일 표현 I64/Ptr) | 코드 생성에 불필요 |
| GADT 생성자 | `UPPER ':' T1 '->' T2` 완전 지원 | 파싱만 됨, 일반 생성자로 처리 | 타입 시스템 없이 GADT 불가 |
| 타입 별칭 | `type alias = ...` 타입 수준에서 사용 | 파싱만 됨, 코드 생성 시 무시 | |
| 슈퍼클래스/조건부 인스턴스 | 타입 레벨 제약 강제 | 파싱 및 elaboration OK, 제약 미강제 | 타입 시스템 없음 |
| Hashtable | 정수/문자열 키 통합 | `create`/`create_str` 분리 | C 런타임 ABI 차이 |
| GC | .NET GC | Boehm GC (libgc) | 런타임 환경 차이 |
| 예외 처리 | .NET 예외 | setjmp/longjmp | C 수준 구현 |
| 정수 타입 | .NET int32 | LLVM i64 | 아키텍처 차이 |

### 파서 디슈거 (Parser-level Desugaring)

다음 문법 형태는 파서가 자동으로 변환하여 컴파일러가 별도 처리할 필요 없음:

| 구문 | 디슈거 결과 |
|------|------------|
| `fun x y z -> e` | `fun x -> fun y -> fun z -> e` (nested Lambda) |
| `try e with x -> h` | `TryWith(e, [(VarPat(x), None, h)])` |
| `e1; e2` | `let _ = e1 in e2` (LetPat) |
| `if c then e` | `if c then e else ()` |
| `e1 \|> f` | `App(f, e1)` |
| `f >> g` | `fun x -> g(f(x))` |

### MLIR 코드 생성 고유 사항

| 기능 | 설명 |
|------|------|
| 클로저 ABI | `{fn_ptr, env}` 구조체, 균일 `(ptr, i64) -> i64` 시그니처 |
| 모듈 평탄화 | `flattenDecls`로 `M.f` → `M_f`, `M.N.f` → `M_N_f` 변환, 런타임 모듈 없음 |
| 연산자 심볼명 | `sanitizeMlirName`으로 `^^` → `_caret__caret_` 등 변환 |
| 블록 패칭 | FIX-02/FIX-04 패턴으로 중첩된 if/match의 CFG 블록 올바르게 연결 |
| 2-lambda 함수 | maker + inner 함수 쌍으로 커링 구현, `KnownFuncs` 직접 호출 최적화 |

---

## 11. 들여쓰기 규칙 (Indentation Rules)

FunLang와 동일. `IndentFilter`가 raw 토큰에서 `INDENT`/`DEDENT`를 생성한다.

- 탭 문자 사용 금지 (스페이스만)
- 표현식 컨텍스트의 `let`은 오프사이드 규칙으로 암묵적 `in` 삽입
- `match` / `try` 뒤의 `|`는 키워드 열에 정렬
- 모듈 컨텍스트의 `let`은 `in` 삽입 없음
