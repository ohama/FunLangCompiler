module Elaboration

open Ast
open MlirIR
open MatchCompiler

// Phase 5: Closure metadata — distinguishes closure-making funcs from direct-call funcs
type ClosureInfo = {
    InnerLambdaFn: string    // MLIR name of the llvm.func body, e.g. "@closure_fn_0"
    NumCaptures:   int       // number of captured variables
    InnerReturnIsBool: bool  // Phase 43: inner closure returns bool (for to_string dispatch)
}

type FuncSignature = {
    MlirName:    string
    ParamTypes:  MlirType list
    ReturnType:  MlirType
    ClosureInfo: ClosureInfo option  // None = direct-call func; Some = closure-maker
    ReturnIsBool: bool       // Phase 43: direct return is bool (for to_string dispatch)
    InnerReturnIsBool: bool  // Phase 43: 2-param func's inner closure returns bool
}

// Phase 16: TypeInfo for ADT constructor entries in TypeEnv
type TypeInfo = { Tag: int; Arity: int }

// Phase 34-03: LANG-03/04 — collection kind for ForInExpr dispatch
type CollectionKind = HashSet | Queue | MutableList | Hashtable

type ElabEnv = {
    Vars:           Map<string, MlirValue>
    Counter:        int ref
    LabelCounter:   int ref
    Blocks:         MlirBlock list ref
    KnownFuncs:     Map<string, FuncSignature>
    Funcs:          FuncOp list ref
    ClosureCounter: int ref   // Phase 5: generates unique closure function names
    Globals:        (string * string) list ref   // Phase 7: (name, rawValue) pairs for string constants
    GlobalCounter:  int ref                       // Phase 7: counter for unique global names
    // Phase 16: Declaration environment — populated by prePassDecls before elaboration
    TypeEnv:        Map<string, TypeInfo>            // constructor name -> tag + arity
    RecordEnv:      Map<string, Map<string, int>>    // record type name -> (field name -> index)
    ExnTags:        Map<string, int>                 // exception ctor name -> tag index
    // Phase 21: Mutable variable tracking — names that live in GC ref cells
    MutableVars:    Set<string>
    // Phase 30: Array variable tracking — names bound to array-type collections (for for-in dispatch)
    ArrayVars:      Set<string>
    // Phase 34-03: Collection variable tracking — names bound to Phase 33 collection types
    CollectionVars: Map<string, CollectionKind>
    // Phase 43: Bool variable tracking — names bound to boolean-producing expressions
    BoolVars: Set<string>
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
      KnownFuncs = Map.empty; Funcs = ref []; ClosureCounter = ref 0
      Globals = ref []; GlobalCounter = ref 0
      TypeEnv = Map.empty; RecordEnv = Map.empty; ExnTags = Map.empty
      MutableVars = Set.empty; ArrayVars = Set.empty; CollectionVars = Map.empty
      BoolVars = Set.empty }

/// Phase 44: Raise an error with source location in "file:line:col: message" format.
/// Uses Printf.ksprintf to support format strings like failwithf.
let inline private failWithSpan (span: Ast.Span) fmt =
    Printf.ksprintf (fun msg ->
        let loc = sprintf "%s:%d:%d" span.FileName span.StartLine span.StartColumn
        failwith (sprintf "[Elaboration] %s: %s" loc msg)
    ) fmt

/// Phase 30: Determine if an expression is statically known to produce an array (not a list).
/// Used by ForInExpr to select lang_for_in_array vs lang_for_in_list at compile time.
/// Conservative: returns false (assume list) for variables or unknown expressions.
let rec private isArrayExpr (arrayVars: Set<string>) (expr: Ast.Expr) : bool =
    match expr with
    // Direct array-creating builtins
    | Ast.App (Ast.Var ("array_of_list", _), _, _)
    | Ast.App (Ast.Var ("array_create", _), _, _)
    | Ast.App (Ast.Var ("array_init", _), _, _)
    | Ast.App (Ast.App (Ast.Var ("array_create", _), _, _), _, _)
    | Ast.App (Ast.App (Ast.Var ("array_init", _), _, _), _, _) -> true
    // Variable previously determined to be an array
    | Ast.Var (name, _) -> Set.contains name arrayVars
    // Type annotation — check inner expression
    | Ast.Annot (inner, _, _) -> isArrayExpr arrayVars inner
    | _ -> false

/// Phase 34-03: Detect which Phase 33 collection kind an expression produces.
/// Returns Some kind if the expression is a known collection-creating call or variable.
let rec private detectCollectionKind (collVars: Map<string, CollectionKind>) (expr: Ast.Expr) : CollectionKind option =
    match expr with
    | Ast.App (Ast.Var ("hashset_create", _), _, _)   -> Some HashSet
    | Ast.App (Ast.Var ("queue_create", _), _, _)     -> Some Queue
    | Ast.App (Ast.Var ("mutablelist_create", _), _, _) -> Some MutableList
    | Ast.App (Ast.Var ("hashtable_create", _), _, _)     -> Some Hashtable
    | Ast.App (Ast.Var ("hashtable_create_str", _), _, _) -> Some Hashtable
    | Ast.Var (name, _)                                   -> Map.tryFind name collVars
    | Ast.Annot (inner, _, _)                         -> detectCollectionKind collVars inner
    | _ -> None

let private addStringGlobal (env: ElabEnv) (rawValue: string) : string =
    match env.Globals.Value |> List.tryFind (fun (_, v) -> v = rawValue) with
    | Some (name, _) -> name
    | None ->
        let idx = env.GlobalCounter.Value
        env.GlobalCounter.Value <- idx + 1
        let name = sprintf "@__str_%d" idx
        env.Globals.Value <- env.Globals.Value @ [(name, rawValue)]
        name

let private freshName (env: ElabEnv) : string =
    let n = env.Counter.Value
    env.Counter.Value <- n + 1
    sprintf "%%t%d" n

let private freshLabel (env: ElabEnv) (prefix: string) : string =
    let n = env.LabelCounter.Value
    env.LabelCounter.Value <- n + 1
    sprintf "%s%d" prefix n

// Phase 8: Allocate a heap string struct {i64 length, ptr data} for a compile-time string literal.
let private elaborateStringLiteral (env: ElabEnv) (s: string) : MlirValue * MlirOp list =
    let globalName  = addStringGlobal env s
    let sizeVal     = { Name = freshName env; Type = I64 }
    let headerVal   = { Name = freshName env; Type = Ptr }
    let lenPtrVal   = { Name = freshName env; Type = Ptr }
    let lenVal      = { Name = freshName env; Type = I64 }
    let dataPtrVal  = { Name = freshName env; Type = Ptr }
    let dataAddrVal = { Name = freshName env; Type = Ptr }
    let ops = [
        ArithConstantOp(sizeVal, 16L)                              // struct size = 16 bytes
        LlvmCallOp(headerVal, "@GC_malloc", [sizeVal])             // alloc header
        LlvmGEPStructOp(lenPtrVal, headerVal, 0)                   // &header.length
        ArithConstantOp(lenVal, int64 s.Length)                    // compile-time length
        LlvmStoreOp(lenVal, lenPtrVal)                             // header.length = len
        LlvmGEPStructOp(dataPtrVal, headerVal, 1)                  // &header.data
        LlvmAddressOfOp(dataAddrVal, globalName)                   // addressof @__str_N
        LlvmStoreOp(dataAddrVal, dataPtrVal)                       // header.data = &global
    ]
    (headerVal, ops)

// Phase 5: Free variable analysis — computes free variables in expr relative to boundVars
let rec freeVars (boundVars: Set<string>) (expr: Expr) : Set<string> =
    match expr with
    | Number _ | Bool _ -> Set.empty
    | Var (name, _) ->
        if Set.contains name boundVars then Set.empty else Set.singleton name
    | Add (l, r, _) | Subtract (l, r, _) | Multiply (l, r, _) | Divide (l, r, _)
    | Equal (l, r, _) | NotEqual (l, r, _) | LessThan (l, r, _) | GreaterThan (l, r, _)
    | LessEqual (l, r, _) | GreaterEqual (l, r, _) | And (l, r, _) | Or (l, r, _) ->
        Set.union (freeVars boundVars l) (freeVars boundVars r)
    | Negate (e, _) -> freeVars boundVars e
    | If (c, t, e, _) ->
        Set.unionMany [ freeVars boundVars c; freeVars boundVars t; freeVars boundVars e ]
    | Let (name, e1, e2, _) ->
        Set.union (freeVars boundVars e1) (freeVars (Set.add name boundVars) e2)
    | Lambda (param, body, _) ->
        freeVars (Set.add param boundVars) body
    | App (f, a, _) ->
        Set.union (freeVars boundVars f) (freeVars boundVars a)
    | LetRec (bindings, inExpr, _) ->
        let names = bindings |> List.map (fun (n, _, _, _, _) -> n) |> Set.ofList
        let innerBound = bindings |> List.fold (fun s (n, p, _, _, _) -> Set.add n (Set.add p s)) boundVars
        let bodyFree = bindings |> List.map (fun (_, _, _, body, _) -> freeVars innerBound body) |> Set.unionMany
        let inFree = freeVars (Set.union boundVars names) inExpr
        Set.union bodyFree inFree
    | String _ -> Set.empty
    | Tuple (exprs, _) ->
        exprs |> List.map (freeVars boundVars) |> Set.unionMany
    | LetPat (WildcardPat _, bindExpr, bodyExpr, _) ->
        Set.union (freeVars boundVars bindExpr) (freeVars boundVars bodyExpr)
    | LetPat (VarPat (name, _), bindExpr, bodyExpr, _) ->
        Set.union (freeVars boundVars bindExpr) (freeVars (Set.add name boundVars) bodyExpr)
    | LetPat (TuplePat (pats, _), bindExpr, bodyExpr, _) ->
        let bindFree = freeVars boundVars bindExpr
        let extractVarName p = match p with | VarPat (name, _) -> [name] | _ -> []
        let patBound = pats |> List.collect extractVarName |> Set.ofList
        let bodyFree = freeVars (Set.union boundVars patBound) bodyExpr
        Set.union bindFree bodyFree
    | Modulo (l, r, _) ->
        Set.union (freeVars boundVars l) (freeVars boundVars r)
    | PipeRight (l, r, _) | ComposeRight (l, r, _) | ComposeLeft (l, r, _) ->
        Set.union (freeVars boundVars l) (freeVars boundVars r)
    | Constructor(_, None, _) -> Set.empty
    | Constructor(_, Some argExpr, _) -> freeVars boundVars argExpr
    | RecordExpr(_, fields, _) ->
        fields |> List.map (fun (_, e) -> freeVars boundVars e) |> Set.unionMany
    | FieldAccess(recExpr, _, _) ->
        freeVars boundVars recExpr
    | RecordUpdate(sourceExpr, overrides, _) ->
        let srcFree = freeVars boundVars sourceExpr
        let overFree = overrides |> List.map (fun (_, e) -> freeVars boundVars e) |> Set.unionMany
        Set.union srcFree overFree
    | SetField(recExpr, _, valueExpr, _) ->
        Set.union (freeVars boundVars recExpr) (freeVars boundVars valueExpr)
    | Raise(e, _) -> freeVars boundVars e
    | TryWith(body, clauses, _) ->
        let bodyFree = freeVars boundVars body
        let clauseFree =
            clauses |> List.map (fun (pat, guardOpt, armBody) ->
                let rec patBoundVars p =
                    match p with
                    | VarPat(n, _) -> Set.singleton n
                    | TuplePat(ps, _) -> ps |> List.map patBoundVars |> Set.unionMany
                    | ConsPat(h, t, _) -> Set.union (patBoundVars h) (patBoundVars t)
                    | ConstructorPat(_, Some inner, _) -> patBoundVars inner
                    | _ -> Set.empty
                let patBound = patBoundVars pat
                let armBound = Set.union boundVars patBound
                let guardFree = match guardOpt with Some g -> freeVars armBound g | None -> Set.empty
                Set.union guardFree (freeVars armBound armBody)
            ) |> Set.unionMany
        Set.union bodyFree clauseFree
    | LetMut (name, initExpr, bodyExpr, _) ->
        Set.union (freeVars boundVars initExpr)
                  (freeVars (Set.add name boundVars) bodyExpr)
    | Assign (name, valExpr, _) ->
        let nameFree =
            if Set.contains name boundVars then Set.empty
            else Set.singleton name
        Set.union nameFree (freeVars boundVars valExpr)
    | IndexGet (collExpr, idxExpr, _) ->
        Set.union (freeVars boundVars collExpr) (freeVars boundVars idxExpr)
    | IndexSet (collExpr, idxExpr, valExpr, _) ->
        Set.unionMany [freeVars boundVars collExpr; freeVars boundVars idxExpr; freeVars boundVars valExpr]
    | WhileExpr (cond, body, _) ->
        Set.union (freeVars boundVars cond) (freeVars boundVars body)
    | ForExpr (var, startExpr, _, stopExpr, body, _) ->
        Set.unionMany [
            freeVars boundVars startExpr
            freeVars boundVars stopExpr
            freeVars (Set.add var boundVars) body   // var is bound inside body
        ]
    | ForInExpr (var, collExpr, body, _) ->
        let varName = match var with Ast.VarPat(n, _) -> n | _ -> "_"
        Set.union (freeVars boundVars collExpr) (freeVars (Set.add varName boundVars) body)
    | Annot (expr, _, _) -> freeVars boundVars expr
    | LambdaAnnot (param, _, body, _) -> freeVars (Set.add param boundVars) body
    | Match(scrutineeExpr, clauses, _) ->
        let scrutFree = freeVars boundVars scrutineeExpr
        let clauseFree =
            clauses |> List.map (fun (pat, guardOpt, armBody) ->
                let rec patBoundVars p =
                    match p with
                    | VarPat(n, _) -> Set.singleton n
                    | TuplePat(ps, _) -> ps |> List.map patBoundVars |> Set.unionMany
                    | ConsPat(h, t, _) -> Set.union (patBoundVars h) (patBoundVars t)
                    | ConstructorPat(_, Some inner, _) -> patBoundVars inner
                    | _ -> Set.empty
                let patBound = patBoundVars pat
                let armBound = Set.union boundVars patBound
                let guardFree = match guardOpt with Some g -> freeVars armBound g | None -> Set.empty
                Set.union guardFree (freeVars armBound armBody)
            ) |> Set.unionMany
        Set.union scrutFree clauseFree
    | Cons (headExpr, tailExpr, _) ->
        Set.union (freeVars boundVars headExpr) (freeVars boundVars tailExpr)
    | List (elems, _) ->
        elems |> List.map (freeVars boundVars) |> Set.unionMany
    | EmptyList _ -> Set.empty
    | Char _ -> Set.empty
    | _ -> Set.empty  // conservative: other exprs (Char, etc.) have no free vars

/// Phase 35: Coerce a value to I64 for uniform closure return type.
/// I1 → zext to I64; Ptr → ptrtoint to I64; I64 → no-op.
let private coerceToI64 (env: ElabEnv) (v: MlirValue) : (MlirValue * MlirOp list) =
    match v.Type with
    | I64 -> (v, [])
    | I1  ->
        let r = { Name = freshName env; Type = I64 }
        (r, [ArithExtuIOp(r, v)])
    | Ptr ->
        let r = { Name = freshName env; Type = I64 }
        (r, [LlvmPtrToIntOp(r, v)])
    | _ -> (v, [])  // I32 or other — pass through unchanged

/// Phase 35: Coerce a value to Ptr for builtin functions that expect pointer arguments.
/// I64 → inttoptr; Ptr → no-op.
let private coerceToPtrArg (env: ElabEnv) (v: MlirValue) : (MlirValue * MlirOp list) =
    match v.Type with
    | Ptr -> (v, [])
    | I64 ->
        let r = { Name = freshName env; Type = Ptr }
        (r, [LlvmIntToPtrOp(r, v)])
    | _ -> (v, [])

/// Phase 39: Format specifier type for compile-time dispatch.
type FmtSpec = IntSpec | StrSpec

/// Phase 39: Return ordered list of format specifier types in a format string.
/// %d, %x, %02x, %c, %ld, %lx → IntSpec; %s → StrSpec; %% → ignored.
let private fmtSpecTypes (fmt: string) : FmtSpec list =
    let specs = System.Collections.Generic.List<FmtSpec>()
    let mutable i = 0
    while i < fmt.Length do
        if fmt.[i] = '%' && i + 1 < fmt.Length then
            if fmt.[i+1] = '%' then
                i <- i + 2
            else
                let mutable j = i + 1
                // Skip flags: -, +, space, 0, #
                while j < fmt.Length && "-+ 0#".Contains(fmt.[j]) do j <- j + 1
                // Skip width digits
                while j < fmt.Length && fmt.[j] >= '0' && fmt.[j] <= '9' do j <- j + 1
                // Skip precision
                if j < fmt.Length && fmt.[j] = '.' then
                    j <- j + 1
                    while j < fmt.Length && fmt.[j] >= '0' && fmt.[j] <= '9' do j <- j + 1
                // Skip length modifiers: l, h, z, t
                while j < fmt.Length && "lhzt".Contains(fmt.[j]) do j <- j + 1
                // Conversion character
                if j < fmt.Length then
                    match fmt.[j] with
                    | 's' -> specs.Add(StrSpec)
                    | _   -> specs.Add(IntSpec)  // d, i, o, u, x, X, c, p all map to i64
                    j <- j + 1
                i <- j
        else
            i <- i + 1
    specs |> Seq.toList

/// Phase 39: Coerce a value to I64 for int-typed sprintf wrapper args.
/// I1 → zext to I64; I64 → pass through; Ptr → ptrtoint; I32 → pass through.
let private coerceToI64Arg (env: ElabEnv) (v: MlirValue) : (MlirValue * MlirOp list) =
    match v.Type with
    | I64 -> (v, [])
    | I1  ->
        let ext = { Name = freshName env; Type = I64 }
        (ext, [ArithExtuIOp(ext, v)])
    | Ptr ->
        let i = { Name = freshName env; Type = I64 }
        (i, [LlvmPtrToIntOp(i, v)])
    | I32 ->
        (v, [])  // treat as I64 (should not normally arise)

/// Phase 43: Strip Annot/LambdaAnnot wrappers from the outermost position of an expression.
/// Converts LambdaAnnot to Lambda, removing type annotations.
/// Used for pattern matching that needs to see through type annotation wrappers.
let rec private stripAnnot (expr: Expr) : Expr =
    match expr with
    | Annot (inner, _, _) -> stripAnnot inner
    | LambdaAnnot (param, _, body, span) -> Lambda(param, stripAnnot body, span)
    | _ -> expr

/// Phase 43: Active pattern for stripAnnot — use in match expressions to see through annotations.
let private (|StripAnnot|) (expr: Expr) = stripAnnot expr

/// Detect whether a LetRec/Lambda body uses list patterns on the parameter,
/// indicating the parameter should be typed Ptr (list pointer) rather than I64.
let private isListParamBody (paramName: string) (bodyExpr: Expr) : bool =
    match stripAnnot bodyExpr with
    | Match(Var(scrutinee, _), clauses, _) when scrutinee = paramName ->
        clauses |> List.exists (fun (pat, _, _) ->
            match pat with
            | EmptyListPat _ | ConsPat _ -> true
            | _ -> false)
    | _ -> false

/// Detect whether a function-call expression likely returns a string (Ptr).
/// Used to accumulate known-string variables from let-bindings.
let private isStringReturningExpr (expr: Expr) : bool =
    match stripAnnot expr with
    | App(Var(fname, _), _, _) ->
        fname = "readIdent" || fname = "lexeme" || fname = "to_string" ||
        fname.Contains("toString") || fname.Contains("string") ||
        fname = "string_concat" || fname = "String.concat" || fname = "String.sub"
    | App(App(Var(fname, _), _, _), _, _) ->
        fname = "string_concat" || fname = "String.concat" || fname = "^"
    | String _ -> true
    | _ -> false

/// Phase 43-fix: Detect whether a param needs Ptr type (list OR record OR string access in body).
/// Used to correctly type function parameters in the single-arg Lambda case.
let rec private isPtrParamBody (paramName: string) (bodyExpr: Expr) : bool =
    // First check the existing list-pattern detection
    if isListParamBody paramName bodyExpr then true
    else
    // Also check if body accesses param as a record or uses it in string ops (needs Ptr).
    // strVars: set of let-bound variable names known to hold string values.
    let rec hasParamPtrUse (strVars: Set<string>) (expr: Expr) : bool =
        match stripAnnot expr with
        // Record field access on param directly → param must be Ptr
        | FieldAccess(Var(v, _), _, _) when v = paramName -> true
        // String builtins that expect Ptr (string concat, etc.) applied to param
        | App(App(Var("string_concat", _), Var(v, _), _), _, _) when v = paramName -> true
        | App(App(Var("string_concat", _), _, _), Var(v, _), _) when v = paramName -> true
        | App(Var("string_length", _), Var(v, _), _) when v = paramName -> true
        | App(Var("to_string", _), Var(v, _), _) when v = paramName -> false  // to_string works on I64 too
        // fst/snd on param → param is a tuple (Ptr)
        | App(Var("fst", _), Var(v, _), _) when v = paramName -> true
        | App(Var("snd", _), Var(v, _), _) when v = paramName -> true
        // Passing param to eprintfn/eprintln/println/printfn → param is likely a string (Ptr)
        | App(Var("eprintln", _), Var(v, _), _) when v = paramName -> true
        | App(Var("eprint", _), Var(v, _), _) when v = paramName -> true
        | App(Var("println", _), Var(v, _), _) when v = paramName -> true
        | App(App(Var("eprintfn", _), _, _), Var(v, _), _) when v = paramName -> true
        | App(App(Var("printfn", _), _, _), Var(v, _), _) when v = paramName -> true
        // Note: Add(Var(v), _) is NOT treated as Ptr — Add is integer arith (arith.addi).
        // String concat uses string_concat builtin, not Add.
        // Check match arms on param (for string or non-list param)
        | Match(Var(scrutinee, _), clauses, _) when scrutinee = paramName ->
            clauses |> List.exists (fun (pat, _, arm) ->
                match pat with
                | WildcardPat _ | VarPat _ -> false  // neutral patterns
                | ConstructorPat _ -> true  // ADT constructor pattern → param is Ptr
                | ConstPat(StringConst _, _) -> true  // string constant pattern → param is string (Ptr)
                | _ -> false)
        // Constructor call with param as argument → param is ADT/Ptr
        | Constructor(_, Some(Var(v, _)), _) when v = paramName -> true
        | Constructor(_, Some arg, _) -> hasParamPtrUse strVars arg
        // Equality comparison on param with a string literal → param is Ptr
        | Equal(Var(v, _), String(_, _), _) when v = paramName -> true
        | Equal(String(_, _), Var(v, _), _) when v = paramName -> true
        | NotEqual(Var(v, _), String(_, _), _) when v = paramName -> true
        | NotEqual(String(_, _), Var(v, _), _) when v = paramName -> true
        // Equality comparison on param with a known-string variable → param is Ptr
        | Equal(Var(v, _), Var(w, _), _) when v = paramName && Set.contains w strVars -> true
        | Equal(Var(w, _), Var(v, _), _) when v = paramName && Set.contains w strVars -> true
        | NotEqual(Var(v, _), Var(w, _), _) when v = paramName && Set.contains w strVars -> true
        | NotEqual(Var(w, _), Var(v, _), _) when v = paramName && Set.contains w strVars -> true
        // param compared to non-string var — recurse in case there are other hints
        | Equal(Var(v, _), other, _) when v = paramName -> hasParamPtrUse strVars other
        | Equal(other, Var(v, _), _) when v = paramName -> hasParamPtrUse strVars other
        | NotEqual(Var(v, _), other, _) when v = paramName -> hasParamPtrUse strVars other
        | NotEqual(other, Var(v, _), _) when v = paramName -> hasParamPtrUse strVars other
        // Recurse through let bindings; accumulate known-string vars
        | Let(name, bindE, bodyE, _) ->
            let strVars' = if isStringReturningExpr bindE then Set.add name strVars else strVars
            hasParamPtrUse strVars bindE || hasParamPtrUse strVars' bodyE
        // Phase 62: LetPat(WildcardPat, e1, e2) is the desugared form of e1; e2 (statement sequence).
        // Must traverse to find param usage deeper in the body (was causing premature false).
        | LetPat(VarPat(vname, _), bindE, bodyE, _) ->
            let strVars' = if isStringReturningExpr bindE then Set.add vname strVars else strVars
            hasParamPtrUse strVars bindE || hasParamPtrUse strVars' bodyE
        | LetPat(_, bindE, bodyE, _) ->
            hasParamPtrUse strVars bindE || hasParamPtrUse strVars bodyE
        | Match(scrut, clauses, _) ->
            hasParamPtrUse strVars scrut || clauses |> List.exists (fun (_, _, arm) -> hasParamPtrUse strVars arm)
        | If(c, t, e, _) -> hasParamPtrUse strVars c || hasParamPtrUse strVars t || hasParamPtrUse strVars e
        | Equal(l, r, _) -> hasParamPtrUse strVars l || hasParamPtrUse strVars r
        | NotEqual(l, r, _) -> hasParamPtrUse strVars l || hasParamPtrUse strVars r
        | App(f, a, _) -> hasParamPtrUse strVars f || hasParamPtrUse strVars a
        | Lambda(p, b, _) when p <> paramName -> hasParamPtrUse strVars b
        | Annot(inner, _, _) -> hasParamPtrUse strVars inner
        // Phase 62: Recurse through SetField, LetMut, Assign, TryWith, LetRec, ForIn, While, For, Tuple, List
        | SetField(e, _, v, _) -> hasParamPtrUse strVars e || hasParamPtrUse strVars v
        | LetMut(_, v, b, _) -> hasParamPtrUse strVars v || hasParamPtrUse strVars b
        | Assign(_, v, _) -> hasParamPtrUse strVars v
        | TryWith(b, clauses, _) -> hasParamPtrUse strVars b || clauses |> List.exists (fun (_, _, arm) -> hasParamPtrUse strVars arm)
        | LetRec(bindings, body, _) -> hasParamPtrUse strVars body || bindings |> List.exists (fun (_, _, _, b, _) -> hasParamPtrUse strVars b)
        | ForInExpr(_, coll, body, _) -> hasParamPtrUse strVars coll || hasParamPtrUse strVars body
        | WhileExpr(cond, body, _) -> hasParamPtrUse strVars cond || hasParamPtrUse strVars body
        | ForExpr(_, s, _, e, body, _) -> hasParamPtrUse strVars s || hasParamPtrUse strVars e || hasParamPtrUse strVars body
        | Tuple(elems, _) -> elems |> List.exists (hasParamPtrUse strVars)
        | List(elems, _) -> elems |> List.exists (hasParamPtrUse strVars)
        | Cons(h, t, _) -> hasParamPtrUse strVars h || hasParamPtrUse strVars t
        | Raise(e, _) -> hasParamPtrUse strVars e
        | _ -> false
    hasParamPtrUse Set.empty bodyExpr

/// Phase 43: Detect whether an expression statically produces a boolean value.
/// Used to populate BoolVars for correct to_string dispatch.
let rec private isBoolExpr (boolVars: Set<string>) (knownFuncs: Map<string, FuncSignature>) (expr: Ast.Expr) : bool =
    match expr with
    | Ast.Bool _ -> true
    | Ast.Equal _ | Ast.NotEqual _ | Ast.LessThan _ | Ast.GreaterThan _
    | Ast.LessEqual _ | Ast.GreaterEqual _ -> true
    | Ast.And _ | Ast.Or _ -> true
    | Ast.Var (name, _) -> Set.contains name boolVars
    | Ast.App (Ast.Var (name, _), _, _) ->
        match Map.tryFind name knownFuncs with
        | Some sig_ -> sig_.ReturnIsBool
        | None -> Set.contains name boolVars
    // Phase 43: curried 2-lambda call — App(App(Var(f), arg1), arg2) returns inner closure's result
    | Ast.App (Ast.App (Ast.Var (name, _), _, _), _, _) ->
        match Map.tryFind name knownFuncs with
        | Some sig_ -> sig_.InnerReturnIsBool
        | None -> false
    | Ast.Annot (inner, _, _) -> isBoolExpr boolVars knownFuncs inner
    | Ast.LambdaAnnot _ -> false
    | _ -> false

