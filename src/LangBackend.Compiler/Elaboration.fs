module Elaboration

open Ast
open MlirIR
open MatchCompiler

// Phase 5: Closure metadata — distinguishes closure-making funcs from direct-call funcs
type ClosureInfo = {
    InnerLambdaFn: string    // MLIR name of the llvm.func body, e.g. "@closure_fn_0"
    NumCaptures:   int       // number of captured variables
}

type FuncSignature = {
    MlirName:    string
    ParamTypes:  MlirType list
    ReturnType:  MlirType
    ClosureInfo: ClosureInfo option  // None = direct-call func; Some = closure-maker
}

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
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
      KnownFuncs = Map.empty; Funcs = ref []; ClosureCounter = ref 0
      Globals = ref []; GlobalCounter = ref 0 }

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
    | LetRec (name, param, body, inExpr, _) ->
        let innerBound = Set.add name (Set.add param boundVars)
        Set.union (freeVars innerBound body) (freeVars (Set.add name boundVars) inExpr)
    | String _ -> Set.empty
    | Tuple (exprs, _) ->
        exprs |> List.map (freeVars boundVars) |> Set.unionMany
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
    | _ -> Set.empty  // conservative: other exprs (Char, etc.) have no free vars

/// Detect whether a LetRec body uses list patterns on the parameter,
/// indicating the parameter should be typed Ptr (list pointer) rather than I64.
let private isListParamBody (paramName: string) (bodyExpr: Expr) : bool =
    match bodyExpr with
    | Match(Var(scrutinee, _), clauses, _) when scrutinee = paramName ->
        clauses |> List.exists (fun (pat, _, _) ->
            match pat with
            | EmptyListPat _ | ConsPat _ -> true
            | _ -> false)
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
        let headName = match hPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failwithf "Elaboration: ConsPat head must be VarPat or WildcardPat"
        let tailName = match tPat with VarPat(n, _) -> Some n | WildcardPat _ -> None | _ -> failwithf "Elaboration: ConsPat tail must be VarPat or WildcardPat"
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
                    failwithf "testPattern: TuplePat sub-pattern %A not supported" subPat
            ) ([], env)
        (None, [], setupOps, bindEnv)

    | _ ->
        failwithf "testPattern: pattern %A not supported in v2" pat

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
    | Var (name, _) ->
        match Map.tryFind name env.Vars with
        | Some v -> (v, [])
        | None -> failwithf "Elaboration: unbound variable '%s'" name
    // Phase 5: special-case Let(name, Lambda(outerParam, Lambda(innerParam, innerBody)), inExpr)
    // This compiles to an llvm.func body + func.func closure-maker + KnownFuncs entry
    | Let (name, Lambda (outerParam, Lambda (innerParam, innerBody, _), _), inExpr, _) ->
        // Step 1: Compute free variables of the inner lambda body relative to innerParam only.
        // These are variables that need to come from the closure environment struct.
        // outerParam IS one such variable — it's passed to the closure-maker and stored at env[1+i].
        // Using only {innerParam} as bound means outerParam appears free when it's used in innerBody.
        let captures =
            freeVars (Set.singleton innerParam) innerBody
            |> Set.toList
            |> List.sort
        let numCaptures = List.length captures

        // Step 2: Generate fresh closure function name
        let closureFnIdx = env.ClosureCounter.Value
        env.ClosureCounter.Value <- closureFnIdx + 1
        let closureFnName = sprintf "@closure_fn_%d" closureFnIdx

        // Step 3: Compile inner lambda body (llvm.func)
        // Build the initial vars: %arg0 = env ptr, %arg1 = innerParam
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = I64 }
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(innerParam, arg1Val)]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter }

        // For each capture at index i: GEP to slot i+1, then load
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" i; Type = Ptr }
                let capVal = { Name = sprintf "%%t%d" (i + numCaptures); Type = I64 }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                // Advance counter past GEP and load names we pre-allocated
                let gepOp = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)

        // Advance inner env counter past the pre-allocated GEP/load SSA names
        innerEnvWithCaptures.Counter.Value <- numCaptures * 2

        // Elaborate inner body
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures innerBody
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value

        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps @ [LlvmReturnOp [bodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [LlvmReturnOp [bodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched

        let innerFuncOp : FuncOp =
            { Name        = closureFnName
              InputTypes  = [Ptr; I64]
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
                        | None -> failwithf "Elaboration: closure capture '%s' not found in outer scope" capName
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
        env.Funcs.Value <- env.Funcs.Value @ [innerFuncOp; makerFuncOp]

        // Step 6: Add to KnownFuncs
        let closureInfo = { InnerLambdaFn = closureFnName; NumCaptures = numCaptures }
        let sig_ : FuncSignature =
            { MlirName    = "@" + name
              ParamTypes  = [I64]
              ReturnType  = Ptr
              ClosureInfo = Some closureInfo }
        let env' = { env with KnownFuncs = Map.add name sig_ env.KnownFuncs }

        // Step 7: Elaborate inExpr with updated env
        elaborateExpr env' inExpr

    | Let (name, bindExpr, bodyExpr, _) ->
        let (bv, bops) = elaborateExpr env bindExpr
        let env' = { env with Vars = Map.add name bv env.Vars }
        let (rv, rops) = elaborateExpr env' bodyExpr
        (rv, bops @ rops)
    // Phase 7: LetPat with wildcard or var pattern — enables "let _ = print ... in ..."
    | LetPat (WildcardPat _, bindExpr, bodyExpr, _) ->
        let (_bv, bops) = elaborateExpr env bindExpr
        let (rv, rops) = elaborateExpr env bodyExpr
        (rv, bops @ rops)
    | LetPat (VarPat (name, _), bindExpr, bodyExpr, _) ->
        let (bv, bops) = elaborateExpr env bindExpr
        let env' = { env with Vars = Map.add name bv env.Vars }
        let (rv, rops) = elaborateExpr env' bodyExpr
        (rv, bops @ rops)
    // Phase 9: LetPat with TuplePat — destructure a heap-allocated tuple via GEP + load
    | LetPat (TuplePat (pats, _), bindExpr, bodyExpr, _) ->
        let (tupPtrVal, bindOps) = elaborateExpr env bindExpr
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
                    failwithf "Elaboration: unsupported sub-pattern in TuplePat: %A" pat
            ) ([], envAcc)
        let (extractOps, env') = bindTuplePat env tupPtrVal pats
        let (bodyVal, bodyOps) = elaborateExpr env' bodyExpr
        (bodyVal, bindOps @ extractOps @ bodyOps)
    | Bool (b, _) ->
        let v = { Name = freshName env; Type = I1 }
        let n = if b then 1L else 0L
        (v, [ArithConstantOp(v, n)])
    | Equal (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
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
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "slt", lv, rv)])
    | GreaterThan (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "sgt", lv, rv)])
    | LessEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "sle", lv, rv)])
    | GreaterEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "sge", lv, rv)])
    | If (condExpr, thenExpr, elseExpr, _) ->
        let (condVal, condOps) = elaborateExpr env condExpr
        let thenLabel  = freshLabel env "then"
        let elseLabel  = freshLabel env "else"
        let mergeLabel = freshLabel env "merge"
        let (thenVal, thenOps) = elaborateExpr env thenExpr
        let (elseVal, elseOps) = elaborateExpr env elseExpr
        let mergeArg = { Name = freshName env; Type = thenVal.Type }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some thenLabel; Args = []; Body = thenOps @ [CfBrOp(mergeLabel, [thenVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some elseLabel; Args = []; Body = elseOps @ [CfBrOp(mergeLabel, [elseVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        (mergeArg, condOps @ [CfCondBrOp(condVal, thenLabel, [], elseLabel, [])])
    | And (lhsExpr, rhsExpr, _) ->
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let evalRightLabel = freshLabel env "and_right"
        let mergeLabel     = freshLabel env "and_merge"
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        let mergeArg = { Name = freshName env; Type = I1 }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ [CfBrOp(mergeLabel, [rightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        (mergeArg, leftOps @ [CfCondBrOp(leftVal, evalRightLabel, [], mergeLabel, [leftVal])])
    | Or (lhsExpr, rhsExpr, _) ->
        let (leftVal, leftOps) = elaborateExpr env lhsExpr
        let evalRightLabel = freshLabel env "or_right"
        let mergeLabel     = freshLabel env "or_merge"
        let (rightVal, rightOps) = elaborateExpr env rhsExpr
        let mergeArg = { Name = freshName env; Type = I1 }
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some evalRightLabel; Args = []; Body = rightOps @ [CfBrOp(mergeLabel, [rightVal])] } ]
        env.Blocks.Value <- env.Blocks.Value @
            [ { Label = Some mergeLabel; Args = [mergeArg]; Body = [] } ]
        (mergeArg, leftOps @ [CfCondBrOp(leftVal, mergeLabel, [leftVal], evalRightLabel, [])])
    | LetRec (name, param, body, inExpr, _) ->
        let paramType = if isListParamBody param body then Ptr else I64
        let sig_ : FuncSignature =
            { MlirName = "@" + name; ParamTypes = [paramType]; ReturnType = I64; ClosureInfo = None }
        let bodyEnv : ElabEnv =
            { Vars = Map.ofList [(param, { Name = "%arg0"; Type = paramType })]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = Map.ofList [(name, sig_)]
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter }
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
        let env' = { env with KnownFuncs = Map.add name { sig_ with ReturnType = bodyVal.Type } env.KnownFuncs }
        elaborateExpr env' inExpr
    // Phase 8: String literal → GC_malloc'd header struct
    | String (s, _) ->
        elaborateStringLiteral env s

    // Phase 8: string_concat builtin — App(App(Var("string_concat"), a), b)
    // Must be placed BEFORE general App to avoid being caught by general App dispatch
    | App (App (Var ("string_concat", _), aExpr, _), bExpr, _) ->
        let (aVal, aOps) = elaborateExpr env aExpr
        let (bVal, bOps) = elaborateExpr env bExpr
        let result = { Name = freshName env; Type = Ptr }
        (result, aOps @ bOps @ [LlvmCallOp(result, "@lang_string_concat", [aVal; bVal])])

    // Phase 8: to_string builtin — dispatch on elaborated arg type
    | App (Var ("to_string", _), argExpr, _) ->
        let (argVal, argOps) = elaborateExpr env argExpr
        let result = { Name = freshName env; Type = Ptr }
        match argVal.Type with
        | I1 ->
            // Zero-extend I1 to I64 for C ABI compatibility (lang_to_string_bool takes int64_t)
            let extVal = { Name = freshName env; Type = I64 }
            (result, argOps @ [ArithExtuIOp(extVal, argVal); LlvmCallOp(result, "@lang_to_string_bool", [extVal])])
        | _ ->
            // I64 and other numeric types — call lang_to_string_int directly
            (result, argOps @ [LlvmCallOp(result, "@lang_to_string_int", [argVal])])

    // Phase 8: string_length builtin — GEP field 0 and load
    | App (Var ("string_length", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let lenPtrVal = { Name = freshName env; Type = Ptr }
        let lenVal    = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(lenPtrVal, strVal, 0)
            LlvmLoadOp(lenVal, lenPtrVal)
        ]
        (lenVal, strOps @ ops)

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
    | App (Var ("print", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let fmtRes      = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, strVal, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes, "@printf", [dataAddrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, strOps @ ops)

    | App (Var ("println", _), strExpr, _) ->
        let (strVal, strOps) = elaborateExpr env strExpr
        let dataPtrVal  = { Name = freshName env; Type = Ptr }
        let dataAddrVal = { Name = freshName env; Type = Ptr }
        let nlGlobal    = addStringGlobal env "\n"
        let nlPtrVal    = { Name = freshName env; Type = Ptr }
        let fmtRes1     = { Name = freshName env; Type = I32 }
        let fmtRes2     = { Name = freshName env; Type = I32 }
        let unitVal     = { Name = freshName env; Type = I64 }
        let ops = [
            LlvmGEPStructOp(dataPtrVal, strVal, 1)
            LlvmLoadOp(dataAddrVal, dataPtrVal)
            LlvmCallOp(fmtRes1, "@printf", [dataAddrVal])
            LlvmAddressOfOp(nlPtrVal, nlGlobal)
            LlvmCallOp(fmtRes2, "@printf", [nlPtrVal])
            ArithConstantOp(unitVal, 0L)
        ]
        (unitVal, strOps @ ops)

    | App (funcExpr, argExpr, _) ->
        match funcExpr with
        | Var (name, _) ->
            match Map.tryFind name env.KnownFuncs with
            | Some sig_ when sig_.ClosureInfo.IsNone ->
                // DIRECT CALL (Phase 4 behavior) — known non-closure function
                let (argVal, argOps) = elaborateExpr env argExpr
                let result = { Name = freshName env; Type = sig_.ReturnType }
                (result, argOps @ [DirectCallOp(result, sig_.MlirName, [argVal])])
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
                let callOp = DirectCallOp(resultVal, sig_.MlirName, [argVal; envPtrVal])
                (resultVal, argOps @ setupOps @ [callOp])
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
                | _ ->
                    failwithf "Elaboration: unsupported App — '%s' is not a known function or closure value" name
        | Lambda(param, body, _) ->
            // Inline lambda application: (fun x -> body) arg  ≡  let x = arg in body
            let (argVal, argOps) = elaborateExpr env argExpr
            let env' = { env with Vars = Map.add param argVal env.Vars }
            let (bodyVal, bodyOps) = elaborateExpr env' body
            (bodyVal, argOps @ bodyOps)
        | _ ->
            failwithf "Elaboration: unsupported App (only named function application supported in Phase 5)"
    // Phase 9: Tuple construction — GC_malloc(n*8) + sequential GEP + store
    | Tuple (exprs, _) ->
        let n = List.length exprs
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

    // Phase 11 (v2): General Match compiler — Jules Jacobs decision tree algorithm.
    // Compiles pattern matching to a binary decision tree, then emits MLIR from the tree.
    // Handles: VarPat, WildcardPat, ConstPat(Int/Bool/String), EmptyListPat, ConsPat, TuplePat.
    // Phase 13: adds OrPat expansion (PAT-07), when-guard (PAT-06), CharConst (PAT-08).
    | Match(scrutineeExpr, clauses, _) ->
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
                    let (parentVal, parentOps) = resolveAccessor parent
                    // Determine the type of the loaded field.
                    // For cons cells: field 0 = head (I64), field 1 = tail (Ptr).
                    // For tuples: all fields are I64 (or Ptr for nested tuples, but
                    // we load as I64 by default and the accessor will re-interpret).
                    // We use Ptr for field 1 of a cons cell (tail pointer), I64 otherwise.
                    // Actually, we need to know the parent's constructor to decide.
                    // For simplicity: if the parent is Ptr-typed and we know it's a cons cell,
                    // field 0 → I64, field 1 → Ptr.  For tuples, all fields are I64.
                    // But since we can't easily tell from just the accessor, we use a heuristic:
                    // load as I64 for field 0, and check parentVal.Type for the rest.
                    //
                    // Better approach: the field type depends on usage. For now:
                    // - If parentVal.Type = Ptr, field 1 should be Ptr (tail of list), field 0 = I64 (head)
                    //   But tuples also use Ptr and all fields could be I64 or Ptr.
                    // We'll default to I64 and handle Ptr cases as they arise.
                    // The decision tree will re-resolve with the right type when needed.
                    //
                    // Actually the safest approach: we always load as I64 initially.
                    // When the decision tree needs to test a field as a list (null check etc.),
                    // the emitDecisionTree will handle the type correctly.
                    let slotVal  = { Name = freshName env; Type = Ptr }
                    let gepOp    = LlvmGEPLinearOp(slotVal, parentVal, idx)
                    // For list tails (field 1 of a cons cell), load as Ptr
                    // For tuple fields that will be further destructured, also Ptr
                    // We can't know statically here, so we'll handle this in emitDecisionTree
                    // by loading with the right type based on the constructor being tested.
                    // Default: I64
                    let fieldVal = { Name = freshName env; Type = I64 }
                    let loadOp   = LlvmLoadOp(fieldVal, slotVal)
                    accessorCache.[acc] <- fieldVal
                    (fieldVal, parentOps @ [gepOp; loadOp])

        // Resolve an accessor, but load with a specific type override.
        // This is needed when we know the field should be Ptr (e.g. list tail, nested tuple).
        let resolveAccessorTyped (acc: MatchCompiler.Accessor) (ty: MlirType) : MlirValue * MlirOp list =
            match accessorCache.TryGetValue(acc) with
            | true, v when v.Type = ty -> (v, [])
            | true, v ->
                // Cached with wrong type — we need to re-load with correct type.
                // This happens when a field was first loaded as I64 but actually needs to be Ptr.
                // Emit a new load from the parent.
                match acc with
                | MatchCompiler.Root _ -> (v, [])  // root type is fixed
                | MatchCompiler.Field (parent, idx) ->
                    let (parentVal, parentOps) = resolveAccessor parent
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
                    let (parentVal, parentOps) = resolveAccessor parent
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
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                // Create a body block and branch to merge
                let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel
                        Args  = []
                        Body  = bodyOps @ [CfBrOp(mergeLabel, [bodyVal])] } ]
                bindOps @ [ CfBrOp(bodyLabel, []) ]
            | MatchCompiler.Switch (scrutAcc, tag, argAccs, ifMatch, ifNoMatch) ->
                // Resolve the scrutinee accessor with the correct type for this tag
                let expectedType = scrutineeTypeForTag tag
                let (sVal, resolveOps) = resolveAccessorTyped scrutAcc expectedType
                // Emit the test
                let (cond, testOps) = emitCtorTest sVal tag
                // For ConsCtor, pre-load sub-fields with correct types into the cache
                let preloadOps =
                    match tag with
                    | MatchCompiler.ConsCtor -> ensureConsFieldTypes scrutAcc argAccs
                    | _ -> []
                // Emit ifMatch and ifNoMatch as separate blocks
                let matchLabel   = freshLabel env "match_yes"
                let noMatchLabel = freshLabel env "match_no"
                let matchOps   = emitDecisionTree ifMatch
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
                // 4. Emit the body block (same as Leaf)
                let (bodyVal, bodyOps) = elaborateExpr bindEnv bodyExpr
                resultType.Value <- bodyVal.Type
                let bodyLabel = freshLabel env (sprintf "match_body%d" bodyIdx)
                env.Blocks.Value <- env.Blocks.Value @
                    [ { Label = Some bodyLabel; Args = [];
                        Body = bodyOps @ [CfBrOp(mergeLabel, [bodyVal])] } ]
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

        // Build inner llvm.func: (%arg0: !llvm.ptr, %arg1: i64) -> i64
        let arg0Val = { Name = "%arg0"; Type = Ptr }
        let arg1Val = { Name = "%arg1"; Type = I64 }
        let innerEnv : ElabEnv =
            { Vars = Map.ofList [(param, arg1Val)]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = env.KnownFuncs
              Funcs = env.Funcs
              ClosureCounter = env.ClosureCounter
              Globals = env.Globals
              GlobalCounter = env.GlobalCounter }
        let captureLoadOps, innerEnvWithCaptures =
            captures |> List.mapi (fun i capName ->
                let gepVal = { Name = sprintf "%%t%d" i; Type = Ptr }
                let capVal = { Name = sprintf "%%t%d" (i + numCaptures); Type = I64 }
                (gepVal, capVal, capName, i)
            )
            |> List.fold (fun (opsAcc, envAcc: ElabEnv) (gepVal, capVal, capName, i) ->
                let gepOp  = LlvmGEPLinearOp(gepVal, arg0Val, i + 1)
                let loadOp = LlvmLoadOp(capVal, gepVal)
                let envAcc' = { envAcc with Vars = Map.add capName capVal envAcc.Vars }
                (opsAcc @ [gepOp; loadOp], envAcc')
            ) ([], innerEnv)
        innerEnvWithCaptures.Counter.Value <- numCaptures * 2
        let (bodyVal, bodyEntryOps) = elaborateExpr innerEnvWithCaptures body
        let bodySideBlocks = innerEnvWithCaptures.Blocks.Value
        let allBodyBlocks =
            if bodySideBlocks.IsEmpty then
                [ { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps @ [LlvmReturnOp [bodyVal]] } ]
            else
                let entryBlock = { Label = None; Args = []; Body = captureLoadOps @ bodyEntryOps }
                let lastBlock = List.last bodySideBlocks
                let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [LlvmReturnOp [bodyVal]] }
                let sideBlocksPatched = (List.take (bodySideBlocks.Length - 1) bodySideBlocks) @ [lastBlockWithReturn]
                entryBlock :: sideBlocksPatched
        let innerFuncOp : FuncOp =
            { Name = closureFnName; InputTypes = [Ptr; I64]; ReturnType = Some I64
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

    | _ ->
        failwithf "Elaboration: unsupported expression %A" expr

let elaborateModule (expr: Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, entryOps) = elaborateExpr env expr
    let sideBlocks = env.Blocks.Value
    let allBlocks =
        if sideBlocks.IsEmpty then
            [ { Label = None; Args = []; Body = entryOps @ [ReturnOp [resultVal]] } ]
        else
            let entryBlock = { Label = None; Args = []; Body = entryOps }
            let lastBlock = List.last sideBlocks
            let lastBlockWithReturn = { lastBlock with Body = lastBlock.Body @ [ReturnOp [resultVal]] }
            let sideBlocksPatched = (List.take (sideBlocks.Length - 1) sideBlocks) @ [lastBlockWithReturn]
            entryBlock :: sideBlocksPatched
    let gcInitOp = LlvmCallVoidOp("@GC_init", [])
    let allBlocksWithGC =
        match allBlocks with
        | [] -> allBlocks
        | entryBlock :: rest ->
            { entryBlock with Body = gcInitOp :: entryBlock.Body } :: rest
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = []
          ReturnType  = Some resultVal.Type
          Body        = { Blocks = allBlocksWithGC }
          IsLlvmFunc  = false }
    let globals = env.Globals.Value |> List.map (fun (name, value) -> StringConstant(name, value))
    let externalFuncs = [
        { ExtName = "@GC_init";              ExtParams = [];         ExtReturn = None;     IsVarArg = false }
        { ExtName = "@GC_malloc";            ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false }
        { ExtName = "@printf";               ExtParams = [Ptr];      ExtReturn = Some I32; IsVarArg = true  }
        { ExtName = "@strcmp";               ExtParams = [Ptr; Ptr]; ExtReturn = Some I32; IsVarArg = false }
        { ExtName = "@lang_string_concat";   ExtParams = [Ptr; Ptr]; ExtReturn = Some Ptr; IsVarArg = false }
        { ExtName = "@lang_to_string_int";   ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false }
        { ExtName = "@lang_to_string_bool";  ExtParams = [I64];      ExtReturn = Some Ptr; IsVarArg = false }
        { ExtName = "@lang_match_failure";   ExtParams = [];         ExtReturn = None;     IsVarArg = false }
    ]
    { Globals = globals; ExternalFuncs = externalFuncs; Funcs = env.Funcs.Value @ [mainFunc] }
