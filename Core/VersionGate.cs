namespace CrossPlatformPatcher.Core;

/// <summary>Outcome of classifying an input file before patching.</summary>
public enum VersionGateKind
{
    /// <summary>Known and certified; patching may proceed.</summary>
    Supported,

    /// <summary>A PAIcom executable, but this build is not in the registry.</summary>
    Unsupported,

    /// <summary>Not a PAIcom executable at all (wrong file, decoy, corrupt).</summary>
    NotPaiCom,
}

/// <summary>Classification result plus the message the CLI should print.</summary>
public sealed record VersionGateResult(
    VersionGateKind Kind,
    string Sha256,
    VersionEntry? Entry,
    IReadOnlyList<string> Message,
    int ExitCode)
{
    public bool CanPatch => Kind == VersionGateKind.Supported && Entry is { IsRunnable: true };
}

/// <summary>
/// Decides whether a file may be patched, before anything is written.
///
/// Two distinct refusals:
///  - not a PAIcom executable (wrong file, decoy, corrupt): structural check;
///  - a PAIcom executable whose hash is not a runnable registry entry.
///
/// A refusal never touches the input. Pure and testable.
/// </summary>
public static class VersionGate
{
    /// <summary>GitHub's PE header is far smaller than the real game; a floor
    /// this low rejects the launcher decoy (about 0.9 MB) and stray files while
    /// leaving room for future trimmed builds.</summary>
    public const long MinimumPaiComBytes = 40L * 1024 * 1024;

    public const int ExitNotPaiCom = 5;
    public const int ExitUnsupported = 6;

    public static VersionGateResult Classify(byte[] bytes, string sha256, bool allowUnknownForTesting = false)
    {
        // The size floor is what separates "wrong file/decoy" from "a game build
        // we do not know"; tests patch synthetic fixtures, so the override also
        // relaxes the floor (structural PE checks still apply).
        var minimum = allowUnknownForTesting ? 0L : MinimumPaiComBytes;
        if (!LooksLikeManagedPaiCom(bytes, out var structuralReason, minimum))
            return NotPaiCom(sha256, structuralReason);

        var entry = VersionRegistry.Find(sha256);
        if (entry is null)
        {
            if (allowUnknownForTesting)
            {
                var synthetic = new VersionEntry(
                    InputSha256: sha256,
                    DisplayName: "Unknown version (test override)",
                    Support: VersionSupport.Certified,
                    Features: PatchFeatures.Full,
                    RequiredOutputResources: Array.Empty<string>(),
                    ExpectedOutputSha256: null,
                    Certification: "Test override: version gate bypassed.",
                    Note: "Test-only synthetic entry.");
                return new VersionGateResult(
                    VersionGateKind.Supported, sha256, synthetic,
                    new[] { "Checking version... unknown version allowed by test override." }, 0);
            }

            return Unsupported(sha256, entry: null);
        }

        if (!entry.IsRunnable)
        {
            if (allowUnknownForTesting)
            {
                var syntheticUnsupported = new VersionEntry(
                    InputSha256: sha256,
                    DisplayName: $"{entry.DisplayName} (test override)",
                    Support: VersionSupport.Certified,
                    Features: PatchFeatures.Full,
                    RequiredOutputResources: Array.Empty<string>(),
                    ExpectedOutputSha256: null,
                    Certification: "Test override: unsupported entry bypassed.",
                    Note: "Test-only synthetic entry.");
                return new VersionGateResult(
                    VersionGateKind.Supported, sha256, syntheticUnsupported,
                    new[] { "Checking version... unsupported entry allowed by test override." }, 0);
            }

            return Unsupported(sha256, entry);
        }

        return new VersionGateResult(
            VersionGateKind.Supported,
            sha256,
            entry,
            new[]
            {
                $"Checking version... PAIcom {entry.ShortHash} ({entry.DisplayName}).",
            },
            ExitCode: 0);
    }

