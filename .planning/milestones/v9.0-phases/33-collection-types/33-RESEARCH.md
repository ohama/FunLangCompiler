# Phase 33: Collection Types - Research

**Researched:** 2026-03-29
**Domain:** FunLangCompiler C runtime + Elaboration.fs — four new collection types (StringBuilder, HashSet, Queue, MutableList)
**Confidence:** HIGH

## Summary

Phase 33 adds four new mutable collection types — StringBuilder (COL-01), HashSet (COL-02), Queue (COL-03), and MutableList (COL-04). Each follows the established three-layer pattern: (1) a new C struct + functions in `lang_runtime.c`, (2) a header declaration in `lang_runtime.h`, and (3) elaboration arms + externalFuncs entries in `Elaboration.fs`. All four types are fully absent from the compiler today; Phase 32 is complete and all its builtins are in place.

The LangThree reference interpreter (Eval.fs) defines the exact semantics for all four types using .NET collection classes. The builtin naming in LangThree uses `stringbuilder_append` (not `stringbuilder_add`); the Phase 33 description says "add" but the correct canonical name from LangThree is `stringbuilder_append`. All other types use `hashset_add`, `queue_enqueue`/`queue_dequeue`, `mutablelist_add`/`mutablelist_get`/`mutablelist_set`/`mutablelist_count` — matching LangThree exactly.

The dominant pattern is: create-function returns `Ptr`, most mutating functions return void (→ `LlvmCallVoidOp` + `ArithConstantOp(unitVal, 0L)` for unit), query functions return I64. All new structs use `GC_malloc`. HashSet reuses the existing `lang_ht_hash` murmurhash3 infrastructure from LangHashtable. Queue uses a linked-list (LangCons) or a head/tail pointer struct. MutableList uses a growable int64_t array.

**Primary recommendation:** Implement all four types as C structs in lang_runtime.c following the LangHashtable pattern, use the exact builtin names from LangThree, and follow the established void-return/unit pattern for all mutating operations.

## Standard Stack

### Core
| Component | Location | Purpose | Why Standard |
|-----------|----------|---------|--------------|
| lang_runtime.c | src/FunLangCompiler.Compiler/lang_runtime.c | C runtime — new structs + functions | All collection implementations live here; 880 lines established |
| lang_runtime.h | src/FunLangCompiler.Compiler/lang_runtime.h | Header declarations for new types | All structs and function prototypes must be declared here |
| Elaboration.fs | src/FunLangCompiler.Compiler/Elaboration.fs | AST-to-MLIR translation | Pattern-matches on `App(Var("builtin_name"))`, emits MLIR ops; 3126 lines |

### Supporting
| Component | Purpose | When to Use |
|-----------|---------|-------------|
| GC_malloc | Heap allocation | All struct and buffer allocations — never malloc |
| lang_ht_hash | murmurhash3 for int64 keys | HashSet bucket dispatch — already static in lang_runtime.c |
| LlvmCallVoidOp + ArithConstantOp | Void return → unit | All mutating ops (hashset_add returns bool, queue_enqueue void, mutablelist_add void) |
| LlvmCallOp | C call returning a value | create functions (→ Ptr), count functions (→ I64), get functions (→ I64) |
| externalFuncs list (×2) | MLIR module external declarations | Lines 2853 and 3058 in Elaboration.fs — both must be updated |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Linked-list Queue | Circular buffer Queue | Linked list simpler to implement and GC-friendly; circular buffer needs modular arithmetic + resize |
| Separate HashSet C struct | Reuse LangHashtable with dummy values | New struct is cleaner; reuse would muddle semantics and `lang_index_get` dispatch |
| Growable array for MutableList | Static-sized array | Must support arbitrary `.Add()` calls; growable (cap doubling) is the correct approach |
| stringbuilder_append name | stringbuilder_add | LangThree uses `stringbuilder_append`; phase description says "add" — use `stringbuilder_append` to match LangThree |

