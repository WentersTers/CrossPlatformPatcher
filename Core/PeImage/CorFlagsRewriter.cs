using System;

namespace CrossPlatformPatcher.PeImage;

/// <summary>
/// Pure, stateless byte-level manipulation of the PE/CLI CorFlags for 64-bit
/// probe migration.  This type performs no file I/O and holds no mutable state:
/// callers read the target PE into a <see cref="byte"/> buffer, invoke these
/// helpers, and write the (possibly modified) buffer back themselves.
/// </summary>
/// <remarks>
/// Extracted from <c>AssemblyPatcher</c> so the CorFlags rewriting logic can be
/// unit-tested against a fixture PE without touching the filesystem.
/// </remarks>
public static class CorFlagsRewriter
{
    /// <summary>COMIMAGE_FLAGS_ILONLY — image contains only managed code.</summary>
    private const uint ComImageFlagsILOnly = 0x00000001;

    /// <summary>COMIMAGE_FLAGS_32BITREQUIRED — image must run in a 32-bit process.</summary>
    private const uint ComImageFlags32BitRequired = 0x00000002;

    /// <summary>COMIMAGE_FLAGS_32BITPREFERRED — image prefers a 32-bit process.</summary>
    private const uint ComImageFlags32BitPreferred = 0x00020000;

    /// <summary>
    /// Clears the <c>32BITREQUIRED</c> and <c>32BITPREFERRED</c> CorFlags in
    /// <paramref name="peBytes"/> in place, allowing a 32-bit IL-only image to
    /// run as a 64-bit process.  The buffer is only modified on success when the
    /// flags actually change.
    /// </summary>
    /// <param name="peBytes">The full PE image bytes.</param>
    /// <param name="cliFlagsOffset">
    /// The file offset of the CLI header's <c>Flags</c> field when found,
    /// otherwise <c>-1</c>.
    /// </param>
    /// <param name="error">
    /// A structured reason code (e.g. <c>CLI_HEADER_NOT_FOUND</c>,
    /// <c>NOT_IL_ONLY</c>, <c>ALREADY_CLEARED</c>, <c>APPLIED</c>) or
    /// <c>null</c> when <paramref name="peBytes"/> is valid and rewritten.
    /// </param>
    /// <returns>
    /// <c>true</c> when the image is eligible and the 32-bit flags are now clear
    /// (including the case where they were already clear); otherwise <c>false</c>.
    /// </returns>
    public static bool TryApply64BitProbeCorFlags(byte[] peBytes, out int cliFlagsOffset, out string? error)
    {
        cliFlagsOffset = -1;
        error = null;

        if (!TryGetCliFlagsOffset(peBytes, out cliFlagsOffset, out error))
        {
            error ??= "CLI_HEADER_NOT_FOUND";
            return false;
        }

        var flags = BitConverter.ToUInt32(peBytes, cliFlagsOffset);
        if ((flags & ComImageFlagsILOnly) == 0)
        {
            error = "NOT_IL_ONLY";
            return false;
        }

        var rewritten = flags & ~ComImageFlags32BitRequired & ~ComImageFlags32BitPreferred;
        if (rewritten == flags)
        {
            error = "ALREADY_CLEARED";
            return true;
        }

        var rewrittenBytes = BitConverter.GetBytes(rewritten);
        Buffer.BlockCopy(rewrittenBytes, 0, peBytes, cliFlagsOffset, rewrittenBytes.Length);
        error = "APPLIED";
        return true;
    }

    /// <summary>
    /// Reads the CLI header <c>Flags</c> value from a PE image without
    /// modifying anything. Lets callers bake the actual post-patch flags
    /// into generated artifacts instead of asserting them.
    /// </summary>
    public static bool TryReadCliFlags(byte[] peBytes, out uint flags, out string? error)
    {
        flags = 0;
        error = null;

        if (!TryGetCliFlagsOffset(peBytes, out var offset, out error))
        {
            error ??= "CLI_HEADER_NOT_FOUND";
            return false;
        }

        if (offset + 4 > peBytes.Length)
        {
            error = "CLI_FLAGS_OUT_OF_BOUNDS";
            return false;
        }

        flags = BitConverter.ToUInt32(peBytes, offset);
        return true;
    }

