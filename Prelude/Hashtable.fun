module Hashtable =
    let create () = hashtable_create ()
    let get (ht : hashtable<'k, 'v>) (key : 'k) : 'v = hashtable_get ht key
    let set (ht : hashtable<'k, 'v>) (key : 'k) (value : 'v) : unit = hashtable_set ht key value
    let containsKey (ht : hashtable<'k, 'v>) (key : 'k) : bool = hashtable_containsKey ht key
    let keys (ht : hashtable<'k, 'v>) : 'k list = hashtable_keys ht
    let remove (ht : hashtable<'k, 'v>) (key : 'k) : unit = hashtable_remove ht key
    // Note: FunLang's tryGetValue returns (bool * 'v), but FunLangCompiler transforms it
    // into 'v option (Phase 100/Issue #23). Left without annotation to avoid type conflict.
    let tryGetValue ht key  = hashtable_trygetvalue ht key
    let count (ht : hashtable<'k, 'v>) : int = hashtable_count ht
