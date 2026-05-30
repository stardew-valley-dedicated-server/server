---
paths:
  - "mod/JunimoServer/**"
---

# Mutate `NetDictionary` via its public API, not `FieldDict`

When mutating a `NetDictionary` (add, remove, set), use the public methods (`Remove(key)`, `Add(key, value)`, indexer assignment). Don't reach into `FieldDict.Remove`, `FieldDict.Add`, or any other underlying-dict mutation. Reads (`ContainsKey`, `TryGetValue`, iteration, `Count`, `Values`) via `FieldDict` are fine — the trap is mutation-only.

**Why:** `NetDictionary.Remove(key)` (`Netcode/NetDictionary.cs:622-633`) calls `removed(key, value, reassign)` which (1) queues an outgoing replication delta, (2) clears the field's parent reference via `clearFieldParent`, (3) fires `OnValueRemoved`. `FieldDict.Remove` is `Dictionary<TKey, TValue>.Remove` and does none of these — peers never learn about the removal, the field's parent stays dangling, and `OnValueRemoved` subscribers don't fire. `ApiService.ExecuteFarmhandDeletion`'s fallback path silently desynced clients for an unknown duration before this was caught.

**How to apply:** Before writing any `.FieldDict.<Mutating>` call against a `NetDictionary`, drop the `.FieldDict` and use the wrapper directly. Audit by greping for `\.FieldDict\.(Remove|Add|Clear)\b` — any hit is suspect. Read-only access via `FieldDict` is intentional and stays.
