module String =
    let concat sep lst = string_concat_list sep lst
    let endsWith s suffix = string_endswith s suffix
    let startsWith s prefix = string_startswith s prefix
    let trim s = string_trim s
    let length s = string_length s
    let contains s needle = string_contains s needle
