using System;
using System.IO;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

public class ProtonWarningTests
{
    [Theory]
    [InlineData("proton-9.0-4f", true)]
    [InlineData("proton-9.0", true)]
    [InlineData("9.0-4f", true)]
    [InlineData("PROTON-9.0-4F", true)]
    [InlineData("proton-8.0-5d", false)]
    [InlineData("proton-7.0-6", false)]
    [InlineData("proton-10.0", false)]
    [InlineData("experimental-11.0-20261027b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProton9_MatchesOnlyMajorNine(string? version, bool expected)
        => Assert.Equal(expected, ProtonWarning.IsProton9(version));

    [Fact]
    public void TryGetProtonVersion_PrefersExplicitEnv()
    {
        var old = Environment.GetEnvironmentVariable("PROTON_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", "proton-9.0-4f");
            Assert.True(ProtonWarning.TryGetProtonVersion(out var v));
            Assert.Equal("proton-9.0-4f", v);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", old);
        }
    }

    [Fact]
    public void TryGetProtonVersion_ReadsToolPathVersionFile()
    {
        var oldV = Environment.GetEnvironmentVariable("PROTON_VERSION");
        var oldT = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS");
        var dir = Path.Combine(Path.GetTempPath(), "pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "version"), "proton-9.0-4f\n");
        try
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", null);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS", dir);
            Assert.True(ProtonWarning.TryGetProtonVersion(out var v));
            Assert.Equal("proton-9.0-4f", v);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", oldV);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS", oldT);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetProtonVersion_ReadsCompatDataVersionFile()
    {
        var oldV = Environment.GetEnvironmentVariable("PROTON_VERSION");
        var oldT = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS");
        var oldD = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH");
        var dir = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "version"), "9.0-203\n");
        try
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", null);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS", null);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_DATA_PATH", dir);
            Assert.True(ProtonWarning.TryGetProtonVersion(out var v));
            Assert.Equal("9.0-203", v);
            Assert.True(ProtonWarning.IsProton9(v));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", oldV);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS", oldT);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_DATA_PATH", oldD);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetProtonVersion_NoProtonEnv_ReturnsFalse()
    {
        var oldV = Environment.GetEnvironmentVariable("PROTON_VERSION");
        var oldT = Environment.GetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS");
        var oldD = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", null);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS", null);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_DATA_PATH", null);
            Assert.False(ProtonWarning.TryGetProtonVersion(out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROTON_VERSION", oldV);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_TOOL_PATHS", oldT);
            Environment.SetEnvironmentVariable("STEAM_COMPAT_DATA_PATH", oldD);
        }
    }

    [Fact]
    public void BuildMessage_NamesVersionAndRemedy()
    {
        var text = ProtonWarning.BuildMessage("proton-9.0-4f");
        Assert.Contains("proton-9.0-4f", text);
        Assert.Contains("Start button", text);
        Assert.Contains("Proton Experimental", text);
    }
}
