# Isolated realm runtimes

A realm can ask for its own client directory instead of sharing the player's WoW folder. This is opt-in and not used by the built-in realm, which runs with `RuntimeMode=Legacy`.

```ini
[Client]
RuntimeMode=Isolated
Requirements=protected-framexml
```

`RuntimeMode` accepts `Legacy` (the default when omitted) or `Isolated`, case-insensitively; an empty, numeric or unknown value is an error. `Requirements` is described below. See [config/example.realm.conf](../config/example.realm.conf).

## What changes for an isolated realm

- `Settings.ClientPath` is always the player's **source** client. The runtime is never saved as the client path, and a legacy realm keeps resolving to the source. `RealmRuntimeResolver.ResolveEffectiveClientPath` is the one place that decides which root is used.
- ENTER REALM prepares the runtime on first use in the background (validate the source, build the runtime, install the realm's required patches and addons, validate), then launches from it. A failed preparation never falls back to installing into or launching the source.
- Addons, patches, `.portalkeeper/wow-patches.json`, `WTF/Config.wtf`, `realmlist.wtf` and their backups all live inside the runtime. Two isolated realms can each allocate `Data/patch-4.MPQ`. Recommended and optional components stay explicit choices in the management UI and do not gate readiness.
- Armory reads the source client, so previews do not wait for runtime preparation.
- On Linux, Wine prefix discovery still uses the source executable; the argument passed to Wine is the runtime's realm executable and the working directory is the runtime root.

## Where it lives and how it is built

The runtime is `<ApplicationData>/Portalkeeper/runtimes/<realm-id>`. The realm id is the SHA-256 of the upper-cased realm name, address, auth port and world port (`Models/RealmIdentity.cs`, the same identity the WowPatch ownership ledger uses).

`ManagedRuntimeBuilder` constructs it in a sibling `<realm-id>.staging-<unique>` directory and promotes it with a rename, so construction only works within one volume; a runtime root on another volume is refused rather than copied.

- The source is validated with the normal client validator first.
- Root files (`Wow.exe` and the stock dlls) are **copied**. Stock `Data` archives, locale archives and the stock Blizzard addon files are **hard-linked** from an explicit allowlist (`BaselineInventoryProvider`, `BaselineManifestService`). Nothing is picked up by scanning a folder or trusting a `Blizzard_` name prefix.
- Unknown source content (custom MPQs, personal addons) is never inherited.
- `.portalkeeper/managed-runtime.json` (schema 1) records the realm id, source client, runtime generation, file entries (`LinkedBaseline`, `CopiedBaseline`, `RealmOwned`) and a `ProvisionedConfiguration` fingerprint of the realm's client requirements, addons and patches.
- `ManagedRuntimeValidator` rejects anything that is not a complete, manifest-backed runtime: wrong realm or source, missing files, hash mismatches, a linked file that is no longer the source inode, or a copy that is not independent. `ManagedRuntimeWriteGuard` stops patch and addon operations from touching baseline entries or runtime metadata.
- A structurally invalid runtime is kept and reported as needing repair. Nothing deletes or rebuilds it automatically. A changed fingerprint (new patches, addons or requirements) re-provisions content in place.
- Only the realm's own `ExecutableSHA256` is an authoritative hash; other baseline files are path-allowlisted only.

## Realm executable

Launch uses a separate, independent copy of the executable, named `<label>-<64-hex-realm-id>.exe` (the label is the realm name with non-alphanumeric runs turned into `-`, at most 40 characters). It is never a hard link, and an existing file at that name that Portalkeeper did not create is never adopted or overwritten. A corrupt owned file is preserved and launch is refused until it is moved away.

- Generation 1 is a byte-identical copy of the source executable.
- Generation 2 is the fixed FrameXML digest-acceptance recipe (`FrameXmlDigestOverrideRecipe`). It applies to one exact source: build 12340, 7,704,216 bytes, SHA-256 `aa63a5750d60ef16746c686b3d5e26876d98953eab08b1c026cd0faf78e88cb8`. It changes two bytes of the `.text` jump table at RVA `0x12AEBC` (`FF AB 52 00` becomes `1C AC 52 00`) and must produce SHA-256 `15c47945d5c461fda32a34645a23a9f3488bbb2b10855fe328c0e866d9ca8001`. Nothing about the recipe is read from realm.conf. It leaves the certificate and stored checksum alone.
- `Requirements=protected-framexml` is the only known requirement. Tokens are split on comma, semicolon or whitespace, compared case-insensitively, de-duplicated and sorted; an unknown value fails closed. Only an isolated realm that declares it needs Generation 2, and only then is the exact-source check applied. Realms without it run the copied baseline from any valid 3.3.5a client. A realm that drops the requirement keeps its old Generation 2 file untouched and launches the baseline copy.
- `RealmExecutableService` generates Generation 2 from the verified source only, never from an existing realm executable. The result is written to a temporary file, then promoted through a journal at `.portalkeeper/realm-executable-generation2.json` (the old executable is kept until commit). A pending journal blocks launch; ENTER REALM retries recovery. Handled failures remove only their own temporary file.

The change makes FrameXML accept a valid-signature, content-digest mismatch for the whole FrameXML and Bindings tree. A game-startup test with one changed Friends frame title showed the modified file loading with the patched executable and being rejected with the unpatched one. GlueXML and addon verification, authentication and server-side checks are unchanged.

## Developer tools

- Settings has an "ISOLATED RUNTIME (DEVELOPER TEST)" section (`MainViewModel.RuntimeTest.cs`) to construct and launch a runtime explicitly without the normal flow.
- `tests/Portalkeeper.RuntimeTests` is a console harness with no external framework: `dotnet run --project tests/Portalkeeper.RuntimeTests -- [checkpoint3|checkpoint4|checkpoint5|checkpoint6|launcher|patch-cache]`, or with no argument for the baseline fixture battery. `real --source <client> --root <runtime-root> [--realm-conf <file>]` builds a runtime from a real client. The Generation 2 tests need `PORTALKEEPER_CP5_EXE` pointing at a verified `Wow.exe` and skip without it; `PORTALKEEPER_TEST_CROSS_VOLUME_ROOT` names a directory on another filesystem for the cross-volume refusal checks.

## Not yet verified

- Windows: the hard-link calls (`CreateHardLinkW`, `GetFileInformationByHandle`) were only compiled on Linux, and native Windows acceptance of a Generation 2 executable (including the system's handling of the stale checksum and signature) has not been done.
- A complete game launch from a constructed runtime (construct, launch, log in) is a manual check that the automated tests do not cover.
- Not covered: copy fallback across volumes, a repair UI, cancellation, coordination between a running game and provisioning, and replication of personal addons. Do not change runtime or source files while preparation or the game is running.
