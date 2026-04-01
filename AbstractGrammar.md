# FunLangCompiler Abstract Grammar

## 소개

이 문서는 FunLangCompiler 컴파일러가 지원하는 추상 문법을 정의한다.
FunLangCompiler는 LangThree의 파서/AST를 재사용하며, `Elaboration.fs`에서 AST를 MLIR IR로 변환한다.
따라서 **파싱 가능한 문법은 LangThree와 동일**하며, 이 문서는 컴파일러가 실제로 코드 생성하는 범위를 기술한다.

---

## 표기법 (Notation)

| 표기 | 의미 |
|------|------|
| `::=` | 정의 |
| `\|` | 선택 (alternatives) |
| `*` | 0회 이상 반복 |
| `+` | 1회 이상 반복 |
| `?` | 0 또는 1회 (optional) |
| `'token'` | 리터럴 토큰 |
| `IDENT` | 소문자 시작 식별자 |
| `UPPER` | 대문자 시작 식별자 (생성자) |
| `INT` | 정수 리터럴 |
| `BOOL` | `true` 또는 `false` |
| `STRING` | 문자열 리터럴 |
| `CHAR` | 문자 리터럴 |
| `INFIXOP` | 사용자 정의 중위 연산자 |

---

## 1. 프로그램과 모듈 (Programs and Modules)

```
program     ::= 'module' qualified_ident decl* EOF
             |  'namespace' qualified_ident decl* EOF
             |  decl* EOF

qualified_ident ::= IDENT ('.' IDENT)*
```

Prelude 파일(`Prelude/*.fun`)은 컴파일 전에 소스 앞에 자동 연결된다.
`open "file.fun"` 파일 임포트는 `Program.fs`의 `expandImports`에서 AST 수준으로 인라인된다.

---

## 2. 선언 (Declarations)

```
decl ::= 'let' IDENT '=' expr
       | 'let' IDENT param+ '=' expr
       | 'let' IDENT param+ ':' type_expr '=' expr    -- 반환 타입 어노테이션
       | 'let' IDENT '(' ')' '=' expr
       | 'let' '(' op_name ')' param+ '=' expr
       | 'let' tuple_pattern '=' expr
       | 'let' '_' '=' expr
       | 'let' IDENT '=' expr 'in' expr

       | 'let' 'rec' IDENT param+ '=' expr
           ('and' IDENT param+ '=' expr)*
       | 'let' 'rec' IDENT param+ ':' type_expr '=' expr  -- let rec 반환 타입
       | 'let' 'rec' '(' op_name ')' param+ '=' expr

       | 'let' ('mut' | 'mutable') IDENT '=' expr

       | 'type' IDENT type_var* '=' constructor ('|' constructor)*
       | 'type' IDENT '<' type_var (',' type_var)* '>' '=' constructor ('|' constructor)*
       | 'type' IDENT type_var* '=' '{' field_decl (';' field_decl)* ';'? '}'
       | 'type' IDENT '<' type_var (',' type_var)* '>' '=' '{' field_decl (';' field_decl)* ';'? '}'

       | 'exception' IDENT ('of' type_expr)?

       | 'module' IDENT '=' decl+

       | 'open' IDENT                                  -- 모듈 열기
       | 'open' STRING                                 -- 파일 임포트

param    ::= IDENT
           | '(' IDENT ':' type_expr ')'               -- 타입 어노테이션 파라미터
op_name  ::= INFIXOP0 | INFIXOP1 | INFIXOP2 | INFIXOP3 | INFIXOP4
```

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
```

GADT 생성자(`UPPER ':' type_expr '->' type_expr`)는 파싱은 되지만 정교화에서 일반 생성자로 처리된다.

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
       | 'let' IDENT param+ ':' type_expr '=' expr 'in' expr  -- 반환 타입
       | 'let' IDENT '(' ')' '=' expr 'in' expr
       | 'let' tuple_pattern '=' expr 'in' expr
       | 'let' '_' '=' expr 'in' expr
       | 'let' 'rec' IDENT param+ '=' expr 'in' expr
       | 'let' 'rec' IDENT param+ ':' type_expr '=' expr 'in' expr  -- let rec 반환 타입
       | 'let' ('mut' | 'mutable') IDENT '=' expr 'in' expr
       | IDENT '<-' expr                               -- 가변 변수 재할당

         -- 패턴 매칭 / 예외 처리
       | 'match' expr 'with' match_clause+
       | 'try' expr 'with' match_clause+

         -- 람다
       | 'fun' IDENT '->' expr
       | 'fun' '(' IDENT ':' type_expr ')' '->' expr   -- 어노테이션 람다
       | 'fun' param+ '->' expr                        -- 커리된 어노테이션 파라미터
       | 'fun' '(' ')' '->' expr
       | 'fun' tuple_pattern '->' expr

         -- 제어 흐름
       | 'if' expr 'then' expr 'else' expr
       | 'if' expr 'then' expr                        -- else 없는 if (unit 반환)

         -- 시퀀싱
       | expr ';' expr                                 -- let _ = e1 in e2로 디슈거

         -- 루프
       | 'while' expr 'do' expr
       | 'for' IDENT '=' expr 'to' expr 'do' expr
       | 'for' IDENT '=' expr 'downto' expr 'do' expr
       | 'for' IDENT 'in' expr 'do' expr
       | 'for' tuple_pattern 'in' expr 'do' expr

         -- 파이프 및 합성
       | expr '|>' expr                             -- App(f, x)로 디슈거
       | expr '>>' expr                             -- fun x -> g(f(x))로 디슈거
       | expr '<<' expr                             -- fun x -> f(g(x))로 디슈거

         -- 논리 연산 (단락 평가, CFG 블록 생성)
       | expr '||' expr
       | expr '&&' expr

         -- 비교 연산
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
       | expr '*' expr | expr '/' expr | expr '%' expr
       | expr INFIXOP3 expr
       | expr INFIXOP4 expr                         -- 지수 수준 (우결합)

         -- 단항 / raise
       | '-' expr
       | 'raise' atom

         -- 필드 대입
       | atom '.' IDENT '<-' expr

         -- 인덱싱
       | atom '.[' expr ']' '<-' expr               -- 인덱스 쓰기

         -- 함수 적용 (좌결합, 최고 우선순위)
       | expr atom+

         -- 원자 표현식
       | atom
```