/// Phase 43: Detect whether a function body's final expression returns bool.
/// Traverses through let bindings to find the tail expression.
let rec private bodyReturnsBool (expr: Expr) : bool =
    match stripAnnot expr with
    | Bool _ -> true
    | Equal _ | NotEqual _ | LessThan _ | GreaterThan _
    | LessEqual _ | GreaterEqual _ -> true
    | And _ | Or _ -> true
    | If (_, thenE, elseE, _) -> bodyReturnsBool thenE || bodyReturnsBool elseE
    | Let (_, _, cont, _) | LetMut (_, _, cont, _) -> bodyReturnsBool cont
    | LetRec (_, cont, _) -> bodyReturnsBool cont
    | LetPat (_, _, cont, _) -> bodyReturnsBool cont
    | Match (_, clauses, _) -> clauses |> List.exists (fun (_, _, arm) -> bodyReturnsBool arm)
    | _ -> false

/// Phase 11: Test a pattern against a scrutinee value.
/// Returns (condOpt, testOps, bodySetupOps, bindEnv) where:
///   condOpt      = None  -> unconditional match (WildcardPat / VarPat)
///   condOpt      = Some v -> I1 condition value; test ops must be emitted before the cond_br
///   testOps      = ops that compute the condition (run in the test/entry block)
///   bodySetupOps = ops that run at the START of the body block (e.g. ConsPat head/tail loads)
///   bindEnv      = env with any variable bindings from the pattern added
let rec private testPattern (env: ElabEnv) (scrutVal: MlirValue) (pat: Pattern)
    : MlirValue option * MlirOp list * MlirOp list * ElabEnv =
    match pat with
    | WildcardPat _ ->
        (None, [], [], env)
    | VarPat(name, _) ->
        let env' = { env with Vars = Map.add name scrutVal env.Vars }
        (None, [], [], env')
    | ConstPat(IntConst n, _) ->
        let kVal = { Name = freshName env; Type = I64 }
        let cond = { Name = freshName env; Type = I1 }
        let ops  = [ ArithConstantOp(kVal, int64 n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
        (Some cond, ops, [], env)
    | ConstPat(BoolConst b, _) ->
        let kVal = { Name = freshName env; Type = I1 }
        let cond = { Name = freshName env; Type = I1 }
        let n    = if b then 1L else 0L
        let ops  = [ ArithConstantOp(kVal, n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
        (Some cond, ops, [], env)
    | EmptyListPat _ ->
        // Test: scrutVal == null (is empty list)
        let nullVal = { Name = freshName env; Type = Ptr }
        let cond    = { Name = freshName env; Type = I1 }
        let ops     = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "eq", scrutVal, nullVal) ]
        (Some cond, ops, [], env)
    | ConsPat(hPat, tPat, _) ->
        // Test: scrutVal != null (is a cons cell)
        let nullVal  = { Name = freshName env; Type = Ptr }
        let cond     = { Name = freshName env; Type = I1 }
        let testOps  = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "ne", scrutVal, nullVal) ]
        // Body setup: load head and tail from cons cell
        let headVal  = { Name = freshName env; Type = I64 }
        let tailSlot = { Name = freshName env; Type = Ptr }
        let tailVal  = { Name = freshName env; Type = Ptr }
        let setupOps = [
            LlvmLoadOp(headVal, scrutVal)
            LlvmGEPLinearOp(tailSlot, scrutVal, 1)
            LlvmLoadOp(tailVal, tailSlot)
        ]
        // Bind head and tail names
        let headName = match hPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failWithSpan (Ast.patternSpanOf hPat) "Elaboration: ConsPat head must be VarPat or WildcardPat"
        let tailName = match tPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failWithSpan (Ast.patternSpanOf tPat) "Elaboration: ConsPat tail must be VarPat or WildcardPat"
        let env' =
            env
            |> (fun e -> match headName with Some n -> { e with Vars = Map.add n headVal e.Vars } | None -> e)
            |> (fun e -> match tailName with Some n -> { e with Vars = Map.add n tailVal e.Vars } | None -> e)
        (Some cond, testOps, setupOps, env')
    | ConstPat(StringConst s, _) ->
        // Elaborate the string literal for the pattern constant
        let (patStrVal, patStrOps) = elaborateStringLiteral env s
        // Get data ptr for pattern string: GEP field 1
        let patDataPtrVal   = { Name = freshName env; Type = Ptr }
        let patDataVal      = { Name = freshName env; Type = Ptr }
        // Get data ptr for scrutinee string: GEP field 1
        let scrutDataPtrVal = { Name = freshName env; Type = Ptr }
        let scrutDataVal    = { Name = freshName env; Type = Ptr }
        // strcmp result
        let cmpRes  = { Name = freshName env; Type = I32 }
        let zero32  = { Name = freshName env; Type = I32 }
        let cond    = { Name = freshName env; Type = I1 }
        let ops = patStrOps @ [
            LlvmGEPStructOp(patDataPtrVal, patStrVal, 1)
            LlvmLoadOp(patDataVal, patDataPtrVal)
            LlvmGEPStructOp(scrutDataPtrVal, scrutVal, 1)
            LlvmLoadOp(scrutDataVal, scrutDataPtrVal)
            LlvmCallOp(cmpRes, "@strcmp", [scrutDataVal; patDataVal])
            ArithConstantOp(zero32, 0L)
            ArithCmpIOp(cond, "eq", cmpRes, zero32)
        ]
        (Some cond, ops, [], env)

    | TuplePat(pats, _) ->
        // Tuple always matches structurally — unconditional.
        // GEP each field, load with appropriate type, bind sub-patterns.
        // All ops go into bodySetupOps (no condition needed).
        let loadTypeOfPat = function
            | TuplePat _ -> Ptr
            | _          -> I64
        let (setupOps, bindEnv) =
            pats
            |> List.mapi (fun i subPat -> (i, subPat))
            |> List.fold (fun (opsAcc, eAcc: ElabEnv) (i, subPat) ->
                let slotVal  = { Name = freshName env; Type = Ptr }
                let fieldVal = { Name = freshName env; Type = loadTypeOfPat subPat }
                let gepOp    = LlvmGEPLinearOp(slotVal, scrutVal, i)
                let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                match subPat with
                | VarPat(vname, _) ->
                    let eAcc' = { eAcc with Vars = Map.add vname fieldVal eAcc.Vars }
                    (opsAcc @ [gepOp; loadOp], eAcc')
                | WildcardPat _ ->
                    (opsAcc @ [gepOp; loadOp], eAcc)
                | TuplePat _ ->
                    // Nested tuple — recurse with fieldVal as the new scrutinee
                    let (_, innerTestOps, innerSetupOps, innerEnv) = testPattern eAcc fieldVal subPat
                    (opsAcc @ [gepOp; loadOp] @ innerTestOps @ innerSetupOps, innerEnv)
                | _ ->
                    failWithSpan (Ast.patternSpanOf subPat) "testPattern: TuplePat sub-pattern %A not supported" subPat
            ) ([], env)
        (None, [], setupOps, bindEnv)

    | _ ->
        failWithSpan (Ast.patternSpanOf pat) "testPattern: pattern %A not supported in v2" pat

// Phase 59: Decode a nested module path expression into a segment list.
// FieldAccess(FieldAccess(Constructor("Outer"), "Inner"), "foo") → Some ["Outer"; "Inner"; "foo"]
// Returns None if innermost expression is not a no-arg Constructor (i.e., it's a real record access).
// The Constructor(name, None, _) guard is critical — excludes data constructors with arguments.
let rec private tryDecodeModulePath (expr: Expr) : string list option =
    match expr with
    | Constructor(name, None, _) -> Some [name]
    | FieldAccess(inner, field, _) ->
        match tryDecodeModulePath inner with
        | Some segments -> Some (segments @ [field])
        | None -> None
    | _ -> None

let rec elaborateExpr (env: ElabEnv) (expr: Expr) : MlirValue * MlirOp list =
    match expr with
    | Number (n, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, int64 n)])
    | Char (c, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, int64 (int c))])
    | Add (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithAddIOp(result, lv, rv)])
    | Subtract (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithSubIOp(result, lv, rv)])
    | Multiply (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithMulIOp(result, lv, rv)])
    | Divide (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithDivSIOp(result, lv, rv)])
    | Modulo (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I64 }
        (result, lops @ rops @ [ArithRemSIOp(result, lv, rv)])
    | PipeRight (left, right, span) ->
        // x |> f  ≡  f x  ≡  App(right, left)
        elaborateExpr env (App(right, left, span))
    | ComposeRight (f, g, span) ->
        // f >> g  ≡  fun x -> g (f x)
        let n = env.Counter.Value
        env.Counter.Value <- n + 1
        let paramName = sprintf "__comp_%d" n
        let innerApp = App(f, Var(paramName, span), span)
        let outerApp = App(g, innerApp, span)
        elaborateExpr env (Lambda(paramName, outerApp, span))
    | ComposeLeft (f, g, span) ->
        // f << g  ≡  fun x -> f (g x)
        let n = env.Counter.Value
        env.Counter.Value <- n + 1
        let paramName = sprintf "__comp_%d" n
        let innerApp = App(g, Var(paramName, span), span)
        let outerApp = App(f, innerApp, span)
        elaborateExpr env (Lambda(paramName, outerApp, span))
    | Negate (inner, _) ->
        let (iv, iops) = elaborateExpr env inner
        let zero = { Name = freshName env; Type = I64 }
        let result = { Name = freshName env; Type = I64 }
        (result, iops @ [ArithConstantOp(zero, 0L); ArithSubIOp(result, zero, iv)])
    | Var (name, span) ->
        match Map.tryFind name env.Vars with
        | Some v ->
            if Set.contains name env.MutableVars then
                let loaded = { Name = freshName env; Type = I64 }
                (loaded, [LlvmLoadOp(loaded, v)])
            else
                (v, [])
        | None -> failWithSpan span "Elaboration: unbound variable '%s'" name
    // Phase 21: Mutable variable allocation
    | LetMut (name, initExpr, bodyExpr, _) ->
        let (initVal, initOps) = elaborateExpr env initExpr
        let sizeVal  = { Name = freshName env; Type = I64 }
        let cellPtr  = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(sizeVal, 8L)
            LlvmCallOp(cellPtr, "@GC_malloc", [sizeVal])
            LlvmStoreOp(initVal, cellPtr)
        ]
        let env' = { env with
                        Vars        = Map.add name cellPtr env.Vars
                        MutableVars = Set.add name env.MutableVars }
        let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
        (bodyVal, initOps @ allocOps @ bodyOps)
    // Phase 21: Mutable variable assignment
    | Assign (name, valExpr, span) ->
        let (newVal, valOps) = elaborateExpr env valExpr
        let cellPtr =
            match Map.tryFind name env.Vars with
            | Some v -> v
            | None -> failWithSpan span "Elaboration: unbound mutable variable '%s' in Assign" name
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, valOps @ [LlvmStoreOp(newVal, cellPtr); ArithConstantOp(unitVal, 0L)])
    // Phase 5: special-case Let(name, Lambda(outerParam, Lambda(innerParam, innerBody)), inExpr)
    // This compiles to an llvm.func body + func.func closure-maker + KnownFuncs entry
    // Phase 43: StripAnnot sees through Annot/LambdaAnnot wrappers from type annotations
    | Let (name, StripAnnot (Lambda (outerParam, StripAnnot (Lambda (innerParam, innerBody, _)), _)), inExpr, letSpan) ->
        // Step 1: Compute free variables of the inner lambda body relative to innerParam only.
        // These are variables that need to come from the closure environment struct.
        // outerParam IS one such variable — it's passed to the closure-maker and stored at env[1+i].
        // Using only {innerParam} as bound means outerParam appears free when it's used in innerBody.
        let captures =
            freeVars (Set.singleton innerParam) innerBody
            |> Set.filter (fun name -> Map.containsKey name env.Vars || name = outerParam)
            |> Set.toList
            |> List.sort
        let numCaptures = List.length captures

        // Step 2: Generate fresh closure function name
        let closureFnIdx = env.ClosureCounter.Value
        env.ClosureCounter.Value <- closureFnIdx + 1
        let closureFnName = sprintf "@closure_fn_%d" closureFnIdx

        // Step 3: Compile inner lambda body (llvm.func)
        // Build the initial vars: %arg0 = env ptr, %arg1 = innerParam (always ptr for uniform ABI)
        // If innerParam needs I64 type, coerce ptr arg1 to i64 via ptrtoint.
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = Ptr }
        // If innerParam needs I64 type, coerce ptr arg1 to i64 via ptrtoint.
        // The coerced value gets name %t0 (first SSA name after captures).
        let innerParamNeedsI64 = not (isPtrParamBody innerParam innerBody)
        let (innerParamVal, paramCoerceOps, captureStartIdx) =
            if innerParamNeedsI64 then
                let i64Val = { Name = "%t0"; Type = I64 }
                (i64Val, [LlvmPtrToIntOp(i64Val, arg1Val)], 1)
            else
                (arg1Val, [], 0)
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(innerParam, innerParamVal)]
              Counter = ref captureStartIdx; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              MutableVars = env.MutableVars; ArrayVars = Set.empty; CollectionVars = Map.empty
              BoolVars = Set.empty }

        // For each capture at index i: GEP to slot i+1, then load
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" (captureStartIdx + i); Type = Ptr }
                let capType = if Set.contains capName env.MutableVars then Ptr else I64
                let capVal = { Name = sprintf "%%t%d" (captureStartIdx + i + numCaptures); Type = capType }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                // Advance counter past GEP and load names we pre-allocated
                let gepOp = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)

        // Advance inner env counter past the pre-allocated coerce + GEP/load SSA names
        innerEnvWithCaptures.Counter.Value <- captureStartIdx + numCaptures * 2

        // Elaborate inner body
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures innerBody
        // Phase 35: Normalize body return to I64 (I1→zext, Ptr→ptrtoint) for uniform closure ABI.
        let (finalBodyVal, coerceRetOps) = coerceToI64 innerEnvWithCaptures bodyVal
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value

        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = paramCoerceOps @ captureLoadOps @ bodyEntryOps @ coerceRetOps @ [LlvmReturnOp [finalBodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = paramCoerceOps @ captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ coerceRetOps @ [LlvmReturnOp [finalBodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched

        let innerFuncOp : FuncOp =
            { Name        = closureFnName
              InputTypes  = [Ptr; Ptr]
              ReturnType  = Some I64
              Body        = { Blocks = allBodyBlocks }
              IsLlvmFunc  = true }

        // Step 4: Compile closure-maker func.func
        // %arg0 = outerParam (I64), %arg1 = env ptr (Ptr, caller-allocated)
        let makerArg0 = { Name = "%arg0"; Type = I64 }
        let makerArg1 = { Name = "%arg1"; Type = Ptr }

        // Use a fresh counter starting after 0 for the closure-maker body ops
        let makerCounter = ref 0
        let nextMakerName () =
            let n = makerCounter.Value
            makerCounter.Value <- n + 1
            sprintf "%%t%d" n

        let fnPtrName = nextMakerName ()
        let fnPtrVal = { Name = fnPtrName; Type = Ptr }

        // addressof + store fn_ptr at env[0]
        let makerOps =
            [ LlvmAddressOfOp(fnPtrVal, closureFnName)
              LlvmStoreOp(fnPtrVal, makerArg1) ]

        // For each capture at index i: GEP to slot i+1, store the captured value
        let captureStoreOps =
            captures |> List.mapi (fun i capName ->
                let slotName = nextMakerName ()
                let slotVal = { Name = slotName; Type = Ptr }
                let gepOp = LlvmGEPLinearOp(slotVal, makerArg1, i + 1)
                // Look up capture value in the OUTER env
                // If the capture name is the outerParam, it IS %arg0
                let captureVal =
                    if capName = outerParam then makerArg0
                    else
                        match Map.tryFind capName env.Vars with
                        | Some v -> v
                        | None -> failWithSpan letSpan "Elaboration: closure capture '%s' not found in outer scope" capName
                let storeOp = LlvmStoreOp(captureVal, slotVal)
                [gepOp; storeOp]
            ) |> List.concat

        let makerBodyOps = makerOps @ captureStoreOps @ [ReturnOp [makerArg1]]

        let makerFuncOp : FuncOp =
            { Name        = "@" + name
              InputTypes  = [I64; Ptr]
              ReturnType  = Some Ptr
              Body        = { Blocks = [ { Label = None; Args = []; Body = makerBodyOps } ] }
              IsLlvmFunc  = false }

        // Step 5: Add both FuncOps to env.Funcs
        // Phase 53: If maker function with same name already exists (e.g., from Prelude InstanceDecl
        // shadowing of eq/show), replace it to avoid MLIR "redefinition of symbol" errors.
        let makerMlirName = "@" + name
        let existingFuncs = env.Funcs.Value
        if existingFuncs |> List.exists (fun f -> f.Name = makerMlirName) then
            env.Funcs.Value <- (existingFuncs |> List.filter (fun f -> f.Name <> makerMlirName)) @ [innerFuncOp; makerFuncOp]
        else
            env.Funcs.Value <- existingFuncs @ [innerFuncOp; makerFuncOp]

        // Step 6: Add to KnownFuncs
        let closureInfo = { InnerLambdaFn = closureFnName; NumCaptures = numCaptures
                            InnerReturnIsBool = bodyReturnsBool innerBody }
        let sig_ : FuncSignature =
            { MlirName    = "@" + name
              ParamTypes  = [I64]
              ReturnType  = Ptr
              ClosureInfo = Some closureInfo
              ReturnIsBool = false
              InnerReturnIsBool = bodyReturnsBool innerBody }
        // Phase 35: Module-qualified naming — also register short name alias in KnownFuncs
        // so that references within the same module body resolve correctly.
        let twoLambdaShortAlias =
            let idx = name.IndexOf('_')
            if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
            else None
        let env' =
            let kf = Map.add name sig_ env.KnownFuncs
            let kf' = match twoLambdaShortAlias with Some sn -> Map.add sn sig_ kf | None -> kf
            { env with KnownFuncs = kf' }

        // Step 7: Elaborate inExpr with updated env
        elaborateExpr env' inExpr

    // Phase 41: OpenDecl alias — Let(shortName, Var(qualifiedName), cont) where qualifiedName is in
    // KnownFuncs (two-lambda direct-call function) but not Vars. Add shortName as KnownFuncs alias.
    | Let (name, Var (qualName, _), bodyExpr, _) when
        not (Map.containsKey qualName env.Vars) && Map.containsKey qualName env.KnownFuncs ->
        let sig_ = Map.find qualName env.KnownFuncs
        let env' = { env with KnownFuncs = Map.add name sig_ env.KnownFuncs }
        elaborateExpr env' bodyExpr
    // Single-arg Let-Lambda: compile as named func.func and add to KnownFuncs (not Vars).
    // This prevents the function value being captured as a closure in nested two-arg functions,
    // which would cause MLIR "value defined outside region" errors.
    | Let (name, StripAnnot (Lambda (param, body, _)), inExpr, _)
        when (match stripAnnot body with Lambda _ -> false | _ -> true)
          && (freeVars (Set.singleton param) body
              |> Set.filter (fun v -> Map.containsKey v env.Vars)
              |> Set.isEmpty) ->
        let paramType = if isPtrParamBody param body then Ptr else I64
        let preReturnType = match stripAnnot body with | Lambda _ -> Ptr | _ -> I64
        let retIsBool = preReturnType = I64 && bodyReturnsBool body
        let innerRetIsBool = match stripAnnot body with Lambda(_, innerBody, _) -> bodyReturnsBool innerBody | _ -> false
        let sig_ : FuncSignature =
            { MlirName = "@" + name; ParamTypes = [paramType]; ReturnType = preReturnType; ClosureInfo = None
              ReturnIsBool = retIsBool; InnerReturnIsBool = innerRetIsBool }
        let shortNameAlias =
            let idx = name.IndexOf('_')
            if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
            else None
        let bodyEnv : ElabEnv =
            { Vars = Map.ofList [(param, { Name = "%arg0"; Type = paramType })]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs =
                let kf = env.KnownFuncs
                match shortNameAlias with Some sn -> Map.add sn sig_ kf | None -> kf
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              MutableVars = Set.empty; ArrayVars = Set.empty; CollectionVars = Map.empty
              BoolVars = Set.empty }
        let (bodyVal, bodyEntryOps) = elaborateExpr bodyEnv body
        let bodySideBlocks = bodyEnv.Blocks.Value
        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = bodyEntryOps @ [ReturnOp [bodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [ReturnOp [bodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched
        let funcOp : FuncOp =
            { Name = "@" + name
              InputTypes = [paramType]
              ReturnType = Some bodyVal.Type
              Body = { Blocks = allBodyBlocks }
              IsLlvmFunc = false }
        // Phase 53: If a function with the same name already exists (e.g., from Prelude InstanceDecl
        // shadowing), replace it rather than appending, to avoid MLIR "redefinition of symbol" errors.
        let mlirName = "@" + name
        let existingFuncs = env.Funcs.Value
        if existingFuncs |> List.exists (fun f -> f.Name = mlirName) then
            env.Funcs.Value <- existingFuncs |> List.map (fun f -> if f.Name = mlirName then funcOp else f)
        else
            env.Funcs.Value <- existingFuncs @ [funcOp]
        let finalSig = { sig_ with ReturnType = bodyVal.Type }
        let env' =
            let kf = Map.add name finalSig env.KnownFuncs
            let kf' = match shortNameAlias with Some sn -> Map.add sn finalSig kf | None -> kf
            { env with KnownFuncs = kf' }
        elaborateExpr env' inExpr
    | Let (name, bindExpr, bodyExpr, _) ->
        let blocksBeforeBind = env.Blocks.Value.Length
        let (bv, bops) = elaborateExpr env bindExpr
        // Phase 36 FIX-02: Capture block count AFTER bindExpr but BEFORE bodyExpr.
        // This records the index of the OUTER expression's merge block, so we patch
        // the correct block even when bodyExpr adds more side blocks (e.g. a second if).
        let blocksAfterBind = env.Blocks.Value.Length
        let arrayVars' = if isArrayExpr env.ArrayVars bindExpr then Set.add name env.ArrayVars else env.ArrayVars
        let collVars' =
            match detectCollectionKind env.CollectionVars bindExpr with
            | Some kind -> Map.add name kind env.CollectionVars
            | None -> env.CollectionVars
        // Phase 35: Module-qualified naming — if name is module-prefixed (e.g., "List_hd"),
        // also bind the short name ("hd") so internal cross-references within the module body work.
        let moduleShortName =
            let idx = name.IndexOf('_')
            if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
            else None
        // Phase 43: Track bool-producing bindings for to_string dispatch
        // Phase 43: Track bool-producing bindings AND bool-returning closures for to_string dispatch
        let boolVars' =
            if bv.Type = I1 || isBoolExpr env.BoolVars env.KnownFuncs bindExpr
               || (match stripAnnot bindExpr with Lambda(_, body, _) -> bodyReturnsBool body | _ -> false)
            then Set.add name env.BoolVars
            else env.BoolVars
        let baseVars = Map.add name bv env.Vars
        let varsWithAlias = match moduleShortName with Some sn -> Map.add sn bv baseVars | None -> baseVars
        let env' = { env with Vars = varsWithAlias; ArrayVars = arrayVars'; CollectionVars = collVars'; BoolVars = boolVars' }
        let (rv, rops) = elaborateExpr env' bodyExpr
        // If bops ends with a block terminator (from nested Match/TryWith/If), the
        // continuation code (rops) must go into the outer if's merge block (at blocksAfterBind - 1),
        // not the last block in env.Blocks (which may be the INNER if's merge block).
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        match List.tryLast bops with
        | Some op when isTerminator op && blocksAfterBind > blocksBeforeBind ->
            // Place rops in the OUTER merge block (captured BEFORE bodyExpr elaboration)
            let innerBlocks = env.Blocks.Value
            let targetIdx = blocksAfterBind - 1
            let targetBlock = innerBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = rops @ targetBlock.Body }
            env.Blocks.Value <- innerBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (rv, bops)  // bops alone (terminator as last op — valid MLIR block ending)
        | _ ->
            (rv, bops @ rops)
    // Phase 7: LetPat with wildcard or var pattern — enables "let _ = print ... in ..."
    // Phase 28: Also handles "e1; e2" (desugared to LetPat(WildcardPat, e1, e2)) where e1 is If(cond, then, unit).
    //   If bops ends with a block terminator (from nested If/Match), rops must go into the merge block,
    //   not appended inline (which would violate MLIR's block terminator rule).
    | LetPat (WildcardPat _, bindExpr, bodyExpr, _) ->
        let blocksBeforeBind = env.Blocks.Value.Length
        let (_bv, bops) = elaborateExpr env bindExpr
        // Phase 36 FIX-02: Capture block count AFTER bindExpr but BEFORE bodyExpr.
        // Same fix as Let case — targets the OUTER merge block, not the innermost one.
        let blocksAfterBind = env.Blocks.Value.Length
        let (rv, rops) = elaborateExpr env bodyExpr
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        let lastOp = List.tryLast bops
        eprintfn "DEBUG LetPat WildcardPat: bops.Length=%d lastOp=%A isterm=%A blocksAfterBind=%d blocksBeforeBind=%d rops.Length=%d" bops.Length lastOp (lastOp |> Option.map isTerminator) blocksAfterBind blocksBeforeBind rops.Length
        match lastOp with
        | Some op when isTerminator op && blocksAfterBind > blocksBeforeBind ->
            // Place rops in the OUTER merge block (captured BEFORE bodyExpr elaboration)
            let innerBlocks = env.Blocks.Value
            let targetIdx = blocksAfterBind - 1
            let targetBlock = innerBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = rops @ targetBlock.Body }
            eprintfn "DEBUG: patching block at idx=%d label=%A with %d ops" targetIdx targetBlock.Label rops.Length
            env.Blocks.Value <- innerBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (rv, bops)
        | _ ->
            (rv, bops @ rops)
    | LetPat (VarPat (name, _), bindExpr, bodyExpr, _) ->
        let (bv, bops) = elaborateExpr env bindExpr
        let arrayVars' = if isArrayExpr env.ArrayVars bindExpr then Set.add name env.ArrayVars else env.ArrayVars
        let collVars' =
            match detectCollectionKind env.CollectionVars bindExpr with
            | Some kind -> Map.add name kind env.CollectionVars
            | None -> env.CollectionVars
        let env' = { env with Vars = Map.add name bv env.Vars; ArrayVars = arrayVars'; CollectionVars = collVars' }
        let (rv, rops) = elaborateExpr env' bodyExpr
        (rv, bops @ rops)
    // Phase 9: LetPat with TuplePat — destructure a heap-allocated tuple via GEP + load
    | LetPat (TuplePat (pats, _), bindExpr, bodyExpr, _) ->
        let (rawTupVal, bindOps) = elaborateExpr env bindExpr
        // Phase 34-03: If the bind expression returns I64 (e.g., closure parameter from ForInExpr TuplePat),
        // coerce to Ptr before GEP-based destructuring.
        let (tupPtrVal, coerceOps) =
            if rawTupVal.Type = I64 then
                let pv = { Name = freshName env; Type = Ptr }
                (pv, [LlvmIntToPtrOp(pv, rawTupVal)])
            else
                (rawTupVal, [])
        // Recursively bind patterns to extracted values from a tuple ptr
        let rec bindTuplePat (envAcc: ElabEnv) (ptrVal: MlirValue) (subPats: Pattern list) : MlirOp list * ElabEnv =
            let loadTypeOfPat = function
                | TuplePat _ -> Ptr
                | _          -> I64
            subPats
            |> List.mapi (fun i pat -> (i, pat))
            |> List.fold (fun (opsAcc, eAcc: ElabEnv) (i, pat) ->
                let slotVal  = { Name = freshName env; Type = Ptr }
                let fieldVal = { Name = freshName env; Type = loadTypeOfPat pat }
                let gepOp    = LlvmGEPLinearOp(slotVal, ptrVal, i)
                let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                match pat with
                | VarPat (vname, _) ->
                    let eAcc' = { eAcc with Vars = Map.add vname fieldVal eAcc.Vars }
                    (opsAcc @ [gepOp; loadOp], eAcc')
                | WildcardPat _ ->
                    (opsAcc @ [gepOp; loadOp], eAcc)
                | TuplePat (innerPats, _) ->
                    // fieldVal is a Ptr to the inner tuple — recurse
                    let (innerOps, innerEnv) = bindTuplePat eAcc fieldVal innerPats
                    (opsAcc @ [gepOp; loadOp] @ innerOps, innerEnv)
                | _ ->
                    failWithSpan (Ast.patternSpanOf pat) "Elaboration: unsupported sub-pattern in TuplePat: %A" pat
            ) ([], envAcc)
        let (extractOps, env') = bindTuplePat env tupPtrVal pats
        let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
        (bodyVal, bindOps @ coerceOps @ extractOps @ bodyOps)
    | Bool (b, _) ->
        let v = { Name = freshName env; Type = I1 }
        let n = if b then 1L else 0L
        (v, [ArithConstantOp(v, n)])
    | Equal (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        // Phase 62: Coerce mismatched types before comparison (e.g., I64 vs Ptr from null list)
        let (lv, lops, rv, rops) =
            if lv.Type = Ptr && rv.Type = I64 then
                let c = { Name = freshName env; Type = I64 }
                (c, lops @ [LlvmPtrToIntOp(c, lv)], rv, rops)
            elif lv.Type = I64 && rv.Type = Ptr then
                let c = { Name = freshName env; Type = I64 }
                (lv, lops, c, rops @ [LlvmPtrToIntOp(c, rv)])
            else (lv, lops, rv, rops)
        if lv.Type = Ptr then
            // String equality via strcmp
            let lDataPtr   = { Name = freshName env; Type = Ptr }
            let lData      = { Name = freshName env; Type = Ptr }
            let rDataPtr   = { Name = freshName env; Type = Ptr }
            let rData      = { Name = freshName env; Type = Ptr }
            let cmpResult  = { Name = freshName env; Type = I32 }
            let zero32     = { Name = freshName env; Type = I32 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmGEPStructOp(lDataPtr, lv, 1)
                LlvmLoadOp(lData, lDataPtr)
                LlvmGEPStructOp(rDataPtr, rv, 1)
                LlvmLoadOp(rData, rDataPtr)
                LlvmCallOp(cmpResult, "@strcmp", [lData; rData])
                ArithConstantOp(zero32, 0L)
                ArithCmpIOp(boolResult, "eq", cmpResult, zero32)
            ]
            (boolResult, lops @ rops @ ops)
        else
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "eq", lv, rv)])
    | NotEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        // Phase 62: Coerce mismatched types before comparison
        let (lv, lops, rv, rops) =
            if lv.Type = Ptr && rv.Type = I64 then
                let c = { Name = freshName env; Type = I64 }
                (c, lops @ [LlvmPtrToIntOp(c, lv)], rv, rops)
            elif lv.Type = I64 && rv.Type = Ptr then
                let c = { Name = freshName env; Type = I64 }
                (lv, lops, c, rops @ [LlvmPtrToIntOp(c, rv)])
            else (lv, lops, rv, rops)
        if lv.Type = Ptr then
            // String inequality via strcmp != 0
            let lDataPtr   = { Name = freshName env; Type = Ptr }
            let lData      = { Name = freshName env; Type = Ptr }
            let rDataPtr   = { Name = freshName env; Type = Ptr }
            let rData      = { Name = freshName env; Type = Ptr }
            let cmpResult  = { Name = freshName env; Type = I32 }
            let zero32     = { Name = freshName env; Type = I32 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmGEPStructOp(lDataPtr, lv, 1)
                LlvmLoadOp(lData, lDataPtr)
                LlvmGEPStructOp(rDataPtr, rv, 1)
                LlvmLoadOp(rData, rDataPtr)
                LlvmCallOp(cmpResult, "@strcmp", [lData; rData])
                ArithConstantOp(zero32, 0L)
                ArithCmpIOp(boolResult, "ne", cmpResult, zero32)
            ]
            (boolResult, lops @ rops @ ops)
        else
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "ne", lv, rv)])
    | LessThan (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "slt", lv64, rv64)])
    | GreaterThan (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sgt", lv64, rv64)])
    | LessEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sle", lv64, rv64)])
    | GreaterEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let (lv64, lCoerce) = coerceToI64 env lv
        let (rv64, rCoerce) = coerceToI64 env rv
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ lCoerce @ rCoerce @ [ArithCmpIOp(result, "sge", lv64, rv64)])
    | If (condExpr, thenExpr, elseExpr, _) ->
        let blocksBeforeCond = env.Blocks.Value.Length
        let (condVal, condOps) = elaborateExpr env condExpr
        let blocksAfterCond = env.Blocks.Value.Length
        // Phase 35: If condition is I64 (e.g. result from module-wrapped bool builtin),
        // compare != 0 to produce I1 for cf.cond_br.
        let (i1CondVal, coerceCondOps) =
            if condVal.Type = I64 then
                let zeroVal = { Name = freshName env; Type = I64 }
                let boolVal = { Name = freshName env; Type = I1  }
                (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", condVal, zeroVal)])
            else (condVal, [])
        let thenLabel  = freshLabel env "then"
        let elseLabel  = freshLabel env "else"
        let mergeLabel = freshLabel env "merge"
        let blocksBeforeThen = env.Blocks.Value.Length
        let (thenVal, thenOps) = elaborateExpr env thenExpr
        let blocksAfterThen = env.Blocks.Value.Length
        let blocksBeforeElse = env.Blocks.Value.Length
        let (elseVal, elseOps) = elaborateExpr env elseExpr
        let blocksAfterElse = env.Blocks.Value.Length
        // If branch types differ, coerce both to I64 for uniform merge block type.
        // This handles cases like: then = Ptr (cons cell), else = I64 (closure result).
        let (finalThenVal, thenCoerceOps) =
            if thenVal.Type <> elseVal.Type then coerceToI64 env thenVal else (thenVal, [])
        let (finalElseVal, elseCoerceOps) =
            if thenVal.Type <> elseVal.Type then coerceToI64 env elseVal else (elseVal, [])
        let mergeArg = { Name = freshName env; Type = finalThenVal.Type }
        // Phase 42 FIX: When branch expr is a Match/If, its ops end with a terminator and
        // side blocks (match arms, match merge) are created in env.Blocks. The continuation
        // CfBrOp(mergeLabel) must go into the LAST side block (match's merge block), not
        // appended inline after the terminator (which would be unreachable).
        let isBranchTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        // Then block
        let thenBlockBody =
            match List.tryLast thenOps with
            | Some op when isBranchTerminator op && blocksAfterThen > blocksBeforeThen ->
                // thenExpr created side blocks (e.g. nested match).
                // Patch CfBrOp(mergeLabel) into the last side block (match's merge block).
                // IMPORTANT: append AFTER targetBlock.Body (which may contain ops computing thenVal).
                let allBlocks = env.Blocks.Value
                let targetIdx = blocksAfterThen - 1
                let targetBlock = allBlocks.[targetIdx]
                let patchedTarget = { targetBlock with Body = targetBlock.Body @ thenCoerceOps @ [CfBrOp(mergeLabel, [finalThenVal])] }
                env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
                thenOps  // dispatch ops only (terminator ends block)
            | _ ->
                thenOps @ thenCoerceOps @ [CfBrOp(mergeLabel, [finalThenVal])]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some thenLabel; Args = []; Body = thenBlockBody } ]
        // Else block
        let elseBlockBody =
            match List.tryLast elseOps with
            | Some op when isBranchTerminator op && blocksAfterElse > blocksBeforeElse ->
                // elseExpr created side blocks (e.g. nested match).
                // Patch CfBrOp(mergeLabel) into the last side block (match's merge block).
                // IMPORTANT: append AFTER targetBlock.Body (which may contain ops computing elseVal).
                let allBlocks = env.Blocks.Value
                let targetIdx = blocksAfterElse - 1
                let targetBlock = allBlocks.[targetIdx]
                let patchedTarget = { targetBlock with Body = targetBlock.Body @ elseCoerceOps @ [CfBrOp(mergeLabel, [finalElseVal])] }
                env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
                elseOps  // dispatch ops only
            | _ ->
                elseOps @ elseCoerceOps @ [CfBrOp(mergeLabel, [finalElseVal])]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some elseLabel; Args = []; Body = elseBlockBody } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        // Phase 36 FIX-03: If condOps ends with a terminator (e.g. And/Or produced a CfCondBrOp),
        // the If's own CfCondBrOp must go into the And/Or's merge block (blocksAfterCond - 1),
        // not appended inline after the terminator.
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        let ifBranchOp = CfCondBrOp(i1CondVal, thenLabel, [], elseLabel, [])
        match List.tryLast condOps with
        | Some op when isTerminator op && blocksAfterCond > blocksBeforeCond ->
            // Patch the If's CfCondBrOp into the condition's merge block
            let allBlocks = env.Blocks.Value
            let targetIdx = blocksAfterCond - 1
            let targetBlock = allBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = coerceCondOps @ [ifBranchOp] @ targetBlock.Body }
            env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (mergeArg, condOps)
        | _ ->
            (mergeArg, condOps @ coerceCondOps @ [ifBranchOp])
    | And (lhsExpr, rhsExpr, _) ->
        let blocksBeforeAnd = List.length env.Blocks.Value
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let blocksAfterLeft = List.length env.Blocks.Value
        // Phase 36 FIX-03: If leftVal is I64 (e.g. module Bool function returning I64),
        // coerce to I1 via != 0 comparison before use in cf.cond_br.
        let (i1LeftVal, coerceLeftOps) =
            if leftVal.Type = I64 then
                let zeroVal = { Name = freshName env; Type = I64 }
                let boolVal = { Name = freshName env; Type = I1  }
                (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", leftVal, zeroVal)])
            else (leftVal, [])
        let evalRightLabel = freshLabel env "and_right"
        let mergeLabel     = freshLabel env "and_merge"
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        // Phase 36 FIX-03: Coerce rightVal to I1 as well (merge block arg type must be I1).
        let (i1RightVal, coerceRightOps) =
            if rightVal.Type = I64 then
                let zeroVal = { Name = freshName env; Type = I64 }
                let boolVal = { Name = freshName env; Type = I1  }
                (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", rightVal, zeroVal)])
            else (rightVal, [])
        let mergeArg = { Name = freshName env; Type = I1 }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ coerceRightOps @ [CfBrOp(mergeLabel, [i1RightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        let andBranchOp = CfCondBrOp(i1LeftVal, evalRightLabel, [], mergeLabel, [i1LeftVal])
        // Phase 36 FIX-03: If leftOps ends with a terminator (nested And/Or), patch into merge block.
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        match List.tryLast leftOps with
        | Some op when isTerminator op && blocksAfterLeft > blocksBeforeAnd ->
            let allBlocks = env.Blocks.Value
            let targetIdx = blocksAfterLeft - 1
            let targetBlock = allBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = coerceLeftOps @ [andBranchOp] @ targetBlock.Body }
            env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (mergeArg, leftOps)
        | _ ->
            (mergeArg, leftOps @ coerceLeftOps @ [andBranchOp])
    | Or (lhsExpr, rhsExpr, _) ->
        let blocksBeforeOr = List.length env.Blocks.Value
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let blocksAfterLeft = List.length env.Blocks.Value
        // Phase 36 FIX-03: If leftVal is I64 (e.g. module Bool function returning I64),
        // coerce to I1 via != 0 comparison before use in cf.cond_br.
        // Note: Or is short-circuit: true → merge (with left), false → eval right.
        let (i1LeftVal, coerceLeftOps) =
            if leftVal.Type = I64 then
                let zeroVal = { Name = freshName env; Type = I64 }
                let boolVal = { Name = freshName env; Type = I1  }
                (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", leftVal, zeroVal)])
            else (leftVal, [])
        let evalRightLabel = freshLabel env "or_right"
        let mergeLabel     = freshLabel env "or_merge"
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        // Phase 36 FIX-03: Coerce rightVal to I1 as well (merge block arg type must be I1).
        let (i1RightVal, coerceRightOps) =
            if rightVal.Type = I64 then
                let zeroVal = { Name = freshName env; Type = I64 }
                let boolVal = { Name = freshName env; Type = I1  }
                (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", rightVal, zeroVal)])
            else (rightVal, [])
        let mergeArg = { Name = freshName env; Type = I1 }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ coerceRightOps @ [CfBrOp(mergeLabel, [i1RightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        let orBranchOp = CfCondBrOp(i1LeftVal, mergeLabel, [i1LeftVal], evalRightLabel, [])
        // Phase 36 FIX-03: If leftOps ends with a terminator (nested Or/And), patch into merge block.
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        match List.tryLast leftOps with
        | Some op when isTerminator op && blocksAfterLeft > blocksBeforeOr ->
            let allBlocks = env.Blocks.Value
            let targetIdx = blocksAfterLeft - 1
            let targetBlock = allBlocks.[targetIdx]
            let patchedTarget = { targetBlock with Body = coerceLeftOps @ [orBranchOp] @ targetBlock.Body }
            env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = targetIdx then patchedTarget else b)
            (mergeArg, leftOps)
        | _ ->
            (mergeArg, leftOps @ coerceLeftOps @ [orBranchOp])
    | LetRec (bindings, inExpr, _) ->
        // Phase 66: Two-pass elaboration for mutual recursion (let rec ... and ...).
        // Pass 1: Pre-register ALL binding signatures in KnownFuncs so every body can call every sibling.
        // Pass 2: Elaborate each body with the full KnownFuncs, then update with actual return types.
        let bindingInfos =
            bindings |> List.map (fun (name, param, _paramTypeAnnot, body, _) ->
                let paramType = if isPtrParamBody param body then Ptr else I64
                let preReturnType = match stripAnnot body with | Lambda _ -> Ptr | _ -> I64
                let retIsBool = preReturnType = I64 && bodyReturnsBool body
                let innerRetIsBool = match stripAnnot body with Lambda(_, ib, _) -> bodyReturnsBool ib | _ -> false
                let shortNameAlias =
                    let idx = name.IndexOf('_')
                    if idx > 0 && System.Char.IsUpper(name.[0]) then Some (name.Substring(idx + 1))
                    else None
                let sig_ : FuncSignature =
                    { MlirName = "@" + name; ParamTypes = [paramType]; ReturnType = preReturnType; ClosureInfo = None
                      ReturnIsBool = retIsBool; InnerReturnIsBool = innerRetIsBool }
                (name, param, body, paramType, sig_, shortNameAlias))
        // Pass 1: Add all signatures to KnownFuncs (pre-return types)
        let allKnownFuncs =
            bindingInfos |> List.fold (fun kf (name, _, _, _, sig_, shortAlias) ->
                let kf' = Map.add name sig_ kf
                match shortAlias with Some sn -> Map.add sn sig_ kf' | None -> kf'
            ) env.KnownFuncs
        // Pass 2: Elaborate each body with full KnownFuncs, emit func.func, collect final sigs
        let finalKnownFuncs =
            bindingInfos |> List.fold (fun kfAcc (name, param, body, paramType, sig_, shortAlias) ->
                let bodyEnv : ElabEnv =
                    { Vars = Map.ofList [(param, { Name = "%arg0"; Type = paramType })]
                      Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
                      KnownFuncs = allKnownFuncs
                      Funcs = env.Funcs
                      ClosureCounter = env.ClosureCounter
                      Globals = env.Globals
                      GlobalCounter = env.GlobalCounter
                      TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
                      MutableVars = Set.empty; ArrayVars = Set.empty; CollectionVars = Map.empty
                      BoolVars = Set.empty }
                let (bodyVal, bodyEntryOps) = elaborateExpr bodyEnv body
                let bodySideBlocks = bodyEnv.Blocks.Value
                let allBodyBlocks =
                    if bodySideBlocks.IsEmpty then
                        [ { Label = None; Args = []; Body = bodyEntryOps @ [ReturnOp [bodyVal]] } ]
                    else
                        let entryBlock = { Label = None; Args = []; Body = bodyEntryOps }
                        let lastBlock = List.last bodySideBlocks
                        let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [ReturnOp [bodyVal]] }
                        let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                        entryBlock :: sideBlocksPatched
                let funcOp : FuncOp =
                    { Name = "@" + name
                      InputTypes = [paramType]
                      ReturnType = Some bodyVal.Type
                      Body = { Blocks = allBodyBlocks }
                      IsLlvmFunc = false }
                env.Funcs.Value <- env.Funcs.Value @ [funcOp]
                let finalSig = { sig_ with ReturnType = bodyVal.Type }
                let kfAcc' = Map.add name finalSig kfAcc
                match shortAlias with Some sn -> Map.add sn finalSig kfAcc' | None -> kfAcc'
            ) env.KnownFuncs
        let env' = { env with KnownFuncs = finalKnownFuncs }
        elaborateExpr env' inExpr
    // Phase 8: String literal → GC_malloc'd header struct
    | String (s, _) ->
        elaborateStringLiteral env s

    // Phase 14: string_sub builtin — App(App(App(Var("string_sub"), s), start), len)
    // Three-arg curried: must be matched before two-arg and one-arg App patterns
    | App (App (App (Var ("string_sub", _), strExpr, _), startExpr, _), lenExpr, _) ->
        let (strVal,   strOps)   = elaborateExpr env strExpr
        let (startVal, startOps) = elaborateExpr env startExpr
        let (lenVal,   lenOps)   = elaborateExpr env lenExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ startOps @ lenOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_sub", [strPtr; startVal; lenVal])])

    // Phase 14: string_contains builtin — App(App(Var("string_contains"), s), sub)
    | App (App (Var ("string_contains", _), strExpr, _), subExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (subVal, subOps) = elaborateExpr env subExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (subPtr, subCoerce) = coerceToPtrArg env subVal
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_string_contains", [strPtr; subPtr])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, strOps @ subOps @ strCoerce @ subCoerce @ ops)

    // Phase 31: string_endswith builtin — App(App(Var("string_endswith"), s), suffix)
    | App (App (Var ("string_endswith", _), strExpr, _), suffixExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (sufVal, sufOps) = elaborateExpr env suffixExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (sufPtr, sufCoerce) = coerceToPtrArg env sufVal
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_string_endswith", [strPtr; sufPtr])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, strOps @ sufOps @ strCoerce @ sufCoerce @ ops)

    // Phase 31: string_startswith builtin — App(App(Var("string_startswith"), s), prefix)
    | App (App (Var ("string_startswith", _), strExpr, _), prefixExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (pfxVal, pfxOps) = elaborateExpr env prefixExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (pfxPtr, pfxCoerce) = coerceToPtrArg env pfxVal
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_string_startswith", [strPtr; pfxPtr])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, strOps @ pfxOps @ strCoerce @ pfxCoerce @ ops)

    // Phase 31: string_trim builtin — App(Var("string_trim"), s)
    | App (Var ("string_trim", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_trim", [strPtr])])

    // Phase 54: string_split builtin — App(App(Var("string_split"), s), sep)
    | App (App (Var ("string_split", _), strExpr, _), sepExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (sepVal, sepOps) = elaborateExpr env sepExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (sepPtr, sepCoerce) = coerceToPtrArg env sepVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ sepOps @ strCoerce @ sepCoerce @ [LlvmCallOp(result, "@lang_string_split", [strPtr; sepPtr])])

    // Phase 54: string_indexof builtin — App(App(Var("string_indexof"), s), sub)
    | App (App (Var ("string_indexof", _), strExpr, _), subExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (subVal, subOps) = elaborateExpr env subExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (subPtr, subCoerce) = coerceToPtrArg env subVal
        let result = { Name = freshName env; Type = I64 }
        (result, strOps @ subOps @ strCoerce @ subCoerce @ [LlvmCallOp(result, "@lang_string_indexof", [strPtr; subPtr])])

    // Phase 54: string_replace builtin — App(App(App(Var("string_replace"), s), old), rep)
    | App (App (App (Var ("string_replace", _), strExpr, _), oldExpr, _), repExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (oldVal, oldOps) = elaborateExpr env oldExpr
        let (repVal, repOps) = elaborateExpr env repExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let (oldPtr, oldCoerce) = coerceToPtrArg env oldVal
        let (repPtr, repCoerce) = coerceToPtrArg env repVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ oldOps @ repOps @ strCoerce @ oldCoerce @ repCoerce @ [LlvmCallOp(result, "@lang_string_replace", [strPtr; oldPtr; repPtr])])

    // Phase 54: string_toupper builtin — App(Var("string_toupper"), s)
    | App (Var ("string_toupper", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_toupper", [strPtr])])

    // Phase 54: string_tolower builtin — App(Var("string_tolower"), s)
    | App (Var ("string_tolower", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_tolower", [strPtr])])

    // Phase 31: string_concat_list builtin — App(App(Var("string_concat_list"), sep), list)
    | App (App (Var ("string_concat_list", _), sepExpr, _), listExpr, _) ->
        let (sepVal,  sepOps)  = elaborateExpr env sepExpr
        let (listVal, listOps) = elaborateExpr env listExpr
        let (sepPtr,  sepCoerce)  = coerceToPtrArg env sepVal
        let (listPtr, listCoerce) = coerceToPtrArg env listVal
        let result = { Name = freshName env; Type = Ptr }
        (result, sepOps @ listOps @ sepCoerce @ listCoerce @ [LlvmCallOp(result, "@lang_string_concat_list", [sepPtr; listPtr])])

    // Phase 8: string_concat builtin — App(App(Var("string_concat"), a), b)
    // Must be placed BEFORE general App to avoid being caught by general App dispatch
    | App (App (Var ("string_concat", _), aExpr, _), bExpr, _) ->
        let (aVal, aOps) = elaborateExpr env aExpr
        let (bVal, bOps) = elaborateExpr env bExpr
        let (aPtr, aCoerce) = coerceToPtrArg env aVal
        let (bPtr, bCoerce) = coerceToPtrArg env bVal
        let result = { Name = freshName env; Type = Ptr }
        (result, aOps @ bOps @ aCoerce @ bCoerce @ [LlvmCallOp(result, "@lang_string_concat", [aPtr; bPtr])])

    // Phase 53: show builtin — polymorphic display, dispatches on static argument type.
    // Fires when argument is statically a string literal OR an integer/bool expression.
    // If the argument is a constructor, ADT variable, or other Ptr, falls through to the
    // derived/user-defined show function in KnownFuncs.
    | App (Var ("show", _), String (s, _), appSpan) ->
        // show on string literal: return the string as-is (identity)
        elaborateExpr env (Ast.String(s, appSpan))
    | App (Var ("show", _), argExpr, _) when
            (let isStr = match stripAnnot argExpr with
                         | Ast.Var(v, _) -> Map.tryFind v env.Vars |> Option.exists (fun mv -> mv.Type = Ptr)
                                            || Map.tryFind v env.KnownFuncs |> Option.exists (fun s -> s.ReturnType = Ptr)
                         | _ -> false
             not isStr && not (Map.containsKey "show" env.KnownFuncs &&
                               (Map.find "show" env.KnownFuncs).ParamTypes = [Ptr])) ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let result = { Name = freshName env; Type = Ptr }
        let isBool = argVal.Type = I1 || isBoolExpr env.BoolVars env.KnownFuncs argExpr
        if isBool then
            if argVal.Type = I1 then
                let extVal = { Name = freshName env; Type = I64 }
                (result, argOps @ [ArithExtuIOp(extVal, argVal); LlvmCallOp(result, "@lang_to_string_bool", [extVal])])
            else
                (result, argOps @ [LlvmCallOp(result, "@lang_to_string_bool", [argVal])])
        elif argVal.Type = Ptr then
            // Ptr: already a LangString* — return unchanged (handles string variables)
            (argVal, argOps)
        else
            // I64 — call lang_to_string_int
            (result, argOps @ [LlvmCallOp(result, "@lang_to_string_int", [argVal])])

    // Phase 53: eq builtin — polymorphic equality, dispatches on static argument type.
    // Fires when arguments are string literals or non-Ptr values.
    | App (App (Var ("eq", _), String(ls, lsSpan), _), String(rs, rsSpan), _) ->
        // eq on two string literals
        let (lv, lops) = elaborateExpr env (Ast.String(ls, lsSpan))
        let (rv, rops) = elaborateExpr env (Ast.String(rs, rsSpan))
        let lDataPtr   = { Name = freshName env; Type = Ptr }
        let lData      = { Name = freshName env; Type = Ptr }
        let rDataPtr   = { Name = freshName env; Type = Ptr }
        let rData      = { Name = freshName env; Type = Ptr }
        let cmpResult  = { Name = freshName env; Type = I32 }
        let zero32     = { Name = freshName env; Type = I32 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmGEPStructOp(lDataPtr, lv, 1)
            LlvmLoadOp(lData, lDataPtr)
            LlvmGEPStructOp(rDataPtr, rv, 1)
            LlvmLoadOp(rData, rDataPtr)
            LlvmCallOp(cmpResult, "@strcmp", [lData; rData])
            ArithConstantOp(zero32, 0L)
            ArithCmpIOp(boolResult, "eq", cmpResult, zero32)
        ]
        (boolResult, lops @ rops @ ops)
    | App (App (Var ("eq", _), lhsExpr, _), rhsExpr, _) when not (Map.containsKey "eq" env.KnownFuncs) ->
        let (lv, lops) = elaborateExpr env lhsExpr
        let (rv, rops) = elaborateExpr env rhsExpr
        if lv.Type = Ptr then
            // String equality via strcmp (same as Equal case for Ptr)
            let lDataPtr   = { Name = freshName env; Type = Ptr }
            let lData      = { Name = freshName env; Type = Ptr }
            let rDataPtr   = { Name = freshName env; Type = Ptr }
            let rData      = { Name = freshName env; Type = Ptr }
            let cmpResult  = { Name = freshName env; Type = I32 }
            let zero32     = { Name = freshName env; Type = I32 }
            let boolResult = { Name = freshName env; Type = I1 }
            let ops = [
                LlvmGEPStructOp(lDataPtr, lv, 1)
                LlvmLoadOp(lData, lDataPtr)
                LlvmGEPStructOp(rDataPtr, rv, 1)
                LlvmLoadOp(rData, rDataPtr)
                LlvmCallOp(cmpResult, "@strcmp", [lData; rData])
                ArithConstantOp(zero32, 0L)
                ArithCmpIOp(boolResult, "eq", cmpResult, zero32)
            ]
            (boolResult, lops @ rops @ ops)
        else
            // I64 (int, bool, char) — integer comparison
            let result = { Name = freshName env; Type = I1 }
            (result, lops @ rops @ [ArithCmpIOp(result, "eq", lv, rv)])

    // Phase 8: to_string builtin — dispatch on elaborated arg type
    // Phase 43: also check BoolVars/KnownFuncs for bool-returning function calls
    | App (Var ("to_string", _), argExpr, _) ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let result = { Name = freshName env; Type = Ptr }
        let isBool = argVal.Type = I1 || isBoolExpr env.BoolVars env.KnownFuncs argExpr
        if isBool then
            if argVal.Type = I1 then
                // Zero-extend I1 to I64 for C ABI compatibility (lang_to_string_bool takes int64_t)
                let extVal = { Name = freshName env; Type = I64 }
                (result, argOps @ [ArithExtuIOp(extVal, argVal); LlvmCallOp(result, "@lang_to_string_bool", [extVal])])
            else
                // I64 value known to be bool — call lang_to_string_bool directly
                (result, argOps @ [LlvmCallOp(result, "@lang_to_string_bool", [argVal])])
        elif argVal.Type = Ptr then
            // Phase 53: Ptr value is already a LangString* — return it unchanged.
            // This handles to_string on string arguments (and show string via Show string instance).
            (argVal, argOps)
        else
            // I64 and other numeric types — call lang_to_string_int directly
            (result, argOps @ [LlvmCallOp(result, "@lang_to_string_int", [argVal])])

    // Phase 8: string_length builtin — GEP field 0 and load
    | App (Var ("string_length", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let lenPtrVal = { Name = freshName env; Type = Ptr }
        let lenVal    = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(lenPtrVal, strPtr, 0)
            LlvmLoadOp(lenVal, lenPtrVal)
        ]
        (lenVal, strOps @ strCoerce @ ops)

    // Phase 14: failwith builtin — extract char* from LangString, call lang_failwith (noreturn)
    | App (Var ("failwith", _), msgExpr, _) ->
        let (msgVal, msgOps) = elaborateExpr env msgExpr
        let (msgPtr, msgCoerce) = coerceToPtrArg env msgVal
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, msgPtr, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallVoidOp("@lang_failwith", [dataAddrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, msgOps @ msgCoerce @ ops)

    // Phase 14: string_to_int builtin
    | App (Var ("string_to_int", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = I64 }
        (result, strOps @ strCoerce @ [LlvmCallOp(result, "@lang_string_to_int", [strPtr])])

    // Phase 22: array_set — three-arg (must appear before two-arg and one-arg patterns)
    // array_set arr idx val: bounds check, compute slot idx+1, GEP, store, return unit
    | App (App (App (Var ("array_set", _), arrExpr, _), idxExpr, _), valExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let oneVal    = { Name = freshName env; Type = I64 }
        let slotVal   = { Name = freshName env; Type = I64 }
        let elemPtr   = { Name = freshName env; Type = Ptr }
        let unitVal   = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmCallVoidOp("@lang_array_bounds_check", [arrPtrVal; idxVal])
            ArithConstantOp(oneVal, 1L)
            ArithAddIOp(slotVal, idxVal, oneVal)
            LlvmGEPDynamicOp(elemPtr, arrPtrVal, slotVal)
            LlvmStoreOp(valVal, elemPtr)
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, arrOps @ arrCoerceOps @ idxOps @ valOps @ ops)

    // Phase 22: array_get — two-arg
    // array_get arr idx: bounds check, compute slot idx+1, GEP, load
    | App (App (Var ("array_get", _), arrExpr, _), idxExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let oneVal  = { Name = freshName env; Type = I64 }
        let slotVal = { Name = freshName env; Type = I64 }
        let elemPtr = { Name = freshName env; Type = Ptr }
        let result  = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmCallVoidOp("@lang_array_bounds_check", [arrPtrVal; idxVal])
            ArithConstantOp(oneVal, 1L)
            ArithAddIOp(slotVal, idxVal, oneVal)
            LlvmGEPDynamicOp(elemPtr, arrPtrVal, slotVal)
            LlvmLoadOp(result, elemPtr)
        ]
        (result, arrOps @ arrCoerceOps @ idxOps @ ops)

    // Phase 22: array_create — two-arg
    // array_create n defVal: call lang_array_create(n, defVal)
    | App (App (Var ("array_create", _), nExpr, _), defExpr, _) ->
        let (nVal,   nOps)   = elaborateExpr env nExpr
        let (defVal, defOps) = elaborateExpr env defExpr
        // Ensure both args are I64 (defVal may be I1 from bool literals)
        let nI64 =
            if nVal.Type = I64 then (nVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, nVal)])
        let defI64 =
            if defVal.Type = I64 then (defVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, defVal)])
        let result = { Name = freshName env; Type = Ptr }
        let (nV, nCoerce)     = nI64
        let (defV, defCoerce) = defI64
        (result, nOps @ defOps @ nCoerce @ defCoerce @ [LlvmCallOp(result, "@lang_array_create", [nV; defV])])

    // Phase 22: array_length — one-arg
    // array_length arr: GEP slot 0 (length slot), load
    | App (Var ("array_length", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let lenPtr = { Name = freshName env; Type = Ptr }
        let result = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(lenPtr, arrPtrVal, 0)
            LlvmLoadOp(result, lenPtr)
        ]
        (result, arrOps @ arrCoerceOps @ ops)

    // Phase 22: array_of_list — one-arg
    | App (Var ("array_of_list", _), lstExpr, _) ->
        let (lstVal, lstOps) = elaborateExpr env lstExpr
        let (lstPtrVal, lstCoerceOps) = coerceToPtrArg env lstVal
        let result = { Name = freshName env; Type = Ptr }
        (result, lstOps @ lstCoerceOps @ [LlvmCallOp(result, "@lang_array_of_list", [lstPtrVal])])

    // Phase 22: array_to_list — one-arg
    | App (Var ("array_to_list", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = Ptr }
        (result, arrOps @ arrCoerceOps @ [LlvmCallOp(result, "@lang_array_to_list", [arrPtrVal])])

    // Phase 23: hashtable builtins
    // coerceToI64: converts a MlirValue to I64 if it isn't already (Ptr→I64, I1→I64)
    // Returns (coerced value, coercion ops list)

    // hashtable_set — three-arg (must appear before two-arg and one-arg patterns)
    // hashtable_set ht key val: dispatch to _str variant when key is Ptr; else coerce key+val to i64, call lang_hashtable_set (void), return unit
    | App (App (App (Var ("hashtable_set", _), htExpr, _), keyExpr, _), valExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let (valI64, valCoerce) =
            match valVal.Type with
            | I64 -> (valVal, [])
            | I1  -> let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, valVal)])
            | Ptr -> let v = { Name = freshName env; Type = I64 } in (v, [LlvmPtrToIntOp(v, valVal)])
            | _   -> (valVal, [])
        let unitVal = { Name = freshName env; Type = I64 }
        match keyVal.Type with
        | Ptr ->
            let ops = htCoerce @ valCoerce @ [LlvmCallVoidOp("@lang_hashtable_set_str", [htPtr; keyVal; valI64]); ArithConstantOp(unitVal, 0L)]
            (unitVal, htOps @ keyOps @ valOps @ ops)
        | _ ->
            let (keyI64, keyCoerce) =
                match keyVal.Type with
                | I64 -> (keyVal, [])
                | I1  -> let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, keyVal)])
                | _   -> (keyVal, [])
            let ops = htCoerce @ keyCoerce @ valCoerce @ [LlvmCallVoidOp("@lang_hashtable_set", [htPtr; keyI64; valI64]); ArithConstantOp(unitVal, 0L)]
            (unitVal, htOps @ keyOps @ valOps @ ops)

    // hashtable_get — two-arg
    // hashtable_get ht key: dispatch to _str variant when key is Ptr; else coerce key to i64, call lang_hashtable_get → i64
    | App (App (Var ("hashtable_get", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let result = { Name = freshName env; Type = I64 }
        match keyVal.Type with
        | Ptr ->
            (result, htOps @ keyOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_get_str", [htPtr; keyVal])])
        | _ ->
            let (keyI64, keyCoerce) =
                match keyVal.Type with
                | I64 -> (keyVal, [])
                | I1  -> let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, keyVal)])
                | _   -> (keyVal, [])
            (result, htOps @ keyOps @ htCoerce @ keyCoerce @ [LlvmCallOp(result, "@lang_hashtable_get", [htPtr; keyI64])])

    // Phase 28: IndexGet — arr.[i] or ht.[key] via runtime dispatch
    // Phase 37: dispatch to _str variant when index is Ptr (string key)
    | IndexGet (collExpr, idxExpr, _) ->
        let (collVal, collOps) = elaborateExpr env collExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let result = { Name = freshName env; Type = I64 }
        // Coerce collection to Ptr if it was loaded from a record slot (I64 → Ptr)
        let (collPtr, collCoerce) =
            if collVal.Type = Ptr then (collVal, [])
            else let v = { Name = freshName env; Type = Ptr } in (v, [LlvmIntToPtrOp(v, collVal)])
        match idxVal.Type with
        | Ptr ->
            (result, collOps @ collCoerce @ idxOps @ [LlvmCallOp(result, "@lang_index_get_str", [collPtr; idxVal])])
        | _ ->
            let (idxV, idxCoerce) =
                if idxVal.Type = I64 then (idxVal, [])
                else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, idxVal)])
            (result, collOps @ collCoerce @ idxOps @ idxCoerce @ [LlvmCallOp(result, "@lang_index_get", [collPtr; idxV])])

    // Phase 28: IndexSet — arr.[i] <- v or ht.[key] <- v via runtime dispatch
    // Phase 37: dispatch to _str variant when index is Ptr (string key); also handle Ptr-typed values via LlvmPtrToIntOp
    | IndexSet (collExpr, idxExpr, valExpr, _) ->
        let (collVal, collOps) = elaborateExpr env collExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (valV, valCoerce) =
            if valVal.Type = I64 then (valVal, [])
            else if valVal.Type = Ptr then
                let v = { Name = freshName env; Type = I64 } in (v, [LlvmPtrToIntOp(v, valVal)])
            else
                let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, valVal)])
        let unitVal = { Name = freshName env; Type = I64 }
        match idxVal.Type with
        | Ptr ->
            (unitVal, collOps @ idxOps @ valOps @ valCoerce @ [LlvmCallVoidOp("@lang_index_set_str", [collVal; idxVal; valV]); ArithConstantOp(unitVal, 0L)])
        | _ ->
            let (idxV, idxCoerce) =
                if idxVal.Type = I64 then (idxVal, [])
                else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, idxVal)])
            (unitVal, collOps @ idxOps @ valOps @ idxCoerce @ valCoerce @ [LlvmCallVoidOp("@lang_index_set", [collVal; idxV; valV]); ArithConstantOp(unitVal, 0L)])

    // hashtable_containsKey — two-arg
    // hashtable_containsKey ht key: dispatch to _str variant when key is Ptr; else call lang_hashtable_containsKey → i64 (0 or 1), compare ne 0 → I1
    | App (App (Var ("hashtable_containsKey", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let rawVal  = { Name = freshName env; Type = I64 }
        let zeroVal = { Name = freshName env; Type = I64 }
        let boolVal = { Name = freshName env; Type = I1  }
        match keyVal.Type with
        | Ptr ->
            let ops = htCoerce @ [
                LlvmCallOp(rawVal, "@lang_hashtable_containsKey_str", [htPtr; keyVal])
                ArithConstantOp(zeroVal, 0L)
                ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)
            ]
            (boolVal, htOps @ keyOps @ ops)
        | _ ->
            let (keyI64, keyCoerce) =
                match keyVal.Type with
                | I64 -> (keyVal, [])
                | I1  -> let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, keyVal)])
                | _   -> (keyVal, [])
            let ops = htCoerce @ keyCoerce @ [
                LlvmCallOp(rawVal, "@lang_hashtable_containsKey", [htPtr; keyI64])
                ArithConstantOp(zeroVal, 0L)
                ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)
            ]
            (boolVal, htOps @ keyOps @ ops)

    // hashtable_remove — two-arg
    // hashtable_remove ht key: dispatch to _str variant when key is Ptr; else coerce key to i64, call lang_hashtable_remove (void), return unit
    | App (App (Var ("hashtable_remove", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let unitVal = { Name = freshName env; Type = I64 }
        match keyVal.Type with
        | Ptr ->
            let ops = htCoerce @ [LlvmCallVoidOp("@lang_hashtable_remove_str", [htPtr; keyVal]); ArithConstantOp(unitVal, 0L)]
            (unitVal, htOps @ keyOps @ ops)
        | _ ->
            let (keyI64, keyCoerce) =
                match keyVal.Type with
                | I64 -> (keyVal, [])
                | I1  -> let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, keyVal)])
                | _   -> (keyVal, [])
            let ops = htCoerce @ keyCoerce @ [LlvmCallVoidOp("@lang_hashtable_remove", [htPtr; keyI64]); ArithConstantOp(unitVal, 0L)]
            (unitVal, htOps @ keyOps @ ops)

    // hashtable_keys — one-arg
    // hashtable_keys ht: call lang_hashtable_keys → Ptr (LangCons* list)
    | App (Var ("hashtable_keys", _), htExpr, _) ->
        let (htVal, htOps) = elaborateExpr env htExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let result = { Name = freshName env; Type = Ptr }
        (result, htOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_keys", [htPtr])])

    // Phase 37: hashtable_keys_str — one-arg (separate builtin; keys has no key arg to dispatch on)
    // hashtable_keys_str ht: call lang_hashtable_keys_str → Ptr (LangCons* list of string ptrs)
    | App (Var ("hashtable_keys_str", _), htExpr, _) ->
        let (htVal, htOps) = elaborateExpr env htExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let result = { Name = freshName env; Type = Ptr }
        (result, htOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_keys_str", [htPtr])])

    // Phase 37: hashtable_create_str — one-arg (takes unit), call lang_hashtable_create_str() → Ptr
    | App (Var ("hashtable_create_str", _), unitExpr, _) ->
        let (_, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_hashtable_create_str", [])])

    // hashtable_create — one-arg (takes unit, which the parser gives as App(Var "hashtable_create", unitExpr))
    // hashtable_create (): elaborate and discard the unit arg, call lang_hashtable_create() → Ptr
    | App (Var ("hashtable_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr   // elaborate unit arg for side-effects (none); discard result
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_hashtable_create", [])])

    // hashtable_trygetvalue — two-arg curried builtin returning Ptr (2-slot tuple: [bool, value])
    // hashtable_trygetvalue ht key: dispatch to _str variant when key is Ptr; else call lang_hashtable_trygetvalue → Ptr
    | App (App (Var ("hashtable_trygetvalue", _), htExpr, _), keyExpr, _) ->
        let (htVal,  htOps)  = elaborateExpr env htExpr
        let (keyVal, keyOps) = elaborateExpr env keyExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let result = { Name = freshName env; Type = Ptr }
        match keyVal.Type with
        | Ptr ->
            (result, htOps @ keyOps @ htCoerce @ [LlvmCallOp(result, "@lang_hashtable_trygetvalue_str", [htPtr; keyVal])])
        | _ ->
            let (kv, kCoerce) =
                if keyVal.Type = I64 then (keyVal, [])
                else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, keyVal)])
            (result, htOps @ keyOps @ htCoerce @ kCoerce @ [LlvmCallOp(result, "@lang_hashtable_trygetvalue", [htPtr; kv])])

    // hashtable_count — one-arg, inline GEP+load at field index 2 (size field of LangHashtable struct)
    // No C call needed: LangHashtable.size is at field index 2
    | App (Var ("hashtable_count", _), htExpr, _) ->
        let (htVal, htOps) = elaborateExpr env htExpr
        let (htPtr, htCoerce) = coerceToPtrArg env htVal
        let sizePtr = { Name = freshName env; Type = Ptr }
        let result  = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(sizePtr, htPtr, 2)   // field index 2 = size
            LlvmLoadOp(result, sizePtr)
        ]
        (result, htOps @ htCoerce @ ops)

    // Phase 24: array HOF builtins
    // array_fold — three-arg (must appear before two-arg patterns)
    // array_fold closure init arr: coerce closure to Ptr, coerce init to I64, call lang_array_fold → I64
    | App (App (App (Var ("array_fold", _), closureExpr, _), initExpr, _), arrExpr, _) ->
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let (initVal, initOps) = elaborateExpr env initExpr
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let closurePtrVal =
            if fVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else fVal
        let closureOps =
            if fVal.Type = I64
            then [LlvmIntToPtrOp(closurePtrVal, fVal)]
            else []
        let initI64 =
            if initVal.Type = I64 then (initVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, initVal)])
        let (initV, initCoerce) = initI64
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = I64 }
        (result, fOps @ closureOps @ initOps @ initCoerce @ arrOps @ arrCoerceOps @ [LlvmCallOp(result, "@lang_array_fold", [closurePtrVal; initV; arrPtrVal])])

    // array_iter — two-arg
    // array_iter closure arr: coerce closure to Ptr, call lang_array_iter (void), return unit
    | App (App (Var ("array_iter", _), closureExpr, _), arrExpr, _) ->
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let closurePtrVal =
            if fVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else fVal
        let closureOps =
            if fVal.Type = I64
            then [LlvmIntToPtrOp(closurePtrVal, fVal)]
            else []
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, fOps @ closureOps @ arrOps @ arrCoerceOps @ [LlvmCallVoidOp("@lang_array_iter", [closurePtrVal; arrPtrVal]); ArithConstantOp(unitVal, 0L)])

    // array_map — two-arg
    // array_map closure arr: coerce closure to Ptr, call lang_array_map → Ptr
    | App (App (Var ("array_map", _), closureExpr, _), arrExpr, _) ->
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let closurePtrVal =
            if fVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else fVal
        let closureOps =
            if fVal.Type = I64
            then [LlvmIntToPtrOp(closurePtrVal, fVal)]
            else []
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let result = { Name = freshName env; Type = Ptr }
        (result, fOps @ closureOps @ arrOps @ arrCoerceOps @ [LlvmCallOp(result, "@lang_array_map", [closurePtrVal; arrPtrVal])])

    // array_init — two-arg
    // array_init n closure: coerce n to I64, coerce closure to Ptr, call lang_array_init → Ptr
    | App (App (Var ("array_init", _), nExpr, _), closureExpr, _) ->
        let (nVal,   nOps)   = elaborateExpr env nExpr
        let (fVal,   fOps)   = elaborateExpr env closureExpr
        let nI64 =
            if nVal.Type = I64 then (nVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, nVal)])
        let (nV, nCoerce) = nI64
        let closurePtrVal =
            if fVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else fVal
        let closureOps =
            if fVal.Type = I64
            then [LlvmIntToPtrOp(closurePtrVal, fVal)]
            else []
        let result = { Name = freshName env; Type = Ptr }
        (result, nOps @ nCoerce @ fOps @ closureOps @ [LlvmCallOp(result, "@lang_array_init", [nV; closurePtrVal])])

    // list_sort_by — two-arg curried with closure coercion (mirrors array_map pattern)
    | App (App (Var ("list_sort_by", _), closureExpr, _), listExpr, _) ->
        let (fVal,    fOps)    = elaborateExpr env closureExpr
        let (listVal, listOps) = elaborateExpr env listExpr
        let closurePtrVal =
            if fVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else fVal
        let closureOps =
            if fVal.Type = I64
            then [LlvmIntToPtrOp(closurePtrVal, fVal)]
            else []
        let (listPtrVal, listCoerceOps) = coerceToPtrArg env listVal
        let result = { Name = freshName env; Type = Ptr }
        (result, fOps @ closureOps @ listOps @ listCoerceOps @ [LlvmCallOp(result, "@lang_list_sort_by", [closurePtrVal; listPtrVal])])

    // list_of_seq — one-arg identity pass-through
    | App (Var ("list_of_seq", _), seqExpr, _) ->
        let (seqVal, seqOps) = elaborateExpr env seqExpr
        let (seqPtr, coerceOps) = coerceToPtrArg env seqVal
        let result = { Name = freshName env; Type = Ptr }
        (result, seqOps @ coerceOps @ [LlvmCallOp(result, "@lang_list_of_seq", [seqPtr])])

    // Phase 32-03: array_sort — one-arg, void return (mirrors array_iter pattern)
    | App (Var ("array_sort", _), arrExpr, _) ->
        let (arrVal, arrOps) = elaborateExpr env arrExpr
        let (arrPtrVal, arrCoerceOps) = coerceToPtrArg env arrVal
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, arrOps @ arrCoerceOps @ [LlvmCallVoidOp("@lang_array_sort", [arrPtrVal]); ArithConstantOp(unitVal, 0L)])

    // Phase 32-03: array_of_seq — one-arg returning Ptr
    | App (Var ("array_of_seq", _), seqExpr, _) ->
        let (seqVal, seqOps) = elaborateExpr env seqExpr
        let (seqPtrVal, seqCoerceOps) = coerceToPtrArg env seqVal
        let result = { Name = freshName env; Type = Ptr }
        (result, seqOps @ seqCoerceOps @ [LlvmCallOp(result, "@lang_array_of_seq", [seqPtrVal])])

    // Phase 33-01: COL-01 StringBuilder
    | App (Var ("stringbuilder_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_sb_create", [])])

    | App (App (Var ("stringbuilder_append", _), sbExpr, _), strExpr, _) ->
        let (sbVal,  sbOps)  = elaborateExpr env sbExpr
        let (strVal, strOps) = elaborateExpr env strExpr
        let (sbPtr,  sbCoerce)  = coerceToPtrArg env sbVal
        let (strPtr, strCoerce) = coerceToPtrArg env strVal
        let result = { Name = freshName env; Type = Ptr }
        (result, sbOps @ strOps @ sbCoerce @ strCoerce @ [LlvmCallOp(result, "@lang_sb_append", [sbPtr; strPtr])])

    | App (Var ("stringbuilder_tostring", _), sbExpr, _) ->
        let (sbVal, sbOps) = elaborateExpr env sbExpr
        let (sbPtr, sbCoerce) = coerceToPtrArg env sbVal
        let result = { Name = freshName env; Type = Ptr }
        (result, sbOps @ sbCoerce @ [LlvmCallOp(result, "@lang_sb_tostring", [sbPtr])])

    // Phase 33-01: COL-02 HashSet
    | App (Var ("hashset_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_hashset_create", [])])

    | App (App (Var ("hashset_add", _), hsExpr, _), valExpr, _) ->
        let (hsVal,  hsOps)  = elaborateExpr env hsExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (hsPtr, hsCoerce) = coerceToPtrArg env hsVal
        let result = { Name = freshName env; Type = I64 }
        (result, hsOps @ valOps @ hsCoerce @ [LlvmCallOp(result, "@lang_hashset_add", [hsPtr; valVal])])

    | App (App (Var ("hashset_contains", _), hsExpr, _), valExpr, _) ->
        let (hsVal,  hsOps)  = elaborateExpr env hsExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (hsPtr, hsCoerce) = coerceToPtrArg env hsVal
        let result = { Name = freshName env; Type = I64 }
        (result, hsOps @ valOps @ hsCoerce @ [LlvmCallOp(result, "@lang_hashset_contains", [hsPtr; valVal])])

    | App (Var ("hashset_count", _), hsExpr, _) ->
        let (hsVal, hsOps) = elaborateExpr env hsExpr
        let (hsPtr, hsCoerce) = coerceToPtrArg env hsVal
        let result = { Name = freshName env; Type = I64 }
        (result, hsOps @ hsCoerce @ [LlvmCallOp(result, "@lang_hashset_count", [hsPtr])])

    // Phase 33-02: COL-03 Queue
    | App (Var ("queue_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_queue_create", [])])

    | App (App (Var ("queue_enqueue", _), qExpr, _), valExpr, _) ->
        let (qVal,   qOps)   = elaborateExpr env qExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (qPtr, qCoerce) = coerceToPtrArg env qVal
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, qOps @ valOps @ qCoerce @ [LlvmCallVoidOp("@lang_queue_enqueue", [qPtr; valVal]); ArithConstantOp(unitVal, 0L)])

    | App (App (Var ("queue_dequeue", _), qExpr, _), unitExpr, _) ->
        let (qVal,  qOps) = elaborateExpr env qExpr
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let (qPtr, qCoerce) = coerceToPtrArg env qVal
        let result = { Name = freshName env; Type = I64 }
        (result, qOps @ uOps @ qCoerce @ [LlvmCallOp(result, "@lang_queue_dequeue", [qPtr])])

    | App (Var ("queue_count", _), qExpr, _) ->
        let (qVal, qOps) = elaborateExpr env qExpr
        let (qPtr, qCoerce) = coerceToPtrArg env qVal
        let result = { Name = freshName env; Type = I64 }
        (result, qOps @ qCoerce @ [LlvmCallOp(result, "@lang_queue_count", [qPtr])])

    // Phase 33-02: COL-04 MutableList
    // NOTE: mutablelist_set (three-arg) MUST appear BEFORE mutablelist_get (two-arg)
    | App (App (App (Var ("mutablelist_set", _), mlExpr, _), idxExpr, _), valExpr, _) ->
        let (mlVal,  mlOps)  = elaborateExpr env mlExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        let (idxV, idxCoerce) =
            if idxVal.Type = I64 then (idxVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, idxVal)])
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, mlOps @ idxOps @ idxCoerce @ valOps @ mlCoerce @ [LlvmCallVoidOp("@lang_mlist_set", [mlPtr; idxV; valVal]); ArithConstantOp(unitVal, 0L)])

    | App (App (Var ("mutablelist_get", _), mlExpr, _), idxExpr, _) ->
        let (mlVal,  mlOps)  = elaborateExpr env mlExpr
        let (idxVal, idxOps) = elaborateExpr env idxExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        let (idxV, idxCoerce) =
            if idxVal.Type = I64 then (idxVal, [])
            else let v = { Name = freshName env; Type = I64 } in (v, [ArithExtuIOp(v, idxVal)])
        let result = { Name = freshName env; Type = I64 }
        (result, mlOps @ idxOps @ idxCoerce @ mlCoerce @ [LlvmCallOp(result, "@lang_mlist_get", [mlPtr; idxV])])

    | App (Var ("mutablelist_create", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_mlist_create", [])])

    | App (App (Var ("mutablelist_add", _), mlExpr, _), valExpr, _) ->
        let (mlVal,  mlOps)  = elaborateExpr env mlExpr
        let (valVal, valOps) = elaborateExpr env valExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, mlOps @ valOps @ mlCoerce @ [LlvmCallVoidOp("@lang_mlist_add", [mlPtr; valVal]); ArithConstantOp(unitVal, 0L)])

    | App (Var ("mutablelist_count", _), mlExpr, _) ->
        let (mlVal, mlOps) = elaborateExpr env mlExpr
        let (mlPtr, mlCoerce) = coerceToPtrArg env mlVal
        let result = { Name = freshName env; Type = I64 }
        (result, mlOps @ mlCoerce @ [LlvmCallOp(result, "@lang_mlist_count", [mlPtr])])

    // write_file — two-arg, void return
    | App (App (Var ("write_file", _), pathExpr, _), contentExpr, _) ->
        let (pathVal,    pathOps)    = elaborateExpr env pathExpr
        let (contentVal, contentOps) = elaborateExpr env contentExpr
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [LlvmCallVoidOp("@lang_file_write", [pathVal; contentVal]); ArithConstantOp(unitVal, 0L)]
        (unitVal, pathOps @ contentOps @ ops)

    // append_file — two-arg, void return (identical shape to write_file)
    | App (App (Var ("append_file", _), pathExpr, _), contentExpr, _) ->
        let (pathVal,    pathOps)    = elaborateExpr env pathExpr
        let (contentVal, contentOps) = elaborateExpr env contentExpr
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [LlvmCallVoidOp("@lang_file_append", [pathVal; contentVal]); ArithConstantOp(unitVal, 0L)]
        (unitVal, pathOps @ contentOps @ ops)

    // read_file — one-arg, returns Ptr (LangString*)
    | App (Var ("read_file", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, pathOps @ [LlvmCallOp(result, "@lang_file_read", [pathVal])])

    // file_exists — one-arg, returns bool (I1 via I64 comparison)
    | App (Var ("file_exists", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let rawVal  = { Name = freshName env; Type = I64 }
        let zeroVal = { Name = freshName env; Type = I64 }
        let boolVal = { Name = freshName env; Type = I1  }
        let ops = [
            LlvmCallOp(rawVal, "@lang_file_exists", [pathVal])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolVal, "ne", rawVal, zeroVal)
        ]
        (boolVal, pathOps @ ops)

    // eprint — one-arg, void return
    | App (Var ("eprint", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [LlvmCallVoidOp("@lang_eprint", [strVal]); ArithConstantOp(unitVal, 0L)]
        (unitVal, strOps @ ops)

    // eprintln — one-arg, void return
    | App (Var ("eprintln", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [LlvmCallVoidOp("@lang_eprintln", [strVal]); ArithConstantOp(unitVal, 0L)]
        (unitVal, strOps @ ops)

    // Phase 39: printfn — 2-arg case (MUST come before 1-arg case)
    | App (App (App (Var ("printfn", _), String (fmt, _), _), arg1Expr, _), arg2Expr, s) ->
        let sprintfExpr = App(App(App(Var("sprintf", s), String(fmt, s), s), arg1Expr, s), arg2Expr, s)
        elaborateExpr env (App(Var("println", s), sprintfExpr, s))

    // Phase 39: printfn — 1-arg case (MUST come before 0-arg case)
    | App (App (Var ("printfn", _), String (fmt, _), _), argExpr, s) ->
        let sprintfExpr = App(App(Var("sprintf", s), String(fmt, s), s), argExpr, s)
        elaborateExpr env (App(Var("println", s), sprintfExpr, s))

    // Phase 39: printfn — 0-arg case: printfn "literal" (desugar to println "literal")
    | App (Var ("printfn", _), String (fmt, _), s) ->
        elaborateExpr env (App(Var("println", s), String(fmt, s), s))

    // Phase 39: sprintf — 2-arg case (MUST come BEFORE 1-arg cases — see Pitfall 1)
    | App (App (App (Var ("sprintf", _), String (fmt, _), _), arg1Expr, _), arg2Expr, sprintfSpan)
        when (let specs = fmtSpecTypes fmt in specs.Length = 2) ->
        let specs = fmtSpecTypes fmt
        let fmtGlobal  = addStringGlobal env fmt
        let fmtPtrVal  = { Name = freshName env; Type = Ptr }
        let (arg1Val, arg1Ops) = elaborateExpr env arg1Expr
        let (arg2Val, arg2Ops) = elaborateExpr env arg2Expr
        let result = { Name = freshName env; Type = Ptr }
        match specs with
        | [IntSpec; IntSpec] ->
            let (a1, c1) = coerceToI64Arg env arg1Val
            let (a2, c2) = coerceToI64Arg env arg2Val
            let ops = [ LlvmAddressOfOp(fmtPtrVal, fmtGlobal); LlvmCallOp(result, "@lang_sprintf_2ii", [fmtPtrVal; a1; a2]) ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | [StrSpec; IntSpec] ->
            let (a1Ptr, c1) = coerceToPtrArg env arg1Val
            let dp1 = { Name = freshName env; Type = Ptr }
            let da1 = { Name = freshName env; Type = Ptr }
            let (a2, c2) = coerceToI64Arg env arg2Val
            let ops = [
                LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
                LlvmGEPStructOp(dp1, a1Ptr, 1); LlvmLoadOp(da1, dp1)
                LlvmCallOp(result, "@lang_sprintf_2si", [fmtPtrVal; da1; a2])
            ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | [IntSpec; StrSpec] ->
            let (a1, c1) = coerceToI64Arg env arg1Val
            let (a2Ptr, c2) = coerceToPtrArg env arg2Val
            let dp2 = { Name = freshName env; Type = Ptr }
            let da2 = { Name = freshName env; Type = Ptr }
            let ops = [
                LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
                LlvmGEPStructOp(dp2, a2Ptr, 1); LlvmLoadOp(da2, dp2)
                LlvmCallOp(result, "@lang_sprintf_2is", [fmtPtrVal; a1; da2])
            ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | [StrSpec; StrSpec] ->
            let (a1Ptr, c1) = coerceToPtrArg env arg1Val
            let (a2Ptr, c2) = coerceToPtrArg env arg2Val
            let dp1 = { Name = freshName env; Type = Ptr }
            let da1 = { Name = freshName env; Type = Ptr }
            let dp2 = { Name = freshName env; Type = Ptr }
            let da2 = { Name = freshName env; Type = Ptr }
            let ops = [
                LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
                LlvmGEPStructOp(dp1, a1Ptr, 1); LlvmLoadOp(da1, dp1)
                LlvmGEPStructOp(dp2, a2Ptr, 1); LlvmLoadOp(da2, dp2)
                LlvmCallOp(result, "@lang_sprintf_2ss", [fmtPtrVal; da1; da2])
            ]
            (result, arg1Ops @ arg2Ops @ c1 @ c2 @ ops)
        | _ -> failWithSpan sprintfSpan "sprintf: unsupported 2-arg specifier combo in '%s'" fmt

    // Phase 39: sprintf — 1-arg integer case (%d, %x, %02x, %c, etc.)
    | App (App (Var ("sprintf", _), String (fmt, _), _), argExpr, _)
        when (let specs = fmtSpecTypes fmt in specs.Length = 1 && specs.[0] = IntSpec) ->
        let fmtGlobal = addStringGlobal env fmt
        let fmtPtrVal = { Name = freshName env; Type = Ptr }
        let (argVal, argOps) = elaborateExpr env argExpr
        let (i64Val, coerceOps) = coerceToI64Arg env argVal
        let result = { Name = freshName env; Type = Ptr }
        let ops = [
            LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
            LlvmCallOp(result, "@lang_sprintf_1i", [fmtPtrVal; i64Val])
        ]
        (result, argOps @ coerceOps @ ops)

    // Phase 39: sprintf — 1-arg string case (%s)
    | App (App (Var ("sprintf", _), String (fmt, _), _), argExpr, _)
        when (let specs = fmtSpecTypes fmt in specs.Length = 1 && specs.[0] = StrSpec) ->
        let fmtGlobal   = addStringGlobal env fmt
        let fmtPtrVal   = { Name = freshName env; Type = Ptr }
        let (argVal, argOps) = elaborateExpr env argExpr
        let (argPtr, coerce) = coerceToPtrArg env argVal
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let result      = { Name = freshName env; Type = Ptr }
        let ops = [
            LlvmAddressOfOp(fmtPtrVal, fmtGlobal)
            LlvmGEPStructOp(dataPtrVal, argPtr, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(result, "@lang_sprintf_1s", [fmtPtrVal; dataAddrVal])
        ]
        (result, argOps @ coerce @ ops)

    // eprintfn — two-arg case: eprintfn "%s" str  (MUST come before one-arg case)
    | App (App (Var ("eprintfn", _), String (fmt, _), _), argExpr, _) when fmt = "%s" ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [LlvmCallVoidOp("@lang_eprintln", [argVal]); ArithConstantOp(unitVal, 0L)]
        (unitVal, argOps @ ops)

    // eprintfn — one-arg case: eprintfn "literal" (desugar to eprintln "literal")
    | App (Var ("eprintfn", _), String (fmt, _), s) ->
        elaborateExpr env (App(Var("eprintln", s), String(fmt, s), s))

    // Phase 27: write_lines — two-arg, void return (MUST come before one-arg arms)
    | App (App (Var ("write_lines", _), pathExpr, _), linesExpr, _) ->
        let (pathVal,  pathOps)  = elaborateExpr env pathExpr
        let (linesVal, linesOps) = elaborateExpr env linesExpr
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [LlvmCallVoidOp("@lang_write_lines", [pathVal; linesVal]); ArithConstantOp(unitVal, 0L)]
        (unitVal, pathOps @ linesOps @ ops)

    // Phase 27: path_combine — two-arg, returns Ptr (MUST come before one-arg arms)
    | App (App (Var ("path_combine", _), dirExpr, _), fileExpr, _) ->
        let (dirVal,  dirOps)  = elaborateExpr env dirExpr
        let (fileVal, fileOps) = elaborateExpr env fileExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, dirOps @ fileOps @ [LlvmCallOp(result, "@lang_path_combine", [dirVal; fileVal])])

    // Phase 27: read_lines — one-arg, returns Ptr
    | App (Var ("read_lines", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, pathOps @ [LlvmCallOp(result, "@lang_read_lines", [pathVal])])

    // Phase 27: get_env — one-arg, returns Ptr
    | App (Var ("get_env", _), nameExpr, _) ->
        let (nameVal, nameOps) = elaborateExpr env nameExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, nameOps @ [LlvmCallOp(result, "@lang_get_env", [nameVal])])

    // Phase 27: dir_files — one-arg, returns Ptr
    | App (Var ("dir_files", _), pathExpr, _) ->
        let (pathVal, pathOps) = elaborateExpr env pathExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, pathOps @ [LlvmCallOp(result, "@lang_dir_files", [pathVal])])

    // Phase 27: stdin_read_line — unit-arg, returns Ptr (elaborate unit, discard, call with no args)
    | App (Var ("stdin_read_line", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_stdin_read_line", [])])

    // Phase 27: stdin_read_all — unit-arg, returns Ptr
    | App (Var ("stdin_read_all", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_stdin_read_all", [])])

    // Phase 27: get_cwd — unit-arg, returns Ptr
    | App (Var ("get_cwd", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_get_cwd", [])])

    // Phase 38: get_args — unit-arg, returns Ptr (LangCons* list of CLI arguments starting from argv[1])
    | App (Var ("get_args", _), unitExpr, _) ->
        let (_uVal, uOps) = elaborateExpr env unitExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, uOps @ [LlvmCallOp(result, "@lang_get_args", [])])

    // Phase 14: char_to_int — identity (char is already i64)
    | App (Var ("char_to_int", _), charExpr, _) ->
        elaborateExpr env charExpr

    // Phase 14: int_to_char — identity (int treated as char code point)
    | App (Var ("int_to_char", _), intExpr, _) ->
        elaborateExpr env intExpr

    // Phase 31: char_is_digit — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_digit", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_char_is_digit", [charVal])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, charOps @ ops)

    // Phase 31: char_is_letter — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_letter", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_char_is_letter", [charVal])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, charOps @ ops)

    // Phase 31: char_is_upper — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_upper", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_char_is_upper", [charVal])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, charOps @ ops)

    // Phase 31: char_is_lower — returns bool (bool-wrapping pattern)
    | App (Var ("char_is_lower", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        let rawResult  = { Name = freshName env; Type = I64 }
        let zeroVal    = { Name = freshName env; Type = I64 }
        let boolResult = { Name = freshName env; Type = I1 }
        let ops = [
            LlvmCallOp(rawResult, "@lang_char_is_lower", [charVal])
            ArithConstantOp(zeroVal, 0L)
            ArithCmpIOp(boolResult, "ne", rawResult, zeroVal)
        ]
        (boolResult, charOps @ ops)

    // Phase 31: char_to_upper — pass-through (returns i64 char code)
    | App (Var ("char_to_upper", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        let result = { Name = freshName env; Type = I64 }
        (result, charOps @ [LlvmCallOp(result, "@lang_char_to_upper", [charVal])])

    // Phase 31: char_to_lower — pass-through (returns i64 char code)
    | App (Var ("char_to_lower", _), charExpr, _) ->
        let (charVal, charOps) = elaborateExpr env charExpr
        let result = { Name = freshName env; Type = I64 }
        (result, charOps @ [LlvmCallOp(result, "@lang_char_to_lower", [charVal])])

    // Phase 7: print/println builtins — static literal fast path (keep before general case)
    | App (Var ("print", _), String (s, _), _) ->
        let globalName = addStringGlobal env s
        let ptrVal  = { Name = freshName env; Type = Ptr }
        let fmtRes  = { Name = freshName env; Type = I32 }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmAddressOfOp(ptrVal, globalName)
            LlvmCallOp(fmtRes, "@printf", [ptrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, ops)

    | App (Var ("println", _), String (s, _), _) ->
        let globalName = addStringGlobal env (s + "\n")
        let ptrVal  = { Name = freshName env; Type = Ptr }
        let fmtRes  = { Name = freshName env; Type = I32 }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmAddressOfOp(ptrVal, globalName)
            LlvmCallOp(fmtRes, "@printf", [ptrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, ops)

    // Phase 8: general print/println for string struct variables (Ptr type)
    // Phase 27: if strVal has I64 type (e.g., extracted from list cons cell head), cast to Ptr first
    | App (Var ("print", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (ptrVal, castOps) =
            if strVal.Type = I64 then
                let p = { Name = freshName env; Type = Ptr }
                (p, [LlvmIntToPtrOp(p, strVal)])
            else
                (strVal, [])
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let fmtRes      = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, ptrVal, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes, "@printf", [dataAddrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, strOps @ castOps @ ops)

    | App (Var ("println", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let (ptrVal, castOps) =
            if strVal.Type = I64 then
                let p = { Name = freshName env; Type = Ptr }
                (p, [LlvmIntToPtrOp(p, strVal)])
            else
                (strVal, [])
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let nlGlobal    = addStringGlobal env "\n"
        let nlPtrVal    = { Name = freshName env; Type = Ptr }
        let fmtRes1     = { Name = freshName env; Type = I32 }
        let fmtRes2     = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, ptrVal, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes1, "@printf", [dataAddrVal])
            LlvmAddressOfOp(nlPtrVal, nlGlobal)
            LlvmCallOp(fmtRes2, "@printf", [nlPtrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, strOps @ castOps @ ops)

    // Phase 59: Nested qualified function call — Outer.Inner.f arg → App(Var("Outer_Inner_f"), arg)
    // Handles chains of any depth. Must come BEFORE the single-level Constructor arm below.
    | App(FieldAccess(FieldAccess(_, _, _) as innerExpr, memberName, fspan), argExpr, span)
        when (tryDecodeModulePath innerExpr).IsSome
          && not (Map.containsKey memberName env.TypeEnv) ->
        let segments = (tryDecodeModulePath innerExpr).Value @ [memberName]
        let varName = segments |> String.concat "_"
        elaborateExpr env (App(Var(varName, fspan), argExpr, span))

    // Phase 25: Qualified function call desugar — M.f arg → App(Var("f"), arg)
    // Only for non-constructor members (constructors are handled by the FieldAccess arm → Constructor node).
    // Phase 35: Use module-qualified name (modName + "_" + memberName) — matches flattenDecls prefixing.
    // Must come BEFORE the general App arm so direct-call dispatch applies.
    | App (FieldAccess (Constructor (modName, None, _), memberName, fspan), argExpr, span)
        when not (Map.containsKey memberName env.TypeEnv) ->
        elaborateExpr env (App (Var (modName + "_" + memberName, fspan), argExpr, span))

    | App (funcExpr, argExpr, appSpan) ->
        match funcExpr with
        | Var (name, _) ->
            match Map.tryFind name env.KnownFuncs with
            | Some sig_ when sig_.ClosureInfo.IsNone ->
                // DIRECT CALL (Phase 4 behavior) — known non-closure function
                let (argVal, argOps) = elaborateExpr env argExpr
                // Coerce argument type to match function signature (e.g., Ptr→I64 for closure args).
                // This handles calls like `map closure_arg` where map expects I64 but closure is Ptr.
                let (coercedArgVal, coerceArgOps) =
                    match sig_.ParamTypes with
                    | [I64] when argVal.Type = Ptr ->
                        let coerced = { Name = freshName env; Type = I64 }
                        (coerced, [LlvmPtrToIntOp(coerced, argVal)])
                    | [Ptr] when argVal.Type = I64 ->
                        let coerced = { Name = freshName env; Type = Ptr }
                        (coerced, [LlvmIntToPtrOp(coerced, argVal)])
                    | _ -> (argVal, [])
                let result = { Name = freshName env; Type = sig_.ReturnType }
                (result, argOps @ coerceArgOps @ [DirectCallOp(result, sig_.MlirName, [coercedArgVal])])
            | Some sig_ ->
                // CLOSURE-MAKING CALL — allocate env on GC heap, then call
                let ci = sig_.ClosureInfo.Value
                let (argVal, argOps) = elaborateExpr env argExpr
                let bytesVal = { Name = freshName env; Type = I64 }
                let envPtrVal = { Name = freshName env; Type = Ptr }
                let resultVal = { Name = freshName env; Type = Ptr }
                let setupOps = [
                    ArithConstantOp(bytesVal, int64 ((ci.NumCaptures + 1) * 8))
                    LlvmCallOp(envPtrVal, "@GC_malloc", [bytesVal])
                ]
                // Closure-maker always expects I64 as first arg (uniform ABI).
                // If argVal is Ptr (e.g., a constructor closure), emit ptrtoint first.
                let (i64ArgVal, coerceOps) =
                    if argVal.Type = Ptr then
                        let coerced = { Name = freshName env; Type = I64 }
                        (coerced, [LlvmPtrToIntOp(coerced, argVal)])
                    else
                        (argVal, [])
                let callOp = DirectCallOp(resultVal, sig_.MlirName, [i64ArgVal; envPtrVal])
                (resultVal, argOps @ setupOps @ coerceOps @ [callOp])
            | None ->
                // Check Vars for closure value (type Ptr)
                match Map.tryFind name env.Vars with
                | Some closureVal when closureVal.Type = Ptr ->
                    // INDIRECT/CLOSURE CALL
                    let (argVal, argOps) = elaborateExpr env argExpr
                    let fnPtrVal = { Name = freshName env; Type = Ptr }
                    let result = { Name = freshName env; Type = I64 }
                    let loadOp = LlvmLoadOp(fnPtrVal, closureVal)
                    let callOp = IndirectCallOp(result, fnPtrVal, closureVal, argVal)
                    (result, argOps @ [loadOp; callOp])
                | Some closureVal when closureVal.Type = I64 ->
                    // I64 value may be a closure passed through uniform ABI — cast to Ptr and attempt indirect call
                    let (argVal, argOps) = elaborateExpr env argExpr
                    let closurePtrVal = { Name = freshName env; Type = Ptr }
                    let fnPtrVal = { Name = freshName env; Type = Ptr }
                    let result = { Name = freshName env; Type = I64 }
                    let castOp = LlvmIntToPtrOp(closurePtrVal, closureVal)
                    let loadOp = LlvmLoadOp(fnPtrVal, closurePtrVal)
                    let callOp = IndirectCallOp(result, fnPtrVal, closurePtrVal, argVal)
                    (result, argOps @ [castOp; loadOp; callOp])
                | _ ->
                    let knownNames =
                        (env.KnownFuncs |> Map.toList |> List.map fst) @
                        (env.Vars |> Map.toList |> List.map fst)
                        |> List.filter (fun n -> not (n.StartsWith("%")) && not (n.StartsWith("@")))
                        |> List.truncate 10
                        |> String.concat ", "
                    failWithSpan appSpan "Elaboration: unsupported App — '%s' is not a known function or closure value. In scope: %s" name knownNames
        | Lambda(param, body, _) ->
            // Inline lambda application: (fun x -> body) arg  ≡  let x = arg in body
            let (argVal, argOps) = elaborateExpr env argExpr
            let env' = { env with Vars = Map.add param argVal env.Vars }
            let (bodyVal, bodyOps) = elaborateExpr env' body
            (bodyVal, argOps @ bodyOps)
        | _ ->
            // General case: evaluate funcExpr to get a closure value, then dispatch
            let (funcVal, funcOps) = elaborateExpr env funcExpr
            let (argVal, argOps) = elaborateExpr env argExpr
            if funcVal.Type = Ptr then
                // Ptr-typed closure: load fn_ptr from slot 0, call indirect
                let fnPtrVal = { Name = freshName env; Type = Ptr }
                let result = { Name = freshName env; Type = I64 }
                let loadOp = LlvmLoadOp(fnPtrVal, funcVal)
                let callOp = IndirectCallOp(result, fnPtrVal, funcVal, argVal)
                (result, funcOps @ argOps @ [loadOp; callOp])
            elif funcVal.Type = I64 then
                // I64-typed closure (passed through uniform ABI): inttoptr then indirect call
                let closurePtrVal = { Name = freshName env; Type = Ptr }
                let fnPtrVal = { Name = freshName env; Type = Ptr }
                let result = { Name = freshName env; Type = I64 }
                let castOp = LlvmIntToPtrOp(closurePtrVal, funcVal)
                let loadOp = LlvmLoadOp(fnPtrVal, closurePtrVal)
                let callOp = IndirectCallOp(result, fnPtrVal, closurePtrVal, argVal)
                (result, funcOps @ argOps @ [castOp; loadOp; callOp])
            else
                failWithSpan appSpan "Elaboration: unsupported App — function expression elaborated to unsupported type %A" funcVal.Type
    // Phase 9: Tuple construction — GC_malloc(n*8) + sequential GEP + store
    // Phase 28: Tuple([]) = unit — return I64 0 (matches print/println unit convention; avoids type mismatch in if-then-without-else)
    | Tuple (exprs, _) ->
        let n = List.length exprs
        if n = 0 then
            // Empty tuple = unit value: return I64 0 (same as print/println)
            let unitVal = { Name = freshName env; Type = I64 }
            (unitVal, [ArithConstantOp(unitVal, 0L)])
        else
        // Elaborate all field expressions first
        let fieldResults = exprs |> List.map (fun e -> elaborateExpr env e)
        let allFieldOps  = fieldResults |> List.collect snd
        let fieldVals    = fieldResults |> List.map fst
        // GC_malloc(n * 8) to allocate the tuple struct
        let bytesVal  = { Name = freshName env; Type = I64 }
        let tupPtrVal = { Name = freshName env; Type = Ptr }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 (n * 8))
            LlvmCallOp(tupPtrVal, "@GC_malloc", [bytesVal])
        ]
        // Store each field: GEP ptr[i] + store value at slot
        let storeOps =
            fieldVals |> List.mapi (fun i fv ->
                let slotVal = { Name = freshName env; Type = Ptr }
                [ LlvmGEPLinearOp(slotVal, tupPtrVal, i)
                  LlvmStoreOp(fv, slotVal) ]
            ) |> List.concat
        (tupPtrVal, allFieldOps @ allocOps @ storeOps)

    // Phase 9: Match with single TuplePat arm — desugar to LetPat(TuplePat)
    | Match (scrutinee, [(TuplePat (pats, patSpan), None, body)], span) ->
        let syntheticPat = TuplePat(pats, patSpan)
        elaborateExpr env (LetPat(syntheticPat, scrutinee, body, span))

    // Phase 10: EmptyList — null pointer constant
    | EmptyList _ ->
        let v = { Name = freshName env; Type = Ptr }
        (v, [LlvmNullOp(v)])

    // Phase 10: Cons — GC_malloc(16) cons cell with head at slot 0, tail at slot 1
    | Cons(headExpr, tailExpr, _) ->
        let (headVal, headOps) = elaborateExpr env headExpr
        let (tailVal, tailOps) = elaborateExpr env tailExpr
        let bytesVal = { Name = freshName env; Type = I64 }
        let cellPtr  = { Name = freshName env; Type = Ptr }
        let tailSlot = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(bytesVal, 16L)
            LlvmCallOp(cellPtr, "@GC_malloc", [bytesVal])
            LlvmStoreOp(headVal, cellPtr)               // head at slot 0 (base ptr)
            LlvmGEPLinearOp(tailSlot, cellPtr, 1)       // slot 1 for tail
            LlvmStoreOp(tailVal, tailSlot)              // store tail ptr at slot 1
        ]
        (cellPtr, headOps @ tailOps @ allocOps)

    // Phase 10: List literal — desugar [e1; e2; e3] to Cons(e1, Cons(e2, Cons(e3, EmptyList)))
    | List(elems, span) ->
        let desugared = List.foldBack (fun elem acc -> Cons(elem, acc, span)) elems (EmptyList span)
        elaborateExpr env desugared

    // Phase 15: Range [start..stop] or [start..step..stop]
    // Compiled to a call to C runtime lang_range(start, stop, step).
    // Default step is 1 when not specified.
    | Range(startExpr, stopExpr, stepOpt, _) ->
        let (startVal, startOps) = elaborateExpr env startExpr
        let (stopVal,  stopOps)  = elaborateExpr env stopExpr
        let (stepVal,  stepOps)  =
            match stepOpt with
            | Some stepExpr -> elaborateExpr env stepExpr
            | None ->
                let v = { Name = freshName env; Type = I64 }
                (v, [ArithConstantOp(v, 1L)])
        let result = { Name = freshName env; Type = Ptr }
        (result, startOps @ stopOps @ stepOps @ [LlvmCallOp(result, "@lang_range", [startVal; stopVal; stepVal])])

    // Phase 11 (v2): General Match compiler — Jules Jacobs decision tree algorithm.
    // Compiles pattern matching to a binary decision tree, then emits MLIR from the tree.
    // Handles: VarPat, WildcardPat, ConstPat(Int/Bool/String), EmptyListPat, ConsPat, TuplePat.
    // Phase 13: adds OrPat expansion (PAT-07), when-guard (PAT-06), CharConst (PAT-08).
    | Match(scrutineeExpr, clauses, matchSpan) ->
        let (scrutVal, scrutOps) = elaborateExpr env scrutineeExpr
        let mergeLabel = freshLabel env "match_merge"
        let failLabel  = freshLabel env "match_fail"

        // PAT-07: Expand OrPat arms into individual arms sharing same guard and body
        let clauses =
            clauses |> List.collect (fun (pat, guard, body) ->
                match pat with
                | OrPat (alts, _) -> alts |> List.map (fun altPat -> (altPat, guard, body))
                | _ -> [(pat, guard, body)]
            )

        // Build arms for MatchCompiler: (Pattern * hasGuard * bodyIndex) list
        let arms = clauses |> List.mapi (fun i (pat, guardOpt, _body) -> (pat, guardOpt.IsSome, i))
        let rootAcc = MatchCompiler.Root scrutVal.Name

        // Compile to decision tree
        let tree = MatchCompiler.compile rootAcc arms

        // Map from Accessor to MlirValue, built up as we emit GEP/load ops.
        // Start with the root accessor mapped to the scrutinee value.
        let accessorCache = System.Collections.Generic.Dictionary<MatchCompiler.Accessor, MlirValue>()
        accessorCache.[rootAcc] <- scrutVal

        // Resolve an accessor to an MlirValue, emitting GEP + load ops as needed.
        // Returns (value, ops).
        let rec resolveAccessor (acc: MatchCompiler.Accessor) : MlirValue * MlirOp list =
            match accessorCache.TryGetValue(acc) with
            | true, v -> (v, [])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessor: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    let (rawParentVal, parentOps) = resolveAccessor parent
                    // Guard: GEP requires a Ptr parent. If the parent was cached as I64
                    // (e.g., ADT slot 1 loaded as I64 but holding a tuple Ptr), re-resolve
                    // the parent as Ptr so we can GEP into the pointed-to block.
                    let (parentVal, retypeOps) =
                        if rawParentVal.Type = Ptr then (rawParentVal, [])
                        else resolveAccessorTyped parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    // Default: load field as I64. resolveAccessorTyped handles re-loads
                    // when a specific type is needed (e.g., Ptr for list tail, tuple payload).
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache.[acc] <- fieldVal
                    (fieldVal, parentOps @ retypeOps @ [gepOp; loadOp])

        // Resolve an accessor, but load with a specific type override.
        // This is needed when we know the field should be Ptr (e.g. list tail, nested tuple).
        and resolveAccessorTyped (acc: MatchCompiler.Accessor) (ty: MlirType) : MlirValue * MlirOp list =
            match accessorCache.TryGetValue(acc) with
            | true, v when v.Type = ty -> (v, [])
            | true, v ->
                // Cached with wrong type — we need to re-load with correct type.
                // This happens when a field was first loaded as I64 but actually needs to be Ptr.
                // Emit a new load from the parent.
                match acc with
                | MatchCompiler.Root _ ->
                    // Phase 20: Root accessor type mismatch — emit inttoptr when root is I64 but Ptr needed.
                    // This occurs when a first-class constructor closure returns ptrtoint(ADT block),
                    // and the match scrutinee is the i64 result of the indirect call.
                    if v.Type = I64 && ty = Ptr then
                        let ptrVal = { Name = freshName env; Type = Ptr }
                        accessorCache.[acc] <- ptrVal
                        (ptrVal, [LlvmIntToPtrOp(ptrVal, v)])
                    else (v, [])
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessorTyped: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])

        // Determine what MlirType an accessor should have based on the constructor tag
        // being tested.  For ConsCtor: scrutinee is Ptr, field 0 = I64, field 1 = Ptr.
        // For NilCtor: scrutinee is Ptr.
        // For TupleCtor: scrutinee is Ptr, all fields default to I64 (nested tuples Ptr).
        // For IntLit/BoolLit: scrutinee is I64/I1 respectively.
        // For StringLit: scrutinee is Ptr.
        let scrutineeTypeForTag (tag: MatchCompiler.CtorTag) : MlirType =
            match tag with
            | MatchCompiler.IntLit _    -> I64
            | MatchCompiler.BoolLit _   -> I1
            | MatchCompiler.StringLit _ -> Ptr
            | MatchCompiler.ConsCtor    -> Ptr
            | MatchCompiler.NilCtor     -> Ptr
            | MatchCompiler.TupleCtor _ -> Ptr
            | MatchCompiler.AdtCtor _    -> Ptr
            | MatchCompiler.RecordCtor _ -> Ptr

        // Emit test ops for a constructor match against a scrutinee value.
        // Returns (condValue, testOps).
        let emitCtorTest (scrutVal: MlirValue) (tag: MatchCompiler.CtorTag) : MlirValue * MlirOp list =
            match tag with
            | MatchCompiler.IntLit n ->
                let kVal = { Name = freshName env; Type = I64 }
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(kVal, int64 n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.BoolLit b ->
                let kVal = { Name = freshName env; Type = I1 }
                let cond = { Name = freshName env; Type = I1 }
                let n = if b then 1L else 0L
                let ops = [ ArithConstantOp(kVal, n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.StringLit s ->
                let (patStrVal, patStrOps) = elaborateStringLiteral env s
                let patDataPtrVal   = { Name = freshName env; Type = Ptr }
                let patDataVal      = { Name = freshName env; Type = Ptr }
                let scrutDataPtrVal = { Name = freshName env; Type = Ptr }
                let scrutDataVal    = { Name = freshName env; Type = Ptr }
                let cmpRes  = { Name = freshName env; Type = I32 }
                let zero32  = { Name = freshName env; Type = I32 }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = patStrOps @ [
                    LlvmGEPStructOp(patDataPtrVal, patStrVal, 1)
                    LlvmLoadOp(patDataVal, patDataPtrVal)
                    LlvmGEPStructOp(scrutDataPtrVal, scrutVal, 1)
                    LlvmLoadOp(scrutDataVal, scrutDataPtrVal)
                    LlvmCallOp(cmpRes, "@strcmp", [scrutDataVal; patDataVal])
                    ArithConstantOp(zero32, 0L)
                    ArithCmpIOp(cond, "eq", cmpRes, zero32)
                ]
                (cond, ops)
            | MatchCompiler.NilCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "eq", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.ConsCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "ne", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.TupleCtor _ ->
                // Tuples always match structurally — emit an unconditional true
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)
            | MatchCompiler.AdtCtor(name, _, _) ->
                // Look up real tag from TypeEnv; load tag slot 0 and compare
                let info     = Map.find name env.TypeEnv
                let tagSlot  = { Name = freshName env; Type = Ptr }
                let tagLoad  = { Name = freshName env; Type = I64 }
                let tagConst = { Name = freshName env; Type = I64 }
                let cond     = { Name = freshName env; Type = I1 }
                let ops = [
                    LlvmGEPLinearOp(tagSlot, scrutVal, 0)
                    LlvmLoadOp(tagLoad, tagSlot)
                    ArithConstantOp(tagConst, int64 info.Tag)
                    ArithCmpIOp(cond, "eq", tagLoad, tagConst)
                ]
                (cond, ops)
            | MatchCompiler.RecordCtor _ ->
                // Records always match structurally — emit unconditional true
                let cond = { Name = freshName env; Type = I1 }
                let ops  = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)

        // Pre-populate accessor cache for ConsCtor sub-fields with correct types.
        // field 0 = head (I64), field 1 = tail (Ptr).
        let ensureConsFieldTypes (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                let (_, headOps) = resolveAccessorTyped argAccs.[0] I64
                ops <- ops @ headOps
            if argAccs.Length >= 2 then
                let (_, tailOps) = resolveAccessorTyped argAccs.[1] Ptr
                ops <- ops @ tailOps
            ops

        // Pre-populate accessor cache for AdtCtor sub-fields with correct types.
        // ADT blocks: slot 0 = tag (I64), slot 1 = payload (stored directly — I64 for int, Ptr for tuple/string).
        // For unary constructors the payload accessor is argAccs.[0] = Field(scrutAcc, 1).
        // We pre-load it as I64 (the default), which is correct for integer payloads.
        // For Ptr payloads the resolveAccessorTyped call in emitDecisionTree will re-load with the right type.
        let ensureAdtFieldTypes (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                // Payload is at slot 1; default load as I64
                let (_, payOps) = resolveAccessorTyped argAccs.[0] I64
                ops <- ops @ payOps
            ops

        // Pre-populate accessor cache for RecordCtor sub-fields with declaration-order slot indices.
        // MatchCompiler generates argAccessors in alphabetical field-name order, but RecordEnv stores
        // fields using declaration-order slot indices.  We must remap: for each field name (in
        // alphabetical order, matching argAccs.[i]), look up its declaration-order slot index in
        // RecordEnv and emit GEP+load using that slot so the correct heap word is read.
        let ensureRecordFieldTypes (fields: string list) (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let fieldSet = Set.ofList fields
            let fieldMap =
                env.RecordEnv
                |> Map.toSeq
                |> Seq.tryPick (fun (_, fm) ->
                    let fmFields = fm |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                    if fieldSet |> Set.forall (fun f -> Set.contains f fmFields) then Some fm
                    else None)
                |> Option.defaultWith (fun () ->
                    let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                    failWithSpan matchSpan "ensureRecordFieldTypes: cannot resolve record type for fields %A. Available record types: %s" fields availableTypes)
            let mutable ops = []
            fields |> List.iteri (fun i fieldName ->
                if i < argAccs.Length then
                    let declSlotIdx = Map.find fieldName fieldMap
                    let (parentVal, parentOps) = resolveAccessorTyped scrutAcc Ptr
                    let slotPtr  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let gepOp  = LlvmGEPLinearOp(slotPtr, parentVal, declSlotIdx)
                    let loadOp = LlvmLoadOp(fieldVal, slotPtr)
                    accessorCache.[argAccs.[i]] <- fieldVal
                    ops <- ops @ parentOps @ [gepOp; loadOp]
            )
            ops

        // Track the result type (determined by first leaf reached)
        let resultType = ref I64  // default; will be set by first arm body

        // Recursively emit blocks for a DecisionTree node.
        // Returns the list of ops for the current/entry block.
        let rec emitDecisionTree (tree: MatchCompiler.DecisionTree) : MlirOp list =
            match tree with
            | MatchCompiler.Fail ->
                [ CfBrOp(failLabel, []) ]
            | MatchCompiler.Leaf (bindings, bodyIdx) ->
                // Resolve all bindings: each (varName, accessor) → env binding
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                // Get the body expression from the original clauses
                let (_pat, _guard, bodyExpr) = clauses.[bodyIdx]
                let blocksBeforeBody = env.Blocks.Value.Length
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                // Coerce arm value to I64 for uniform merge block type (same as function body ABI)
                let (coercedVal, coerceOps) = coerceToI64 bindEnv bodyVal
                resultType.Value <- I64
                // Create a body block and branch to merge (skip merge branch if body already terminated)
                let isTerminator op =
                    match op with CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true | _ -> false
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some op when isTerminator op && env.Blocks.Value.Length > blocksBeforeBody ->
                        // Body ended with a block terminator (e.g., nested if/match).
                        // Continuation (coerce + branch to merge) goes into the last side block (nested merge block).
                        // Same pattern as Let handler: prepend rops to last side block's body.
                        let innerBlocks = env.Blocks.Value
                        let lastBlock = List.last innerBlocks
                        let contOps = coerceOps @ [CfBrOp(mergeLabel, [coercedVal])]
                        let patchedLast = { lastBlock with Body = contOps @ lastBlock.Body }
                        env.Blocks.Value <- (List.take (innerBlocks.Length - 1) innerBlocks) @ [patchedLast]
                        bodyOps  // entry ops only (end with terminator); side blocks already patched
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ coerceOps @ [CfBrOp(mergeLabel, [coercedVal])]
                let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel
                        Args  = []
                        Body  = terminatedOps } ]
                bindOps @ [ CfBrOp(bodyLabel, []) ]
            | MatchCompiler.Switch (scrutAcc, tag, argAccs, ifMatch, ifNoMatch) ->
                // Resolve the scrutinee accessor with the correct type for this tag
                let expectedType = scrutineeTypeForTag tag
                let (sVal, resolveOps) = resolveAccessorTyped scrutAcc expectedType
                // Emit the test
                let (cond, testOps) = emitCtorTest sVal tag
                // Snapshot accessor cache before preloading branch-specific fields.
                // preloadOps are only valid in the matchLabel block (ifMatch path).
                // We must restore the cache before emitting ifNoMatch so it doesn't
                // incorrectly reuse values defined only in the ifMatch branch, which
                // would violate MLIR dominance constraints.
                let cacheSnapshot = System.Collections.Generic.Dictionary<_, _>(accessorCache)
                // Pre-load sub-fields with correct types into the cache (for ifMatch branch)
                let preloadOps =
                    match tag with
                    | MatchCompiler.ConsCtor -> ensureConsFieldTypes scrutAcc argAccs
                    | MatchCompiler.AdtCtor(_, _, arity) when arity > 0 -> ensureAdtFieldTypes scrutAcc argAccs
                    | MatchCompiler.RecordCtor fields -> ensureRecordFieldTypes fields scrutAcc argAccs
                    | _ -> []
                // Emit ifMatch and ifNoMatch as separate blocks
                let matchLabel   = freshLabel env "match_yes"
                let noMatchLabel = freshLabel env "match_no"
                let matchOps   = emitDecisionTree ifMatch
                // Restore accessor cache to pre-preload snapshot before emitting ifNoMatch.
                // This prevents the ifNoMatch branch from seeing field values that were only
                // loaded in the ifMatch block (which doesn't dominate the ifNoMatch block).
                accessorCache.Clear()
                for kvp in cacheSnapshot do accessorCache.[kvp.Key] <- kvp.Value
                let noMatchOps = emitDecisionTree ifNoMatch
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some matchLabel; Args = []; Body = preloadOps @ matchOps }
                      { Label = Some noMatchLabel; Args = []; Body = noMatchOps } ]
                resolveOps @ testOps @ [ CfCondBrOp(cond, matchLabel, [], noMatchLabel, []) ]
            | MatchCompiler.Guard (bindings, bodyIdx, ifFalse) ->
                // 1. Resolve variable bindings (same as Leaf)
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                // 2. Evaluate the when-guard under bindEnv → I1 value
                let (_pat, guardOpt, bodyExpr) = clauses.[bodyIdx]
                let guard = guardOpt.Value  // HasGuard=true guarantees Some
                let (guardVal, guardOps) = elaborateExpr bindEnv guard
                // 3. Emit the ifFalse subtree as a separate block
                let guardFailLabel = freshLabel env (sprintf "guard_fail%d" bodyIdx)
                let guardFailOps = emitDecisionTree ifFalse
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some guardFailLabel; Args = []; Body = guardFailOps } ]
                // 4. Emit the body block (same as Leaf, skip merge branch if body already terminated)
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                // Coerce arm value to I64 for uniform merge block type (same as function body ABI)
                let (coercedVal, coerceOps) = coerceToI64 bindEnv bodyVal
                resultType.Value <- I64
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ coerceOps @ [CfBrOp(mergeLabel, [coercedVal])]
                let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel; Args = [];
                        Body = terminatedOps } ]
                // 5. Return: bind ops + guard eval + conditional branch
                bindOps @ guardOps @ [CfCondBrOp(guardVal, bodyLabel, [], guardFailLabel, [])]

        let chainEntryOps = emitDecisionTree tree

        // Failure block
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some failLabel
                Args   = []
                Body   = [ LlvmCallVoidOp("@lang_match_failure", [])
                           LlvmUnreachableOp ] } ]

        // Merge block
        let mergeArg = { Name = freshName env; Type = resultType.Value }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]

        (mergeArg, scrutOps @ chainEntryOps)

    // Phase 12: bare Lambda as a value — create inline closure struct
    // Used by ComposeRight/ComposeLeft which desugar to Lambda(...) as expressions
    | Lambda (param, body, _) ->
        // Free variables relative to param, restricted to those in env.Vars (not KnownFuncs — they are direct calls)
        let allFree = freeVars (Set.singleton param) body
        let captures =
            allFree
            |> Set.filter (fun name -> Map.containsKey name env.Vars)
            |> Set.toList
            |> List.sort
        let numCaptures = captures.Length

        // Generate closure function name
        let closureFnIdx = env.ClosureCounter.Value
        env.ClosureCounter.Value <- closureFnIdx + 1
        let closureFnName = sprintf "@closure_fn_%d" closureFnIdx

        // Build inner llvm.func: (%arg0: !llvm.ptr, %arg1: !llvm.ptr) -> i64
        // If param needs I64 type, coerce ptr arg1 to i64 via ptrtoint.
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = Ptr }
        let lambdaParamNeedsI64 = not (isPtrParamBody param body)
        let (lambdaParamVal, lambdaParamCoerceOps, lambdaCaptureStart) =
            if lambdaParamNeedsI64 then
                let i64Val = { Name = "%t0"; Type = I64 }
                (i64Val, [LlvmPtrToIntOp(i64Val, arg1Val)], 1)
            else
                (arg1Val, [], 0)
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(param, lambdaParamVal)]
              Counter = ref lambdaCaptureStart; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter
              TypeEnv = env.TypeEnv; RecordEnv = env.RecordEnv; ExnTags = env.ExnTags
              MutableVars = env.MutableVars; ArrayVars = Set.empty; CollectionVars = Map.empty
              BoolVars = Set.empty }
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" (lambdaCaptureStart + i); Type = Ptr }
                let capType = if Set.contains capName env.MutableVars then Ptr else I64
                let capVal = { Name = sprintf "%%t%d" (lambdaCaptureStart + i + numCaptures); Type = capType }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                let gepOp  = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)
        innerEnvWithCaptures.Counter.Value <- lambdaCaptureStart + numCaptures * 2
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures body
        // Phase 20/35: Normalize body return to I64 (Ptr→ptrtoint, I1→zext) for uniform closure return type.
        let (finalRetVal, normalizeRetOps) = coerceToI64 innerEnvWithCaptures bodyVal
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value
        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = lambdaParamCoerceOps @ captureLoadOps @ bodyEntryOps @ normalizeRetOps @ [LlvmReturnOp [finalRetVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = lambdaParamCoerceOps @ captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ normalizeRetOps @ [LlvmReturnOp [finalRetVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched
        let innerFuncOp : FuncOp =
            { Name = closureFnName; InputTypes = [Ptr; Ptr]; ReturnType = Some I64
              Body = { Blocks = allBodyBlocks }; IsLlvmFunc = true }
        env.Funcs.Value <- env.Funcs.Value @ [innerFuncOp]

        // Inline-allocate closure struct and populate it
        let bytesVal  = { Name = freshName env; Type = I64 }
        let envPtrVal = { Name = freshName env; Type = Ptr }
        let fnPtrVal  = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(bytesVal, int64 ((numCaptures + 1) * 8))
            LlvmCallOp(envPtrVal, "@GC_malloc", [bytesVal])
            LlvmAddressOfOp(fnPtrVal, closureFnName)
            LlvmStoreOp(fnPtrVal, envPtrVal)
        ]
        let captureStoreOps =
            captures |> List.mapi (fun i capName ->
                let slotVal = { Name = freshName env; Type = Ptr }
                let capVal  = Map.find capName env.Vars
                [ LlvmGEPLinearOp(slotVal, envPtrVal, i + 1)
                  LlvmStoreOp(capVal, slotVal) ]
            ) |> List.concat
        (envPtrVal, allocOps @ captureStoreOps)

    // Phase 17: ADT constructor — nullary variant (e.g. Red in type Color = Red | Green | Blue)
    // Allocates a 16-byte block: slot 0 = i64 tag, slot 1 = null ptr.
    // Phase 20: If arity >= 1, the constructor is used as a first-class value (e.g. `apply Some 42`).
    // In that case, wrap as Lambda(param, Constructor(name, Some(Var(param)), _)) and re-elaborate.
    | Constructor(name, None, ctorSpan) ->
        let info = Map.find name env.TypeEnv
        if info.Arity >= 1 then
            // First-class unary+ constructor: produce a closure fun __ctor_N_Name x -> Name x
            let n = env.Counter.Value
            env.Counter.Value <- n + 1
            let paramName = sprintf "__ctor_%d_%s" n name
            let s = ctorSpan
            elaborateExpr env (Lambda(paramName, Constructor(name, Some(Var(paramName, s)), s), s))
        else
            // Nullary constructor: allocate 16-byte block with tag and null payload
            let sizeVal     = { Name = freshName env; Type = I64 }
            let blockPtr    = { Name = freshName env; Type = Ptr }
            let tagSlot     = { Name = freshName env; Type = Ptr }
            let tagVal      = { Name = freshName env; Type = I64 }
            let paySlot     = { Name = freshName env; Type = Ptr }
            let nullPayload = { Name = freshName env; Type = Ptr }
            let ops = [
                ArithConstantOp(sizeVal, 16L)
                LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
                LlvmGEPLinearOp(tagSlot, blockPtr, 0)
                ArithConstantOp(tagVal, int64 info.Tag)
                LlvmStoreOp(tagVal, tagSlot)
                LlvmGEPLinearOp(paySlot, blockPtr, 1)
                LlvmNullOp(nullPayload)
                LlvmStoreOp(nullPayload, paySlot)
            ]
            (blockPtr, ops)

    // Phase 17: ADT constructor — unary/multi-arg variant (e.g. Some 42, Pair(3,4))
    // Allocates a 16-byte block: slot 0 = i64 tag, slot 1 = argVal (I64 or Ptr).
    // Multi-arg constructors: the parser produces Constructor("Pair", Some(Tuple([3;4])), _);
    // elaborating the Tuple arg already yields a heap-allocated Ptr — stored directly at slot 1.
    | Constructor(name, Some argExpr, _) ->
        let info     = Map.find name env.TypeEnv
        let (argVal, argOps) = elaborateExpr env argExpr
        let sizeVal  = { Name = freshName env; Type = I64 }
        let blockPtr = { Name = freshName env; Type = Ptr }
        let tagSlot  = { Name = freshName env; Type = Ptr }
        let tagVal   = { Name = freshName env; Type = I64 }
        let paySlot  = { Name = freshName env; Type = Ptr }
        let allocOps = [
            ArithConstantOp(sizeVal, 16L)
            LlvmCallOp(blockPtr, "@GC_malloc", [sizeVal])
            LlvmGEPLinearOp(tagSlot, blockPtr, 0)
            ArithConstantOp(tagVal, int64 info.Tag)
            LlvmStoreOp(tagVal, tagSlot)
            LlvmGEPLinearOp(paySlot, blockPtr, 1)
            LlvmStoreOp(argVal, paySlot)
        ]
        (blockPtr, argOps @ allocOps)

    // Phase 18: RecordExpr construction — allocate n-slot GC_malloc block, store fields in declaration order
    | RecordExpr(typeNameOpt, fields, recSpan) ->
        let fieldNames = fields |> List.map fst |> Set.ofList
        let typeName =
            match typeNameOpt with
            | Some n -> n
            | None ->
                env.RecordEnv
                |> Map.tryFindKey (fun _ fmap ->
                    Set.ofSeq (fmap |> Map.toSeq |> Seq.map fst) = fieldNames)
                |> Option.defaultWith (fun () ->
                    let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                    failWithSpan recSpan "RecordExpr: cannot resolve record type for fields %A. Available record types: %s" (Set.toList fieldNames) availableTypes)
        let fieldMap = Map.find typeName env.RecordEnv
        let n = Map.count fieldMap
        let fieldResults = fields |> List.map (fun (_, e) -> elaborateExpr env e)
        let allFieldOps  = fieldResults |> List.collect snd
        let fieldVals    = fieldResults |> List.map fst
        let bytesVal  = { Name = freshName env; Type = I64 }
        let recPtrVal = { Name = freshName env; Type = Ptr }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 (n * 8))
            LlvmCallOp(recPtrVal, "@GC_malloc", [bytesVal])
        ]
        let storeOps =
            fields |> List.collect (fun (fieldName, _) ->
                let slotIdx = Map.find fieldName fieldMap
                let fieldVal = List.item (fields |> List.findIndex (fun (fn, _) -> fn = fieldName)) fieldVals
                let slotPtr = { Name = freshName env; Type = Ptr }
                [ LlvmGEPLinearOp(slotPtr, recPtrVal, slotIdx)
                  LlvmStoreOp(fieldVal, slotPtr) ]
            )
        (recPtrVal, allFieldOps @ allocOps @ storeOps)

    // Phase 59: Nested qualified value access — Outer.Inner.foo → Var("Outer_Inner_foo")
    // Handles chains of any depth. Must come BEFORE the single-level Constructor arm below.
    | FieldAccess(FieldAccess(_, _, _) as innerExpr, memberName, span)
        when (tryDecodeModulePath innerExpr).IsSome ->
        let segments = (tryDecodeModulePath innerExpr).Value @ [memberName]
        if Map.containsKey memberName env.TypeEnv then
            elaborateExpr env (Constructor(memberName, None, span))
        else
            elaborateExpr env (Var(segments |> String.concat "_", span))

    // Phase 25: Qualified name desugar — M.x → Var(M_x), M.Ctor → Constructor(Ctor)
    // Phase 35: Use module-qualified name (modName + "_" + memberName) to avoid collisions
    // when multiple modules define functions with the same name (e.g., Option.map vs Result.map).
    // Must come BEFORE the record FieldAccess arm.
    | FieldAccess(Constructor(modName, None, _), memberName, span) ->
        if Map.containsKey memberName env.TypeEnv then
            elaborateExpr env (Constructor(memberName, None, span))
        else
            elaborateExpr env (Var(modName + "_" + memberName, span))

    // Phase 18: FieldAccess — GEP into record block at declaration-order slot, load value
    | FieldAccess(recExpr, fieldName, faSpan) ->
        let (recVal, recOps) = elaborateExpr env recExpr
        let slotIdx =
            env.RecordEnv
            |> Map.toSeq
            |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
            |> Option.defaultWith (fun () ->
                let hint =
                    env.RecordEnv |> Map.toList
                    |> List.map (fun (name, fields) -> sprintf "%s: {%s}" name (fields |> Map.toList |> List.map fst |> String.concat "; "))
                    |> String.concat " | "
                failWithSpan faSpan "FieldAccess: unknown field '%s'. Known records: %s" fieldName hint)
        // Coerce record to Ptr if it came in as I64 (e.g., through a closure argument)
        let (recPtr, recCoerce) =
            if recVal.Type = Ptr then (recVal, [])
            else let v = { Name = freshName env; Type = Ptr } in (v, [LlvmIntToPtrOp(v, recVal)])
        let slotPtr  = { Name = freshName env; Type = Ptr }
        let fieldVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(slotPtr, recPtr, slotIdx)
            LlvmLoadOp(fieldVal, slotPtr)
        ]
        (fieldVal, recOps @ recCoerce @ ops)

    // Phase 18: RecordUpdate — allocate new block, copy non-overridden fields, write overridden fields
    | RecordUpdate(sourceExpr, overrides, ruSpan) ->
        let (srcValRaw, srcOpsRaw) = elaborateExpr env sourceExpr
        // Issue #3: If source record is I64 (e.g., extracted from list match), coerce to Ptr
        let (srcVal, srcOps) =
            if srcValRaw.Type = I64 then
                let ptrVal = { Name = freshName env; Type = Ptr }
                (ptrVal, srcOpsRaw @ [LlvmIntToPtrOp(ptrVal, srcValRaw)])
            else (srcValRaw, srcOpsRaw)
        let overrideNames = overrides |> List.map fst |> Set.ofList
        let (typeName, fieldMap) =
            env.RecordEnv
            |> Map.tryFindKey (fun _ fmap ->
                overrideNames |> Set.forall (fun fn -> Map.containsKey fn fmap))
            |> Option.map (fun tn -> (tn, Map.find tn env.RecordEnv))
            |> Option.defaultWith (fun () ->
                let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                failWithSpan ruSpan "RecordUpdate: cannot resolve record type for fields %A. Available record types: %s" (Set.toList overrideNames) availableTypes)
        let n = Map.count fieldMap
        let overrideResults = overrides |> List.map (fun (fn, e) -> (fn, elaborateExpr env e))
        let overrideOps     = overrideResults |> List.collect (fun (_, (_, ops)) -> ops)
        let overrideVals    = overrideResults |> List.map (fun (fn, (v, _)) -> (fn, v)) |> Map.ofList
        let bytesVal  = { Name = freshName env; Type = I64 }
        let newPtrVal = { Name = freshName env; Type = Ptr }
        let allocOps  = [
            ArithConstantOp(bytesVal, int64 (n * 8))
            LlvmCallOp(newPtrVal, "@GC_malloc", [bytesVal])
        ]
        let copyOps =
            fieldMap |> Map.toList |> List.collect (fun (fieldName, slotIdx) ->
                let dstSlotPtr = { Name = freshName env; Type = Ptr }
                match Map.tryFind fieldName overrideVals with
                | Some newVal ->
                    [ LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx)
                      LlvmStoreOp(newVal, dstSlotPtr) ]
                | None ->
                    let srcSlotPtr = { Name = freshName env; Type = Ptr }
                    let srcFieldVal = { Name = freshName env; Type = I64 }
                    [ LlvmGEPLinearOp(srcSlotPtr, srcVal, slotIdx)
                      LlvmLoadOp(srcFieldVal, srcSlotPtr)
                      LlvmGEPLinearOp(dstSlotPtr, newPtrVal, slotIdx)
                      LlvmStoreOp(srcFieldVal, dstSlotPtr) ]
            )
        (newPtrVal, srcOps @ overrideOps @ allocOps @ copyOps)

    // Phase 18: SetField — store in-place at field slot, return unit (i64=0)
    | SetField(recExpr, fieldName, valueExpr, sfSpan) ->
        let (recVal, recOps)    = elaborateExpr env recExpr
        let (newVal, newValOps) = elaborateExpr env valueExpr
        let slotIdx =
            env.RecordEnv
            |> Map.toSeq
            |> Seq.tryPick (fun (_, fmap) -> Map.tryFind fieldName fmap)
            |> Option.defaultWith (fun () ->
                let hint =
                    env.RecordEnv |> Map.toList
                    |> List.map (fun (name, fields) -> sprintf "%s: {%s}" name (fields |> Map.toList |> List.map fst |> String.concat "; "))
                    |> String.concat " | "
                failWithSpan sfSpan "SetField: unknown field '%s'. Known records: %s" fieldName hint)
        // Coerce record to Ptr if it came in as I64 (e.g., through a closure argument)
        let (recPtr, recCoerce) =
            if recVal.Type = Ptr then (recVal, [])
            else let v = { Name = freshName env; Type = Ptr } in (v, [LlvmIntToPtrOp(v, recVal)])
        let slotPtr = { Name = freshName env; Type = Ptr }
        let unitVal = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPLinearOp(slotPtr, recPtr, slotIdx)
            LlvmStoreOp(newVal, slotPtr)
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, recOps @ recCoerce @ newValOps @ ops)

    // Phase 19: Raise — construct exception value, call @lang_throw, terminate block
    | Raise(exnExpr, _) ->
        let (exnVal, exnOps) = elaborateExpr env exnExpr
        // exnVal is a Ptr to an ADT DataValue block (e.g., {tag=0, payload=LangString*} for Failure "msg")
        // Call @lang_throw(exnVal) — noreturn void call
        // Emit llvm.unreachable after the call to terminate the block
        // deadVal must be defined by ArithConstantOp to satisfy MLIR SSA validity,
        // even though it is never used after llvm.unreachable
        let deadVal = { Name = freshName env; Type = I64 }
        (deadVal, exnOps @ [ ArithConstantOp(deadVal, 0L); LlvmCallVoidOp("@lang_throw", [exnVal]); LlvmUnreachableOp ])

    // Phase 19: TryWith — setjmp/longjmp exception handling
    // Control flow:
    //   [entry] alloc frame, call lang_try_enter (setjmp), branch on result
    //   ^try_body — elaborate body, pop handler, branch to merge
    //   ^exn_caught — pop handler (C-16), get exception, dispatch via MatchCompiler
    //   ^exn_fail — re-raise unmatched exception via @lang_throw
    //   ^merge(%result) — join point
    | TryWith(bodyExpr, clauses, trySpan) ->
        let mergeLabel     = freshLabel env "try_merge"
        let tryBodyLabel   = freshLabel env "try_body"
        let exnCaughtLabel = freshLabel env "exn_caught"
        let exnFailLabel   = freshLabel env "exn_fail"

        // --- Entry ops: allocate frame, push handler, call _setjmp directly, branch ---
        // IMPORTANT: _setjmp must be called in the SAME function body (not a wrapper),
        // so the saved jmp_buf refers to THIS function's stack frame.
        // lang_try_push just links the frame into the handler stack.
        // _setjmp returns i32 (int); we zero-extend to i64 for comparison.
        let frameSizeVal = { Name = freshName env; Type = I64 }
        let framePtr     = { Name = freshName env; Type = Ptr }
        let sjResult32   = { Name = freshName env; Type = I32 }
        let sjResult64   = { Name = freshName env; Type = I64 }
        let zero64       = { Name = freshName env; Type = I64 }
        let isNormal     = { Name = freshName env; Type = I1 }
        let entryOps = [
            ArithConstantOp(frameSizeVal, 272L)
            LlvmCallOp(framePtr, "@GC_malloc", [frameSizeVal])
            LlvmCallVoidOp("@lang_try_push", [framePtr])
            LlvmCallOp(sjResult32, "@_setjmp", [framePtr])
            ArithExtuIOp(sjResult64, sjResult32)
            ArithConstantOp(zero64, 0L)
            ArithCmpIOp(isNormal, "eq", sjResult64, zero64)
            CfCondBrOp(isNormal, tryBodyLabel, [], exnCaughtLabel, [])
        ]

        // --- ^try_body block: elaborate body, pop handler, branch to merge ---
        let bodyBlocksBeforeCount = env.Blocks.Value.Length
        let (bodyVal, bodyOps) = elaborateExpr env bodyExpr
        let resultType = ref bodyVal.Type
        // Determine how the body terminates:
        //   - LlvmUnreachableOp: body raises (noreturn), try_body needs no exit or merge branch
        //   - CfBrOp / CfCondBrOp: body itself has multi-block control flow (nested Match/TryWith)
        //     In this case the lang_try_exit + CfBrOp(mergeLabel) must go into the body's
        //     LAST side block (its own merge block), not after bodyOps (which terminates the block).
        //   - Otherwise: append exit + branch normally
        let tryBodyBlock =
            match List.tryLast bodyOps with
            | Some LlvmUnreachableOp ->
                // Body raises unconditionally — no exit or merge branch needed
                { Label = Some tryBodyLabel; Args = []; Body = bodyOps }
            | Some (CfBrOp _) | Some (CfCondBrOp _) ->
                // Body itself terminates with a branch (e.g. nested Match/TryWith).
                // The branch to outer merge must be appended to the last side block
                // that was added by the inner expression (its merge block, currently empty body).
                // NOTE: do NOT append lang_try_exit here — the inner expression's normal-path
                // block already contains lang_try_exit before branching to its own merge.
                let innerBlocks = env.Blocks.Value
                if innerBlocks.Length > bodyBlocksBeforeCount then
                    let lastBlock = List.last innerBlocks
                    // Check if the last block (inner merge) has any predecessors.
                    // If all inner handler arms raised (noreturn), the inner merge is dead.
                    // In that case, emit llvm.unreachable instead of cf.br to avoid
                    // MLIR type errors from undefined block arguments in dead code.
                    let innerMergeLabel = lastBlock.Label
                    let hasPredecessors =
                        let allBlocks = innerBlocks |> List.take (innerBlocks.Length - 1)
                        allBlocks |> List.exists (fun b ->
                            b.Body |> List.exists (fun op ->
                                match op with
                                | CfBrOp(lbl, _) -> Some lbl = innerMergeLabel
                                | CfCondBrOp(_, tLbl, _, fLbl, _) ->
                                    Some tLbl = innerMergeLabel || Some fLbl = innerMergeLabel
                                | _ -> false))
                    let patchedOps =
                        if hasPredecessors then
                            lastBlock.Body @ [CfBrOp(mergeLabel, [bodyVal])]
                        else
                            lastBlock.Body @ [LlvmUnreachableOp]
                    let patchedLast = { lastBlock with Body = patchedOps }
                    env.Blocks.Value <- (List.take (innerBlocks.Length - 1) innerBlocks) @ [patchedLast]
                // The try_body block just holds the inner entry ops (which branch to inner blocks)
                { Label = Some tryBodyLabel; Args = []; Body = bodyOps }
            | _ ->
                // Normal case: body is a simple expression, append exit + merge branch
                { Label = Some tryBodyLabel; Args = [];
                  Body = bodyOps @ [
                    LlvmCallVoidOp("@lang_try_exit", [])
                    CfBrOp(mergeLabel, [bodyVal])
                  ] }
        env.Blocks.Value <- env.Blocks.Value @ [tryBodyBlock]

        // --- ^exn_caught block: pop handler (C-16), get exception, dispatch ---
        // Expand OrPat in handler clauses
        let clauses =
            clauses |> List.collect (fun (pat, guard, body) ->
                match pat with
                | OrPat(alts, _) -> alts |> List.map (fun altPat -> (altPat, guard, body))
                | _ -> [(pat, guard, body)]
            )

        let exnPtrVal = { Name = freshName env; Type = Ptr }
        let exnPreambleOps = [
            LlvmCallVoidOp("@lang_try_exit", [])   // C-16: pop handler before dispatch
            LlvmCallOp(exnPtrVal, "@lang_current_exception", [])
        ]

        // Build arms for MatchCompiler: (Pattern * hasGuard * bodyIndex) list
        let arms = clauses |> List.mapi (fun i (pat, guardOpt, _body) -> (pat, guardOpt.IsSome, i))
        let rootAcc = MatchCompiler.Root exnPtrVal.Name
        let tree = MatchCompiler.compile rootAcc arms

        // Accessor cache — initialize root to exnPtrVal
        let accessorCache2 = System.Collections.Generic.Dictionary<MatchCompiler.Accessor, MlirValue>()
        accessorCache2.[rootAcc] <- exnPtrVal

        // Resolve an accessor to an MlirValue (duplicated from Match case; root = exnPtrVal)
        let rec resolveAccessor2 (acc: MatchCompiler.Accessor) : MlirValue * MlirOp list =
            match accessorCache2.TryGetValue(acc) with
            | true, v -> (v, [])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessor2: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    let (rawParentVal, parentOps) = resolveAccessor2 parent
                    let (parentVal, retypeOps) =
                        if rawParentVal.Type = Ptr then (rawParentVal, [])
                        else resolveAccessorTyped2 parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache2.[acc] <- fieldVal
                    (fieldVal, parentOps @ retypeOps @ [gepOp; loadOp])

        and resolveAccessorTyped2 (acc: MatchCompiler.Accessor) (ty: MlirType) : MlirValue * MlirOp list =
            match accessorCache2.TryGetValue(acc) with
            | true, v when v.Type = ty -> (v, [])
            | true, v ->
                match acc with
                | MatchCompiler.Root _ ->
                    if v.Type = I64 && ty = Ptr then
                        let ptrVal = { Name = freshName env; Type = Ptr }
                        accessorCache2.[acc] <- ptrVal
                        (ptrVal, [LlvmIntToPtrOp(ptrVal, v)])
                    else (v, [])
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped2 parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache2.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])
            | false, _ ->
                match acc with
                | MatchCompiler.Root _ -> failwith "resolveAccessorTyped2: Root not in cache"
                | MatchCompiler.Field (parent, idx) ->
                    // GEP requires a Ptr base — ensure parent is resolved as Ptr.
                    let (parentVal, parentOps) = resolveAccessorTyped2 parent Ptr
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = ty }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache2.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])

        let scrutineeTypeForTag2 (tag: MatchCompiler.CtorTag) : MlirType =
            match tag with
            | MatchCompiler.IntLit _    -> I64
            | MatchCompiler.BoolLit _   -> I1
            | MatchCompiler.StringLit _ -> Ptr
            | MatchCompiler.ConsCtor    -> Ptr
            | MatchCompiler.NilCtor     -> Ptr
            | MatchCompiler.TupleCtor _ -> Ptr
            | MatchCompiler.AdtCtor _   -> Ptr
            | MatchCompiler.RecordCtor _ -> Ptr

        let emitCtorTest2 (scrutVal: MlirValue) (tag: MatchCompiler.CtorTag) : MlirValue * MlirOp list =
            match tag with
            | MatchCompiler.IntLit n ->
                let kVal = { Name = freshName env; Type = I64 }
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(kVal, int64 n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.BoolLit b ->
                let kVal = { Name = freshName env; Type = I1 }
                let cond = { Name = freshName env; Type = I1 }
                let n = if b then 1L else 0L
                let ops = [ ArithConstantOp(kVal, n); ArithCmpIOp(cond, "eq", scrutVal, kVal) ]
                (cond, ops)
            | MatchCompiler.StringLit s ->
                let (patStrVal, patStrOps) = elaborateStringLiteral env s
                let patDataPtrVal   = { Name = freshName env; Type = Ptr }
                let patDataVal      = { Name = freshName env; Type = Ptr }
                let scrutDataPtrVal = { Name = freshName env; Type = Ptr }
                let scrutDataVal    = { Name = freshName env; Type = Ptr }
                let cmpRes  = { Name = freshName env; Type = I32 }
                let zero32  = { Name = freshName env; Type = I32 }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = patStrOps @ [
                    LlvmGEPStructOp(patDataPtrVal, patStrVal, 1)
                    LlvmLoadOp(patDataVal, patDataPtrVal)
                    LlvmGEPStructOp(scrutDataPtrVal, scrutVal, 1)
                    LlvmLoadOp(scrutDataVal, scrutDataPtrVal)
                    LlvmCallOp(cmpRes, "@strcmp", [scrutDataVal; patDataVal])
                    ArithConstantOp(zero32, 0L)
                    ArithCmpIOp(cond, "eq", cmpRes, zero32)
                ]
                (cond, ops)
            | MatchCompiler.NilCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "eq", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.ConsCtor ->
                let nullVal = { Name = freshName env; Type = Ptr }
                let cond    = { Name = freshName env; Type = I1 }
                let ops = [ LlvmNullOp(nullVal); LlvmIcmpOp(cond, "ne", scrutVal, nullVal) ]
                (cond, ops)
            | MatchCompiler.TupleCtor _ ->
                let cond = { Name = freshName env; Type = I1 }
                let ops = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)
            | MatchCompiler.AdtCtor(name, _, _) ->
                let info     = Map.find name env.TypeEnv
                let tagSlot  = { Name = freshName env; Type = Ptr }
                let tagLoad  = { Name = freshName env; Type = I64 }
                let tagConst = { Name = freshName env; Type = I64 }
                let cond     = { Name = freshName env; Type = I1 }
                let ops = [
                    LlvmGEPLinearOp(tagSlot, scrutVal, 0)
                    LlvmLoadOp(tagLoad, tagSlot)
                    ArithConstantOp(tagConst, int64 info.Tag)
                    ArithCmpIOp(cond, "eq", tagLoad, tagConst)
                ]
                (cond, ops)
            | MatchCompiler.RecordCtor _ ->
                let cond = { Name = freshName env; Type = I1 }
                let ops  = [ ArithConstantOp(cond, 1L) ]
                (cond, ops)

        let ensureConsFieldTypes2 (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                let (_, headOps) = resolveAccessorTyped2 argAccs.[0] I64
                ops <- ops @ headOps
            if argAccs.Length >= 2 then
                let (_, tailOps) = resolveAccessorTyped2 argAccs.[1] Ptr
                ops <- ops @ tailOps
            ops

        // Exception payloads are always heap objects (Ptr): strings, tuples, ADTs.
        // Load as Ptr so handler bindings are correctly typed for GEP/load usage.
        let ensureAdtFieldTypes2 (_scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let mutable ops = []
            if argAccs.Length >= 1 then
                let (_, payOps) = resolveAccessorTyped2 argAccs.[0] Ptr
                ops <- ops @ payOps
            ops

        let ensureRecordFieldTypes2 (fields: string list) (scrutAcc: MatchCompiler.Accessor) (argAccs: MatchCompiler.Accessor list) : MlirOp list =
            let fieldSet = Set.ofList fields
            let fieldMap =
                env.RecordEnv
                |> Map.toSeq
                |> Seq.tryPick (fun (_, fm) ->
                    let fmFields = fm |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                    if fieldSet |> Set.forall (fun f -> Set.contains f fmFields) then Some fm
                    else None)
                |> Option.defaultWith (fun () ->
                    let availableTypes = env.RecordEnv |> Map.toList |> List.map fst |> String.concat ", "
                    failWithSpan trySpan "ensureRecordFieldTypes2: cannot resolve record type for fields %A. Available record types: %s" fields availableTypes)
            let mutable ops = []
            fields |> List.iteri (fun i fieldName ->
                if i < argAccs.Length then
                    let declSlotIdx = Map.find fieldName fieldMap
                    let (parentVal, parentOps) = resolveAccessorTyped2 scrutAcc Ptr
                    let slotPtr  = { Name = freshName env; Type = Ptr }
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let gepOp  = LlvmGEPLinearOp(slotPtr, parentVal, declSlotIdx)
                    let loadOp = LlvmLoadOp(fieldVal, slotPtr)
                    accessorCache2.[argAccs.[i]] <- fieldVal
                    ops <- ops @ parentOps @ [gepOp; loadOp]
            )
            ops

        // Recursively emit blocks for the TryWith decision tree.
        // Fail case: branch to exnFailLabel (re-raise via @lang_throw).
        let rec emitDecisionTree2 (tree: MatchCompiler.DecisionTree) : MlirOp list =
            match tree with
            | MatchCompiler.Fail ->
                [ CfBrOp(exnFailLabel, []) ]
            | MatchCompiler.Leaf (bindings, bodyIdx) ->
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor2 acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                let (_pat, _guard, bodyExpr) = clauses.[bodyIdx]
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                // Skip merge branch if body already terminated (e.g. raise inside handler arm)
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
                let bodyLabel = freshLabel env (sprintf "try_arm%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel
                        Args  = []
                        Body  = terminatedOps } ]
                bindOps @ [ CfBrOp(bodyLabel, []) ]
            | MatchCompiler.Switch (scrutAcc, tag, argAccs, ifMatch, ifNoMatch) ->
                let expectedType = scrutineeTypeForTag2 tag
                let (sVal, resolveOps) = resolveAccessorTyped2 scrutAcc expectedType
                let (cond, testOps) = emitCtorTest2 sVal tag
                // Snapshot cache before preloading ifMatch-specific fields.
                let cacheSnapshot2 = System.Collections.Generic.Dictionary<_, _>(accessorCache2)
                let preloadOps =
                    match tag with
                    | MatchCompiler.ConsCtor -> ensureConsFieldTypes2 scrutAcc argAccs
                    | MatchCompiler.AdtCtor(_, _, arity) when arity > 0 -> ensureAdtFieldTypes2 scrutAcc argAccs
                    | MatchCompiler.RecordCtor fields -> ensureRecordFieldTypes2 fields scrutAcc argAccs
                    | _ -> []
                let matchLabel   = freshLabel env "try_yes"
                let noMatchLabel = freshLabel env "try_no"
                let matchOps   = emitDecisionTree2 ifMatch
                // Restore cache before emitting ifNoMatch branch.
                accessorCache2.Clear()
                for kvp in cacheSnapshot2 do accessorCache2.[kvp.Key] <- kvp.Value
                let noMatchOps = emitDecisionTree2 ifNoMatch
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some matchLabel; Args = []; Body = preloadOps @ matchOps }
                      { Label = Some noMatchLabel; Args = []; Body = noMatchOps } ]
                resolveOps @ testOps @ [ CfCondBrOp(cond, matchLabel, [], noMatchLabel, []) ]
            | MatchCompiler.Guard (bindings, bodyIdx, ifFalse) ->
                let mutable bindOps = []
                let mutable bindEnv = env
                for (varName, acc) in bindings do
                    let (v, ops) = resolveAccessor2 acc
                    bindOps <- bindOps @ ops
                    bindEnv <- { bindEnv with Vars = Map.add varName v bindEnv.Vars }
                let (_pat, guardOpt, bodyExpr) = clauses.[bodyIdx]
                let guard = guardOpt.Value
                let (guardVal, guardOps) = elaborateExpr bindEnv guard
                let guardFailLabel = freshLabel env (sprintf "try_guard_fail%d" bodyIdx)
                let guardFailOps = emitDecisionTree2 ifFalse
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some guardFailLabel; Args = []; Body = guardFailOps } ]
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                // Skip merge branch if body already terminated (e.g. raise inside handler guard)
                let terminatedOps =
                    match List.tryLast bodyOps with
                    | Some LlvmUnreachableOp -> bodyOps
                    | Some (CfBrOp _) | Some (CfCondBrOp _) -> bodyOps
                    | _ -> bodyOps @ [CfBrOp(mergeLabel, [bodyVal])]
                let bodyLabel = freshLabel env (sprintf "try_arm%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel; Args = [];
                        Body = terminatedOps } ]
                bindOps @ guardOps @ [CfCondBrOp(guardVal, bodyLabel, [], guardFailLabel, [])]

        let chainEntryOps = emitDecisionTree2 tree

        // ^exn_caught block
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some exnCaughtLabel
                Args  = []
                Body  = exnPreambleOps @ chainEntryOps } ]

        // ^exn_fail block — re-raise to outer handler (or abort if none)
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some exnFailLabel
                Args  = []
                Body  = [ LlvmCallVoidOp("@lang_throw", [exnPtrVal]); LlvmUnreachableOp ] } ]

        // ^merge block — MUST be last so appendReturnIfNeeded targets it
        let mergeArg = { Name = freshName env; Type = resultType.Value }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]

        (mergeArg, entryOps)

    // Phase 29: WhileExpr — header-block CFG pattern
    // Control flow:
    //   [entry] define unitConst, cf.br ^while_header
    //   ^while_header — elaborate cond, cf.cond_br to ^while_body or ^while_exit
    //   ^while_body — elaborate body (discard value), re-elaborate cond, cf.cond_br back or exit
    //   ^while_exit(%exitArg : i64) — empty body, patched by LetPat/Let continuation
    | WhileExpr (condExpr, bodyExpr, _) ->
        eprintfn "DEBUG WhileExpr: entry, blocks=%d" env.Blocks.Value.Length
        let headerLabel = freshLabel env "while_header"
        let bodyLabel   = freshLabel env "while_body"
        let exitLabel   = freshLabel env "while_exit"
        // Unit constant — defined in entry fragment so it dominates all loop blocks
        let unitConst = { Name = freshName env; Type = I64 }
        // Exit block argument carries the unit result out
        let exitArg = { Name = freshName env; Type = I64 }
        // Track blocks before elaborating the header condition (to detect short-circuit side blocks)
        let blocksBeforeCond = env.Blocks.Value.Length
        // Elaborate condition for the header block
        let (condVal, condOps)    = elaborateExpr env condExpr
        let condSideBlocks = env.Blocks.Value.Length - blocksBeforeCond
        // Phase 36 FIX-03: Coerce condVal to I1 if it is I64 (e.g. module Bool function).
        let (i1CondVal, coerceCondOps) =
            if condVal.Type = I64 then
                let zeroVal = { Name = freshName env; Type = I64 }
                let boolVal = { Name = freshName env; Type = I1  }
                (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", condVal, zeroVal)])
            else (condVal, [])
        let condBrOp = CfCondBrOp(i1CondVal, bodyLabel, [], exitLabel, [unitConst])
        // Track how many side blocks exist before elaborating body (for detecting inner blocks)
        let blocksBeforeBody = env.Blocks.Value.Length
        // Elaborate body for the body block
        let (_bodyVal, bodyOps)   = elaborateExpr env bodyExpr
        // Track blocks before elaborating back-edge condition
        let blocksBeforeBackCond = env.Blocks.Value.Length
        // Re-elaborate condition for mutable-safe back-edge evaluation
        let (condVal2, condOps2)  = elaborateExpr env condExpr
        let backCondSideBlocks = env.Blocks.Value.Length - blocksBeforeBackCond
        // Phase 36 FIX-03: Coerce condVal2 to I1 if it is I64 (same pattern as header cond).
        let (i1CondVal2, coerceCondOps2) =
            if condVal2.Type = I64 then
                let zeroVal = { Name = freshName env; Type = I64 }
                let boolVal = { Name = freshName env; Type = I1  }
                (boolVal, [ArithConstantOp(zeroVal, 0L); ArithCmpIOp(boolVal, "ne", condVal2, zeroVal)])
            else (condVal2, [])
        let backEdgeBrOp = CfCondBrOp(i1CondVal2, bodyLabel, [], exitLabel, [unitConst])
        // Determine where to place the back-edge branch op.
        // If bodyOps ends with a block terminator (from a nested while/if/match inside the body),
        // the back-edge must be appended to the LAST side block (the inner expression's merge/exit
        // block, which was left empty for patching), NOT inline in bodyOps.
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        // Build the back-edge ops depending on whether back-cond created side blocks
        // If backCondSideBlocks > 0, condOps2 ends with a cf.br to its first side block,
        // and the back-edge branch must go into the last back-cond side block.
        let bodyBlockBody, needPatchLast, backEdgeForBody =
            if backCondSideBlocks > 0 then
                // Back-cond has side blocks; backEdgeBrOp goes into the last of those blocks.
                // The "back-edge ops for body" are just condOps2 (entry fragment going to first side block).
                // After body block, we push back-cond side blocks (already in env.Blocks.Value).
                match List.tryLast bodyOps with
                | Some op when isTerminator op && env.Blocks.Value.Length > blocksBeforeBody ->
                    // Body has side blocks too; back-cond entry fragment goes into inner last block.
                    (bodyOps, true, condOps2 @ coerceCondOps2)
                | _ ->
                    (bodyOps @ condOps2 @ coerceCondOps2, false, [])
            else
                // Simple back-cond: no side blocks.
                let backEdgeOps = condOps2 @ coerceCondOps2 @ [backEdgeBrOp]
                match List.tryLast bodyOps with
                | Some op when isTerminator op && env.Blocks.Value.Length > blocksBeforeBody ->
                    (bodyOps, true, backEdgeOps)
                | _ ->
                    (bodyOps @ backEdgeOps, false, [])
        // Build the while_header block.
        // If condOps created side blocks (short-circuit &&/||), the CfCondBrOp must go into
        // the last of those side blocks, not the header block body.
        let headerBody = condOps @ coerceCondOps
        // Push the three while blocks AFTER elaborating both cond and body
        // (so any inner side blocks from nested expressions come first)
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some headerLabel
                Args  = []
                Body  = if condSideBlocks > 0 then headerBody
                        else headerBody @ [condBrOp] }
              { Label = Some bodyLabel
                Args  = []
                Body  = bodyBlockBody }
              { Label = Some exitLabel
                Args  = [exitArg]
                Body  = [] } ]
        // Patch the condBrOp into the last header-cond side block (if any)
        if condSideBlocks > 0 then
            // The last header-cond side block is at position
            // (env.Blocks.Value.Length - 3 - condSideBlocks) = blocksBeforeCond + condSideBlocks - 1
            // We need its index after the 3 while blocks were appended.
            // Before appending: it was at env.Blocks.Value.Length - 3 - condSideBlocks
            // But condSideBlocks blocks were pushed between blocksBeforeCond and blocksBeforeBody.
            // The last header-cond side block index (before the 3 while blocks) = blocksBeforeBody - 1
            let allBlocks = env.Blocks.Value
            let condLastIdx = blocksBeforeBody - 1  // last block pushed by cond elaboration
            if condLastIdx >= 0 then
                let condLast = allBlocks.[condLastIdx]
                let patched = { condLast with Body = condLast.Body @ coerceCondOps @ [condBrOp] }
                env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = condLastIdx then patched else b)
        // Patch back-cond side blocks if needed
        if backCondSideBlocks > 0 then
            // The last back-cond side block is just before the 3 while blocks we appended.
            let allBlocks = env.Blocks.Value
            let backCondLastIdx = allBlocks.Length - 4  // 4th from end = just before header/body/exit
            if backCondLastIdx >= 0 then
                let backCondLast = allBlocks.[backCondLastIdx]
                let patched = { backCondLast with Body = backCondLast.Body @ coerceCondOps2 @ [backEdgeBrOp] }
                env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = backCondLastIdx then patched else b)
        // If the body had inner side blocks, patch the back-edge into what is now the last
        // side block among the while's blocks (which is while_body itself — no, we need the
        // last block pushed before while_body that is the inner merge block).
        // Actually: inner blocks were already in env.Blocks.Value before we appended header/body/exit.
        // We need to patch the last block that was present AFTER elaborating body but BEFORE
        // we appended the three while blocks — i.e., the inner merge/exit block.
        if needPatchLast then
            // The inner merge block is at position (env.Blocks.Value.Length - 4 - backCondSideBlocks)
            let allBlocks = env.Blocks.Value
            let innerLastIdx = allBlocks.Length - 4 - backCondSideBlocks
            if innerLastIdx >= 0 then
                let innerLast = allBlocks.[innerLastIdx]
                let patched = { innerLast with Body = innerLast.Body @ backEdgeForBody }
                env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = innerLastIdx then patched else b)
            // If backCondSideBlocks > 0, also patch back-cond branch into the body's inner-last block
            // (This case: body has inner blocks AND back-cond has side blocks)
            // The back-cond side blocks are at indices (allBlocks.Length - 4 - backCondSideBlocks) to (allBlocks.Length - 4 - 1)
            // backEdgeBrOp should be in the last of those (already patched above in the backCondSideBlocks > 0 branch)
        // Entry fragment: define unit constant (dominates all loop blocks), then branch to header
        eprintfn "DEBUG WhileExpr: exit, blocks=%d" env.Blocks.Value.Length
        (exitArg, [ ArithConstantOp(unitConst, 0L); CfBrOp(headerLabel, []) ])

    // Phase 29: ForExpr — block-argument CFG pattern for loop counter
    // Control flow:
    //   [entry] elaborate start/stop, define unitConst, cf.br ^for_header(%start)
    //   ^for_header(%i : i64) — compare %i sle/%sge %stop, cf.cond_br to ^for_body or ^for_exit(%unitConst)
    //   ^for_body — elaborate body (discard value), compute %next = %i +/- 1, cf.br ^for_header(%next)
    //   ^for_exit(%exitArg : i64) — empty body, patched by LetPat/Let continuation
    | ForExpr (var, startExpr, isTo, stopExpr, bodyExpr, _) ->
        let headerLabel = freshLabel env "for_header"
        let bodyLabel   = freshLabel env "for_body"
        let exitLabel   = freshLabel env "for_exit"
        // Elaborate start and stop in entry fragment
        let (startVal, startOps) = elaborateExpr env startExpr
        let (stopVal, stopOps)   = elaborateExpr env stopExpr
        // Block argument for loop counter (carried into ^for_header)
        let iArg = { Name = freshName env; Type = I64 }
        // Unit constant — defined in entry fragment so it dominates all loop blocks
        let unitConst = { Name = freshName env; Type = I64 }
        // Exit block argument carries the unit result out
        let exitArg = { Name = freshName env; Type = I64 }
        // Body env: loop variable bound immutably (NOT in MutableVars — LOOP-04)
        let bodyEnv = { env with Vars = Map.add var iArg env.Vars }
        // Track side blocks before elaborating body (for nested loop detection)
        let blocksBeforeBody = env.Blocks.Value.Length
        // Elaborate body (value discarded — for loop returns unit)
        let (_bodyVal, bodyOps) = elaborateExpr bodyEnv bodyExpr
        // Increment/decrement: addi for `to`, subi for `downto`
        let oneConst = { Name = freshName env; Type = I64 }
        let nextVal  = { Name = freshName env; Type = I64 }
        let incrOp =
            if isTo then ArithAddIOp(nextVal, iArg, oneConst)
            else          ArithSubIOp(nextVal, iArg, oneConst)
        // Back-edge ops: increment counter and branch back to header
        let backEdgeOps = [ ArithConstantOp(oneConst, 1L); incrOp; CfBrOp(headerLabel, [nextVal]) ]
        // Comparison predicate: sle for ascending, sge for descending
        let predicate = if isTo then "sle" else "sge"
        let cmpVal = { Name = freshName env; Type = I1 }
        // Detect nested loop: same pattern as WhileExpr
        let isTerminator op =
            match op with
            | CfBrOp _ | CfCondBrOp _ | LlvmUnreachableOp -> true
            | _ -> false
        let bodyBlockBody, needPatchLast =
            match List.tryLast bodyOps with
            | Some op when isTerminator op && env.Blocks.Value.Length > blocksBeforeBody ->
                // Body has inner side blocks — back-edge goes into the inner last block
                (bodyOps, true)
            | _ ->
                // Simple body — append back-edge inline
                (bodyOps @ backEdgeOps, false)
        // Push the three for blocks AFTER elaborating body
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some headerLabel
                Args  = [iArg]
                Body  = [ ArithCmpIOp(cmpVal, predicate, iArg, stopVal)
                          CfCondBrOp(cmpVal, bodyLabel, [], exitLabel, [unitConst]) ] }
              { Label = Some bodyLabel
                Args  = []
                Body  = bodyBlockBody }
              { Label = Some exitLabel
                Args  = [exitArg]
                Body  = [] } ]
        // Patch nested loop's inner merge block with back-edge ops if needed
        if needPatchLast then
            let allBlocks = env.Blocks.Value
            let innerLastIdx = allBlocks.Length - 4  // 4th from end = just before header/body/exit
            if innerLastIdx >= 0 then
                let innerLast = allBlocks.[innerLastIdx]
                let patched = { innerLast with Body = innerLast.Body @ backEdgeOps }
                env.Blocks.Value <- allBlocks |> List.mapi (fun i b -> if i = innerLastIdx then patched else b)
        // Entry fragment: elaborate start/stop, define unit constant, branch to header with start value
        (exitArg, startOps @ stopOps @ [ ArithConstantOp(unitConst, 0L); CfBrOp(headerLabel, [startVal]) ])

    // Phase 30: Type annotation pass-through — ignore type at codegen
    | Annot (expr, _, _) -> elaborateExpr env expr
    | LambdaAnnot (param, _, body, span) -> elaborateExpr env (Lambda(param, body, span))

    // Phase 30 + 34-03: ForInExpr — desugar to Lambda closure + lang_for_in_* runtime call.
    // Phase 34-03 extends: TuplePat support (for hashtable (k,v) destructuring) and
    // collection dispatch (HashSet/Queue/MutableList/Hashtable via detectCollectionKind).
    | ForInExpr (var, collExpr, bodyExpr, span) ->
        // Extract variable name from pattern; for TuplePat use a fresh param name.
        let varName = match var with Ast.VarPat(n, _) -> n | _ -> freshName env
        // For TuplePat: build lambda body that destructures the tuple param via LetPat.
        // The closure parameter (varName) arrives as I64 (pointer to heap tuple cast to int64_t).
        // LetPat(TuplePat, ...) now handles the I64->Ptr coercion internally (Phase 34-03 fix above).
        let lambdaBody =
            match var with
            | Ast.TuplePat _ ->
                LetPat(var, Var(varName, span), bodyExpr, span)
            | _ -> bodyExpr
        let closureLambda = Lambda(varName, lambdaBody, span)
        let (closureVal, closureOps) = elaborateExpr env closureLambda
        // Elaborate collection
        let (collVal, collOps) = elaborateExpr env collExpr
        // Coerce closure to Ptr if needed (same pattern as array_iter)
        let closurePtrVal =
            if closureVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else closureVal
        let closureCoerceOps =
            if closureVal.Type = I64
            then [LlvmIntToPtrOp(closurePtrVal, closureVal)]
            else []
        // Coerce collection to Ptr if needed (list pointer may arrive as I64)
        let collPtrVal =
            if collVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else collVal
        let collCoerceOps =
            if collVal.Type = I64
            then [LlvmIntToPtrOp(collPtrVal, collVal)]
            else []
        // Select runtime function based on collection type (Phase 34-03: collection dispatch)
        let forInFn =
            match detectCollectionKind env.CollectionVars collExpr with
            | Some HashSet     -> "@lang_for_in_hashset"
            | Some Queue       -> "@lang_for_in_queue"
            | Some MutableList -> "@lang_for_in_mlist"
            | Some Hashtable   -> "@lang_for_in_hashtable"
            | None ->
                if isArrayExpr env.ArrayVars collExpr
                then "@lang_for_in_array"
                else "@lang_for_in_list"
        // Call lang_for_in_*(closure, collection), return unit
        let unitVal = { Name = freshName env; Type = I64 }
        (unitVal, closureOps @ collOps @ closureCoerceOps @ collCoerceOps @ [LlvmCallVoidOp(forInFn, [closurePtrVal; collPtrVal]); ArithConstantOp(unitVal, 0L)])

    // Phase 34-01: LANG-01 StringSliceExpr — s.[start..stop] or s.[start..] (open-ended)
    // Compiles to lang_string_slice(s, start, stop) where stop=-1 means "to end"
    | StringSliceExpr (strExpr, startExpr, stopOpt, _) ->
        let (strVal, strOps)     = elaborateExpr env strExpr
        let (startVal, startOps) = elaborateExpr env startExpr
        let (stopVal, stopOps) =
            match stopOpt with
            | Some stopExpr -> elaborateExpr env stopExpr
            | None ->
                let sv = { Name = freshName env; Type = I64 }
                (sv, [ArithConstantOp(sv, -1L)])
        let strPtrVal =
            if strVal.Type = I64 then { Name = freshName env; Type = Ptr } else strVal
        let strCoerceOps =
            if strVal.Type = I64 then [LlvmIntToPtrOp(strPtrVal, strVal)] else []
        let result = { Name = freshName env; Type = Ptr }
        (result, strOps @ startOps @ stopOps @ strCoerceOps
                 @ [LlvmCallOp(result, "@lang_string_slice", [strPtrVal; startVal; stopVal])])

    // Phase 34-02: LANG-02 ListCompExpr — [for x in coll -> expr] or [for i in 0..n -> expr]
    // Wraps body as Lambda, calls lang_list_comp(closure, collection) → new LangCons* list.
    // Range form: 0..n elaborates to @lang_range which returns a LangCons* list —
    // so lang_list_comp works uniformly for both collection and range inputs.
    | ListCompExpr (var, collExpr, bodyExpr, span) ->
        let closureLambda = Lambda(var, bodyExpr, span)
        let (closureVal, closureOps) = elaborateExpr env closureLambda
        let (collVal, collOps) = elaborateExpr env collExpr
        // Coerce closure to Ptr if needed (same pattern as ForInExpr)
        let closurePtrVal =
            if closureVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else closureVal
        let closureCoerceOps =
            if closureVal.Type = I64
            then [LlvmIntToPtrOp(closurePtrVal, closureVal)]
            else []
        // Coerce collection to Ptr if needed (list pointer may arrive as I64)
        let collPtrVal =
            if collVal.Type = I64
            then { Name = freshName env; Type = Ptr }
            else collVal
        let collCoerceOps =
            if collVal.Type = I64
            then [LlvmIntToPtrOp(collPtrVal, collVal)]
            else []
        let result = { Name = freshName env; Type = Ptr }
        (result, closureOps @ collOps @ closureCoerceOps @ collCoerceOps
                 @ [LlvmCallOp(result, "@lang_list_comp", [closurePtrVal; collPtrVal])])

    | _ ->
        failWithSpan (Ast.spanOf expr) "Elaboration: unsupported expression %A" expr

