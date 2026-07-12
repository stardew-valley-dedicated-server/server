---
paths:
  - "mod/**/*.cs"
---

# A `[XmlIgnore]` field doesn't mean "not serialized" — check for a sibling serialized property

When deciding whether an SDV `Farmer`/save member round-trips to the save XML, an `[XmlIgnore]` attribute on the *field* is not the answer. The save uses a plain `XmlSerializer` (`SaveSerialization/SaveSerializer.cs`), which serializes every **public read/write property** that lacks `[XmlIgnore]`. A member is genuinely unserialized only when the property **and** its backing field are both `[XmlIgnore]` (or there's no public property at all). Before concluding a member "isn't persisted," grep for a sibling property of the same name and check *its* attributes.

**Why:** Reviewing the farm-importer plan, a subagent and my own first reading concluded `<UniqueMultiplayerID>` doesn't round-trip — because the `uniqueMultiplayerID` *field* is `[XmlIgnore]` (`Farmer.cs:337`). This nearly shipped a false "the feature's UID-stamp/validation is unbuildable" verdict. Direct inspection showed the `UniqueMultiplayerID` *property* (`Farmer.cs:1863`) is `public long { get; set; }` with **no** `[XmlIgnore]`, so the serializer emits `<UniqueMultiplayerID>` and the load-time setter overwrites the random field initializer — identity round-trips. The `[XmlIgnore]` on the field only suppresses a duplicate emission. Contrast `displayName`, which is genuinely unserialized: both the property (`Character.cs:290`) and its `_displayName` backing field (`Character.cs:143`) carry `[XmlIgnore]`; it's recomputed from `<name>` at runtime.

**How to apply:** When reasoning about what's in (or settable via) the save XML for any `Farmer`/`GameLocation`/save type, don't stop at the field's attribute. Grep the decompiled type for a same-named public property and confirm whether *it* is `[XmlIgnore]`. Treat "field is `[XmlIgnore]`" as inconclusive, not as proof of non-serialization. Per [`subagent-findings-are-claims.md`](universal/subagent-findings-are-claims.md), a subagent's "X isn't serialized" is exactly this shape of claim to verify against the property, not just the field.