### 3.1 원자 표현식 (Atomic Expressions)

```
atom ::= '(' ')'                                   -- unit
       | INT                                        -- 정수 리터럴
       | BOOL                                       -- 불리언 리터럴
       | STRING                                     -- 문자열 리터럴
       | CHAR                                       -- 문자 리터럴
       | IDENT                                      -- 변수
       | UPPER                                      -- 인자 없는 생성자
       | '(' expr ')'                               -- 괄호 묶음
       | '(' expr ':' type_expr ')'                 -- 타입 어노테이션 (코드 생성 시 무시)
       | '(' expr ',' expr (',' expr)* ')'          -- 튜플
       | '[' ']'                                    -- 빈 리스트
       | '[' expr (';' expr)* ';'? ']'             -- 리스트 리터럴
       | '[' expr '..' expr ']'                     -- 범위
       | '[' expr '..' expr '..' expr ']'           -- 스텝 범위
       | '[' 'for' IDENT 'in' expr '->' expr ']'   -- 리스트 컴프리헨션
       | '[' 'for' IDENT 'in' expr '..' expr '->' expr ']'
       | '(' INFIXOP ')'                            -- 연산자를 값으로 사용
       | atom '.' IDENT                             -- 필드 접근 / 모듈 정규화 접근
       | atom '.[' expr ']'                          -- 인덱스 읽기
       | atom '.[' expr '..' expr ']'               -- 문자열 슬라이싱
       | atom '.[' expr '..' ']'                    -- 문자열 슬라이싱 (끝까지)
       | '{' field_binding (';' field_binding)* '}' -- 레코드 생성
       | '{' expr 'with' field_binding+ '}'         -- 레코드 갱신
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
          | IDENT                                   -- 변수 패턴
          | UPPER                                   -- 인자 없는 생성자 패턴
          | UPPER pattern                           -- 생성자 + 인자 패턴
          | INT | '-' INT                           -- 정수 상수 패턴
          | BOOL                                    -- 불리언 상수 패턴
          | STRING                                  -- 문자열 상수 패턴
          | CHAR                                    -- 문자 상수 패턴
          | '(' pattern ',' pattern (',' pattern)* ')' -- 튜플 패턴
          | pattern '::' pattern                    -- cons 패턴 (우결합)
          | '[' ']'                                 -- 빈 리스트 패턴
          | '[' pattern (';' pattern)* ']'         -- 리스트 리터럴 패턴
          | '{' IDENT '=' pattern (';' IDENT '=' pattern)* '}' -- 레코드 패턴
          | '(' pattern ')'                         -- 괄호 묶음

or_pattern    ::= pattern ('|' pattern)*
tuple_pattern ::= '(' pattern ',' pattern (',' pattern)* ')'
```

