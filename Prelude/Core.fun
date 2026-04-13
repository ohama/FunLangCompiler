module Core =
    let id (x : 'a) : 'a = x
    let const (x : 'a) (y : 'b) : 'a = x
    let compose (f : 'b -> 'c) (g : 'a -> 'b) (x : 'a) : 'c = f (g x)
    let flip (f : 'a -> 'b -> 'c) (x : 'b) (y : 'a) : 'c = f y x
    let apply (f : 'a -> 'b) (x : 'a) : 'b = f x
    let (^^) (a : string) (b : string) : string = string_concat a b
    #[left 1]
    let (|>) (x : 'a) (f : 'a -> 'b) : 'b = f x
    #[right 2]
    let (>>) (f : 'a -> 'b) (g : 'b -> 'c) (x : 'a) : 'c = g (f x)
    #[left 2]
    let (<<) (f : 'b -> 'c) (g : 'a -> 'b) (x : 'a) : 'c = f (g x)
    #[right 1]
    let (<|) (f : 'a -> 'b) (x : 'a) : 'b = f x
    let not (x : bool) : bool = if x then false else true
    let min (a : 'a) (b : 'a) : 'a = if a < b then a else b
    let max (a : 'a) (b : 'a) : 'a = if a > b then a else b
    let abs (x : int) : int = if x < 0 then 0 - x else x
    let fst (p : 'a * 'b) : 'a = match p with | (a, _) -> a
    let snd (p : 'a * 'b) : 'b = match p with | (_, b) -> b
    let ignore (x : 'a) : unit = ()

open Core
