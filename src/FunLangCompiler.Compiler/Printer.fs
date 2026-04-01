module Printer

open MlirIR

/// Sanitize operator characters in MLIR symbol names (e.g., @List_++ → @List_op_pp).
/// Only affects the part after '@'; leaves names without operator chars unchanged.
let private sanitizeMlirName (name: string) : string =
    if not (name.StartsWith("@")) then name
    else
        let body = name.Substring(1)
        let hasOpChar = body |> Seq.exists (fun c -> not (System.Char.IsLetterOrDigit c) && c <> '_' && c <> '.')
        if not hasOpChar then name
        else
            let sb = System.Text.StringBuilder("@")
            for c in body do
                match c with
                | '+' -> sb.Append("_plus_") |> ignore
                | '^' -> sb.Append("_caret_") |> ignore
                | '|' -> sb.Append("_pipe_") |> ignore
                | '>' -> sb.Append("_gt_") |> ignore
                | '<' -> sb.Append("_lt_") |> ignore
                | '=' -> sb.Append("_eq_") |> ignore
                | '!' -> sb.Append("_bang_") |> ignore
                | '&' -> sb.Append("_amp_") |> ignore
                | '$' -> sb.Append("_dollar_") |> ignore
                | '@' -> sb.Append("_at_") |> ignore
                | '*' -> sb.Append("_star_") |> ignore
                | '/' -> sb.Append("_slash_") |> ignore
                | '%' -> sb.Append("_pct_") |> ignore
                | '-' -> sb.Append("_minus_") |> ignore
                | '.' -> sb.Append("_dot_") |> ignore
                | _ -> sb.Append(c) |> ignore
            sb.ToString()

let private printType = function
    | I64 -> "i64"
    | I32 -> "i32"
    | I1  -> "i1"
    | Ptr -> "!llvm.ptr"

let private printGlobal (g: MlirGlobal) : string =
    match g with
    | StringConstant(name, value) ->
        let escaped = value.Replace("\\", "\\5C").Replace("\n", "\\0A").Replace("\"", "\\22") + "\\00"
        sprintf "  llvm.mlir.global internal constant %s(\"%s\") {addr_space = 0 : i32}" name escaped

let private printExternalDecl (d: ExternalFuncDecl) : string =
    let paramStr = d.ExtParams |> List.map printType |> String.concat ", "
    let varargSuffix = if d.IsVarArg then ", ..." else ""
    let retStr =
        match d.ExtReturn with
        | None -> ""
        | Some t -> sprintf " -> %s" (printType t)
    let attrsStr =
        if d.Attrs.IsEmpty then ""
        else sprintf " attributes {%s}" (d.Attrs |> String.concat ", ")
    sprintf "  llvm.func %s(%s%s)%s%s" d.ExtName paramStr varargSuffix retStr attrsStr

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
    | ArithRemSIOp(result, lhs, rhs) ->
        sprintf "%s%s = arith.remsi %s, %s : %s"
            indent result.Name lhs.Name rhs.Name (printType result.Type)
    | ArithCmpIOp(result, predicate, lhs, rhs) ->
        sprintf "%s%s = arith.cmpi %s, %s, %s : %s"
            indent result.Name predicate lhs.Name rhs.Name (printType lhs.Type)
    | ArithExtuIOp(result, value) ->
        sprintf "%s%s = arith.extui %s : %s to %s"
            indent result.Name value.Name (printType value.Type) (printType result.Type)
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
            indent result.Name (sanitizeMlirName callee) argNames argTypes (printType result.Type)
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
            indent result.Name (sanitizeMlirName fnName)
    | LlvmGEPLinearOp(result, ptr, index) ->
        sprintf "%s%s = llvm.getelementptr %s[%d] : (!llvm.ptr) -> !llvm.ptr, i64"
            indent result.Name ptr.Name index
    | LlvmGEPStructOp(result, ptr, fieldIndex) ->
        sprintf "%s%s = llvm.getelementptr inbounds %s[0, %d] : (!llvm.ptr) -> !llvm.ptr, !llvm.struct<(i64, ptr)>"
            indent result.Name ptr.Name fieldIndex
    | LlvmGEPDynamicOp(result, ptr, index) ->
        sprintf "%s%s = llvm.getelementptr %s[%s] : (!llvm.ptr, i64) -> !llvm.ptr, i64"
            indent result.Name ptr.Name index.Name
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
    // Phase 7: GC/external calls
    | LlvmCallOp(result, callee, args) ->
        let argList = args |> List.map (fun v -> v.Name) |> String.concat ", "
        let argTypes = args |> List.map (fun v -> printType v.Type) |> String.concat ", "
        let sc = sanitizeMlirName callee
        if callee = "@printf" then
            sprintf "%s%s = llvm.call %s(%s) vararg(!llvm.func<i32 (ptr, ...)>) : (%s) -> %s"
                indent result.Name sc argList argTypes (printType result.Type)
        else
            sprintf "%s%s = llvm.call %s(%s) : (%s) -> %s"
                indent result.Name sc argList argTypes (printType result.Type)
    | LlvmCallVoidOp(callee, args) ->
        let sc = sanitizeMlirName callee
        if args.IsEmpty then
            sprintf "%sllvm.call %s() : () -> ()" indent sc
        else
            let argList = args |> List.map (fun v -> v.Name) |> String.concat ", "
            let argTypes = args |> List.map (fun v -> printType v.Type) |> String.concat ", "
            sprintf "%sllvm.call %s(%s) : (%s) -> ()" indent sc argList argTypes
    // Phase 10: list null pointer and null checks
    | LlvmNullOp(result) ->
        sprintf "%s%s = llvm.mlir.zero : !llvm.ptr" indent result.Name
    | LlvmIcmpOp(result, predicate, lhs, rhs) ->
        sprintf "%s%s = llvm.icmp \"%s\" %s, %s : !llvm.ptr"
            indent result.Name predicate lhs.Name rhs.Name
    // Phase 11: noreturn terminator after match failure
    | LlvmUnreachableOp ->
        sprintf "%sllvm.unreachable" indent
    // Phase 20: type coercions between i64 and !llvm.ptr
    | LlvmIntToPtrOp(result, src) ->
        sprintf "%s%s = llvm.inttoptr %s : i64 to !llvm.ptr" indent result.Name src.Name
    | LlvmPtrToIntOp(result, src) ->
        sprintf "%s%s = llvm.ptrtoint %s : !llvm.ptr to i64" indent result.Name src.Name
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
    sprintf "  %s %s%s%s {\n%s\n  }" keyword (sanitizeMlirName func.Name) paramStr retStr bodyLines

/// Serialize an MlirModule to valid MLIR 20 text.
/// Pure function — no I/O. Caller is responsible for writing to disk.
/// Output order: globals, then external func decls, then func definitions (required by MLIR 20).
let printModule (m: MlirModule) : string =
    let globalTexts = m.Globals |> List.map printGlobal |> String.concat "\n"
    let externTexts = m.ExternalFuncs |> List.map printExternalDecl |> String.concat "\n"
    let funcTexts   = m.Funcs |> List.map printFuncOp |> String.concat "\n"
    let sections =
        [ if globalTexts <> "" then globalTexts
          if externTexts <> "" then externTexts
          funcTexts ]
    sprintf "module {\n%s\n}" (sections |> String.concat "\n")