**Installation:** No new packages. Uses existing `<stdlib.h>` (`realloc` for StringBuilder/MutableList) and murmurhash3 already in lang_runtime.c.

## Architecture Patterns

### New Struct Layouts

```c
// COL-01: StringBuilder
typedef struct {
    char*   buf;    // character buffer
    int64_t len;    // current length (number of chars written)
    int64_t cap;    // buffer capacity
} LangStringBuilder;

// COL-02: HashSet (reuses murmurhash3 from LangHashtable)
typedef struct LangHashSetEntry {
    int64_t key;
    struct LangHashSetEntry* next;
} LangHashSetEntry;

typedef struct {
    int64_t capacity;
    int64_t size;
    LangHashSetEntry** buckets;
} LangHashSet;

// COL-03: Queue (singly-linked list with head and tail pointers)
typedef struct LangQueueNode {
    int64_t value;
    struct LangQueueNode* next;
} LangQueueNode;

typedef struct {
    LangQueueNode* head;  // dequeue from front
    LangQueueNode* tail;  // enqueue at back
    int64_t        count;
} LangQueue;

// COL-04: MutableList (growable int64_t array)
typedef struct {
    int64_t* data;  // heap-allocated array of int64_t
    int64_t  len;   // current number of elements
    int64_t  cap;   // allocated capacity
} LangMutableList;
```

### Pattern 1: Create Functions — return Ptr, discard unit arg
```fsharp
// Source: Elaboration.fs (mirrors hashtable_create pattern)
| App (Var ("stringbuilder_create", _), unitExpr, _) ->
    let (_uVal, uOps) = elaborateExpr env unitExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, uOps @ [LlvmCallOp(result, "@lang_sb_create", [])])
```
Same shape for `hashset_create`, `queue_create`, `mutablelist_create`.

### Pattern 2: Mutating Two-Arg Functions Returning Unit
```fsharp
// Source: Elaboration.fs (mirrors hashtable_set void pattern but two args)
| App (App (Var ("hashset_add", _), hsExpr, _), valExpr, _) ->
    let (hsVal,  hsOps)  = elaborateExpr env hsExpr
    let (valVal, valOps) = elaborateExpr env valExpr
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, hsOps @ valOps @ [LlvmCallVoidOp("@lang_hashset_add", [hsVal; valVal]); ArithConstantOp(unitVal, 0L)])
```
Note: LangThree's `hashset_add` returns `bool` (whether newly added). For Phase 33, the phase description says "add" only — the return value is used in some FunLexYacc code. However, the phase success criteria just needs compilation to work. To match LangThree exactly: `hashset_add` returns `I64` (1 if new, 0 if duplicate). Use `LlvmCallOp` (not Void) for `hashset_add`. Use `LlvmCallVoidOp` for `queue_enqueue` and `mutablelist_add`.

### Pattern 3: Query Functions Returning I64 (count)
```fsharp
// Source: Elaboration.fs (mirrors array_length or hashtable_count pattern)
| App (Var ("hashset_count", _), hsExpr, _) ->
    let (hsVal, hsOps) = elaborateExpr env hsExpr
    let result = { Name = freshName env; Type = I64 }
    (result, hsOps @ [LlvmCallOp(result, "@lang_hashset_count", [hsVal])])
```
Same shape for `queue_count`, `mutablelist_count`.

