using System.Security.Cryptography;
using CrossPlatformPatcher.Cli;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class TripwireTests
{
    [Fact]
    public void Evaluate_Match_Is_Case_Insensitive()
    {
        var upper = new string('A', 64);
        Assert.Equal(TripwireCheck.TripwireVerdict.Match,
            TripwireCheck.Evaluate(upper.ToLowerInvariant(), upper));
    }

    [Fact]
    public void Evaluate_Mismatch_On_Differing_Hash()
    {
        Assert.Equal(TripwireCheck.TripwireVerdict.Mismatch,
            TripwireCheck.Evaluate(new string('A', 64), new string('B', 64)));
    }

    [Fact]
    public void Evaluate_UnknownBaseline_On_Missing_Or_Garbage()
    {
        Assert.Equal(TripwireCheck.TripwireVerdict.UnknownBaseline,
            TripwireCheck.Evaluate(new string('A', 64), null));
        Assert.Equal(TripwireCheck.TripwireVerdict.UnknownBaseline,
            TripwireCheck.Evaluate(new string('A', 64), "not-a-hash"));
        Assert.Equal(TripwireCheck.TripwireVerdict.UnknownBaseline,
            TripwireCheck.Evaluate(new string('A', 64), ""));
    }

    [Fact]
    public void Evaluate_Mismatch_When_Current_Unreadable()
    {
        Assert.Equal(TripwireCheck.TripwireVerdict.Mismatch,
            TripwireCheck.Evaluate(null, new string('A', 64)));
    }

    [Fact]
    public void TryParseBaseline_Roundtrips_Writer_Format()
    {
        Assert.True(TripwireCheck.TryParseBaseline(
            $"sha256:{new string('C', 64)}\nutc:2026-09-16T00:00:00Z\n", out var hash));
        Assert.Equal(new string('C', 64), hash);
        Assert.False(TripwireCheck.TryParseBaseline("garbage", out _));
        Assert.False(TripwireCheck.TryParseBaseline(null, out _));
    }

    [Fact]
    public void WriteBaseline_File_Matches_Output_Hash_And_Parses()
    {
        using var temp = new TempDirectory();
        var target = System.IO.Path.Combine(temp.Path, "subject.exe");
        var bytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02 };
        File.WriteAllBytes(target, bytes);

        var hash = TripwireBaseline.WriteBaseline(target, _ => { });

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), hash);
        Assert.True(TripwireCheck.TryParseBaseline(
            File.ReadAllText(TripwireBaseline.BaselinePathFor(target)), out var parsed));
        Assert.Equal(hash, parsed);
    }

    [Fact]
    public void Successful_Patch_Run_Writes_Baseline_Beside_Output()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "tripwire.dll");

        var options = CliOptions.Parse([input, "--out", output]);
        var outcome = PatcherRunner.Run(options, _ => { });

        Assert.Equal(0, outcome.ExitCode);
        var baselinePath = TripwireBaseline.BaselinePathFor(output);
        Assert.True(File.Exists(baselinePath), "Baseline file should exist after a successful run.");
        Assert.True(TripwireCheck.TryParseBaseline(File.ReadAllText(baselinePath), out var parsed));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))), parsed);
    }

    [Fact]
    public void Dry_Run_Writes_No_Baseline()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "dryrun.dll");

        var options = CliOptions.Parse([input, "--dry-run", "--out", output]);
        var outcome = PatcherRunner.Run(options, _ => { });

        Assert.Equal(0, outcome.ExitCode);
        Assert.False(File.Exists(TripwireBaseline.BaselinePathFor(output)));
    }

    private static string BuildNoOpFixture(string directory)
    {
        var source = """
            using System;

            public static class FixtureTripwire
            {
                public static void Main()
                {
                    Console.WriteLine("fixture-tripwire");
                }
            }
            """;

        return FixtureAssemblyBuilder.Build(source, $"fixture-tripwire-{Guid.NewGuid():N}", directory);
    }
}
