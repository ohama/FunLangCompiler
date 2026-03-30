module List =
    let rec map f = fun xs -> match xs with | [] -> [] | h :: t -> f h :: map f t
    let rec filter pred = fun xs -> match xs with | [] -> [] | h :: t -> if pred h then h :: filter pred t else filter pred t
    let rec fold f = fun acc -> fun xs -> match xs with | [] -> acc | h :: t -> fold f (f acc h) t
    let rec length xs = match xs with | [] -> 0 | _ :: t -> 1 + length t
    let rec reverse acc = fun xs -> match xs with | [] -> acc | h :: t -> reverse (h :: acc) t
    let rec append xs = fun ys -> match xs with | [] -> ys | h :: t -> h :: append t ys
    let hd xs = match xs with | h :: _ -> h
    let tl xs = match xs with | _ :: t -> t
    let rec zip xs = fun ys -> match xs with | [] -> [] | x :: xt -> match ys with | [] -> [] | y :: yt -> (x, y) :: zip xt yt
    let rec take n = fun xs -> if n = 0 then [] else match xs with | [] -> [] | h :: t -> h :: take (n - 1) t
    let rec drop n = fun xs -> if n = 0 then xs else match xs with | [] -> [] | _ :: t -> drop (n - 1) t
    let rec any pred = fun xs -> match xs with | [] -> false | h :: t -> if pred h then true else any pred t
    let rec all pred = fun xs -> match xs with | [] -> true | h :: t -> if pred h then all pred t else false
    let rec flatten xss = match xss with | [] -> [] | xs :: rest -> append xs (flatten rest)
    let rec nth n = fun xs -> match xs with | h :: t -> if n = 0 then h else nth (n - 1) t
    let (++) xs ys = append xs ys
    let head xs = hd xs
    let tail xs = tl xs
    let exists pred xs = any pred xs
    let item n xs = nth n xs
    let isEmpty xs = match xs with | [] -> true | _ -> false
    let rec _insert x xs =
        match xs with
        | [] -> [x]
        | h :: t -> if x < h then x :: h :: t else h :: _insert x t
    let rec sort xs =
        match xs with
        | [] -> []
        | h :: t -> _insert h (sort t)
    let sortBy f xs = list_sort_by f xs
    let rec _mapi_helper f i xs =
        match xs with
        | [] -> []
        | h :: t -> f i h :: _mapi_helper f (i + 1) t
    let mapi f xs = _mapi_helper f 0 xs
    let rec tryFind pred xs =
        match xs with
        | [] -> None
        | h :: t -> if pred h then Some h else tryFind pred t
    let rec choose f xs =
        match xs with
        | [] -> []
        | h :: t ->
            match f h with
            | Some v -> v :: choose f t
            | None -> choose f t
    let rec _distinctBy_helper f seen xs =
        match xs with
        | [] -> []
        | h :: t ->
            let key = f h
            if any (fun k -> k = key) seen
            then _distinctBy_helper f seen t
            else h :: _distinctBy_helper f (key :: seen) t
    let distinctBy f xs = _distinctBy_helper f [] xs
    let ofSeq coll = list_of_seq coll

open List
