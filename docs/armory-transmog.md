# Optional realm transmogrification

Current feature details; start with [README](../README.md) for realm setup and [operations](operations.md) for cache troubleshooting.

Armory supports the additive mod-realm-armory transmog fields while retaining real equipped items in slots and tooltips. Applied names appear as “Transmogrified to: …”; explicitly hidden items show “Transmogrified to: Hidden”. Disabling rendering does not remove tooltip information.

The Settings checkbox “Show transmogrified appearances” is immediately below the hide-while-running setting. It defaults on and is saved separately for each realm's Armory URL. Unsupported/older feeds disable the checkbox; the enclosing row provides the hover text “This realm doesn’t support transmogrification.” Capability discovery runs when a realm is loaded and when Settings/Armory is opened. Changes also refresh an open Armory preview through its existing selection/cancellation path.

Both roster and profile must explicitly advertise capabilities.transmogrification=true before appearances affect the render. No capability means unsupported. Original data is never modified: a render-only projection replaces visual metadata or omits hidden equipment. Appearance inventory type and class are used for robes, armor and attachments. Unresolved visible appearances fall back gracefully rather than displaying guessed items.

Preview keys include capability, preference and appearance metadata/hidden state, in addition to original gear, character appearance and archive fingerprint. The cache format version changed. JSON caches are now scoped by realm URL to prevent one realm's cached capability/profile being used for another realm. Old unscoped JSON caches are not reused because their realm cannot be established; refresh once after upgrading. Item icon caching remains shared.

Not yet verified: a linked server build and live JSON publication end to end. Raw client assets are not included or modified.
