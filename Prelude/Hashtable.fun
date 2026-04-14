module Hashtable =
    let create () = hashtable_create ()
    let get (ht : hashtable<'k, 'v>) (key : 'k) : 'v = hashtable_get ht key
    let set (ht : hashtable<'k, 'v>) (key : 'k) (value : 'v) : unit = hashtable_set ht key value
    let containsKey (ht : hashtable<'k, 'v>) (key : 'k) : bool = hashtable_containsKey ht key
    let keys (ht : hashtable<'k, 'v>) : 'k list = hashtable_keys ht
    let remove (ht : hashtable<'k, 'v>) (key : 'k) : unit = hashtable_remove ht key
    // FunLang#28 (v0.1.8): builtin scheme unified to `'v option` — matches
    // FunLangCompiler's runtime (Phase 100/Issue #23). Safe to annotate now.
    let tryGetValue (ht : hashtable<'k, 'v>) (key : 'k) : 'v option = hashtable_trygetvalue ht key
    let count (ht : hashtable<'k, 'v>) : int = hashtable_count ht
