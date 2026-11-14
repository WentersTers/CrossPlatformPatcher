using dnlib.DotNet;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// What fixes a version entry applies. Data, not code: a new version is a new
/// <see cref="VersionEntry"/>, and a new fix is a new flag here.
/// </summary>
[Flags]
public enum PatchFeatures
{
    None = 0,

    /// <summary>The two 7nbx materialization resource ADDs (ISS-027 class).</summary>
    MaterializationFix = 1 << 0,

    /// <summary>WebClient download watchdog (frozen-menu mitigation).</summary>
    WebClientWatchdog = 1 << 1,

    /// <summary>System.Speech compatibility wrappers.</summary>
    SpeechCompat = 1 << 2,

    /// <summary>OpenWakeWord injection (audio hooks, dispatch, wake engine).</summary>
    OpenWakeWord = 1 << 3,

    /// <summary>Speech + OpenWakeWord engine injection (voice pipeline).</summary>
    VoiceEngine = SpeechCompat | OpenWakeWord,

    /// <summary>The certified baseline strategy: all of the above.</summary>
    Full = MaterializationFix | WebClientWatchdog | VoiceEngine,
}

/// <summary>Certification state of a known version.</summary>
public enum VersionSupport
{
    /// <summary>Tested end to end; the strategy is frozen.</summary>
    Certified,

    /// <summary>Recognised, strategy implemented, but end-to-end certification
    /// is incomplete. Patching is allowed and marked as experimental.</summary>
    Experimental,

    /// <summary>Recognised but deliberately refused until a strategy exists.</summary>
    Unsupported,
}