### Pattern 4: Contains/Get Functions Returning I64
```fsharp
// hashset_contains: HashSet -> 'a -> bool (I64)
| App (App (Var ("hashset_contains", _), hsExpr, _), valExpr, _) ->
    let (hsVal,  hsOps)  = elaborateExpr env hsExpr
    let (valVal, valOps) = elaborateExpr env valExpr
    let result = { Name = freshName env; Type = I64 }
    (result, hsOps @ valOps @ [LlvmCallOp(result, "@lang_hashset_contains", [hsVal; valVal])])

// mutablelist_get: MutableList -> int -> 'a (I64)
| App (App (Var ("mutablelist_get", _), mlExpr, _), idxExpr, _) ->
    let (mlVal,  mlOps)  = elaborateExpr env mlExpr
    let (idxVal, idxOps) = elaborateExpr env idxExpr
    let idxI64 =
        if idxVal.Type = I64 then (idxVal, [])
        else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, idxVal)])
    let (idxV, idxCoerce) = idxI64
    let result = { Name = freshName env; Type = I64 }
    (result, mlOps @ idxOps @ idxCoerce @ [LlvmCallOp(result, "@lang_mlist_get", [mlVal; idxV])])
```

### Pattern 5: queue_dequeue — two-arg (queue, unit discarded)
```fsharp
// queue_dequeue queue (): elaborate unit arg and discard, call lang_queue_dequeue → I64
| App (App (Var ("queue_dequeue", _), qExpr, _), unitExpr, _) ->
    let (qVal,  qOps) = elaborateExpr env qExpr
    let (_uVal, uOps) = elaborateExpr env unitExpr
    let result = { Name = freshName env; Type = I64 }
    (result, qOps @ uOps @ [LlvmCallOp(result, "@lang_queue_dequeue", [qVal])])
```

### Pattern 6: stringbuilder_append — two-arg, returning Ptr (the StringBuilder itself for chaining)
```fsharp
// stringbuilder_append sb str: call lang_sb_append → Ptr (returns same sb for chaining)
| App (App (Var ("stringbuilder_append", _), sbExpr, _), strExpr, _) ->
    let (sbVal,  sbOps)  = elaborateExpr env sbExpr
    let (strVal, strOps) = elaborateExpr env strExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, sbOps @ strOps @ [LlvmCallOp(result, "@lang_sb_append", [sbVal; strVal])])
```
LangThree returns the StringBuilder value itself (for chaining). The C function should return `LangStringBuilder*`.

### Pattern 7: mutablelist_set — three-arg, void return
```fsharp
// mutablelist_set ml idx val: three-arg, returns unit
| App (App (App (Var ("mutablelist_set", _), mlExpr, _), idxExpr, _), valExpr, _) ->
    let (mlVal,  mlOps)  = elaborateExpr env mlExpr
    let (idxVal, idxOps) = elaborateExpr env idxExpr
    let (valVal, valOps) = elaborateExpr env valExpr
    let idxI64 =
        if idxVal.Type = I64 then (idxVal, [])
        else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, idxVal)])
    let (idxV, idxCoerce) = idxI64
    let unitVal = { Name = freshName env; Type = I64 }
    (unitVal, mlOps @ idxOps @ idxCoerce @ valOps @ [LlvmCallVoidOp("@lang_mlist_set", [mlVal; idxV; valVal]); ArithConstantOp(unitVal, 0L)])
```
Note: Must appear BEFORE two-arg `mutablelist_get` pattern (same three-arg-first rule as `hashtable_set`).

