module Hashtable =
    // Int-key hashtable
    let create ()           = hashtable_create ()
    let get ht key          = hashtable_get ht key
    let set ht key value    = hashtable_set ht key value
    let containsKey ht key  = hashtable_containsKey ht key
    let keys ht             = hashtable_keys ht
    let remove ht key       = hashtable_remove ht key
    let tryGetValue ht key  = hashtable_trygetvalue ht key
    let count ht            = hashtable_count ht
    // String-key hashtable
    let createStr ()            = hashtable_create_str ()
    let getStr ht key           = hashtable_get_str ht key
    let setStr ht key value     = hashtable_set_str ht key value
    let containsKeyStr ht key   = hashtable_containsKey_str ht key
    let keysStr ht              = hashtable_keys_str ht
    let removeStr ht key        = hashtable_remove_str ht key
    let tryGetValueStr ht key   = hashtable_trygetvalue_str ht key
