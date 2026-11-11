using dnlib.DotNet;
using System.Security.Cryptography;

namespace CrossPlatformPatcher.Core;

/// <summary>
/// Adds the two Option-1 materialization resource ADDs that the in-place fix
/// (B.4a, B.4e) depends on. The subject's resource-mapping layer resolves
/// these by probed name at load; without them the memo-gate failure
/// (MissingManifestResourceException during form construction) fires
/// (ISS-027/ISS-030; ISS-046/REV-041).
///
/// Scope is DELIBERATELY narrow — only the two ADDs that a neutral
/// .resources blob requires:
///   1. Dialog-pinned 24-stem literal name (dialog transcription + REV-021):
///      `7nbx}a5?U:!^=d#]"~1pIQ;X.resources` — the entry the runtime's
///      materializer looks for at probed-name resolution.
///   2. Metadata-type-based 26-char name (trailing ampersand, same stem):
///      `7nbx}a5?U:!^=d#]"~1pIQ;X&.resources` — the entry the metadata
///      type's own resource lookup requires (the three-way pairing: manifest
///      entry / metadata type / probed name — all must agree).
/// Both carry the SAME neutral blob bytes (the 180-byte empty .resources
/// container that the deterministic lineage used; sha e13ed2c59366...). No
/// renames, no metadata restructuring, no IL body edits, no re-encryption.
///
/// Idempotent: refuses (loudly) if either name is already present, so a
/// double-patch cannot silently corrupt the pairing.
/// </summary>
public static class MaterializationResourceFixer
{
    private const string DialogPinnedStem = "7nbx}a5?U:!^=d#]\"~1pIQ;X"; // 24 chars
    public static string DialogPinnedName => DialogPinnedStem + ".resources";
    public static string TypeBasedName => DialogPinnedStem + "&.resources";

    // Node: the neutral blob must be a VALID .NET .resources container - the
    // runtime materializer parses it (BEEFCACE magic + ResourceReader type
    // string + RuntimeResourceSet header). A zero-filled buffer would not
    // parse. These are the exact 180 bytes of the deterministic lineage's
    // empty.resources (sha e13ed2c59366d0eea74863fd71a81f0cb977cce1edfde304fc538690a4f6ac89).
    private static readonly byte[] NeutralBlob =
    {
        0xCE, 0xCA, 0xEF, 0xBE, 0x01, 0x00, 0x00, 0x00, 0x91, 0x00, 0x00, 0x00, 0x6C, 0x53, 0x79, 0x73,
        0x74, 0x65, 0x6D, 0x2E, 0x52, 0x65, 0x73, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x73, 0x2E, 0x52, 0x65,
        0x73, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x52, 0x65, 0x61, 0x64, 0x65, 0x72, 0x2C, 0x20, 0x6D, 0x73,
        0x63, 0x6F, 0x72, 0x6C, 0x69, 0x62, 0x2C, 0x20, 0x56, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x3D,
        0x34, 0x2E, 0x30, 0x2E, 0x30, 0x2E, 0x30, 0x2C, 0x20, 0x43, 0x75, 0x6C, 0x74, 0x75, 0x72, 0x65,
        0x3D, 0x6E, 0x65, 0x75, 0x74, 0x72, 0x61, 0x6C, 0x2C, 0x20, 0x50, 0x75, 0x62, 0x6C, 0x69, 0x63,
        0x4B, 0x65, 0x79, 0x54, 0x6F, 0x6B, 0x65, 0x6E, 0x3D, 0x62, 0x37, 0x37, 0x61, 0x35, 0x63, 0x35,
        0x36, 0x31, 0x39, 0x33, 0x34, 0x65, 0x30, 0x38, 0x39, 0x23, 0x53, 0x79, 0x73, 0x74, 0x65, 0x6D,
        0x2E, 0x52, 0x65, 0x73, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x73, 0x2E, 0x52, 0x75, 0x6E, 0x74, 0x69,
        0x6D, 0x65, 0x52, 0x65, 0x73, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x53, 0x65, 0x74, 0x02, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x41, 0x44, 0x50, 0x41, 0x44, 0x50,
        0xB4, 0x00, 0x00, 0x00,
    };

    /// <summary>SHA-256 of <see cref="NeutralBlob"/> (pin for the build log).</summary>
    public static string NeutralBlobSha256 =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(NeutralBlob)).ToLowerInvariant();

    /// <summary>Exposes the neutral blob for verification tests only.</summary>
    public static byte[] NeutralBlobBytesForTests => NeutralBlob;

    /// <summary>
    /// Adds the two materialization resource entries if (and only if) the
    /// module's manifest carries the 7nbx-family linked entry that the fix is
    /// scoped to. Returns the number of entries added (0, 1, or 2).
    /// Throws <see cref="InvalidOperationException"/> if the pairing would be
    /// violated (one of the two names already present).
    /// </summary>
    public static int EnsureMaterializationResources(ModuleDef module, bool verbose = false)
    {
        if (module is null) throw new ArgumentNullException(nameof(module));

        // Scope guard: only act on modules that actually carry the linked
        // entry the fix is for (pristine-era or unrelated modules stay clean).
        var linked = module.Resources.OfType<AssemblyLinkedResource>().ToList();
        bool hasSeven = linked.Any(r =>
            r.Name.String.Contains("7nbx}") && r.Name.String.EndsWith(".resources"));
        if (!hasSeven)
        {
            if (verbose)
                Console.WriteLine("    [materialize] no 7nbx linked entry in manifest — skipping (out of scope).");
            return 0;
        }

        // Pairing idempotence guard: refuse on drift, never silently patch.
        foreach (var nm in new[] { DialogPinnedName, TypeBasedName })
        {
            if (module.Resources.Any(r => r.Name.String == nm))
                throw new InvalidOperationException(
                    $"Materialization pairing drift: {nm} already present — refusing to double-patch.");
        }

        module.Resources.Add(new EmbeddedResource(DialogPinnedName, NeutralBlob,
            ManifestResourceAttributes.Public));
        module.Resources.Add(new EmbeddedResource(TypeBasedName, NeutralBlob,
            ManifestResourceAttributes.Public));

        if (verbose)
        {
            Console.WriteLine($"    [materialize] added {DialogPinnedName} ({NeutralBlob.Length} bytes)");
            Console.WriteLine($"    [materialize] added {TypeBasedName} ({NeutralBlob.Length} bytes)");
        }
        return 2;
    }
}