### Anti-Patterns to Avoid
- **Forgetting the duplicate externalFuncs:** Two lists at lines 2853 and 3058 — both must be updated.
- **Using malloc/realloc without GC_malloc for new struct allocations:** StringBuilder and MutableList buffers must use GC_malloc for the initial buffer; however, the grow operation can use GC_malloc for a new block + memcpy (since Boehm GC does not support realloc). Do NOT use `realloc` — use a GC_malloc new block + memcpy + old block is collected.
- **LangCons typedef ordering:** New struct definitions in lang_runtime.c must be placed after `LangCons` typedef. Use forward typedef if needed.
- **hashset_add return type:** LangThree returns `bool` (1 or 0). The C function should return `int64_t` (not void). Use `LlvmCallOp` (not `LlvmCallVoidOp`) for `hashset_add`.
- **queue_dequeue signature mismatch:** LangThree's `queue_dequeue` takes a Queue and a unit. The Elaboration pattern is two-arg curried: `App(App(Var "queue_dequeue", qExpr), unitExpr)`. The C function only takes the queue pointer: `lang_queue_dequeue(LangQueue* q)` — the unit arg is elaborated and discarded.
- **stringbuilder_append vs stringbuilder_add:** Use `stringbuilder_append` to match LangThree exactly. Phase description says "add" but the interpreter name is `stringbuilder_append`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Hash function for HashSet | Custom hash | `lang_ht_hash` (already static in lang_runtime.c) | Murmurhash3 already correct; place `lang_hashset_*` after `lang_ht_hash` for visibility |
| Buffer growth for StringBuilder/MutableList | realloc | GC_malloc new block + memcpy | Boehm GC doesn't track realloc'd pointers; always allocate new block |
| Queue size tracking | Traverse list to count | Maintain `count` field in LangQueue struct | O(1) count; same as LangHashtable.size pattern |

**Key insight:** The HashSet implementation can share `lang_ht_hash` directly (same translation unit, static function visible after its definition). Place all `lang_hashset_*` functions immediately after the hashtable block.

## Common Pitfalls

### Pitfall 1: externalFuncs list duplicated — both must be updated
**What goes wrong:** Adding new C functions to only one of the two `externalFuncs` lists causes "undefined external function" MLIR errors.
**Why it happens:** `elaborateModule` (lines 2853) and `elaborateProgram` (line 3058) each build their own MLIR module independently.
**How to avoid:** Update the list at line 2853 AND line 3058 for every new function.
**Warning signs:** Test passes for expression-only tests but fails for top-level `let` declaration tests.

### Pitfall 2: GC_malloc vs realloc for growing buffers
**What goes wrong:** Using `realloc` for StringBuilder or MutableList capacity growth causes GC to lose track of the pointer.
**Why it happens:** Boehm GC scans known heap ranges; `realloc` may return a new pointer that's not registered with GC.
**How to avoid:** When growing: `new_buf = GC_malloc(new_cap)`, `memcpy(new_buf, old_buf, old_len)`, update struct pointer. The old block is collected normally.
**Warning signs:** Random crashes or corruption after many `.Add()` calls on a growing collection.

### Pitfall 3: LangCons typedef ordering for new struct types
**What goes wrong:** A new struct in lang_runtime.c that uses `LangCons*` internally fails to compile if placed before the LangCons typedef.
**Why it happens:** `LangCons` is typedef'd via `struct LangCons` at the top of lang_runtime.c (visible after the header include since lang_runtime.h declares it).
**How to avoid:** All new structs go AFTER the existing LangString typedef block (line 12). The header includes the forward declaration for LangCons.
**Warning signs:** C compilation error: "unknown type name 'LangCons'".

### Pitfall 4: hashset_add return value — I64, not void
**What goes wrong:** `hashset_add` should return `int64_t` (1 = newly added, 0 = already present), matching LangThree's `bool`. If declared void, callers that check `if hashset_add hs v then ...` will fail.
**Why it happens:** Phase description says "add" without specifying return type; temptation to use void pattern.
**How to avoid:** `int64_t lang_hashset_add(LangHashSet* hs, int64_t key)` returns 1 or 0. Use `LlvmCallOp` (not Void) in elaboration.
**Warning signs:** FunLexYacc code that checks `if hs.Add(v) then ...` will produce wrong results.

### Pitfall 5: queue_dequeue currying — two-arg pattern required
**What goes wrong:** Implementing `queue_dequeue` as one-arg (just the queue) while the language-level call is `queue_dequeue q ()` (two args).
**Why it happens:** LangThree shows `"queue_dequeue", BuiltinValue (fun qVal -> BuiltinValue (fun _ -> ...))` — it's curried. The parser will generate `App(App(Var "queue_dequeue", q), unit)`.
**How to avoid:** Match `App(App(Var "queue_dequeue", qExpr), unitExpr)` in Elaboration.fs. The C function takes only the queue pointer.
**Warning signs:** "Partial application of queue_dequeue" elaboration failure — the one-arg arm would fire prematurely.

