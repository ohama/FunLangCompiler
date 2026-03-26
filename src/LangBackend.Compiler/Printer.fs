module Printer

open MlirIR

let private printType = function
    | I64 -> "i64"
    | I32 -> "i32"
    | I1  -> "i1"
    | Ptr -> "!llvm.ptr"

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
    | DirectCallOp(result, callee, args) ->
        let argNames = args |> List.map (fun (v: MlirValue) -> v.Name) |> String.concat ", "
        let argTypes = args |> List.map (fun (v: MlirValue) -> printType v.Type) |> String.concat ", "
        sprintf "%s%s = func.call %s(%s) : (%s) -> %s"
            indent result.Name callee argNames argTypes (printType result.Type)
    // Phase 5: LLVM-level ops for closure mechanics
    | LlvmAllocaOp(result, count, numCaptures) ->
        let fields =
            if numCaptures = 0 then "ptr"
            else "ptr" + String.concat "" (List.replicate numCaptures ", i64")
        sprintf "%s%s = llvm.alloca %s x !llvm.struct<(%s)> : (i64) -> !llvm.ptr"
            indent result.Name count.Name fields
    | LlvmStoreOp(value, ptr) ->
        sprintf "%sllvm.store %s, %s : %s, !llvm.ptr"
            indent value.Name ptr.Name (printType value.Type)
    | LlvmLoadOp(result, ptr) ->
        sprintf "%s%s = llvm.load %s : !llvm.ptr -> %s"
            indent result.Name ptr.Name (printType result.Type)
    | LlvmAddressOfOp(result, fnName) ->
        sprintf "%s%s = llvm.mlir.addressof %s : !llvm.ptr"
            indent result.Name fnName
    | LlvmGEPLinearOp(result, ptr, index) ->
        sprintf "%s%s = llvm.getelementptr %s[%d] : (!llvm.ptr) -> !llvm.ptr, i64"
            indent result.Name ptr.Name index
    | LlvmReturnOp [] ->
        sprintf "%sllvm.return" indent
    | LlvmReturnOp operands ->
        let vals =
            operands
            |> List.map (fun v -> sprintf "%s : %s" v.Name (printType v.Type))
            |> String.concat ", "
        sprintf "%sllvm.return %s" indent vals
    | IndirectCallOp(result, fnPtr, envPtr, arg) ->
        sprintf "%s%s = llvm.call %s(%s, %s) : !llvm.ptr, (!llvm.ptr, %s) -> %s"
            indent result.Name fnPtr.Name envPtr.Name arg.Name (printType arg.Type) (printType result.Type)
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
    let keyword = if func.IsLlvmFunc then "llvm.func" else "func.func"
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
    sprintf "  %s %s%s%s {\n%s\n  }" keyword func.Name paramStr retStr bodyLines

/// Serialize an MlirModule to valid MLIR 20 text.
/// Pure function — no I/O. Caller is responsible for writing to disk.
let printModule (m: MlirModule) : string =
    let funcTexts = m.Funcs |> List.map printFuncOp |> String.concat "\n"
    sprintf "module {\n%s\n}" funcTexts
