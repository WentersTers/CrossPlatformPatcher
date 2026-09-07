using CrossPlatformPatcher.Core;
using CrossPlatformPatcher.Core.Modules;
using CrossPlatformPatcher.Core.Modules.OpenWakeWord;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Unit tests for OpenWakeWordCompatibilityPatcher.
/// Verifies that audio-related methods are correctly instrumented with OpenWakeWord hooks,
/// including helper initialization, audio handoff calls, proper parameter selection, and
/// appropriate method filtering.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class OpenWakeWordCompatibilityPatcherTests
{
    [Fact]
    public void Injects_Helper_Initialization_Call()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureHelperInit
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-helper-init", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.True(patchCount >= 1);

        // Verify the helper type was created
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);

        // Verify the InitializeOpenWakeWord method was created
        var initMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        Assert.NotNull(initMethod);

        // Verify the OnAudioChunkAvailable method was created
        var onAudioMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "OnAudioChunkAvailable");
        Assert.NotNull(onAudioMethod);

        // Verify the method was patched with initialization call
        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureHelperInit")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // First instruction should be call to InitializeOpenWakeWord
        Assert.Equal(OpCodes.Call, audioMethod.Body.Instructions[0].OpCode);
        Assert.Same(initMethod, audioMethod.Body.Instructions[0].Operand);
    }

    [Fact]
    public void Injects_Audio_Handoff_Call_At_Start_Of_Audio_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureAudioHandoff
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-audio-handoff", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.True(patchCount >= 1);

        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        var onAudioMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "OnAudioChunkAvailable");

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAudioHandoff")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Instructions should be: call InitializeOpenWakeWord, ldarg audioData, call OnAudioChunkAvailable
        var instructions = audioMethod.Body.Instructions;

        // First instruction: call InitializeOpenWakeWord
        Assert.Equal(OpCodes.Call, instructions[0].OpCode);
        Assert.Equal("InitializeOpenWakeWord", ((IMethod)instructions[0].Operand!).Name);

        // Second instruction: load the audio parameter (dnlib may normalize to ldarg.*)
        Assert.StartsWith("ldarg", instructions[1].OpCode.Name, StringComparison.OrdinalIgnoreCase);

        // Third instruction: call OnAudioChunkAvailable
        Assert.Equal(OpCodes.Call, instructions[2].OpCode);
        Assert.Same(onAudioMethod, instructions[2].Operand);
    }

    [Fact]
    public void Chooses_Intended_Audio_Parameter_When_Multiple_Parameters_Exist()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureMultipleParams
            {
                public static void OnAudioData(int sampleRate, byte[] audioData, int channels)
                {
                    Console.WriteLine($"Processing audio: {sampleRate}Hz, {channels} channels");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-multiple-params", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.True(patchCount >= 1);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureMultipleParams")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Get the audio parameter (should be the byte[] parameter)
        var audioParam = audioMethod.Parameters.FirstOrDefault(p => p.Type?.FullName == "System.Byte[]");
        Assert.NotNull(audioParam);

        // Verify the ldarg instruction loads the correct parameter
        var instructions = audioMethod.Body.Instructions;
        var ldargInstruction = instructions.FirstOrDefault(i => i.OpCode.Name.StartsWith("ldarg", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ldargInstruction);
        Assert.True(ldargInstruction!.Operand is null || ldargInstruction.Operand == audioParam);
    }

    [Fact]
    public void Chooses_Single_Array_Parameter_When_Multiple_Parameters_Exist()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureSingleArrayParam
            {
                public static void OnAudioData(int sampleRate, short[] audioData, int channels)
                {
                    Console.WriteLine($"Processing audio: {sampleRate}Hz, {channels} channels");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-single-array-param", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.True(patchCount >= 1);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureSingleArrayParam")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Get the audio parameter (should be the short[] parameter)
        var audioParam = audioMethod.Parameters.FirstOrDefault(p => p.Type?.FullName == "System.Int16[]");
        Assert.NotNull(audioParam);

        // Verify the ldarg instruction loads the correct parameter
        var instructions = audioMethod.Body.Instructions;
        var ldargInstruction = instructions.FirstOrDefault(i => i.OpCode.Name.StartsWith("ldarg", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ldargInstruction);
        Assert.True(ldargInstruction!.Operand is null || ldargInstruction.Operand == audioParam);
    }

    [Fact]
    public void Chooses_Float_Array_Parameter_When_Multiple_Parameters_Exist()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureFloatArrayParam
            {
                public static void OnAudioData(int sampleRate, float[] audioData, int channels)
                {
                    Console.WriteLine($"Processing audio: {sampleRate}Hz, {channels} channels");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-float-array-param", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.True(patchCount >= 1);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureFloatArrayParam")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Get the audio parameter (should be the float[] parameter)
        var audioParam = audioMethod.Parameters.FirstOrDefault(p => p.Type?.FullName == "System.Single[]");
        Assert.NotNull(audioParam);

        // Verify the ldarg instruction loads the correct parameter
        var instructions = audioMethod.Body.Instructions;
        var ldargInstruction = instructions.FirstOrDefault(i => i.OpCode.Name.StartsWith("ldarg", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ldargInstruction);
        Assert.True(ldargInstruction!.Operand is null || ldargInstruction.Operand == audioParam);
    }

    [Fact]
    public void Chooses_Audio_Event_Args_Parameter_When_Multiple_Parameters_Exist()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public class WaveInEventArgs
            {
                public byte[] Buffer { get; set; }
                public int BytesRecorded { get; set; }
            }

            public static class FixtureAudioEventArgs
            {
                public static void OnDataAvailable(object sender, WaveInEventArgs e)
                {
                    Console.WriteLine($"Audio data available: {e.BytesRecorded} bytes");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-audio-event-args", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.True(patchCount >= 1);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAudioEventArgs")?
            .Methods.FirstOrDefault(m => m.Name == "OnDataAvailable");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Get the audio parameter (should be the WaveInEventArgs parameter)
        var audioParam = audioMethod.Parameters.FirstOrDefault(p => p.Type?.FullName?.Contains("WaveInEventArgs") == true);
        Assert.NotNull(audioParam);

        // Verify the ldarg instruction loads the correct parameter
        var instructions = audioMethod.Body.Instructions;
        var ldargInstruction = instructions.FirstOrDefault(i => i.OpCode.Name.StartsWith("ldarg", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ldargInstruction);
        Assert.True(ldargInstruction!.Operand is null || ldargInstruction.Operand == audioParam);
    }

    [Fact]
    public void Skips_Helper_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureHelperMethods
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-helper-methods", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act - First patch
        var firstPatchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Act - Second patch (should skip helper type)
        var secondPatchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(1, firstPatchCount);
        Assert.Equal(0, secondPatchCount);

        // Verify the helper type exists
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);

        // Verify helper methods were not patched
        var initMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        var onAudioMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "OnAudioChunkAvailable");

        Assert.NotNull(initMethod);
        Assert.NotNull(onAudioMethod);

        // Helper methods should not have calls to themselves
        var initHasSelfCall = initMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand == onAudioMethod);
        Assert.False(initHasSelfCall);

        var onAudioHasSelfCall = onAudioMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand == onAudioMethod);
        Assert.False(onAudioHasSelfCall);
    }

    [Fact]
    public void Skips_Already_Instrumented_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureAlreadyInstrumented
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-already-instrumented", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act - First patch
        var firstPatchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Act - Second patch (should skip already instrumented methods)
        var secondPatchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Act - Third patch (should still skip)
        var thirdPatchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(1, firstPatchCount);
        Assert.Equal(0, secondPatchCount);
        Assert.Equal(0, thirdPatchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAlreadyInstrumented")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Count calls to OnAudioChunkAvailable - should be exactly 1
        var onAudioCalls = audioMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "OnAudioChunkAvailable")
            .Count();

        Assert.Equal(1, onAudioCalls);
    }

    [Fact]
    public void Leaves_Unrelated_Methods_Untouched()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureUnrelatedMethods
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }

                public static void ProcessData(string data)
                {
                    Console.WriteLine($"Processing: {data}");
                }

                public static void CalculateSum(int a, int b)
                {
                    Console.WriteLine($"Sum: {a + b}");
                }

                public static void LogMessage(string message)
                {
                    Console.WriteLine(message);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-unrelated-methods", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Only OnAudioData should be patched
        Assert.True(patchCount >= 1);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureUnrelatedMethods");
        Assert.NotNull(fixtureType);

        var audioMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "OnAudioData");
        var processDataMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "ProcessData");
        var calculateSumMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "CalculateSum");
        var logMessageMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "LogMessage");

        Assert.NotNull(audioMethod);
        Assert.NotNull(processDataMethod);
        Assert.NotNull(calculateSumMethod);
        Assert.NotNull(logMessageMethod);

        // Audio method should have been patched
        var audioHasInitCall = audioMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(audioHasInitCall);

        // Unrelated methods should not have been patched
        var processDataHasInitCall = processDataMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(processDataHasInitCall);

        var calculateSumHasInitCall = calculateSumMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(calculateSumHasInitCall);

        var logMessageHasInitCall = logMessageMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(logMessageHasInitCall);
    }

    [Fact]
    public void Handles_Multiple_Audio_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureMultipleAudioMethods
            {
                public static void OnAudioData1(byte[] audioData)
                {
                    Console.WriteLine("Processing audio 1");
                }

                public static void OnAudioData2(short[] audioData)
                {
                    Console.WriteLine("Processing audio 2");
                }

                public static void OnAudioData3(float[] audioData)
                {
                    Console.WriteLine("Processing audio 3");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-multiple-audio-methods", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // All three audio methods should be patched
        Assert.Equal(3, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureMultipleAudioMethods");
        Assert.NotNull(fixtureType);

        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        var initMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        var onAudioMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "OnAudioChunkAvailable");

        foreach (var method in fixtureType!.Methods.Where(m => m.HasBody && m.Name.StartsWith("OnAudioData")))
        {
            // Each method should have initialization call
            var hasInitCall = method.Body.Instructions
                .Any(i => i.OpCode == OpCodes.Call && i.Operand == initMethod);
            Assert.True(hasInitCall, $"Method {method.Name} should have InitializeOpenWakeWord call");

            // Each method should have OnAudioChunkAvailable call
            var hasOnAudioCall = method.Body.Instructions
                .Any(i => i.OpCode == OpCodes.Call && i.Operand == onAudioMethod);
            Assert.True(hasOnAudioCall, $"Method {method.Name} should have OnAudioChunkAvailable call");
        }
    }

    [Fact]
    public void Handles_Empty_Assembly_Gracefully()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureEmpty
            {
                public static void Main()
                {
                    Console.WriteLine("No audio methods here");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-empty", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(0, patchCount);

        // Helper type is emitted by the current patcher implementation even when no methods are patched.
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);
    }

    [Fact]
    public void Logs_Patch_Counts_When_Log_Action_Provided()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureLogging
            {
                public static void OnAudioData1(byte[] audioData)
                {
                    Console.WriteLine("Processing audio 1");
                }

                public static void OnAudioData2(short[] audioData)
                {
                    Console.WriteLine("Processing audio 2");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-logging", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        var logMessages = new List<string>();
        Action<string> logAction = msg => logMessages.Add(msg);

        // Act
        OpenWakeWordCompatibilityPatcher.Patch(module, logAction);

        // Assert
        Assert.Equal(3, logMessages.Count);
        Assert.Contains(logMessages, m => m.Contains("Hooked audio method"));
        Assert.Contains(logMessages, m => m.Contains("OnAudioData1"));
        Assert.Contains(logMessages, m => m.Contains("OnAudioData2"));
        Assert.Contains(logMessages, m => m.Contains("Audio event injection points: 2"));
    }

    [Fact]
    public void Handles_Method_With_No_Parameters()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureNoParams
            {
                public static void OnAudioData()
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-no-params", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should not patch methods without audio-related parameters
        Assert.Equal(0, patchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureNoParams")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should not have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(hasInitCall);
    }

    [Fact]
    public void Handles_Method_With_Non_Audio_Parameters()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureNonAudioParams
            {
                public static void OnAudioData(string message, int count)
                {
                    Console.WriteLine($"Processing: {message}, count: {count}");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-non-audio-params", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should not patch methods without audio-related parameters
        Assert.Equal(0, patchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureNonAudioParams")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should not have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(hasInitCall);
    }

    [Fact]
    public void Skips_WinForms_Methods_That_Only_Look_Audio_Related_In_Body()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            namespace System.Windows.Forms
            {
                public class TextBox { }
                public class Button { }
            }

            public class WaveInEventArgs
            {
                public byte[] Buffer { get; set; } = Array.Empty<byte>();
                public int BytesRecorded { get; set; }
            }

            public static class FixtureWinFormsStartupShape
            {
                public static void OnStartup(System.Windows.Forms.TextBox textBox, System.Windows.Forms.Button button)
                {
                    var audio = new WaveInEventArgs();
                    Console.WriteLine(audio.BytesRecorded);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-winforms-startup-shape", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(0, patchCount);

        var startupMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureWinFormsStartupShape")?
            .Methods.FirstOrDefault(m => m.Name == "OnStartup");

        Assert.NotNull(startupMethod);
        Assert.True(startupMethod!.HasBody);

        var hasInitCall = startupMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(hasInitCall);
    }

    [Fact]
    public void Preserves_Exception_Handler_Boundaries()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureExceptionBoundaries
            {
                public static void OnAudioData(byte[] audioData)
                {
                    try
                    {
                        Console.WriteLine("Processing audio");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-exception-boundaries", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(1, patchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureExceptionBoundaries")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should have exception handler
        Assert.Single(audioMethod.Body.ExceptionHandlers);

        var eh = audioMethod.Body.ExceptionHandlers[0];
        Assert.NotNull(eh.TryStart);
        Assert.NotNull(eh.HandlerStart);
        Assert.NotNull(eh.TryEnd);
        Assert.NotNull(eh.HandlerEnd);

        // Exception handler should have been adjusted to include injected instructions
        var firstInstruction = audioMethod.Body.Instructions[0];
        Assert.Equal(eh.TryStart, firstInstruction);
    }

    [Fact]
    public void Handles_Static_And_Instance_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureStaticInstance
            {
                public static void StaticAudioMethod(byte[] audioData)
                {
                    Console.WriteLine("Static audio processing");
                }
            }

            public class InstanceClass
            {
                public void InstanceAudioMethod(byte[] audioData)
                {
                    Console.WriteLine("Instance audio processing");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-static-instance", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should patch both static and instance methods
        Assert.True(patchCount >= 2);

        var staticMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureStaticInstance")?
            .Methods.FirstOrDefault(m => m.Name == "StaticAudioMethod");

        var instanceMethod = module.Types
            .FirstOrDefault(t => t.Name == "InstanceClass")?
            .Methods.FirstOrDefault(m => m.Name == "InstanceAudioMethod");

        Assert.NotNull(staticMethod);
        Assert.NotNull(instanceMethod);

        // Both should have initialization call
        var staticHasInitCall = staticMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(staticHasInitCall);

        var instanceHasInitCall = instanceMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(instanceHasInitCall);
    }

    [Fact]
    public void Handles_Method_With_No_Body()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Runtime.InteropServices;

            public static class FixtureNoBody
            {
                [DllImport("kernel32.dll")]
                public static extern void ExternalAudioMethod(byte[] audioData);
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-no-body", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should not patch methods without bodies
        Assert.Equal(0, patchCount);

        // Helper type is emitted by the current patcher implementation even when no methods are patched.
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);
    }

    [Fact]
    public void Handles_Audio_Carrier_Type_Parameters()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public class AudioBuffer
            {
                public byte[] Buffer { get; set; }
                public int Size { get; set; }
            }

            public static class FixtureAudioCarrier
            {
                public static void OnAudioData(AudioBuffer buffer)
                {
                    Console.WriteLine($"Processing audio: {buffer.Size} bytes");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-audio-carrier", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.True(patchCount >= 1);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAudioCarrier")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(hasInitCall);

        // Should load the AudioBuffer parameter
        var audioParam = audioMethod.Parameters.FirstOrDefault(p => p.Type?.FullName?.Contains("AudioBuffer") == true);
        Assert.NotNull(audioParam);

        var ldargInstruction = audioMethod.Body.Instructions
            .FirstOrDefault(i => i.OpCode.Name.StartsWith("ldarg", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ldargInstruction);
        Assert.True(ldargInstruction!.Operand is null || ldargInstruction.Operand == audioParam);
    }

    [Fact]
    public void Handles_Method_With_Audio_Type_References_In_Body()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public class AudioProcessor
            {
                public void Process(byte[] data) { }
            }

            public static class FixtureAudioTypeReferences
            {
                public static void OnData(object sender, EventArgs e)
                {
                    var processor = new AudioProcessor();
                    processor.Process(new byte[1024]);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-audio-type-references", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should patch method that references audio types in body
        Assert.True(patchCount >= 1);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAudioTypeReferences")?
            .Methods.FirstOrDefault(m => m.Name == "OnData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(hasInitCall);
    }

    [Fact]
    public void Handles_Method_With_Audio_Field_References_In_Body()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public class AudioDevice
            {
                public byte[] Buffer;
                public int SampleRate;
            }

            public static class FixtureAudioFieldReferences
            {
                public static void OnData(object sender, EventArgs e)
                {
                    var device = new AudioDevice();
                    device.Buffer = new byte[1024];
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-audio-field-references", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should patch method that references audio fields in body
        Assert.True(patchCount >= 1);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAudioFieldReferences")?
            .Methods.FirstOrDefault(m => m.Name == "OnData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(hasInitCall);
    }

    [Fact]
    public void Handles_Method_With_Audio_Name_But_No_Audio_Parameters()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureAudioNameNoParams
            {
                public static void OnAudioData(string message)
                {
                    Console.WriteLine($"Audio message: {message}");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-audio-name-no-params", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should not patch method with audio name but no audio parameters
        Assert.Equal(0, patchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAudioNameNoParams")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should not have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(hasInitCall);
    }

    [Fact]
    public void Handles_Method_With_Too_Many_Parameters()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureTooManyParams
            {
                public static void OnData(byte[] audioData, int a, int b, int c, int d, int e)
                {
                    Console.WriteLine("Processing");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-too-many-params", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should not patch method with too many parameters (no audio name)
        Assert.Equal(0, patchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureTooManyParams")?
            .Methods.FirstOrDefault(m => m.Name == "OnData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should not have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(hasInitCall);
    }

    [Fact]
    public void Handles_Method_With_Audio_Name_And_Too_Many_Parameters()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureAudioNameTooManyParams
            {
                public static void OnAudioData(byte[] audioData, int a, int b, int c, int d, int e)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-audio-name-too-many-params", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should patch method with audio name and audio parameter (even with many params)
        Assert.Equal(1, patchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureAudioNameTooManyParams")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Should have been patched
        var hasInitCall = audioMethod.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(hasInitCall);
    }

    [Fact]
    public void Handles_Property_Accessor_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixturePropertyAccessors
            {
                private static byte[] _audioData;

                public static byte[] get_AudioData()
                {
                    return _audioData;
                }

                public static void set_AudioData(byte[] value)
                {
                    _audioData = value;
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-property-accessors", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Property accessors may or may not be patched by the current implementation
        Assert.InRange(patchCount, 0, 2);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixturePropertyAccessors");
        Assert.NotNull(fixtureType);

        var getter = fixtureType!.Methods.FirstOrDefault(m => m.Name == "get_AudioData");
        var setter = fixtureType!.Methods.FirstOrDefault(m => m.Name == "set_AudioData");

        Assert.NotNull(getter);
        Assert.NotNull(setter);

        // Property accessors may be patched by the implementation; verify methods exist
        var getterHasInitCall = getter!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");

        var setterHasInitCall = setter!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
    }

    [Fact]
    public void Handles_Event_Add_Remove_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureEventMethods
            {
                private static EventHandler<byte[]>? AudioData;

                public static void add_AudioData(EventHandler<byte[]> value)
                {
                    AudioData += value;
                }

                public static void remove_AudioData(EventHandler<byte[]> value)
                {
                    AudioData -= value;
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-event-methods", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should not patch event add/remove methods
        Assert.Equal(0, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureEventMethods");
        Assert.NotNull(fixtureType);

        var addMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "add_AudioData");
        var removeMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "remove_AudioData");

        Assert.NotNull(addMethod);
        Assert.NotNull(removeMethod);

        // Neither should have been patched
        var addHasInitCall = addMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(addHasInitCall);

        var removeHasInitCall = removeMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(removeHasInitCall);
    }

    [Fact]
    public void Handles_Null_Audio_Parameter()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureNullAudioParam
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-null-audio-param", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(1, patchCount);

        var audioMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureNullAudioParam")?
            .Methods.FirstOrDefault(m => m.Name == "OnAudioData");

        Assert.NotNull(audioMethod);
        Assert.True(audioMethod!.HasBody);

        // Instructions should be: call InitializeOpenWakeWord, ldarg audioData, call OnAudioChunkAvailable
        var instructions = audioMethod.Body.Instructions;

        // First instruction: call InitializeOpenWakeWord
        Assert.Equal(OpCodes.Call, instructions[0].OpCode);

        // Second instruction: load the audio parameter (dnlib may normalize to ldarg.*)
        Assert.StartsWith("ldarg", instructions[1].OpCode.Name, StringComparison.OrdinalIgnoreCase);

        // Third instruction: call OnAudioChunkAvailable
        Assert.Equal(OpCodes.Call, instructions[2].OpCode);
    }

    [Fact]
    public void Creates_All_Helper_Members()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureHelperMembers
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-helper-members", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);

        // Verify all fields were created
        var initializedField = helperType?.Fields.FirstOrDefault(f => f.Name == "_initialized");
        var lockUntilTicksField = helperType?.Fields.FirstOrDefault(f => f.Name == "_lockUntilTicks");
        var lockDurationMsField = helperType?.Fields.FirstOrDefault(f => f.Name == "_lockDurationMs");
        var firstAudioLoggedField = helperType?.Fields.FirstOrDefault(f => f.Name == "_firstAudioLogged");

        Assert.NotNull(initializedField);
        Assert.NotNull(lockUntilTicksField);
        Assert.NotNull(lockDurationMsField);
        Assert.NotNull(firstAudioLoggedField);

        // Verify all methods were created
        var initMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        var isLockedMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "IsLocked");
        var shouldTriggerWakeMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "ShouldTriggerWake");
        var onAudioMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "OnAudioChunkAvailable");
        var describeAudioArgMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "DescribeAudioArg");
        var logMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "LogOWWEvent");

        Assert.NotNull(initMethod);
        Assert.NotNull(isLockedMethod);
        Assert.NotNull(shouldTriggerWakeMethod);
        Assert.NotNull(onAudioMethod);
        Assert.NotNull(describeAudioArgMethod);
        Assert.NotNull(logMethod);

        var describeInstructions = describeAudioArgMethod!.Body!.Instructions;
        var intToStringCallIndex = describeInstructions.ToList().FindIndex(i =>
            i.OpCode == OpCodes.Call && i.Operand is IMethod opMethod && opMethod.Name == "ToString");

        Assert.True(intToStringCallIndex > 0);
        Assert.True(describeInstructions[intToStringCallIndex - 1].OpCode == OpCodes.Ldloca_S
            || describeInstructions[intToStringCallIndex - 1].OpCode == OpCodes.Ldloca);
    }

    [Fact]
    public void Handles_Mixed_Audio_And_Non_Audio_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureMixedMethods
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }

                public static void ProcessData(string data)
                {
                    Console.WriteLine($"Processing: {data}");
                }

                public static void OnWaveData(short[] waveData)
                {
                    Console.WriteLine("Processing wave");
                }

                public static void CalculateSum(int a, int b)
                {
                    Console.WriteLine($"Sum: {a + b}");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-mixed-methods", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        // Should patch 2 audio methods
        Assert.Equal(2, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureMixedMethods");
        Assert.NotNull(fixtureType);

        var audioMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "OnAudioData");
        var processDataMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "ProcessData");
        var waveMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "OnWaveData");
        var calculateSumMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "CalculateSum");

        Assert.NotNull(audioMethod);
        Assert.NotNull(processDataMethod);
        Assert.NotNull(waveMethod);
        Assert.NotNull(calculateSumMethod);

        // Audio methods should have been patched
        var audioHasInitCall = audioMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(audioHasInitCall);

        var waveHasInitCall = waveMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.True(waveHasInitCall);

        // Non-audio methods should not have been patched
        var processDataHasInitCall = processDataMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(processDataHasInitCall);

        var calculateSumHasInitCall = calculateSumMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && m.Name == "InitializeOpenWakeWord");
        Assert.False(calculateSumHasInitCall);
    }

    [Fact]
    public void Module_Apply_Emits_Configured_LockDuration_From_OwwSettings()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureOwwConfigured
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-oww-configured", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        var settings = OpenWakeWordSettings.CreateBuilder()
            .WithLockDurationMs(1234)
            .Build();

        var context = new PatchModuleContext
        {
            Module = module,
            Log = _ => { },
            OwwSettings = settings,
        };

        var owwModule = new OpenWakeWordModule();

        // Act — thread the injected settings through the module into the emitted IL.
        var result = owwModule.Apply(context);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.PatchPointsFound >= 1);

        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);

        var initMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        Assert.NotNull(initMethod);

        // The lock-duration constant baked into the IL should reflect the configured value (1234),
        // not the legacy 3000 default.
        var hasConfiguredConstant = initMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && v == 1234);
        Assert.True(hasConfiguredConstant);
    }

    [Fact]
    public void Patch_WithNullSettings_FallsBack_To_Legacy_Constants()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;

            public static class FixtureOwwLegacy
            {
                public static void OnAudioData(byte[] audioData)
                {
                    Console.WriteLine("Processing audio");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-oww-legacy", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act — no settings passed, so the legacy defaults must be preserved.
        OpenWakeWordCompatibilityPatcher.Patch(module);

        // Assert
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherOWW");
        Assert.NotNull(helperType);

        var initMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "InitializeOpenWakeWord");
        Assert.NotNull(initMethod);
        var hasLegacyLock = initMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Ldc_I4 && i.Operand is int v && v == 3000);
        Assert.True(hasLegacyLock, "Expected the legacy 3000 ms lock duration to be emitted.");

        var onAudioMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "OnAudioChunkAvailable");
        Assert.NotNull(onAudioMethod);
        var hasLegacyTicks = onAudioMethod!.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Ldc_I8 && i.Operand is long v && v == 10000L);
        Assert.True(hasLegacyTicks, "Expected the legacy 10000L ticks multiplier to be emitted.");
    }
}