### Pitfall 6: Three-arg mutablelist_set ordering in elaboration
**What goes wrong:** The two-arg `mutablelist_get` pattern accidentally matches `App(App(App(Var "mutablelist_set", ml), idx), val)` at the outer `App` level if placed before the three-arg pattern.
**Why it happens:** F# pattern matching is top-to-bottom; the three-arg `App(App(App(...)))` case must appear BEFORE the two-arg `App(App(...))` case — same rule as `hashtable_set` before `hashtable_get`.
**How to avoid:** Place `mutablelist_set` (three-arg) before `mutablelist_get` (two-arg) in Elaboration.fs.
**Warning signs:** `mutablelist_set` partially applies as a closure instead of emitting the void call.

## Code Examples

### COL-01: StringBuilder C runtime
```c
// Source: lang_runtime.c (new, after Phase 32 block)
typedef struct {
    char*   buf;
    int64_t len;
    int64_t cap;
} LangStringBuilder;

LangStringBuilder* lang_sb_create(void) {
    LangStringBuilder* sb = (LangStringBuilder*)GC_malloc(sizeof(LangStringBuilder));
    sb->cap = 64;
    sb->buf = (char*)GC_malloc((size_t)sb->cap);
    sb->len = 0;
    return sb;
}

LangStringBuilder* lang_sb_append(LangStringBuilder* sb, LangString* s) {
    int64_t new_len = sb->len + s->length;
    if (new_len >= sb->cap) {
        int64_t new_cap = sb->cap * 2;
        while (new_cap <= new_len) new_cap *= 2;
        char* new_buf = (char*)GC_malloc((size_t)new_cap);
        memcpy(new_buf, sb->buf, (size_t)sb->len);
        sb->buf = new_buf;
        sb->cap = new_cap;
    }
    memcpy(sb->buf + sb->len, s->data, (size_t)s->length);
    sb->len = new_len;
    return sb;
}

LangString* lang_sb_tostring(LangStringBuilder* sb) {
    char* data = (char*)GC_malloc((size_t)(sb->len + 1));
    memcpy(data, sb->buf, (size_t)sb->len);
    data[sb->len] = '\0';
    LangString* s = (LangString*)GC_malloc(sizeof(LangString));
    s->length = sb->len;
    s->data = data;
    return s;
}
```

### COL-01: Elaboration patterns
```fsharp
// stringbuilder_create () -> Ptr
| App (Var ("stringbuilder_create", _), unitExpr, _) ->
    let (_uVal, uOps) = elaborateExpr env unitExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, uOps @ [LlvmCallOp(result, "@lang_sb_create", [])])

// stringbuilder_append sb str -> Ptr (returns sb)
| App (App (Var ("stringbuilder_append", _), sbExpr, _), strExpr, _) ->
    let (sbVal,  sbOps)  = elaborateExpr env sbExpr
    let (strVal, strOps) = elaborateExpr env strExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, sbOps @ strOps @ [LlvmCallOp(result, "@lang_sb_append", [sbVal; strVal])])

// stringbuilder_tostring sb -> Ptr (LangString*)
| App (Var ("stringbuilder_tostring", _), sbExpr, _) ->
    let (sbVal, sbOps) = elaborateExpr env sbExpr
    let result = { Name = freshName env; Type = Ptr }
    (result, sbOps @ [LlvmCallOp(result, "@lang_sb_tostring", [sbVal])])
```