매치 컴파일은 Jules Jacobs의 결정 트리 알고리즘을 사용한다.
`when` 가드는 결정 트리의 리프에서 조건 분기로 처리된다.

---

## 5. 타입 표현식 (Type Expressions)

```
type_expr ::= tuple_type '->' type_expr             -- 함수 타입 (우결합)
            | tuple_type

tuple_type ::= atomic_type ('*' atomic_type)+       -- 튜플 타입
             | atomic_type

atomic_type ::= 'int' | 'bool' | 'string' | 'char' | 'unit'
              | TYPE_VAR                             -- 'a, 'b, ...
              | IDENT                               -- 명명된 타입
              | IDENT '<' type_expr (',' type_expr)* '>'  -- 앵글 브래킷 제네릭: Result<'a>, Map<'k,'v>
              | atomic_type 'list'                  -- 리스트 타입 (후위)
              | atomic_type IDENT                   -- 타입 적용 (후위): int option
              | '(' type_expr ')'
```

**주의:** 타입 표현식은 파싱되지만 코드 생성 시 무시된다. FunLangCompiler는 균일 표현(uniform representation: I64/I1/Ptr)을 사용하며 런타임 타입 검사를 수행하지 않는다.

**파라미터/반환 타입 어노테이션:** 파서는 `let f (x : int) : bool = ...` 형태를 완전히 지원한다. AST에서 `Annot`(반환 타입)과 `LambdaAnnot`(파라미터 타입)으로 표현되며, Elaboration.fs에서 래퍼를 벗겨 내부 표현식만 코드 생성한다. 단, `Annot`/`LambdaAnnot` 래퍼가 `elaborateExpr`의 핵심 패턴 매칭(2-lambda KnownFuncs 인식, LetRec 반환 타입 결정 등)을 방해하는 알려진 버그가 있다. 자세한 내용은 `survey/funlexyacc-type-annotation-incompatibility.md` Section 8 참조.

---

## 6. 연산자 우선순위 (Operator Precedence)

| 레벨 | 연산자 | 결합성 | 설명 |
|------|--------|--------|------|
| 1 | `\|>` | 좌결합 | 파이프 |
| 2 | `>>` | 좌결합 | 함수 합성 오른쪽 |
| 3 | `<<` | 우결합 | 함수 합성 왼쪽 |
| 4 | `\|\|` | 좌결합 | 논리 OR (단락 평가) |
| 5 | `&&` | 좌결합 | 논리 AND (단락 평가) |
| 6 | `=` `<>` `<` `>` `<=` `>=` | 비결합 | 비교 |
| 7 | `INFIXOP0` | 좌결합 | 사용자 정의 — `=` `<` `>` `\|` `&` `$` `!` 시작 |
| 8 | `INFIXOP1` `::` | 우결합 | 사용자 정의 — `@` `^` 시작; cons |
| 9 | `+` `-` `INFIXOP2` | 좌결합 | 덧셈; 사용자 정의 `+` `-` 시작 |
| 10 | `*` `/` `%` `INFIXOP3` | 좌결합 | 곱셈; 사용자 정의 `*` `/` `%` 시작 |
| 11 | `INFIXOP4` | 우결합 | 사용자 정의 — `**` 시작 |
| 12 | 함수 적용 | 좌결합 | `f x` |
| 13 | 단항 `-`, `raise` | 전위 | |

---

## 7. 내장 함수 (Built-in Functions)

