module MlirIR

// MLIR type system
type MlirType =
    | I64
    | I32
    | I1
    | Ptr   // Phase 5: represents !llvm.ptr (opaque pointer, LLVM 20 convention)

// SSA value — a named result from an operation
type MlirValue = {
    Name: string   // e.g. "%c42", "%0"
    Type: MlirType
}

// Module-level string constant (for print/println)
// name: MLIR global name e.g. "@__str_0"; value: raw content WITHOUT null terminator (Printer adds \00)
// Phase 65: MutablePtrGlobal — a global mutable !llvm.ptr variable initialized to null.
// Used to store template env pointers so LetRec body functions (separate func.funcs) can load them.
type MlirGlobal =
    | StringConstant    of name: string * value: string
    | MutablePtrGlobal  of name: string   // llvm.mlir.global internal @name : !llvm.ptr { null }

// External function declaration (for GC_init, GC_malloc, printf)
type ExternalFuncDecl = {
    ExtName:   string           // e.g. "@GC_init", "@GC_malloc", "@printf"
    ExtParams: MlirType list
    ExtReturn: MlirType option  // None = void
    IsVarArg:  bool             // true for printf
    Attrs:     string list      // Phase 19: extra MLIR attributes e.g. ["returns_twice"]
}

// Operations — one DU case per MLIR op
// Phase 1: arith.constant and func.return
// Phase 2: binary arith ops
// Phase 3: arith.cmpi, cf.cond_br, cf.br
// Phase 4: func.call (direct)
// Phase 5: LLVM-level ops for closure mechanics
// Phase 7: LlvmCallOp + LlvmCallVoidOp for GC and printf
// Phase 10: LlvmNullOp + LlvmIcmpOp for list null pointer and null checks
// Phase 11: LlvmUnreachableOp for noreturn terminator after match failure
// Future phases add cases here without changing MlirModule/FuncOp/Block/Region shape
type MlirOp =
    | ArithConstantOp of result: MlirValue * value: int64
    | ArithAddIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithSubIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithMulIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithDivSIOp    of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithRemSIOp    of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    // Phase 88: shift/or ops for tagged value representation (2n+1)
    | ArithShRSIOp    of result: MlirValue * lhs: MlirValue * rhs: MlirValue  // arith.shrsi (untag)
    | ArithShLIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue  // arith.shli (retag)
    | ArithOrIOp      of result: MlirValue * lhs: MlirValue * rhs: MlirValue  // arith.ori (set LSB)
    | ArithCmpIOp     of result: MlirValue * predicate: string * lhs: MlirValue * rhs: MlirValue
    | ArithExtuIOp    of result: MlirValue * value: MlirValue   // zero-extend (e.g. I1 -> I64)
    | CfCondBrOp      of cond: MlirValue * trueLabel: string * trueArgs: MlirValue list * falseLabel: string * falseArgs: MlirValue list
    | CfBrOp          of label: string * args: MlirValue list
    | DirectCallOp    of result: MlirValue * callee: string * args: MlirValue list
    // Phase 5: LLVM-level ops for closure representation
    | LlvmAllocaOp    of result: MlirValue * count: MlirValue * numCaptures: int
    | LlvmStoreOp     of value: MlirValue * ptr: MlirValue
    | LlvmLoadOp      of result: MlirValue * ptr: MlirValue
    | LlvmAddressOfOp of result: MlirValue * fnName: string
    | LlvmGEPLinearOp  of result: MlirValue * ptr: MlirValue * index: int
    | LlvmGEPStructOp  of result: MlirValue * ptr: MlirValue * fieldIndex: int
    | LlvmGEPDynamicOp of result: MlirValue * ptr: MlirValue * index: MlirValue
    | LlvmReturnOp    of operands: MlirValue list
    | IndirectCallOp  of result: MlirValue * fnPtr: MlirValue * envPtr: MlirValue * arg: MlirValue
    // Phase 7: GC/external calls
    | LlvmCallOp     of result: MlirValue * callee: string * args: MlirValue list
    | LlvmCallVoidOp of callee: string * args: MlirValue list
    // Phase 10: list null pointer and null checks
    | LlvmNullOp     of result: MlirValue
    // result.Type must be Ptr — emits: %result = llvm.mlir.zero : !llvm.ptr
    | LlvmIcmpOp     of result: MlirValue * predicate: string * lhs: MlirValue * rhs: MlirValue
    // result.Type must be I1 — emits: %result = llvm.icmp "pred" %lhs, %rhs : !llvm.ptr
    // predicate: "eq" (null check) or "ne" (non-null check); lhs and rhs must be Ptr typed
    // Phase 11: LlvmUnreachableOp for noreturn terminator after match failure
    | LlvmUnreachableOp
    // Phase 20: type coercions between i64 and !llvm.ptr (for first-class constructor closures)
    | LlvmIntToPtrOp of result: MlirValue * src: MlirValue
    // result.Type must be Ptr — emits: %result = llvm.inttoptr %src : i64 to !llvm.ptr
    | LlvmPtrToIntOp of result: MlirValue * src: MlirValue
    // result.Type must be I64 — emits: %result = llvm.ptrtoint %src : !llvm.ptr to i64
    // emits: llvm.unreachable — terminator after a noreturn void call (e.g. @lang_match_failure)
    | ReturnOp        of operands: MlirValue list

// A basic block: optional label, block arguments, sequence of ops
type MlirBlock = {
    Label: string option       // None for entry block
    Args:  MlirValue list      // block arguments (empty for entry block in Phase 1)
    Body:  MlirOp list
}

// A region: ordered list of basic blocks
type MlirRegion = {
    Blocks: MlirBlock list
}

// A func.func or llvm.func definition
type FuncOp = {
    Name:        string           // e.g. "@main" (include the @ sigil)
    InputTypes:  MlirType list    // [] for @main with no parameters
    ReturnType:  MlirType option  // Some I64; None means void (not needed in Phase 1)
    Body:        MlirRegion
    IsLlvmFunc:  bool             // Phase 5: true -> emit "llvm.func"; false -> emit "func.func"
}

// Top-level MLIR module
type MlirModule = {
    Globals:       MlirGlobal list        // Phase 7: string constants (emitted before funcs)
    ExternalFuncs: ExternalFuncDecl list  // Phase 7: llvm.func forward declarations
    Funcs:         FuncOp list
}

// Hardcoded return 42 — the Phase 1 end-to-end target
let return42Module : MlirModule =
    let c42 = { Name = "%c42"; Type = I64 }
    {
        Globals       = []
        ExternalFuncs = []
        Funcs = [
            {
                Name        = "@main"
                InputTypes  = []
                ReturnType  = Some I64
                IsLlvmFunc  = false
                Body = {
                    Blocks = [
                        {
                            Label = None
                            Args  = []
                            Body  = [
                                ArithConstantOp(c42, 42L)
                                ReturnOp [c42]
                            ]
                        }
                    ]
                }
            }
        ]
    }