### COL-02: HashSet C runtime (sketched)
```c
// Reuses lang_ht_hash (static, already in file — place after it)
typedef struct LangHashSetEntry {
    int64_t key;
    struct LangHashSetEntry* next;
} LangHashSetEntry;

typedef struct {
    int64_t capacity;
    int64_t size;
    LangHashSetEntry** buckets;
} LangHashSet;

LangHashSet* lang_hashset_create(void) {
    LangHashSet* hs = (LangHashSet*)GC_malloc(sizeof(LangHashSet));
    hs->capacity = 16;
    hs->size = 0;
    hs->buckets = (LangHashSetEntry**)GC_malloc((size_t)(16 * (int64_t)sizeof(LangHashSetEntry*)));
    for (int64_t i = 0; i < 16; i++) hs->buckets[i] = NULL;
    return hs;
}

int64_t lang_hashset_add(LangHashSet* hs, int64_t key) {
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)hs->capacity;
    LangHashSetEntry* e = hs->buckets[bucket];
    while (e != NULL) {
        if (e->key == key) return 0; // already present
        e = e->next;
    }
    LangHashSetEntry* ne = (LangHashSetEntry*)GC_malloc(sizeof(LangHashSetEntry));
    ne->key = key;
    ne->next = hs->buckets[bucket];
    hs->buckets[bucket] = ne;
    hs->size++;
    return 1; // newly added
}

int64_t lang_hashset_contains(LangHashSet* hs, int64_t key) {
    uint64_t bucket = lang_ht_hash(key) % (uint64_t)hs->capacity;
    LangHashSetEntry* e = hs->buckets[bucket];
    while (e != NULL) {
        if (e->key == key) return 1;
        e = e->next;
    }
    return 0;
}

int64_t lang_hashset_count(LangHashSet* hs) { return hs->size; }
```

### COL-03: Queue C runtime (sketched)
```c
typedef struct LangQueueNode {
    int64_t value;
    struct LangQueueNode* next;
} LangQueueNode;

typedef struct {
    LangQueueNode* head;
    LangQueueNode* tail;
    int64_t        count;
} LangQueue;

LangQueue* lang_queue_create(void) {
    LangQueue* q = (LangQueue*)GC_malloc(sizeof(LangQueue));
    q->head = NULL; q->tail = NULL; q->count = 0;
    return q;
}

void lang_queue_enqueue(LangQueue* q, int64_t value) {
    LangQueueNode* n = (LangQueueNode*)GC_malloc(sizeof(LangQueueNode));
    n->value = value; n->next = NULL;
    if (q->tail == NULL) { q->head = q->tail = n; }
    else { q->tail->next = n; q->tail = n; }
    q->count++;
}

int64_t lang_queue_dequeue(LangQueue* q) {
    if (q->head == NULL) lang_failwith("Queue.Dequeue: queue is empty");
    int64_t val = q->head->value;
    q->head = q->head->next;
    if (q->head == NULL) q->tail = NULL;
    q->count--;
    return val;
}

int64_t lang_queue_count(LangQueue* q) { return q->count; }
```

### COL-04: MutableList C runtime (sketched)
```c
typedef struct {
    int64_t* data;
    int64_t  len;
    int64_t  cap;
} LangMutableList;

LangMutableList* lang_mlist_create(void) {
    LangMutableList* ml = (LangMutableList*)GC_malloc(sizeof(LangMutableList));
    ml->cap = 8;
    ml->data = (int64_t*)GC_malloc((size_t)(8 * 8));
    ml->len = 0;
    return ml;
}

void lang_mlist_add(LangMutableList* ml, int64_t value) {
    if (ml->len >= ml->cap) {
        int64_t new_cap = ml->cap * 2;
        int64_t* new_data = (int64_t*)GC_malloc((size_t)(new_cap * 8));
        memcpy(new_data, ml->data, (size_t)(ml->len * 8));
        ml->data = new_data; ml->cap = new_cap;
    }
    ml->data[ml->len++] = value;
}

int64_t lang_mlist_get(LangMutableList* ml, int64_t index) {
    if (index < 0 || index >= ml->len) lang_failwith("MutableList index out of bounds");
    return ml->data[index];
}

void lang_mlist_set(LangMutableList* ml, int64_t index, int64_t value) {
    if (index < 0 || index >= ml->len) lang_failwith("MutableList index out of bounds");
    ml->data[index] = value;
}

int64_t lang_mlist_count(LangMutableList* ml) { return ml->len; }
```