FunLangCompiler는 89개의 내장 함수를 `elaborateExpr`에서 직접 패턴 매치하여 컴파일한다.

### 7.1 I/O

| 함수 | 타입 | 구현 |
|------|------|------|
| `print` | `string -> unit` | `@lang_print` |
| `println` | `string -> unit` | `printf("%s\n")` |
| `printfn` | `string -> ... -> unit` | `println(sprintf(...))` 디슈거 |
| `eprint` / `eprintln` / `eprintfn` | stderr 변형 | 각각 `@lang_eprint*` |
| `sprintf` | `string -> ... -> string` | `@lang_sprintf_*` (포맷별 디스패치) |
| `stdin_read_line` / `stdin_read_all` | `unit -> string` | `@lang_stdin_*` |
| `get_args` | `unit -> string list` | `@lang_get_args` |

### 7.2 문자열/문자

| 함수 | C 런타임 |
|------|----------|
| `string_length`, `string_concat`, `string_sub`, `string_contains` | `@lang_string_*` |
| `string_startswith`, `string_endswith`, `string_trim` | `@lang_string_*` |
| `string_concat_list`, `string_to_int`, `to_string` | `@lang_string_*` / `@lang_to_string_*` |
| `char_to_int`, `int_to_char` | 인라인 I64 변환 |
| `char_is_digit`, `char_is_letter`, `char_is_upper`, `char_is_lower` | `@lang_char_*` |
| `char_to_upper`, `char_to_lower` | `@lang_char_*` |

### 7.3 컬렉션

| 카테고리 | 함수들 |
|----------|--------|
| Array (11) | `create`, `init`, `get`, `set`, `length`, `of_list`, `to_list`, `iter`, `map`, `fold`, `sort`, `of_seq` |
| Hashtable (13) | `create`/`create_str`, `get`/`get_str`, `set`/`set_str`, `containsKey`/`_str`, `remove`/`_str`, `keys`/`keys_str`, `trygetvalue`, `count` |
| List (2) | `list_sort_by`, `list_of_seq` |
| StringBuilder (3) | `create`, `append`, `tostring` |
| HashSet (4) | `create`, `add`, `contains`, `count` |
| Queue (4) | `create`, `enqueue`, `dequeue`, `count` |
| MutableList (5) | `create`, `add`, `get`, `set`, `count` |

### 7.4 파일/시스템

| 함수 | 설명 |
|------|------|
| `read_file`, `write_file`, `append_file`, `file_exists` | 파일 I/O |
| `read_lines`, `write_lines` | 줄 단위 I/O |
| `get_env`, `get_cwd`, `path_combine`, `dir_files` | 시스템 |
| `failwith` | 예외 발생 |

---

## 8. Prelude 모듈 (Prelude Modules)

12개 Prelude 파일이 컴파일 전에 자동 로드된다:

| 모듈 | 주요 함수 | 비고 |
|------|----------|------|
| Core | `id`, `const`, `compose`, `flip`, `apply`, `(^^)`, `not`, `min`, `max`, `abs`, `fst`, `snd`, `ignore` | `open Core` |
| Option | `optionMap`, `optionBind`, `optionDefault`, `isSome`, `isNone`, `(<\|>)`, `optionIter`, `optionFilter`, `optionDefaultValue` | `open Option` |
| Result | `resultMap`, `resultBind`, `resultMapError`, `resultDefault`, `isOk`, `isError`, `resultIter`, `resultToOption`, `resultDefaultValue` | `open Result` |
| List | `map`, `filter`, `fold`, `length`, `reverse`, `append`, `hd`/`head`, `tl`/`tail`, `zip`, `take`, `drop`, `any`/`exists`, `all`, `flatten`, `nth`, `(++)`, `isEmpty`, `sort`, `sortBy`, `mapi`, `tryFind`, `choose`, `distinctBy`, `ofSeq` | `open List` |
| Array | `create`, `get`, `set`, `length`, `ofList`, `toList`, `iter`, `map`, `fold`, `init`, `sort`, `ofSeq` | |
| String | `concat`, `endsWith`, `startsWith`, `trim`, `length`, `contains` | |
| Char | `IsDigit`, `ToUpper`, `IsLetter`, `IsUpper`, `IsLower`, `ToLower` | |
| Hashtable | `create`, `createStr`, `get`, `set`, `containsKey`, `keys`, `keysStr`, `remove`, `tryGetValue`, `count` | Backend 전용 `createStr`/`keysStr` 포함 |
| HashSet | `create`, `add`, `contains`, `count` | |
| MutableList | `create`, `add`, `get`, `set`, `count` | |
| Queue | `create`, `enqueue`, `dequeue`, `count` | |
| StringBuilder | `create`, `add`, `toString` | |

