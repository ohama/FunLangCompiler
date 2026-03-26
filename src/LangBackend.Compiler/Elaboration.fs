module Elaboration

open Ast
open MlirIR

type FuncSignature = {
    MlirName:   string
    ParamTypes: MlirType list
    ReturnType: MlirType
}

type ElabEnv = {
    Vars:         Map<string, MlirValue>
    Counter:      int ref
    LabelCounter: int ref
    Blocks:       MlirBlock list ref
    KnownFuncs:   Map<string, FuncSignature>
    Funcs:        FuncOp list ref
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
      KnownFuncs = Map.empty; Funcs = ref [] }

let private freshName (env: ElabEnv) : string =
    let n = env.Counter.Value
    env.Counter.Value <- n + 1
    sprintf "%%t%d" n

let private freshLabel (env: ElabEnv) (prefix: string) : string =
    let n = env.LabelCounter.Value
    env.LabelCounter.Value <- n + 1
    sprintf "%s%d" prefix n

let rec elaborateExpr (env: ElabEnv) (expr: Expr) : MlirValue * MlirOp list =
    match expr with
    | Number (n, _) ->
        let v = { Name = freshName env; Type = I64 }
        (v, [ArithConstantOp(v, int64 n)])
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
    | Negate (inner, _) ->
        let (iv, iops) = elaborateExpr env inner
        let zero = { Name = freshName env; Type = I64 }
        let result = { Name = freshName env; Type = I64 }
        (result, iops @ [ArithConstantOp(zero, 0L); ArithSubIOp(result, zero, iv)])
    | Var (name, _) ->
        match Map.tryFind name env.Vars with
        | Some v -> (v, [])
        | None -> failwithf "Elaboration: unbound variable '%s'" name
    | Let (name, bindExpr, bodyExpr, _) ->
        let (bv, bops) = elaborateExpr env bindExpr
        let env' = { env with Vars = Map.add name bv env.Vars }
        let (rv, rops) = elaborateExpr env' bodyExpr
        (rv, bops @ rops)
    | Bool (b, _) ->
        let v = { Name = freshName env; Type = I1 }
        let n = if b then 1L else 0L
        (v, [ArithConstantOp(v, n)])
    | Equal (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
        let result = { Name = freshName env; Type = I1 }
        (result, lops @ rops @ [ArithCmpIOp(result, "eq", lv, rv)])
    | NotEqual (lhs, rhs, _) ->
        let (lv, lops) = elaborateExpr env lhs
        let (rv, rops) = elaborateExpr env rhs
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
        let sig_ : FuncSignature = { MlirName = "@" + name; ParamTypes = [I64]; ReturnType = I64 }
        let bodyEnv : ElabEnv =
            { Vars = Map.ofList [(param, { Name = "%arg0"; Type = I64 })]
              Counter = ref 0; LabelCounter = ref 0; Blocks = ref []
              KnownFuncs = Map.ofList [(name, sig_)]
              Funcs = env.Funcs }
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
              InputTypes = [I64]
              ReturnType = Some bodyVal.Type
              Body = { Blocks = allBodyBlocks }
              IsLlvmFunc = false }
        env.Funcs.Value <- env.Funcs.Value @ [funcOp]
        let env' = { env with KnownFuncs = Map.add name { sig_ with ReturnType = bodyVal.Type } env.KnownFuncs }
        elaborateExpr env' inExpr
    | App (funcExpr, argExpr, _) ->
        match funcExpr with
        | Var (name, _) ->
            match Map.tryFind name env.KnownFuncs with
            | Some sig_ ->
                let (argVal, argOps) = elaborateExpr env argExpr
                let result = { Name = freshName env; Type = sig_.ReturnType }
                (result, argOps @ [DirectCallOp(result, sig_.MlirName, [argVal])])
            | None ->
                failwithf "Elaboration: unsupported App — '%s' is not a known function in Phase 4" name
        | _ ->
            failwithf "Elaboration: unsupported App (only direct calls to known functions supported in Phase 4)"
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
    let mainFunc : FuncOp =
        { Name        = "@main"
          InputTypes  = []
          ReturnType  = Some resultVal.Type
          Body        = { Blocks = allBlocks }
          IsLlvmFunc  = false }
    { Funcs = env.Funcs.Value @ [mainFunc] }
