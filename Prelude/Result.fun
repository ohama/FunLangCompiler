module Result =
    type Result 'a 'b = Ok of 'a | Error of 'b
    let resultMap (f : 'a -> 'c) (r : result<'a, 'b>) : result<'c, 'b> = match r with | Ok x -> Ok (f x) | Error e -> Error e
    let resultBind (f : 'a -> result<'c, 'b>) (r : result<'a, 'b>) : result<'c, 'b> = match r with | Ok x -> f x | Error e -> Error e
    let resultMapError (f : 'b -> 'c) (r : result<'a, 'b>) : result<'a, 'c> = match r with | Ok x -> Ok x | Error e -> Error (f e)
    let resultDefault (def : 'a) (r : result<'a, 'b>) : 'a = match r with | Ok x -> x | Error _ -> def
    let isOk (r : result<'a, 'b>) : bool = match r with | Ok _ -> true | Error _ -> false
    let isError (r : result<'a, 'b>) : bool = match r with | Ok _ -> false | Error _ -> true
    let resultIter (f : 'a -> unit) (r : result<'a, 'b>) : unit = match r with | Ok x -> f x | Error _ -> ()
    let resultToOption (r : result<'a, 'b>) : 'a option = match r with | Ok x -> Some x | Error _ -> None
    let resultDefaultValue (def : 'a) (r : result<'a, 'b>) : 'a = match r with | Ok x -> x | Error _ -> def

open Result