/// <summary>
/// One known PAIcom build. This is the whole contract: the input hash it
/// applies to, the strategy to run, the checks the output must pass, and how
/// far it has been certified.
///
/// Phase 2 moves this shape to a server file unchanged.
/// </summary>
public sealed record VersionEntry(
    string InputSha256,
    string DisplayName,
    VersionSupport Support,
    PatchFeatures Features,
    IReadOnlyList<string> RequiredOutputResources,
    string? ExpectedOutputSha256,
    string Certification,
    string? Note,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyList<string>? CertifiedOs = null)
{
    /// <summary>Short form used in user-facing messages.</summary>
    public string ShortHash => InputSha256.Length >= 8 ? InputSha256[..8] : InputSha256;

    /// <summary>True when the entry is runnable (certified or experimental).</summary>
    public bool IsRunnable =>
        Features != PatchFeatures.None &&
        Support is VersionSupport.Certified or VersionSupport.Experimental;

    /// <summary>Strategy option: skip OpenWakeWord module-level hook sites.</summary>
    public bool SkipModuleLevelHooks =>
        Options is not null &&
        Options.TryGetValue("oww.skipModuleHooks", out var value) &&
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The embedded version registry (Phase 1). Offline by design: no server, no
/// downloads. Adding support for a game build means adding an entry here.
///
/// Rule: the certified baseline entry is frozen. Improvements become new
/// entries or new <see cref="PatchFeatures"/> flags, never edits to it.
/// </summary>
public static class VersionRegistry
{
    /// <summary>The certified baseline PAIcom build (bundle 0aeacee5 lineage).</summary>
    public const string CertifiedBaselineSha =
        "61942e6ce0f22758756d197089f4fd0c3f4e0ef685584b35b9a799085dabd128";

    /// <summary>The current Steam build, recognised but not yet supported.</summary>
    public const string SteamCurrentSha =
        "88c9daec46d3079ebe35a1613fc298db39b64e0e9578181ffb86fd4a6b55e4ed";

    /// <summary>Patcher-side identity of the analytics name for this registry.</summary>
    public const string RegistryVersion = "2026-11-09.1";

    private static readonly VersionEntry[] EntriesInternal =
    {
        new(
            InputSha256: CertifiedBaselineSha,
            DisplayName: "PAIcom certified baseline",
            Support: VersionSupport.Certified,
            Features: PatchFeatures.Full,
            RequiredOutputResources: new[]
            {
                MaterializationResourceFixer.DialogPinnedName,
                MaterializationResourceFixer.TypeBasedName,
            },
            ExpectedOutputSha256: null, // structural checks are the gate; see note
            Certification:
                "Certified end to end: Wine 11, GE-Proton11-7, Steam Proton Experimental, Fedora 44 (2026-11-09). " +
                "Voice campaign and watchdog battery passed on this build.",
            Note:
                "Frozen strategy. Output hash is build-dependent (embedded helper payload), " +
                "so verification uses structure rather than a fixed output hash.",
            CertifiedOs: new[]
            {
                "Ubuntu 24.04 (Wine 11 full, voice 11/11 + 11 silent; GE-Proton 11-7 and Steam Proton Exp full)",
                "Fedora 41 (GE-Proton 11-7)",
                "Fedora 43 (Distro Wine 11.0)",
                "Fedora 44 (Distro Wine 11.20 full, voice 11/11 + 11 silent)",
                "Debian 12 (Distro Wine 11.10 full, voice 11/11 + 11 silent, run in short sessions)",
                "Arch (GE-Proton 11-7)",
            }),

        new(
            InputSha256: SteamCurrentSha,
            DisplayName: "PAIcom current Steam build",
            Support: VersionSupport.Certified,
            Features: PatchFeatures.Full,
            RequiredOutputResources: new[]
            {
                MaterializationResourceFixer.DialogPinnedName,
                MaterializationResourceFixer.TypeBasedName,
            },
            ExpectedOutputSha256: null,
            Certification:
                "Certified end to end on 2026-11-09/10 under Proton Experimental: startup, menu, " +
                "wake (0.978/0.984), transcript, dispatch (open twitch, 95%), cutoff 700 ms. " +
                "Requires the full runtime set beside the exe (Onnx, Vosk, NAudio, System.*, mingw) " +
                "and a Vosk model under models/ (or first-run fetch).",
            Note:
                "Module-level OpenWakeWord hook sites must be skipped on this build: they run " +
                "during the module constructor and throw TypeInitializationException. " +
                "Strategy: full minus oww module-level hooks (18 of 20 sites).",
            Options: new Dictionary<string, string> { ["oww.skipModuleHooks"] = "true" },
            CertifiedOs: new[]
            {
                "Ubuntu 24.04 (Steam Proton Experimental)",
            }),
    };

    /// <summary>All known versions, in registry order.</summary>
    public static IReadOnlyList<VersionEntry> Entries => EntriesInternal;

    /// <summary>Versions the patcher will actually patch.</summary>
    public static IReadOnlyList<VersionEntry> Supported =>
        EntriesInternal.Where(e => e.IsRunnable).ToArray();

    /// <summary>Look up a full SHA-256 (hex, any case).</summary>
    public static VersionEntry? Find(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        var needle = sha256.Trim().ToLowerInvariant();
        return EntriesInternal.FirstOrDefault(e =>
            string.Equals(e.InputSha256, needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Human-readable list used in refusal messages and <c>--versions</c>.</summary>
    public static string DescribeSupported()
    {
        var supported = Supported;
        if (supported.Count == 0) return "  (none)";

        return string.Join(Environment.NewLine, supported.Select(e =>
            $"  - {e.ShortHash}  {e.DisplayName}  [{DescribeFeatures(e.Features)}]" +
            (e.CertifiedOs is { Count: > 0 }
                ? Environment.NewLine + "      certified on: " + string.Join("; ", e.CertifiedOs)
                : string.Empty)));
    }

    /// <summary>Human-readable list of every recognised version.</summary>
    public static string DescribeAll()
    {
        return string.Join(Environment.NewLine, EntriesInternal.Select(e =>
            $"  - {e.ShortHash}  {e.DisplayName}  status={e.Support}  strategy={DescribeFeatures(e.Features)}" +
            (e.CertifiedOs is { Count: > 0 }
                ? Environment.NewLine + "      certified on: " + string.Join("; ", e.CertifiedOs)
                : string.Empty) +
            (e.Note is null ? string.Empty : Environment.NewLine + $"      {e.Note}")));
    }

    /// <summary>Strategy flag names for logs and manifests.</summary>
    public static string DescribeFeatures(PatchFeatures features)
    {
        if (features == PatchFeatures.None) return "none";
        var parts = new List<string>();
        if (features.HasFlag(PatchFeatures.MaterializationFix)) parts.Add("materialization");
        if (features.HasFlag(PatchFeatures.WebClientWatchdog)) parts.Add("watchdog");
        if (features.HasFlag(PatchFeatures.SpeechCompat)) parts.Add("speech");
        if (features.HasFlag(PatchFeatures.OpenWakeWord)) parts.Add("oww");
        return string.Join("+", parts);
    }
}
