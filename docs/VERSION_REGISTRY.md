# Version registry (Phase 1)

The patcher refuses to work on PAIcom builds it does not recognise. This is a
safety feature: a patch is only attempted for a version whose strategy and
certification are known, so an unsupported game update can never produce a
broken executable.

The registry is **embedded and offline**. There are no downloads and no server
calls in Phase 1.

## How a run behaves

1. The input is read and hashed (SHA-256) before anything else.
2. It is classified:
   - **Supported** - the hash is a runnable registry entry: patch as normal.
   - **Unsupported** - a PAIcom-shaped executable whose hash is not runnable:
     refuse, list supported versions, leave the file untouched.
   - **Not a PAIcom executable** - wrong file, the `[LAUNCH HERE].exe` decoy, or
     a corrupt file: refuse with a structural message, leave the file untouched.
3. Only after a supported classification does the patcher create a backup or
   write output.
4. The output is verified, a manifest is written, and the run reports input
   hash, output hash, strategy, patcher version and timestamp.

Exit codes: `0` success, `2` output verification failed, `5` not a PAIcom
executable, `6` unsupported version.

## Registry entries

`Core/VersionRegistry.cs` holds the data. One entry per known build:

| field | meaning |
|---|---|
| `InputSha256` | exact hash of the game executable the entry applies to |
| `DisplayName` | short human label |
| `Support` | `Certified`, `Experimental` (runnable, end-to-end pending) or `Unsupported` |
| `Features` | strategy: `MaterializationFix`, `WebClientWatchdog`, `SpeechCompat`, `OpenWakeWord` (flags) |
| `RequiredOutputResources` | resources the output must contain |
| `ExpectedOutputSha256` | optional pinned output hash (null when the output is build-dependent) |
| `Certification` | where and how it was tested end to end |
| `CertifiedOs` | operating systems + runtimes actually certified for the entry |
| `Note` | rationale, especially for unsupported builds |
| `Options` | optional strategy options (for example `oww.skipModuleHooks`) |

Current entries:

| input | status | strategy |
|---|---|---|
| `61942e6c...` certified baseline | Certified | materialization + watchdog + speech + oww |
| `88c9daec...` current Steam build | Certified | full strategy, `oww.skipModuleHooks=true` (18 of 20 hook sites) |

Certified operating systems per entry:

- `61942e6c`: Ubuntu 24.04 (Wine 11, full: 11/11 clear-speech positives with
  11/11 expected-silent cases and no repeats needed, mumble reported as
  degradation; GE-Proton 11-7 and Steam Proton Experimental full); Fedora 41
  (GE-Proton 11-7); Fedora 43 (distro Wine 11.0); Fedora 44 (distro Wine
  11.20, full: 11/11 clear-speech positives, 11/11 expected-silent, no
  repeats); Arch (GE-Proton 11-7).
- `88c9daec`: Ubuntu 24.04 (Steam Proton Experimental).

"Menu and pet only" is deliberately weaker than a voice certification. The
Wine 11 entry on Ubuntu 24.04 passed the voice battery on 2026-11-12, scored
under [VOICE_BATTERY.md](VOICE_BATTERY.md): 11 of 11 clear-speech positives
dispatched the correct command, all 11 expected-silent cases (10 speech
negatives plus a command that does not exist in the manifest) stayed silent,
URLs opened, and no take needed the replay allowance. The mumbled take is a
degradation probe and is reported, not scored.
Ubuntu
22.04 and Debian 12 have menu-and-pet walk records from the older build-5
campaign, but those records are not part of this repository and there is no
voice run against the current build, so neither is listed as certified.
SteamOS and Steam Deck have no repository evidence; cite them only as
"reported working (community)" if at all.

### GE-Proton 11-7 glibc floor

GE-Proton 11-7 requires glibc **2.38**. Verified at binary level on the staged
runtime: `wineserver`, `ntdll.so` (x86_64 and i386), `winex11.so` and
`win32u.so` reference `GLIBC_2.38`; only the `wine` wrapper is lower (2.34).
Ubuntu 22.04 (glibc 2.35) therefore cannot run the GE path — its leg is the
pre-registered `EXPECTED-FAIL` glibc gate. Use distro Wine on 22.04, or a
distribution with glibc 2.38 or newer.


### Build-specific strategy options

