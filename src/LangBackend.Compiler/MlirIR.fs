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

// Operations — one DU case per MLIR op
// Phase 1: arith.constant and func.return
// Phase 2: binary arith ops
// Phase 3: arith.cmpi, cf.cond_br, cf.br
// Phase 4: func.call (direct)
// Phase 5: LLVM-level ops for closure mechanics
// Future phases add cases here without changing MlirModule/FuncOp/Block/Region shape
type MlirOp =
    | ArithConstantOp of result: MlirValue * value: int64
    | ArithAddIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithSubIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithMulIOp     of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithDivSIOp    of result: MlirValue * lhs: MlirValue * rhs: MlirValue
    | ArithCmpIOp     of result: MlirValue * predicate: string * lhs: MlirValue * rhs: MlirValue
    | CfCondBrOp      of cond: MlirValue * trueLabel: string * trueArgs: MlirValue list * falseLabel: string * falseArgs: MlirValue list
    | CfBrOp          of label: string * args: MlirValue list
    | DirectCallOp    of result: MlirValue * callee: string * args: MlirValue list
    // Phase 5: LLVM-level ops for closure representation
    | LlvmAllocaOp    of result: MlirValue * count: MlirValue * numCaptures: int
    | LlvmStoreOp     of value: MlirValue * ptr: MlirValue
    | LlvmLoadOp      of result: MlirValue * ptr: MlirValue
    | LlvmAddressOfOp of result: MlirValue * fnName: string
    | LlvmGEPLinearOp of result: MlirValue * ptr: MlirValue * index: int
    | LlvmReturnOp    of operands: MlirValue list
    | IndirectCallOp  of result: MlirValue * fnPtr: MlirValue * envPtr: MlirValue * arg: MlirValue
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
    Funcs: FuncOp list
}

// Hardcoded return 42 — the Phase 1 end-to-end target
let return42Module : MlirModule =
    let c42 = { Name = "%c42"; Type = I64 }
    {
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