    /// <summary>Structural test: a managed PE of plausible game size.</summary>
    public static bool LooksLikeManagedPaiCom(byte[] bytes, out string reason, long minimumBytes = MinimumPaiComBytes)
    {
        reason = string.Empty;

        if (bytes is null || bytes.LongLength < minimumBytes)
        {
            reason = "file is too small to be the game program";
            return false;
        }

        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            reason = "missing MZ header";
            return false;
        }

        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > bytes.Length)
        {
            reason = "PE header offset is outside the file";
            return false;
        }

        if (bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E' ||
            bytes[peOffset + 2] != 0 || bytes[peOffset + 3] != 0)
        {
            reason = "missing PE signature";
            return false;
        }

        var magic = BitConverter.ToUInt16(bytes, peOffset + 24);
        var optionalHeaderSize = magic switch
        {
            0x10B => 96,  // PE32
            0x20B => 112, // PE32+
            _ => -1,
        };
        if (optionalHeaderSize < 0)
        {
            reason = "unknown optional-header format";
            return false;
        }

        // Data directory 14 is the CLI header. Its presence means this is a
        // managed assembly, which the game and its patched build both are.
        var dataDirectories = peOffset + 24 + optionalHeaderSize;
        var cliEntry = dataDirectories + 14 * 8;
        if (cliEntry + 8 > bytes.Length)
        {
            reason = "optional header is truncated";
            return false;
        }

        var cliRva = BitConverter.ToInt32(bytes, cliEntry);
        var cliSize = BitConverter.ToInt32(bytes, cliEntry + 4);
        if (cliRva <= 0 || cliSize <= 0)
        {
            reason = "no managed (.NET) header";
            return false;
        }

        // RVAs are virtual, not file offsets: map the CLI header through the
        // section table before touching it. Small assemblies legitimately have
        // an RVA larger than their file length.
        if (!TryMapRvaToFileOffset(bytes, peOffset, cliRva, cliSize, out _))
        {
            reason = "managed header points outside the file";
            return false;
        }

        return true;
    }

    private static bool TryMapRvaToFileOffset(
        byte[] bytes, int peOffset, int rva, int size, out long fileOffset)
    {
        fileOffset = -1;

        if (peOffset + 24 > bytes.Length) return false;
        var numberOfSections = BitConverter.ToUInt16(bytes, peOffset + 6);
        var sizeOfOptionalHeader = BitConverter.ToUInt16(bytes, peOffset + 20);
        var sectionStart = peOffset + 24 + sizeOfOptionalHeader;

        for (var i = 0; i < numberOfSections; i++)
        {
            var section = sectionStart + i * 40;
            if (section + 40 > bytes.Length) return false;

            var virtualSize = BitConverter.ToUInt32(bytes, section + 8);
            var virtualAddress = BitConverter.ToUInt32(bytes, section + 12);
            var sizeOfRawData = BitConverter.ToUInt32(bytes, section + 16);
            var pointerToRawData = BitConverter.ToUInt32(bytes, section + 20);
            var span = Math.Max(virtualSize, sizeOfRawData);

            if (rva >= virtualAddress && rva < virtualAddress + span)
            {
                var offset = (long)pointerToRawData + (rva - virtualAddress);
                if (offset < 0 || offset + size > bytes.Length) return false;
                fileOffset = offset;
                return true;
            }
        }

        return false;
    }

    private static VersionGateResult NotPaiCom(string sha256, string reason)
    {
        return new VersionGateResult(
            VersionGateKind.NotPaiCom,
            sha256,
            Entry: null,
            Message: new[]
            {
                "This does not appear to be a PAIcom executable.",
                $"Reason: {reason}.",
                string.Empty,
                "Give the patcher the game's real program file, uninstall.exe.",
                "Other executables in the folder (including any file that looks like a launcher)",
                "are not the game and cannot be patched.",
                string.Empty,
                "No files were changed.",
            },
            ExitCode: ExitNotPaiCom);
    }

    private static VersionGateResult Unsupported(string sha256, VersionEntry? entry)
    {
        var shortHash = sha256.Length >= 8 ? sha256[..8].ToLowerInvariant() : sha256.ToLowerInvariant();
        var lines = new List<string>
        {
            "This PAIcom version is not yet supported.",
            $"Your version: {shortHash}",
            string.Empty,
            "The PAIcom developer updates the game frequently - this is normal.",
            "Check for a patcher update.",
        };

        if (entry is not null && entry.Note is not null)
        {
            lines.Add(string.Empty);
            lines.Add($"Note: {entry.Note}");
        }

        lines.Add(string.Empty);
        lines.Add("Supported versions:");
        lines.Add(VersionRegistry.DescribeSupported());
        lines.Add(string.Empty);
        lines.Add("No files were changed.");

        return new VersionGateResult(
            VersionGateKind.Unsupported,
            sha256,
            entry,
            lines,
            ExitCode: ExitUnsupported);
    }
}