`oww.skipModuleHooks` tells the OpenWakeWord injection to skip hook sites on
module-level helper types (`<Module>` / nested types). On the 2026-11 Steam
build those helpers run during the module constructor, so hooking them throws
`TypeInitializationException` before any window appears. The certified
baseline does not need the skip (20 sites); the Steam build needs it (18).

How that was found: a feature bisect on `88c9daec` showed materialization,
watchdog and speech wrappers all run, while OpenWakeWord failed; a hook-limit
bisect then showed even one hook breaks it, and the first hook is the
module-level byte[] helper.

### Deployment prerequisites for the current Steam build

The newer build does not ship the OWW runtime support files. Beside the
patched exe the folder needs `Microsoft.ML.OnnxRuntime.dll`, `onnxruntime.dll`,
`Vosk.dll`, `libvosk.dll`, the `NAudio.*` pair, the `System.Buffers` /
`System.Memory` / `System.Numerics.Vectors` /
`System.Runtime.CompilerServices.Unsafe` set, and the mingw runtime
(`libgcc_s_seh-1`, `libstdc++-6`, `libwinpthread-1`). It also needs a Vosk
model under `models/`; without it the app falls back to a first-run download
and speech dispatch stays empty until the fetch completes. The certified artifact is the full bundle, not an exe swap. The user-facing
copy of this recipe lives in the README under "Voice runtime files" and in the
generated `SETUP_LINUX.md`.


The certified baseline entry is **frozen**. Improvements are new entries or new
strategy flags, never edits to a certified entry.

## Adding a version

Add a data entry - no code changes:

```csharp
new(
    InputSha256: "<full sha256 of that build>",
    DisplayName: "PAIcom <build label>",
    Support: VersionSupport.Certified,
    Features: PatchFeatures.Full,
    RequiredOutputResources: new[]
    {
        MaterializationResourceFixer.DialogPinnedName,
        MaterializationResourceFixer.TypeBasedName,
    },
    ExpectedOutputSha256: null,
    Certification: "Tested on <environments> on <date>.",
    Note: null),
```

To recognise a build before a strategy exists, add it with
`Support: VersionSupport.Unsupported` and a `Note`. The refusal message then
explains that the build is known but not yet supported.

## Strategy flags

`PatchFeatures` is a flags enum. `AssemblyPatcher` gates each fix:

- `MaterializationFix` - the two 7nbx resource ADDs (ISS-027 class).
- `WebClientWatchdog` - the bounded `WebClient.DownloadString` shim.
- `VoiceEngine` - the Speech and OpenWakeWord modules; skipped when absent.

Strategies avoid byte offsets: fixes locate methods/resources structurally.
Offsets remain acceptable only where structure is impractical.

## Verification

`Core/PatchVerifier.cs` checks, after writing:

- the output is still an IL-only managed assembly;
- every `RequiredOutputResources` entry is present exactly once (pairing rule);
- when pinned, the output hash equals `ExpectedOutputSha256`.

A verification failure returns exit code 2 and leaves the file for inspection.

## Manifest

Every successful patch writes `<output>.patch.json`:

```json
{
  "Schema": "paicom-patch-manifest/v1",
  "InputSha256": "...",
  "OutputSha256": "...",
  "Strategy": "PAIcom certified baseline",
  "StrategyFeatures": "materialization+watchdog+speech+oww",
  "PatcherVersion": "...",
  "RegistryVersion": "2026-11-09.1",
  "TimestampUtc": "...",
  "VersionDisplayName": "...",
  "Certification": "...",
  "Verification": ["managed output parsed", "..."]
}
```

This makes every deployment traceable to a patcher build, a registry version
and an output hash.

## Querying the registry

```sh
CrossPlatformPatcher --versions
```

Prints known versions, status, strategy and supported list. The same data is
used by the refusal message and by the tests.

## Tests

- supported entry has the full strategy; Steam build is unsupported;
- registry is queryable (`DescribeSupported`, `DescribeAll`, `DescribeFeatures`);
- small/decoy file -> "does not appear to be a PAIcom executable";
- plausible unknown build -> unsupported refusal with hash and supported list;
- verification catches missing resources and pairing drift;
- the test process sets `PAICOM_PATCHER_ALLOW_UNKNOWN=1` so synthetic fixtures
  can still be patched. That override is **test-only** and must never be set in
  a user-facing run.

## Phase 2

The entry shape is exactly what a server file will contain. Phase 2 replaces
the embedded list with a fetched file (GitHub or a small server), keeping the
same fields, classification and messages. Nothing about the user experience is
expected to change; a newer registry simply lists more supported builds.
