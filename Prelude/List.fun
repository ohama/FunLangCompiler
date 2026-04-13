module List =
    let rec map (f : 'a -> 'b) (xs : 'a list) : 'b list = match xs with | [] -> [] | h :: t -> f h :: map f t
    let rec filter (pred : 'a -> bool) (xs : 'a list) : 'a list = match xs with | [] -> [] | h :: t -> if pred h then h :: filter pred t else filter pred t
    let rec fold (f : 'a -> 'b -> 'a) (acc : 'a) (xs : 'b list) : 'a = match xs with | [] -> acc | h :: t -> fold f (f acc h) t
    let rec length (xs : 'a list) : int = match xs with | [] -> 0 | _ :: t -> 1 + length t
    let rec reverse (acc : 'a list) (xs : 'a list) : 'a list = match xs with | [] -> acc | h :: t -> reverse (h :: acc) t
    let rec append (xs : 'a list) (ys : 'a list) : 'a list = match xs with | [] -> ys | h :: t -> h :: append t ys
    let hd (xs : 'a list) : 'a = match xs with | h :: _ -> h
    let tl (xs : 'a list) : 'a list = match xs with | _ :: t -> t
    let rec zip (xs : 'a list) (ys : 'b list) : ('a * 'b) list = match xs with | [] -> [] | x :: xt -> match ys with | [] -> [] | y :: yt -> (x, y) :: zip xt yt
    let rec take (n : int) (xs : 'a list) : 'a list = if n = 0 then [] else match xs with | [] -> [] | h :: t -> h :: take (n - 1) t
    let rec drop (n : int) (xs : 'a list) : 'a list = if n = 0 then xs else match xs with | [] -> [] | _ :: t -> drop (n - 1) t
    let rec any (pred : 'a -> bool) (xs : 'a list) : bool = match xs with | [] -> false | h :: t -> if pred h then true else any pred t
    let rec all (pred : 'a -> bool) (xs : 'a list) : bool = match xs with | [] -> true | h :: t -> if pred h then all pred t else false
    let rec flatten (xss : 'a list list) : 'a list = match xss with | [] -> [] | xs :: rest -> append xs (flatten rest)
    let rec nth (n : int) (xs : 'a list) : 'a = match xs with | h :: t -> if n = 0 then h else nth (n - 1) t
    let (++) (xs : 'a list) (ys : 'a list) : 'a list = append xs ys
    let head (xs : 'a list) : 'a = hd xs
    let tail (xs : 'a list) : 'a list = tl xs
    let exists (pred : 'a -> bool) (xs : 'a list) : bool = any pred xs
    let item (n : int) (xs : 'a list) : 'a = nth n xs
    let isEmpty (xs : 'a list) : bool = match xs with | [] -> true | _ -> false
    let rec _insert (x : 'a) (xs : 'a list) : 'a list =
        match xs with
        | [] -> [x]
        | h :: t -> if x < h then x :: h :: t else h :: _insert x t
    let rec sort (xs : 'a list) : 'a list =
        match xs with
        | [] -> []
        | h :: t -> _insert h (sort t)
    let sortBy (f : 'a -> 'b) (xs : 'a list) : 'a list = list_sort_by f xs
    let rec _mapi_helper (f : int -> 'a -> 'b) (i : int) (xs : 'a list) : 'b list =
        match xs with
        | [] -> []
        | h :: t -> f i h :: _mapi_helper f (i + 1) t
    let mapi (f : int -> 'a -> 'b) (xs : 'a list) : 'b list = _mapi_helper f 0 xs
    let rec tryFind (pred : 'a -> bool) (xs : 'a list) : 'a option =
        match xs with
        | [] -> None
        | h :: t -> if pred h then Some h else tryFind pred t
    let rec choose (f : 'a -> 'b option) (xs : 'a list) : 'b list =
        match xs with
        | [] -> []
        | h :: t ->
            match f h with
            | Some v -> v :: choose f t
            | None -> choose f t
    let rec _distinctBy_helper (f : 'a -> 'b) (seen : 'b list) (xs : 'a list) : 'a list =
        match xs with
        | [] -> []
        | h :: t ->
            let key = f h
            if any (fun k -> k = key) seen
            then _distinctBy_helper f seen t
            else h :: _distinctBy_helper f (key :: seen) t
    let distinctBy (f : 'a -> 'b) (xs : 'a list) : 'a list = _distinctBy_helper f [] xs
    let ofSeq coll = list_of_seq coll
    // v13.0: New List functions
    let rec init (n : int) (f : int -> 'a) : 'a list =
        if n = 0 then []
        else
            let rec _init_helper i =
                if i = n then []
                else f i :: _init_helper (i + 1)
            _init_helper 0
    let find (pred : 'a -> bool) (xs : 'a list) : 'a =
        match tryFind pred xs with
        | Some v -> v
        | None -> failwith "List.find: no element satisfies the predicate"
    let rec _findIndex_helper (pred : 'a -> bool) (i : int) (xs : 'a list) : int =
        match xs with
        | [] -> 0 - 1
        | h :: t -> if pred h then i else _findIndex_helper pred (i + 1) t
    let findIndex (pred : 'a -> bool) (xs : 'a list) : int = _findIndex_helper pred 0 xs
    let partition (pred : 'a -> bool) (xs : 'a list) : 'a list * 'a list =
        let rec go yes no = fun xs ->
            match xs with
            | [] -> (reverse [] yes, reverse [] no)
            | h :: t -> if pred h then go (h :: yes) no t else go yes (h :: no) t
        go [] [] xs
    let rec groupBy (f : 'a -> 'b) (xs : 'a list) : ('b * 'a list) list =
        match xs with
        | [] -> []
        | h :: _ ->
            let key = f h
            let (matches, rest) = partition (fun x -> f x = key) xs
            (key, matches) :: groupBy f rest
    let rec scan (f : 'a -> 'b -> 'a) (acc : 'a) (xs : 'b list) : 'a list =
        acc :: (match xs with
               | [] -> []
               | h :: t -> scan f (f acc h) t)
    let replicate (n : int) (x : 'a) : 'a list = init n (fun _i -> x)
    let collect (f : 'a -> 'b list) (xs : 'a list) : 'b list = flatten (map f xs)
    let rec pairwise (xs : 'a list) : ('a * 'a) list =
        match xs with
        | a :: b :: rest -> (a, b) :: pairwise (b :: rest)
        | _ -> []
    let sumBy (f : 'a -> int) (xs : 'a list) : int = fold (fun acc -> fun x -> acc + f x) 0 xs
    let sum (xs : int list) : int = fold (fun acc -> fun x -> acc + x) 0 xs
    let minBy (f : 'a -> int) (xs : 'a list) : 'a =
        match xs with
        | [] -> failwith "List.minBy: empty list"
        | h :: t -> fold (fun best -> fun x -> if f x < f best then x else best) h t
    let maxBy (f : 'a -> int) (xs : 'a list) : 'a =
        match xs with
        | [] -> failwith "List.maxBy: empty list"
        | h :: t -> fold (fun best -> fun x -> if f x > f best then x else best) h t
    let contains (x : 'a) (xs : 'a list) : bool = any (fun y -> y = x) xs
    let unzip (xs : ('a * 'b) list) : 'a list * 'b list = (map fst xs, map snd xs)
    let forall (pred : 'a -> bool) (xs : 'a list) : bool = all pred xs
    let iter (f : 'a -> unit) (xs : 'a list) : unit = fold (fun _u -> fun x -> f x) () xs

open List