### Test file pattern (E2E .flt)
```
// COL-01 test
// --- Command: bash -c 'OUTBIN=$(mktemp /tmp/langback_XXXXXX) && dotnet run --project %S/../../src/FunLangCompiler.Cli/FunLangCompiler.Cli.fsproj -- %input -o $OUTBIN && $OUTBIN; echo $?; rm -f $OUTBIN'
// --- Input:
let sb = stringbuilder_create () in
let _ = stringbuilder_append sb "hello" in
let _ = stringbuilder_append sb " world" in
println (stringbuilder_tostring sb)
// --- Output:
hello world
0

// COL-02 test
// --- Input:
let hs = hashset_create () in
let a = hashset_add hs 1 in
let b = hashset_add hs 2 in
let c = hashset_add hs 1 in
println (to_string (hashset_count hs))
// --- Output:
2
0

// COL-03 test
// --- Input:
let q = queue_create () in
let _ = queue_enqueue q 10 in
let _ = queue_enqueue q 20 in
let _ = queue_enqueue q 30 in
let a = queue_dequeue q () in
let b = queue_dequeue q () in
println (to_string a);
println (to_string b)
// --- Output:
10
20
0

// COL-04 test
// --- Input:
let ml = mutablelist_create () in
let _ = mutablelist_add ml 100 in
let _ = mutablelist_add ml 200 in
let _ = mutablelist_set ml 0 999 in
println (to_string (mutablelist_count ml));
println (to_string (mutablelist_get ml 0));
println (to_string (mutablelist_get ml 1))
// --- Output:
2
999
200
0
```