    /// <summary>
    /// Locates the file offset of the CLI header's <c>Flags</c> field within a
    /// PE image.  The CLI header is the first data directory entry of the
    /// optional header; the flags live 16 bytes into that header.
    /// </summary>
    /// <param name="peBytes">The full PE image bytes.</param>
    /// <param name="flagsOffset">The CLI header <c>Flags</c> file offset, or <c>-1</c>.</param>
    /// <param name="error">A structured reason code, or <c>null</c> on success.</param>
    /// <returns><c>true</c> when the CLI flags field was located; otherwise <c>false</c>.</returns>
    public static bool TryGetCliFlagsOffset(byte[] peBytes, out int flagsOffset, out string? error)
    {
        flagsOffset = -1;
        error = null;

        if (peBytes.Length < 0x100)
        {
            error = "PE_TOO_SMALL";
            return false;
        }

        var peHeaderOffset = BitConverter.ToInt32(peBytes, 0x3C);
        if (peHeaderOffset <= 0 || peHeaderOffset + 24 >= peBytes.Length)
        {
            error = "INVALID_PE_HEADER_OFFSET";
            return false;
        }

        if (peBytes[peHeaderOffset] != 'P' || peBytes[peHeaderOffset + 1] != 'E')
        {
            error = "MISSING_PE_SIGNATURE";
            return false;
        }

        var numberOfSections = BitConverter.ToUInt16(peBytes, peHeaderOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(peBytes, peHeaderOffset + 20);
        var optionalHeaderOffset = peHeaderOffset + 24;
        if (optionalHeaderOffset + optionalHeaderSize >= peBytes.Length)
        {
            error = "OPTIONAL_HEADER_OUT_OF_BOUNDS";
            return false;
        }

        var magic = BitConverter.ToUInt16(peBytes, optionalHeaderOffset);
        int dataDirectoryOffset = magic switch
        {
            0x10b => optionalHeaderOffset + 96,
            0x20b => optionalHeaderOffset + 112,
            _ => -1,
        };

        if (dataDirectoryOffset < 0)
        {
            error = "UNSUPPORTED_PE_MAGIC";
            return false;
        }

        const int cliDirectoryIndex = 14;
        var cliDirectoryOffset = dataDirectoryOffset + (cliDirectoryIndex * 8);
        if (cliDirectoryOffset + 8 > peBytes.Length)
        {
            error = "CLI_DIRECTORY_OUT_OF_BOUNDS";
            return false;
        }

        var cliHeaderRva = BitConverter.ToInt32(peBytes, cliDirectoryOffset);
        if (cliHeaderRva <= 0)
        {
            error = "CLI_HEADER_RVA_MISSING";
            return false;
        }

        var sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
        var cliHeaderOffset = MapRvaToFileOffset(peBytes, sectionTableOffset, numberOfSections, cliHeaderRva);
        if (cliHeaderOffset <= 0)
        {
            error = "CLI_HEADER_RVA_NOT_MAPPED";
            return false;
        }

        var candidateFlagsOffset = cliHeaderOffset + 16;
        if (candidateFlagsOffset + 4 > peBytes.Length)
        {
            error = "CLI_FLAGS_OUT_OF_BOUNDS";
            return false;
        }

        flagsOffset = candidateFlagsOffset;
        return true;
    }

    /// <summary>
    /// Maps a relative virtual address (RVA) to a file offset by walking the PE
    /// section table.
    /// </summary>
    /// <param name="peBytes">The full PE image bytes.</param>
    /// <param name="rva">The relative virtual address to map.</param>
    /// <param name="fileOffset">The mapped file offset, or <c>-1</c> when not found.</param>
    /// <param name="error">A structured reason code, or <c>null</c> on success.</param>
    /// <returns><c>true</c> when the RVA falls within a loaded section; otherwise <c>false</c>.</returns>
    public static bool RvaToFileOffset(byte[] peBytes, int rva, out int fileOffset, out string? error)
    {
        fileOffset = -1;
        error = null;

        if (peBytes.Length < 0x100)
        {
            error = "PE_TOO_SMALL";
            return false;
        }

        var peHeaderOffset = BitConverter.ToInt32(peBytes, 0x3C);
        if (peHeaderOffset <= 0 || peHeaderOffset + 24 >= peBytes.Length)
        {
            error = "INVALID_PE_HEADER_OFFSET";
            return false;
        }

        var numberOfSections = BitConverter.ToUInt16(peBytes, peHeaderOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(peBytes, peHeaderOffset + 20);
        var sectionTableOffset = peHeaderOffset + 24 + optionalHeaderSize;

        fileOffset = MapRvaToFileOffset(peBytes, sectionTableOffset, numberOfSections, rva);
        if (fileOffset < 0)
        {
            error = "RVA_NOT_IN_SECTION";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Core RVA→file-offset mapping used by the public helpers.
    /// </summary>
    private static int MapRvaToFileOffset(byte[] peBytes, int sectionTableOffset, int numberOfSections, int rva)
    {
        for (int i = 0; i < numberOfSections; i++)
        {
            var sectionOffset = sectionTableOffset + (i * 40);
            if (sectionOffset + 40 > peBytes.Length)
                return -1;

            var virtualSize = BitConverter.ToInt32(peBytes, sectionOffset + 8);
            var virtualAddress = BitConverter.ToInt32(peBytes, sectionOffset + 12);
            var sizeOfRawData = BitConverter.ToInt32(peBytes, sectionOffset + 16);
            var pointerToRawData = BitConverter.ToInt32(peBytes, sectionOffset + 20);
            var span = Math.Max(virtualSize, sizeOfRawData);

            if (rva >= virtualAddress && rva < virtualAddress + span)
                return pointerToRawData + (rva - virtualAddress);
        }

        return -1;
    }
}
