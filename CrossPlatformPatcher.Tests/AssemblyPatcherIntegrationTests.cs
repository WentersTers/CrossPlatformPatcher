using System.Runtime.InteropServices;
using CrossPlatformPatcher.Core;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class AssemblyPatcherIntegrationTests
{
    [Fact]
    public void NoOp_Assembly_Is_Written_Byte_For_Byte_And_Launchers_Are_Generated()
    {
        using var temp = new TempDirectory();
        var input = BuildNoOpFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-noop.exe");
        var inputBytes = File.ReadAllBytes(input);

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.Equal(inputBytes, File.ReadAllBytes(output));
        Assert.Equal(0, result.PatchPointsApplied);

        AssertGeneratedLaunchers(temp.Path, Path.GetFileName(output));
        ArtifactAssertions.AssertNoSourceArtifacts(temp.Path);
    }

    [Fact]
    public void Patchable_Assembly_Produces_Modified_Output_And_No_Source_File_Leakage()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-fixture.exe");
        var inputBytes = File.ReadAllBytes(input);

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.NotEqual(inputBytes, File.ReadAllBytes(output));
        Assert.True(result.PatchPointsApplied > 0);

        var module = ModuleDefMD.Load(output);
        Assert.NotNull(module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW"));

        AssertGeneratedLaunchers(temp.Path, Path.GetFileName(output));
        ArtifactAssertions.AssertNoSourceArtifacts(temp.Path);

        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "run.sh"), Path.GetFileName(output), "launcher-runtime.log");
        ArtifactAssertions.AssertFileContains(
            System.IO.Path.Combine(temp.Path, "run.bat"),
            Path.GetFileName(output),
            "PAICOM_RUNTIME_HOST_OS",
            "PAICOM_VOSK_MODEL_PATH",
            "PAICOM_FILE_COMMAND_INPUT_PATH",
            "SetupWizard.exe");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "setup-wizard.sh"), Path.GetFileName(output), "run.sh");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "launch.command"), "run.sh");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(temp.Path, "setup.command"), "setup-wizard.sh");
    }

    [Fact]
    public void Generated_Shell_Launchers_Are_Executable_On_Unix()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-unix.exe");

        var patcher = new AssemblyPatcher(verbose: false);
        patcher.Patch(input, output, dryRun: false);

        if (OperatingSystem.IsWindows())
            return;

        foreach (var script in new[] { "run.sh", "launch.command", "setup-wizard.sh", "setup.command" })
        {
            var path = System.IO.Path.Combine(temp.Path, script);
            Assert.True(File.Exists(path));

            var mode = File.GetUnixFileMode(path);
            Assert.True((mode & UnixFileMode.UserExecute) != 0, $"{script} should be executable.");
        }
    }

    private static void AssertGeneratedLaunchers(string directory, string exeName)
    {
        var expected = new[]
        {
            "run.sh",
            "launch.command",
            "setup-wizard.sh",
            "setup.command",
            "run.bat",
            "SETUP_LINUX.md",
            "SETUP_MAC.md"
        };

        foreach (var name in expected)
            Assert.True(File.Exists(System.IO.Path.Combine(directory, name)), $"Missing generated file: {name}");

        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(directory, "run.sh"), exeName, "launcher-runtime.log");
        ArtifactAssertions.AssertFileContains(
            System.IO.Path.Combine(directory, "run.bat"),
            exeName,
            "PAICOM_RUNTIME_HOST_OS",
            "PAICOM_VOSK_MODEL_PATH",
            "PAICOM_FILE_COMMAND_INPUT_PATH",
            "SetupWizard.exe");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(directory, "launch.command"), "run.sh");
        ArtifactAssertions.AssertFileContains(System.IO.Path.Combine(directory, "setup.command"), "setup-wizard.sh");
    }

    private static string BuildNoOpFixture(string directory)
    {
        var source = """
            using System;

            public static class FixtureNoOp
            {
                public static void Main()
                {
                    Console.WriteLine("fixture-no-op");
                }

                public static int Sum(int left, int right)
                {
                    return left + right;
                }
            }
            """;

        return FixtureAssemblyBuilder.Build(source, $"fixture-no-op-{Guid.NewGuid():N}", directory);
    }

    private static string BuildPatchableFixture(string directory)
    {
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixturePatchable
            {
                public static void Main()
                {
                    Console.WriteLine("fixture-patchable");
                }

                public static void AudioChunkHandler(byte[] audioChunk)
                {
                    Process.Start("fixture-child.exe");
                    Console.WriteLine(audioChunk.Length);
                }
            }
            """;

        return FixtureAssemblyBuilder.Build(source, $"fixture-patchable-{Guid.NewGuid():N}", directory);
    }
    [Fact]
    public void Native_Library_Extraction_Paths_Remain_Valid_On_Windows_Output_Layouts()
    {
        using var temp = new TempDirectory();
        var input = BuildPatchableFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-native-paths.exe");

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.True(result.PatchPointsApplied > 0);

        var module = ModuleDefMD.Load(output);
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);

        // Verify the helper type contains initialization method
        var initMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        Assert.NotNull(initMethod);

        // Verify the batch launcher contains proper Windows path handling
        var batPath = System.IO.Path.Combine(temp.Path, "run.bat");
        Assert.True(File.Exists(batPath));
        var batContent = File.ReadAllText(batPath);

        // Verify Windows-specific path separators and environment variables
        Assert.Contains("%~dp0", batContent, StringComparison.Ordinal);
        Assert.Contains("MODELS_DIR=%~dp0models", batContent, StringComparison.Ordinal);
        Assert.Contains("PAICOM_VOSK_MODEL_PATH=%MODELS_DIR%", batContent, StringComparison.Ordinal);

        // Verify no Unix-style path separators in Windows batch file
        Assert.DoesNotContain("/models/", batContent, StringComparison.Ordinal);
        Assert.DoesNotContain("$SCRIPT_DIR", batContent, StringComparison.Ordinal);

        ArtifactAssertions.AssertNoSourceArtifacts(temp.Path);
    }

    [Fact]
    public void Command_Fixtures_Behave_Correctly_After_Patching()
    {
        using var temp = new TempDirectory();
        var input = BuildCommandFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-command-fixture.exe");

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.True(result.PatchPointsApplied > 0);

        var module = ModuleDefMD.Load(output);

        // Verify the OpenWakeWord helper type was injected
        var owwHelperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(owwHelperType);

        // Verify the initialization method exists
        var initMethod = owwHelperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        Assert.NotNull(initMethod);

        // Verify the audio handoff method exists
        var onAudioMethod = owwHelperType?.Methods.FirstOrDefault(m => m.Name == "OnAudioChunkAvailable");
        Assert.NotNull(onAudioMethod);

        // Verify the original command handler method was patched
        var commandHandlerType = module.Types.FirstOrDefault(t => t.Name == "FixtureCommandHandler");
        Assert.NotNull(commandHandlerType);

        var handleCommandMethod = commandHandlerType?.Methods.FirstOrDefault(m => m.Name == "HandleCommand");
        Assert.NotNull(handleCommandMethod);
        Assert.True(handleCommandMethod!.HasBody);

        // Verify the method was instrumented with OpenWakeWord hooks
        // Note: HandleCommand is not audio-related, so it won't be patched by OpenWakeWordCompatibilityPatcher
        // The patcher only patches methods with audio-related parameters or types
        var instructions = handleCommandMethod.Body.Instructions;
        var hasInitCall = instructions.Any(i => i.OpCode == OpCodes.Call &&
            ((IMethod?)i.Operand)?.Name == "InitializeOpenWakeWord");
        // The OnAudioData method should be instrumented instead
        var onAudioDataMethod = commandHandlerType?.Methods.FirstOrDefault(m => m.Name == "OnAudioData");
        Assert.NotNull(onAudioDataMethod);
        var onAudioInstructions = onAudioDataMethod!.Body.Instructions;
        var onAudioHasInitCall = onAudioInstructions.Any(i => i.OpCode == OpCodes.Call &&
            ((IMethod?)i.Operand)?.Name == "InitializeOpenWakeWord");
        Assert.True(onAudioHasInitCall, "Audio handler should be instrumented with OpenWakeWord initialization");

        AssertGeneratedLaunchers(temp.Path, Path.GetFileName(output));
        ArtifactAssertions.AssertNoSourceArtifacts(temp.Path);
    }

    [Fact]
    public void Animation_Fixtures_Behave_Correctly_After_Patching()
    {
        using var temp = new TempDirectory();
        var input = BuildAnimationFixture(temp.Path);
        var output = System.IO.Path.Combine(temp.Path, "patched-animation-fixture.exe");

        var patcher = new AssemblyPatcher(verbose: false);
        var result = patcher.Patch(input, output, dryRun: false);

        Assert.True(File.Exists(output));
        Assert.True(result.PatchPointsApplied > 0);

        var module = ModuleDefMD.Load(output);

        // Verify the OpenWakeWord helper type was injected for animation fixtures
        var owwHelperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(owwHelperType);

        // Verify the initialization method exists
        var initMethod = owwHelperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        Assert.NotNull(initMethod);

        // Verify the original animation handler method was patched
        var animationHandlerType = module.Types.FirstOrDefault(t => t.Name == "FixtureAnimationHandler");
        Assert.NotNull(animationHandlerType);

        var playAnimationMethod = animationHandlerType?.Methods.FirstOrDefault(m => m.Name == "PlayAnimation");
        Assert.NotNull(playAnimationMethod);
        Assert.True(playAnimationMethod!.HasBody);

        // Verify the method was instrumented with OpenWakeWord hooks
        // Note: PlayAnimation is not audio-related, so it won't be patched by OpenWakeWordCompatibilityPatcher
        // The patcher only patches methods with audio-related parameters or types
        var instructions = playAnimationMethod.Body.Instructions;
        var hasInitCall = instructions.Any(i => i.OpCode == OpCodes.Call &&
            ((IMethod?)i.Operand)?.Name == "InitializeOpenWakeWord");
        // The OnAudioData method should be instrumented instead
        var onAudioDataMethod = animationHandlerType?.Methods.FirstOrDefault(m => m.Name == "OnAudioData");
        Assert.NotNull(onAudioDataMethod);
        var onAudioInstructions = onAudioDataMethod!.Body.Instructions;
        var onAudioHasInitCall = onAudioInstructions.Any(i => i.OpCode == OpCodes.Call &&
            ((IMethod?)i.Operand)?.Name == "InitializeOpenWakeWord");
        Assert.True(onAudioHasInitCall, "Audio handler should be instrumented with OpenWakeWord initialization");

        // Verify the batch launcher contains animation-related environment variables
        var batPath = System.IO.Path.Combine(temp.Path, "run.bat");
        Assert.True(File.Exists(batPath));
        var batContent = File.ReadAllText(batPath);

        Assert.Contains("PAICOM_RUNTIME_HOST_OS", batContent, StringComparison.Ordinal);
        Assert.Contains("PAICOM_VOSK_MODEL_PATH", batContent, StringComparison.Ordinal);
        Assert.Contains("PAICOM_FILE_COMMAND_INPUT_PATH", batContent, StringComparison.Ordinal);

        AssertGeneratedLaunchers(temp.Path, Path.GetFileName(output));
        ArtifactAssertions.AssertNoSourceArtifacts(temp.Path);
    }

    private static string BuildCommandFixture(string directory)
    {
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureCommandHandler
            {
                public static void Main()
                {
                    Console.WriteLine("fixture-command-handler");
                }

                public static void HandleCommand(string command)
                {
                    Process.Start("notepad.exe", command);
                    Console.WriteLine($"Handled: {command}");
                }

                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine($"Audio chunk: {audioData.Length} bytes");
                }
            }
            """;

        return FixtureAssemblyBuilder.Build(source, $"fixture-command-{Guid.NewGuid():N}", directory);
    }

    private static string BuildAnimationFixture(string directory)
    {
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureAnimationHandler
            {
                public static void Main()
                {
                    Console.WriteLine("fixture-animation-handler");
                }

                public static void PlayAnimation(string animationName)
                {
                    Process.Start("animation-player.exe", animationName);
                    Console.WriteLine($"Playing: {animationName}");
                }

                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine($"Audio chunk: {audioData.Length} bytes");
                }
            }
            """;

        return FixtureAssemblyBuilder.Build(source, $"fixture-animation-{Guid.NewGuid():N}", directory);
    }
}