### externalFuncs entries (add to both lists at lines 2853 and 3058)
```fsharp
// COL-01 StringBuilder
{ ExtName = "@lang_sb_create";   ExtParams = [];         ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_sb_append";   ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_sb_tostring"; ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
// COL-02 HashSet
{ ExtName = "@lang_hashset_create";   ExtParams = [];         ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashset_add";      ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashset_contains"; ExtParams = [Ptr; I64]; ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_hashset_count";    ExtParams = [Ptr];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
// COL-03 Queue
{ ExtName = "@lang_queue_create";   ExtParams = [];         ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_queue_enqueue";  ExtParams = [Ptr; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_queue_dequeue";  ExtParams = [Ptr];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_queue_count";    ExtParams = [Ptr];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
// COL-04 MutableList
{ ExtName = "@lang_mlist_create"; ExtParams = [];              ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_mlist_add";    ExtParams = [Ptr; I64];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_mlist_get";    ExtParams = [Ptr; I64];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_mlist_set";    ExtParams = [Ptr; I64; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
{ ExtName = "@lang_mlist_count";  ExtParams = [Ptr];           ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| No StringBuilder | LangStringBuilder with append + tostring | Phase 33 | Enables efficient string building (51 call sites in FunLexYacc) |
| No HashSet (use LangHashtable as set) | LangHashSet key-only chained hash table | Phase 33 | Needed for Dfa.fun, Lalr.fun, Lr0.fun |
| No Queue (use functional list as queue) | LangQueue linked-list FIFO | Phase 33 | Needed for DFA epsilon closure (BFS worklist) |
| No MutableList (use array or cons list) | LangMutableList growable array | Phase 33 | Needed for Lalr.fun, Lr0.fun, ParserTables.fun |

**Deprecated/outdated:**
- Nothing deprecated. All additions are additive to the existing runtime.

## Open Questions

1. **stringbuilder_append vs stringbuilder_add naming**
   - What we know: LangThree Eval.fs uses `stringbuilder_append`. Phase 33 description says "add".
   - What's unclear: Whether "add" is intentional renaming or a typo in the phase description.
   - Recommendation: Use `stringbuilder_append` to match LangThree exactly. The FunLexYacc source code uses `.Append()` method calls, and the LangThree interpreter recognizes `stringbuilder_append`. This avoids a name mismatch between interpreter and compiler.

2. **hashset_add boolean return vs unit**
   - What we know: LangThree returns `BoolValue (hs.Add v)` — the return value is used in some FunLexYacc code that checks `if hs.Add(x) then ...`.
   - What's unclear: Phase 33 success criteria doesn't mention testing the return value.
   - Recommendation: Implement `lang_hashset_add` as returning `int64_t` (1 or 0). Use `LlvmCallOp` for the elaboration. This is consistent with LangThree and enables future FunLexYacc compilation.

3. **lang_hashset_* placement relative to lang_ht_hash**
   - What we know: `lang_ht_hash` is declared `static` at line 302 of lang_runtime.c. HashSet needs to call it.
   - Recommendation: Place all `lang_hashset_*` functions AFTER the hashtable block (after line ~430), after `lang_ht_hash` is defined. This avoids a forward declaration.

4. **Lang_runtime.h typedef placement**
   - What we know: The header currently ends at line 103. All four new structs need typedef + function declarations added.
   - Recommendation: Add all four new struct typedefs and function declarations to lang_runtime.h, grouped by type after the existing declarations.

5. **COL-04: mutablelist_set uses `lang_index_set` vs new function**
   - What we know: `lang_index_set` (already in Elaboration.fs) dispatches on collection type (hashtable vs array). It could be extended to dispatch on MutableList too.
   - What's unclear: Whether to extend `lang_index_set` or add a dedicated `mutablelist_set` builtin.
   - Recommendation: Add dedicated `mutablelist_set` builtin (matches LangThree name exactly, no dispatch complexity). Do NOT extend `lang_index_set` — that would add a runtime tag requirement.

## Sources

### Primary (HIGH confidence)
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.c` — lines 297–430 (LangHashtable pattern), 550–612 (Phase 32 additions), all struct and function layout patterns
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/lang_runtime.h` — current declarations (103 lines), no Phase 33 types present
- `/Users/ohama/vibe-coding/FunLangCompiler/src/FunLangCompiler.Compiler/Elaboration.fs` — lines 1086–1222 (Phase 32 patterns), 2853–2920 and 3058–3125 (both externalFuncs lists)
- `/Users/ohama/vibe-coding/FunLangCompiler/../LangThree/src/LangThree/Eval.fs` — lines 684–797 (all four collection types, exact semantics, confirmed builtin names)
- `/Users/ohama/vibe-coding/FunLangCompiler/langbackend-feature-requests.md` — lines 254–420 (Feature 7–11: C struct hints, FunLexYacc usage, test suggestions)
- `/Users/ohama/vibe-coding/FunLangCompiler/tests/compiler/32-*.flt` — test file format and naming conventions

### Secondary (MEDIUM confidence)
- Phase 32 RESEARCH.md (`.planning/phases/32-hashtable-list-array-builtins/32-RESEARCH.md`) — confirmed externalFuncs duplication pitfall, GEP patterns, void-return pattern

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all files examined directly; no new libraries needed
- Architecture: HIGH — four complete C struct implementations derived from working LangHashtable pattern; all LangThree reference implementations confirmed
- Pitfalls: HIGH — GC_malloc vs realloc confirmed from existing pattern; duplicate externalFuncs confirmed from Elaboration.fs grep; ordering issues confirmed from hashtable_set precedent

**Research date:** 2026-03-29
**Valid until:** 2026-04-29 (stable codebase)
