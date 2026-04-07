module Core =
    let id x = x
    let const x = fun y -> x
    let compose f = fun g -> fun x -> f (g x)
    let flip f = fun x -> fun y -> f y x
    let apply f = fun x -> f x
    let (^^) a b = string_concat a b
    #[left 1]
    let (|>) __pipe_x __pipe_f = __pipe_f __pipe_x
    #[right 2]
    let (>>) __comp_lhs __comp_rhs = fun __comp_x -> __comp_rhs (__comp_lhs __comp_x)
    #[left 2]
    let (<<) __comp_lhs __comp_rhs = fun __comp_x -> __comp_lhs (__comp_rhs __comp_x)
    #[right 1]
    let (<|) __pipe_f __pipe_x = __pipe_f __pipe_x
    let not x = if x then false else true
    let min a = fun b -> if a < b then a else b
    let max a = fun b -> if a > b then a else b
    let abs x = if x < 0 then 0 - x else x
    let fst p = match p with | (a, _) -> a
    let snd p = match p with | (_, b) -> b
    let ignore x = ()
    let char_to_int c = c
    let int_to_char n = n

open Core
