using CrossPlatformPatcher.PeImage;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Exercises <see cref="CorFlagsRewriter"/> against a real managed assembly from
/// the test project's output (a valid PE with a CLI header) plus error paths.
/// </summary>
public sealed class CorFlagsRewriterTests
{
    private const uint ComImageFlags32BitRequired = 0x00000002;
    private const uint ComImageFlags32BitPreferred = 0x00020000;

    private static byte[] LoadRealManagedAssemblyBytes()
    {
        var location = typeof(CrossPlatformPatcher.Core.AssemblyPatcher).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(location), "Test assembly location should be available.");
        return File.ReadAllBytes(location);
    }

    [Fact]
    public void TryGetCliFlagsOffset_Finds_Flags_On_Real_Managed_Assembly()
    {
        var pe = LoadRealManagedAssemblyBytes();

        Assert.True(CorFlagsRewriter.TryGetCliFlagsOffset(pe, out var offset, out var error));

        Assert.True(offset > 0, "Flags offset should be positive.");
        Assert.True(offset + 4 <= pe.Length, "Flags field must fit inside the buffer.");
        Assert.Null(error);
    }

    [Fact]
    public void TryApply64BitProbeCorFlags_Clears_32Bit_Flags_On_Real_Assembly()
    {
        var pe = LoadRealManagedAssemblyBytes();

        Assert.True(CorFlagsRewriter.TryApply64BitProbeCorFlags(pe, out var offset, out var error));
        Assert.True(error is "APPLIED" or "ALREADY_CLEARED", $"Unexpected reason: {error}");

        var flags = BitConverter.ToUInt32(pe, offset);
        Assert.Equal(0u, flags & ComImageFlags32BitRequired);
        Assert.Equal(0u, flags & ComImageFlags32BitPreferred);
    }

    [Fact]
    public void TryReadCliFlags_Returns_Flags_On_Real_Managed_Assembly()
    {
        var pe = LoadRealManagedAssemblyBytes();

        Assert.True(CorFlagsRewriter.TryReadCliFlags(pe, out var flags, out var error));
        Assert.Null(error);
        // Any real managed assembly carries ILONLY; the value itself is data.
        Assert.NotEqual(0u, flags & 0x00000001u);
    }

    [Fact]
    public void TryReadCliFlags_On_Non_Pe_Buffer_Returns_False_With_Error()
    {
        var garbage = new byte[512];

        Assert.False(CorFlagsRewriter.TryReadCliFlags(garbage, out var flags, out var error));
        Assert.Equal(0u, flags);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryGetCliFlagsOffset_On_Non_Pe_Buffer_Returns_False_With_Error()
    {
        var garbage = new byte[512];

        Assert.False(CorFlagsRewriter.TryGetCliFlagsOffset(garbage, out var offset, out var error));
        Assert.Equal(-1, offset);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void RvaToFileOffset_On_Non_Pe_Buffer_Returns_False_With_Error()
    {
        var garbage = new byte[512];

        Assert.False(CorFlagsRewriter.RvaToFileOffset(garbage, 0x1000, out var fileOffset, out var error));
        Assert.Equal(-1, fileOffset);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