/// Append ReturnOp to a block body only if the block does not already end with
/// a terminator (llvm.unreachable).  This allows Raise at the end of a function
/// without generating a dead `return` after `llvm.unreachable`.
let private appendReturnIfNeeded (ops: MlirOp list) (retVal: MlirValue) : MlirOp list =
    match List.tryLast ops with
    | Some LlvmUnreachableOp -> ops
    | _ -> ops @ [ReturnOp [retVal]]

let elaborateModule (expr: Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, entryOps) = elaborateExpr env expr
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = appendReturnIfNeeded entryOps resultVal } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = appendReturnIfNeeded lastBlock.Body resultVal }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    // Phase 38: %arg0/%arg1 match Printer's func param naming convention
    let argcVal    = { Name = "%arg0"; Type = I64 }
    let argvVal    = { Name = "%arg1"; Type = Ptr }
    let initArgsOp = LlvmCallVoidOp("@lang_init_args", [argcVal; argvVal])
    let gcInitOp = LlvmCallVoidOp("@GC_init", [])
    let allBlocksWithGC =
        match allBlocks with
        | [] -> allBlocks
        | entryBlock :: rest ->
            { entryBlock with Body = initArgsOp :: gcInitOp :: entryBlock.Body } :: rest
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = [I64; Ptr]
          ReturnType  = Some resultVal.Type
          Body        = { Blocks = allBlocksWithGC }
          IsLlvmFunc  = false }
    let globals = env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value))
    let externalFuncs = [
        { ExtName = "@GC_init";              ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@GC_malloc";            ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@printf";               ExtParams = [Ptr];      ExtReturn = Some I32; IsVarArg = true;  Attrs = [] }
        { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr]; ExtReturn = Some I32; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat";   ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_int";   ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_bool";  ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_match_failure";   ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_failwith";        ExtParams = [Ptr];               ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_sub";      ExtParams = [Ptr; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_contains"; ExtParams = [Ptr; Ptr];          ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_to_int";   ExtParams = [Ptr];               ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_range";           ExtParams = [I64; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_push";           ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_exit";           ExtParams = [];     ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_throw";              ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_current_exception";  ExtParams = [];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@_setjmp";                 ExtParams = [Ptr];  ExtReturn = Some I32; IsVarArg = false; Attrs = ["returns_twice"] }
        { ExtName = "@lang_array_create";       ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_bounds_check"; ExtParams = [Ptr; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_to_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_create";      ExtParams = [];               ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_containsKey"; ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_keys";           ExtParams = [Ptr];            ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_trygetvalue";    ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_create_str";      ExtParams = [];           ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_get_str";         ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_set_str";         ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_containsKey_str"; ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_remove_str";      ExtParams = [Ptr; Ptr];   ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_keys_str";        ExtParams = [Ptr];        ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_trygetvalue_str"; ExtParams = [Ptr; Ptr];   ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get_str";             ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set_str";             ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_iter";            ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_map";             ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_fold";            ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_init";            ExtParams = [I64; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get";             ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set";             ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in";                ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_list";           ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_array";          ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_read";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_write";   ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_append";  ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_exists";  ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprint";       ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprintln";     ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_read_lines";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_write_lines";      ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_line";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_all";   ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_env";          ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_cwd";          ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_path_combine";     ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_dir_files";        ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_endswith";   ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_startswith"; ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_trim";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_split";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_indexof";    ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_replace";    ExtParams = [Ptr; Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_toupper";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_tolower";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat_list";ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_digit";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_letter";    ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_sort_by";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_of_seq";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_sort";        ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_seq";      ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-01 StringBuilder
        { ExtName = "@lang_sb_create";         ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_append";         ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_tostring";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-02 HashSet
        { ExtName = "@lang_hashset_create";    ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_add";       ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_contains";  ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_count";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-03 Queue
        { ExtName = "@lang_queue_create";      ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_enqueue";     ExtParams = [Ptr; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_dequeue";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_count";       ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-04 MutableList
        { ExtName = "@lang_mlist_create";      ExtParams = [];              ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_add";         ExtParams = [Ptr; I64];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_get";         ExtParams = [Ptr; I64];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_count";       ExtParams = [Ptr];           ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 34-01: LANG-01 String slicing
        { ExtName = "@lang_string_slice"; ExtParams = [Ptr; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-02: LANG-02 List comprehension
        { ExtName = "@lang_list_comp"; ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-03: LANG-03/04 for-in over Phase 33 collection types
        { ExtName = "@lang_for_in_hashset";   ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_queue";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_mlist";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_hashtable"; ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        // Phase 38: CLI argument support
        { ExtName = "@lang_init_args"; ExtParams = [I64; Ptr]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_args";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 39: Format string wrappers
        { ExtName = "@lang_sprintf_1i";  ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_1s";  ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ii"; ExtParams = [Ptr; I64; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2si"; ExtParams = [Ptr; Ptr; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2is"; ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ss"; ExtParams = [Ptr; Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
    ]
    { Globals = globals; ExternalFuncs = externalFuncs; Funcs = env.Funcs.Value @ [mainFunc] }

// Phase 16: Declaration pre-pass — scans Decl list and populates TypeEnv, RecordEnv, ExnTags.
// No MLIR IR is emitted in this pass; only F# Map structures are built.
// Phase 25: Made recursive with shared exnCounter so nested modules share the same counter,
// preventing exception tag collisions across module boundaries.
let rec private prePassDecls (exnCounter: int ref) (decls: Ast.Decl list)
    : Map<string, TypeInfo> * Map<string, Map<string, int>> * Map<string, int> =
    let mutable typeEnv  = Map.empty<string, TypeInfo>
    let mutable recordEnv = Map.empty<string, Map<string, int>>
    let mutable exnTags  = Map.empty<string, int>
    for decl in decls do
        match decl with
        | Ast.Decl.TypeDecl (Ast.TypeDecl(_, _, ctors, _, _)) ->
            ctors |> List.iteri (fun idx ctor ->
                match ctor with
                | Ast.ConstructorDecl(name, dataType, _) ->
                    let arity = match dataType with None -> 0 | Some _ -> 1
                    typeEnv <- Map.add name { Tag = idx; Arity = arity } typeEnv
                | Ast.GadtConstructorDecl(name, argTypes, _, _) ->
                    let arity = if argTypes.IsEmpty then 0 else 1
                    typeEnv <- Map.add name { Tag = idx; Arity = arity } typeEnv
            )
        | Ast.Decl.RecordTypeDecl (Ast.RecordDecl(typeName, _, fields, _)) ->
            let fieldMap =
                fields
                |> List.mapi (fun idx (Ast.RecordFieldDecl(name, _, _, _)) -> (name, idx))
                |> Map.ofList
            recordEnv <- Map.add typeName fieldMap recordEnv
        | Ast.Decl.ExceptionDecl(name, dataTypeOpt, _) ->
            let tag = exnCounter.Value
            exnCounter.Value <- tag + 1
            exnTags <- Map.add name tag exnTags
            let arity = match dataTypeOpt with Some _ -> 1 | None -> 0
            typeEnv <- Map.add name { Tag = tag; Arity = arity } typeEnv
        | Ast.Decl.ModuleDecl(_, innerDecls, _) ->
            let (innerTypeEnv, innerRecordEnv, innerExnTags) = prePassDecls exnCounter innerDecls
            typeEnv   <- Map.fold (fun acc k v -> Map.add k v acc) typeEnv   innerTypeEnv
            recordEnv <- Map.fold (fun acc k v -> Map.add k v acc) recordEnv innerRecordEnv
            exnTags   <- Map.fold (fun acc k v -> Map.add k v acc) exnTags   innerExnTags
        | Ast.Decl.TypeClassDecl _ -> ()   // Phase 52: typeclasses handled in elaborateTypeclasses
        | Ast.Decl.InstanceDecl _ -> ()    // Phase 52: instances handled in elaborateTypeclasses
        | Ast.Decl.DerivingDecl _ -> ()    // Phase 52: deriving handled in elaborateTypeclasses
        | _ -> ()
    (typeEnv, recordEnv, exnTags)

// Phase 41/59: collectModuleMembers — first pass scan to build a map from dot-path module key
// to list of underscore-qualified member names.
// e.g., "Outer.Inner" -> ["Outer_Inner_foo"; "Outer_Inner_bar"]
//        "List"        -> ["List_map"; "List_filter"; ...]
// Used by flattenDecls to resolve OpenDecl at compile time.
let private collectModuleMembers (decls: Ast.Decl list) : Map<string, string list> =
    let mutable result = Map.empty<string, string list>
    let rec scan (dotPath: string) (underPath: string) (ds: Ast.Decl list) =
        for d in ds do
            match d with
            | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
                let childDot   = if dotPath   = "" then name else dotPath   + "." + name
                let childUnder = if underPath = "" then name else underPath + "_" + name
                scan childDot childUnder innerDecls
            | Ast.Decl.LetDecl(name, _, _) when underPath <> "" && name <> "_" ->
                let qualifiedName = underPath + "_" + name
                let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
                result <- Map.add dotPath (existing @ [qualifiedName]) result
            | Ast.Decl.LetRecDecl(bindings, _) when underPath <> "" ->
                for (name, _, _, _, _) in bindings do
                    let qualifiedName = underPath + "_" + name
                    let existing = match Map.tryFind dotPath result with Some xs -> xs | None -> []
                    result <- Map.add dotPath (existing @ [qualifiedName]) result
            | _ -> ()
    scan "" "" decls
    result

// Phase 25: flattenDecls — recursively flatten ModuleDecl into a single Decl list.
// This allows extractMainExpr to see all let bindings regardless of nesting depth.
// Phase 35: Module-qualified naming — when flattening a ModuleDecl, prefix all LetDecl/LetRecDecl
// names with the module name (e.g., `module Option = let map f opt = ...` → `let Option_map f opt = ...`).
// This prevents name collisions when multiple modules define functions with the same name (e.g., map, bind).
// Phase 41: OpenDecl handling — when `open ModuleName` is encountered, emit LetDecl aliases for each
// member of the module, making them available as unqualified names. Uses moduleMembers map from first pass.
let rec private flattenDecls (moduleMembers: Map<string, string list>) (modName: string) (decls: Ast.Decl list) : Ast.Decl list =
    decls |> List.collect (fun d ->
        match d with
        | Ast.Decl.ModuleDecl(name, innerDecls, _) ->
            let childPrefix = if modName = "" then name else modName + "_" + name
            flattenDecls moduleMembers childPrefix innerDecls
        | Ast.Decl.LetDecl(name, body, s) when modName <> "" && name <> "_" ->
            [Ast.Decl.LetDecl(modName + "_" + name, body, s)]
        | Ast.Decl.LetRecDecl(bindings, s) when modName <> "" ->
            let prefixed = bindings |> List.map (fun (name, param, pt, body, s2) -> (modName + "_" + name, param, pt, body, s2))
            [Ast.Decl.LetRecDecl(prefixed, s)]
        | Ast.Decl.OpenDecl(path, s) when not (List.isEmpty path) ->
            // Phase 59: Join ALL path segments with "." for the map key (e.g., "Outer.Inner").
            // Previously used List.last which only worked for single-level open.
            let openedKey = path |> String.concat "."
            match Map.tryFind openedKey moduleMembers with
            | Some qualifiedNames ->
                let underscorePrefix = openedKey.Replace(".", "_")
                qualifiedNames |> List.map (fun qualifiedName ->
                    let shortName = qualifiedName.Substring(underscorePrefix.Length + 1)
                    Ast.Decl.LetDecl(shortName, Ast.Var(qualifiedName, s), s))
            | None -> []
        | Ast.Decl.OpenDecl(_, _) -> []
        | _ -> [d])

// Phase 16: Extract the main expression from a Decl list.
// LetDecl("_", expr) → use expr as the body; it already contains nested Let bindings.
// LetDecl(name, body) → wrap in Let(name, body, continuation).
// Non-expression decls (TypeDecl, RecordTypeDecl, ExceptionDecl) are skipped.
// LetRecDecl bindings are wrapped in LetRec expressions.
// Phase 25: Flattens ModuleDecl before processing; handles LetPatDecl;
// Phase 41: OpenDecl is handled by flattenDecls (two-pass: collect members first, then flatten).
let private extractMainExpr (moduleSpan: Ast.Span) (decls: Ast.Decl list) : Expr =
    let s = moduleSpan
    let moduleMembers = collectModuleMembers decls
    let flatDecls = flattenDecls moduleMembers "" decls
    let exprDecls =
        flatDecls |> List.filter (fun d ->
            match d with
            | Ast.Decl.LetDecl _ | Ast.Decl.LetRecDecl _ | Ast.Decl.LetMutDecl _
            | Ast.Decl.LetPatDecl _ -> true
            | _ -> false)
    match exprDecls with
    | [] -> Number(0, s)  // empty module → produce 0 as unit sentinel
    | _ ->
        // Fold right: each decl wraps the continuation
        let rec build (ds: Ast.Decl list) : Expr =
            match ds with
            | [] -> Number(0, s)
            | [Ast.Decl.LetDecl("_", body, _)] -> body
            | [Ast.Decl.LetDecl(name, body, _)] -> Let(name, body, Var(name, s), s)
            | [Ast.Decl.LetRecDecl(bindings, _)] ->
                // Single let rec with no continuation: return 0 as program exit sentinel
                if bindings.IsEmpty then Number(0, s)
                else LetRec(bindings, Number(0, s), s)
            | [Ast.Decl.LetPatDecl(pat, body, sp)] ->
                LetPat(pat, body, Number(0, s), sp)
            | Ast.Decl.LetDecl("_", body, _) :: rest ->
                // let _ = body → evaluate body for side effects, then rest
                // Represent as Let("_", body, continuation)
                Let("_", body, build rest, s)
            | Ast.Decl.LetDecl(name, body, _) :: rest ->
                Let(name, body, build rest, s)
            | Ast.Decl.LetRecDecl(bindings, _) :: rest ->
                if bindings.IsEmpty then build rest
                else LetRec(bindings, build rest, s)
            | Ast.Decl.LetMutDecl(name, body, _) :: rest ->
                LetMut(name, body, build rest, s)
            | Ast.Decl.LetPatDecl(pat, body, sp) :: rest ->
                LetPat(pat, body, build rest, sp)
            | _ :: rest -> build rest
        build exprDecls

// Phase 52: Transform typeclass declarations before main elaboration.
// - TypeClassDecl: removed (class definitions are not needed at runtime)
// - InstanceDecl: each method becomes a plain LetDecl binding
// - DerivingDecl: removed (auto-derivation handled at parse time)
// - ModuleDecl: recurse into inner decls; hoist instance bindings to outer scope
let rec elaborateTypeclasses (decls: Ast.Decl list) : Ast.Decl list =
    // Pass 1: collect constructor info for DerivingDecl expansion
    let ctorMap =
        decls |> List.collect (fun d ->
            match d with
            | Ast.Decl.TypeDecl(Ast.TypeDecl(name, _, ctors, _, _)) ->
                [(name, ctors)]
            | _ -> [])
        |> Map.ofList
    // Pass 2: transform decls
    decls |> List.collect (fun decl ->
        match decl with
        | Ast.Decl.TypeClassDecl _ -> []
        | Ast.Decl.InstanceDecl(_className, _instType, methods, _constraints, span) ->
            methods |> List.map (fun (methodName, methodBody) ->
                Ast.Decl.LetDecl(methodName, methodBody, span))
        | Ast.Decl.ModuleDecl(name, innerDecls, span) ->
            let instanceBindings =
                innerDecls |> List.collect (fun d ->
                    match d with
                    | Ast.Decl.InstanceDecl(_, _, methods, _, ispan) ->
                        methods |> List.map (fun (methodName, methodBody) ->
                            Ast.Decl.LetDecl(methodName, methodBody, ispan))
                    | _ -> [])
            [Ast.Decl.ModuleDecl(name, elaborateTypeclasses innerDecls, span)] @ instanceBindings
        | Ast.Decl.DerivingDecl(typeName, classNames, span) ->
            classNames |> List.collect (fun className ->
                match className with
                | "Show" ->
                    match Map.tryFind typeName ctorMap with
                    | None -> []  // Unknown type: skip silently
                    | Some ctors ->
                        let clauses =
                            ctors |> List.map (fun ctor ->
                                match ctor with
                                | Ast.ConstructorDecl(ctorName, None, _) ->
                                    (Ast.ConstructorPat(ctorName, None, span), None,
                                     Ast.String(ctorName, span))
                                | Ast.ConstructorDecl(ctorName, Some _, _) ->
                                    let vPat = Ast.VarPat("__v", span)
                                    let body = Ast.Add(Ast.String(ctorName + " ", span),
                                                       Ast.App(Ast.Var("show", span), Ast.Var("__v", span), span),
                                                       span)
                                    (Ast.ConstructorPat(ctorName, Some vPat, span), None, body)
                                | Ast.GadtConstructorDecl(ctorName, [], _, _) ->
                                    (Ast.ConstructorPat(ctorName, None, span), None,
                                     Ast.String(ctorName, span))
                                | Ast.GadtConstructorDecl(ctorName, _, _, _) ->
                                    let vPat = Ast.VarPat("__v", span)
                                    let body = Ast.Add(Ast.String(ctorName + " ", span),
                                                       Ast.App(Ast.Var("show", span), Ast.Var("__v", span), span),
                                                       span)
                                    (Ast.ConstructorPat(ctorName, Some vPat, span), None, body))
                        let matchExpr = Ast.Match(Ast.Var("__x", span), clauses, span)
                        let showBody = Ast.Lambda("__x", matchExpr, span)
                        [Ast.Decl.LetDecl("show", showBody, span)]
                | "Eq" ->
                    match Map.tryFind typeName ctorMap with
                    | None -> []
                    | Some ctors ->
                        let clauses =
                            ctors |> List.map (fun ctor ->
                                match ctor with
                                | Ast.ConstructorDecl(ctorName, None, _) ->
                                    let pat = Ast.TuplePat([Ast.ConstructorPat(ctorName, None, span); Ast.ConstructorPat(ctorName, None, span)], span)
                                    (pat, None, Ast.Bool(true, span))
                                | Ast.ConstructorDecl(ctorName, Some _, _) ->
                                    let pat = Ast.TuplePat([Ast.ConstructorPat(ctorName, Some(Ast.VarPat("__a", span)), span);
                                                             Ast.ConstructorPat(ctorName, Some(Ast.VarPat("__b", span)), span)], span)
                                    let body = Ast.App(Ast.App(Ast.Var("eq", span), Ast.Var("__a", span), span), Ast.Var("__b", span), span)
                                    (pat, None, body)
                                | _ -> (Ast.WildcardPat span, None, Ast.Bool(false, span)))
                        let wildcard = (Ast.WildcardPat span, None, Ast.Bool(false, span))
                        let matchExpr = Ast.Match(Ast.Tuple([Ast.Var("__x", span); Ast.Var("__y", span)], span),
                                                   clauses @ [wildcard], span)
                        let eqBody = Ast.Lambda("__x", Ast.Lambda("__y", matchExpr, span), span)
                        [Ast.Decl.LetDecl("eq", eqBody, span)]
                | _ -> [])
        | other -> [other])

// Phase 16: elaborateProgram — new entry point accepting Ast.Module.
// Runs prePassDecls to populate TypeEnv/RecordEnv/ExnTags, then elaborates the program body.
let elaborateProgram (ast: Ast.Module) : MlirModule =
    let decls =
        match ast with
        | Ast.Module(decls, _) | Ast.NamedModule(_, decls, _) -> decls
        | Ast.EmptyModule _ -> []
    let (typeEnv, recordEnv, exnTags) = prePassDecls (ref 0) decls
    let mainExpr = extractMainExpr (Ast.moduleSpanOf ast) decls
    let env = { emptyEnv () with TypeEnv = typeEnv; RecordEnv = recordEnv; ExnTags = exnTags }
    let (resultVal, entryOps) = elaborateExpr env mainExpr
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = appendReturnIfNeeded entryOps resultVal } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = appendReturnIfNeeded lastBlock.Body resultVal }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    // Phase 38: %arg0/%arg1 match Printer's func param naming convention
    let argcVal    = { Name = "%arg0"; Type = I64 }
    let argvVal    = { Name = "%arg1"; Type = Ptr }
    let initArgsOp = LlvmCallVoidOp("@lang_init_args", [argcVal; argvVal])
    let gcInitOp = LlvmCallVoidOp("@GC_init", [])
    let allBlocksWithGC =
        match allBlocks with
        | [] -> allBlocks
        | entryBlock :: rest ->
            { entryBlock with Body = initArgsOp :: gcInitOp :: entryBlock.Body } :: rest
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = [I64; Ptr]
          ReturnType  = Some resultVal.Type
          Body        = { Blocks = allBlocksWithGC }
          IsLlvmFunc  = false }
    let globals = env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value))
    let externalFuncs = [
        { ExtName = "@GC_init";              ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@GC_malloc";            ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@printf";               ExtParams = [Ptr];      ExtReturn = Some I32; IsVarArg = true;  Attrs = [] }
        { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr]; ExtReturn = Some I32; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat";   ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_int";   ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_to_string_bool";  ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_match_failure";   ExtParams = [];         ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_failwith";        ExtParams = [Ptr];               ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_sub";      ExtParams = [Ptr; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_contains"; ExtParams = [Ptr; Ptr];          ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_to_int";   ExtParams = [Ptr];               ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_range";           ExtParams = [I64; I64; I64];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_push";           ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_try_exit";           ExtParams = [];     ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_throw";              ExtParams = [Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_current_exception";  ExtParams = [];     ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@_setjmp";                 ExtParams = [Ptr];  ExtReturn = Some I32; IsVarArg = false; Attrs = ["returns_twice"] }
        { ExtName = "@lang_array_create";       ExtParams = [I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_bounds_check"; ExtParams = [Ptr; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_to_list";      ExtParams = [Ptr];      ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_create";      ExtParams = [];               ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_get";         ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_set";         ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_containsKey"; ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_remove";      ExtParams = [Ptr; I64];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_keys";           ExtParams = [Ptr];            ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_trygetvalue";    ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_create_str";      ExtParams = [];           ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_get_str";         ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_set_str";         ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_containsKey_str"; ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_remove_str";      ExtParams = [Ptr; Ptr];   ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_keys_str";        ExtParams = [Ptr];        ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashtable_trygetvalue_str"; ExtParams = [Ptr; Ptr];   ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get_str";             ExtParams = [Ptr; Ptr];   ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set_str";             ExtParams = [Ptr; Ptr; I64]; ExtReturn = None;  IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_iter";            ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_map";             ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_fold";            ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_init";            ExtParams = [I64; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_get";             ExtParams = [Ptr; I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_index_set";             ExtParams = [Ptr; I64; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in";                ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_list";           ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_array";          ExtParams = [Ptr; Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_read";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_write";   ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_append";  ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_file_exists";  ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprint";       ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_eprintln";     ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_read_lines";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_write_lines";      ExtParams = [Ptr; Ptr];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_line";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_stdin_read_all";   ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_env";          ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_cwd";          ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_path_combine";     ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_dir_files";        ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_endswith";   ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_startswith"; ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_trim";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_split";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_indexof";    ExtParams = [Ptr; Ptr];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_replace";    ExtParams = [Ptr; Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_toupper";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_tolower";    ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_string_concat_list";ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_digit";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_letter";    ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_is_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_upper";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_char_to_lower";     ExtParams = [I64];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_sort_by";      ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_list_of_seq";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_sort";        ExtParams = [Ptr];       ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_array_of_seq";      ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-01 StringBuilder
        { ExtName = "@lang_sb_create";         ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_append";         ExtParams = [Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sb_tostring";       ExtParams = [Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 33-01: COL-02 HashSet
        { ExtName = "@lang_hashset_create";    ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_add";       ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_contains";  ExtParams = [Ptr; I64];  ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_hashset_count";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-03 Queue
        { ExtName = "@lang_queue_create";      ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_enqueue";     ExtParams = [Ptr; I64];  ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_dequeue";     ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_queue_count";       ExtParams = [Ptr];       ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 33-02: COL-04 MutableList
        { ExtName = "@lang_mlist_create";      ExtParams = [];              ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_add";         ExtParams = [Ptr; I64];      ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_get";         ExtParams = [Ptr; I64];      ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_set";         ExtParams = [Ptr; I64; I64]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_mlist_count";       ExtParams = [Ptr];           ExtReturn = Some I64; IsVarArg = false; Attrs = [] }
        // Phase 34-01: LANG-01 String slicing
        { ExtName = "@lang_string_slice"; ExtParams = [Ptr; I64; I64]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-02: LANG-02 List comprehension
        { ExtName = "@lang_list_comp"; ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 34-03: LANG-03/04 for-in over Phase 33 collection types
        { ExtName = "@lang_for_in_hashset";   ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_queue";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_mlist";     ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_for_in_hashtable"; ExtParams = [Ptr; Ptr]; ExtReturn = None; IsVarArg = false; Attrs = [] }
        // Phase 38: CLI argument support
        { ExtName = "@lang_init_args"; ExtParams = [I64; Ptr]; ExtReturn = None;     IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_get_args";  ExtParams = [];          ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        // Phase 39: Format string wrappers
        { ExtName = "@lang_sprintf_1i";  ExtParams = [Ptr; I64];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_1s";  ExtParams = [Ptr; Ptr];       ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ii"; ExtParams = [Ptr; I64; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2si"; ExtParams = [Ptr; Ptr; I64];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2is"; ExtParams = [Ptr; I64; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
        { ExtName = "@lang_sprintf_2ss"; ExtParams = [Ptr; Ptr; Ptr];  ExtReturn = Some Ptr; IsVarArg = false; Attrs = [] }
    ]
    { Globals = globals; ExternalFuncs = externalFuncs; Funcs = env.Funcs.Value @ [mainFunc] }
