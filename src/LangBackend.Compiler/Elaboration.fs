module Elaboration

open Ast
open MlirIR

type ElabEnv = {
    Vars:    Map<string, MlirValue>
    Counter: int ref
}

let emptyEnv () : ElabEnv =
    { Vars = Map.empty; Counter = ref 0 }

let private freshName (env: ElabEnv) : string =
    let n = env.Counter.Value
    env.Counter.Value <- n + 1
    sprintf "%%t%d" n

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
    | _ ->
        failwithf "Elaboration: unsupported expression %A" expr

let elaborateModule (expr: Expr) : MlirModule =
    let env = emptyEnv ()
    let (resultVal, ops) = elaborateExpr env expr
    {
        Funcs = [
            {
                Name       = "@main"
                InputTypes = []
                ReturnType = Some I64
                Body = {
                    Blocks = [
                        {
                            Label = None
                            Args  = []
                            Body  = ops @ [ReturnOp [resultVal]]
                        }
                    ]
                }
            }
        ]
    }