---

## 9. LangThree와의 비교 (Comparison with LangThree)

### 동일한 부분 (Identical)

| 영역 | 상세 |
|------|------|
| 파서/렉서 | LangThree의 `Parser.fsy` / `Lexer.fsl` / `IndentFilter.fs`를 그대로 재사용 |
| AST | LangThree의 `Ast.fs` 타입 정의를 그대로 사용 |
| 모든 표현식 문법 | 47개 Expr 변형 전부 지원 |
| 모든 패턴 문법 | 9개 Pattern 타입 전부 지원 |
| 모든 선언 문법 | `module`, `open`, `type`, `exception`, `let rec`, `let mut` 등 |
| Prelude 내용 | 11/12 파일이 LangThree와 바이트 동일 |
| 연산자 우선순위 | 13단계 전부 동일 |

### 다른 부분 (Differences)

| 영역 | LangThree | FunLangCompiler | 이유 |
|------|-----------|-------------|------|
| 실행 방식 | 트리-워킹 인터프리터 | MLIR → LLVM → 네이티브 바이너리 | 핵심 아키텍처 차이 |
| 타입 검사 | HM 타입 추론 (`Infer.fs`, `Bidir.fs`) | 없음 (균일 표현 I64/Ptr) | 코드 생성에 불필요 |
| 타입 어노테이션 | 파라미터/반환 타입 완전 지원 | 파싱 OK, Elaboration 패턴 매칭 버그 있음 | `stripAnnot` 수정 필요 |
| GADT 생성자 | `UPPER ':' T1 '->' T2` 완전 지원 | 파싱만 됨, 일반 생성자로 처리 | 타입 시스템 없이 GADT 불가 |
| 타입 별칭 | `type alias = ...` 타입 수준에서 사용 | 파싱만 됨, 코드 생성 시 무시 | |
| `open` 범위 | 다중 세그먼트 (`open A.B`) 지원 | 단일 세그먼트 (`open A`)만 지원 | Prelude에서 불필요 |
| Hashtable | 정수/문자열 키 통합 | `create`/`create_str` 분리 | C 런타임 ABI 차이 |
| GC | .NET GC | Boehm GC (libgc) | 런타임 환경 차이 |
| 예외 처리 | .NET 예외 | setjmp/longjmp | C 수준 구현 |
| 정수 타입 | .NET int32 | LLVM i64 | 아키텍처 차이 |

### MLIR 코드 생성 고유 사항

| 기능 | 설명 |
|------|------|
| 클로저 ABI | `{fn_ptr, env}` 구조체, 균일 `(ptr, i64) -> i64` 시그니처 |
| 모듈 평탄화 | `flattenDecls`로 `M.f` → `M_f` 변환, 런타임 모듈 없음 |
| 연산자 심볼명 | `sanitizeMlirName`으로 `^^` → `_caret__caret_` 등 변환 |
| 블록 패칭 | FIX-02/FIX-04 패턴으로 중첩된 if/match의 CFG 블록 올바르게 연결 |
| 2-lambda 함수 | maker + inner 함수 쌍으로 커링 구현, `KnownFuncs` 직접 호출 최적화 |

---

## 10. 들여쓰기 규칙 (Indentation Rules)

LangThree와 동일. `IndentFilter`가 raw 토큰에서 `INDENT`/`DEDENT`를 생성한다.

- 탭 문자 사용 금지 (스페이스만)
- 표현식 컨텍스트의 `let`은 오프사이드 규칙으로 암묵적 `in` 삽입
- `match` / `try` 뒤의 `|`는 키워드 열에 정렬
- 모듈 컨텍스트의 `let`은 `in` 삽입 없음
