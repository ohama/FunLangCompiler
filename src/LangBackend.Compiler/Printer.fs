module Printer

open MlirIR

let private printType = function
    | I64 -> "i64"
    | I32 -> "i32"
    | I1  -> "i1"

let private printOp (indent: string) (op: MlirOp) : string =
    match op with
    | ArithConstantOp(result, value) ->
        sprintf "%s%s = arith.constant %d : %s"
            indent result.Name value (printType result.Type)
    | ArithAddIOp(result, lhs, rhs) ->
        sprintf "%s%s = arith.addi %s, %s : %s"
            indent result.Name lhs.Name rhs.Name (printType result.Type)
    | ArithSubIOp(result, lhs, rhs) ->
        sprintf "%s%s = arith.subi %s, %s : %s"
            indent result.Name lhs.Name rhs.Name (printType result.Type)
    | ArithMulIOp(result, lhs, rhs) ->
        sprintf "%s%s = arith.muli %s, %s : %s"
            indent result.Name lhs.Name rhs.Name (printType result.Type)
    | ArithDivSIOp(result, lhs, rhs) ->
        sprintf "%s%s = arith.divsi %s, %s : %s"
            indent result.Name lhs.Name rhs.Name (printType result.Type)
    | ArithCmpIOp(result, predicate, lhs, rhs) ->
        sprintf "%s%s = arith.cmpi %s, %s, %s : %s"
            indent result.Name predicate lhs.Name rhs.Name (printType lhs.Type)
    | CfCondBrOp(cond, trueLabel, trueArgs, falseLabel, falseArgs) ->
        let fmtArgs (args: MlirValue list) =
            if List.isEmpty args then ""
            else
                let inner = args |> List.map (fun (v: MlirValue) -> sprintf "%s : %s" v.Name (printType v.Type)) |> String.concat ", "
                sprintf "(%s)" inner
        sprintf "%scf.cond_br %s, ^%s%s, ^%s%s"
            indent cond.Name trueLabel (fmtArgs trueArgs) falseLabel (fmtArgs falseArgs)
    | CfBrOp(label, args) ->
        let fmtArgs (args: MlirValue list) =
            if List.isEmpty args then ""
            else
                let inner = args |> List.map (fun (v: MlirValue) -> sprintf "%s : %s" v.Name (printType v.Type)) |> String.concat ", "
                sprintf "(%s)" inner
        sprintf "%scf.br ^%s%s" indent label (fmtArgs args)
    | ReturnOp [] ->
        sprintf "%sreturn" indent
    | ReturnOp operands ->
        let vals =
            operands
            |> List.map (fun v -> sprintf "%s : %s" v.Name (printType v.Type))
            |> String.concat ", "
        sprintf "%sreturn %s" indent vals

let private printBlock (indent: string) (block: MlirBlock) : string =
    let labelLine =
        match block.Label with
        | None -> ""
        | Some lbl ->
            let argStr =
                if block.Args.IsEmpty then ""
                else
                    let args = block.Args |> List.map (fun v -> sprintf "%s: %s" v.Name (printType v.Type)) |> String.concat ", "
                    sprintf "(%s)" args
            sprintf "%s^%s%s:\n" indent lbl argStr
    let opLines = block.Body |> List.map (printOp (indent + "  ")) |> String.concat "\n"
    labelLine + opLines

let private printFuncOp (func: FuncOp) : string =
    let retStr =
        match func.ReturnType with
        | None   -> ""
        | Some t -> sprintf " -> %s" (printType t)
    let paramStr =
        if func.InputTypes.IsEmpty then "()"
        else
            let ps = func.InputTypes |> List.mapi (fun i t -> sprintf "%%arg%d: %s" i (printType t)) |> String.concat ", "
            sprintf "(%s)" ps
    let bodyLines = func.Body.Blocks |> List.map (printBlock "    ") |> String.concat "\n"
    sprintf "  func.func %s%s%s {\n%s\n  }" func.Name paramStr retStr bodyLines

/// Serialize an MlirModule to valid MLIR 20 text.
/// Pure function — no I/O. Caller is responsible for writing to disk.
let printModule (m: MlirModule) : string =
    let funcTexts = m.Funcs |> List.map printFuncOp |> String.concat "\n"
    sprintf "module {\n%s\n}" funcTexts
