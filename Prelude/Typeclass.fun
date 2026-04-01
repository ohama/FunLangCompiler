typeclass Show 'a =
    | show : 'a -> string

instance Show int =
    let show x = to_string x

instance Show bool =
    let show x = if x then "true" else "false"

instance Show string =
    let show x = x

instance Show char =
    let show x = to_string x

typeclass Eq 'a =
    | eq : 'a -> 'a -> bool

instance Eq int =
    let eq x = fun y -> x = y

instance Eq bool =
    let eq x = fun y -> x = y

instance Eq string =
    let eq x = fun y -> x = y

instance Eq char =
    let eq x = fun y -> x = y
