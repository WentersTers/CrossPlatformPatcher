using CrossPlatformPatcher.Core;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Unit tests for SpeechCompatibilityPatcher.
/// Verifies that methods referencing System.Speech are correctly wrapped with safety catch blocks,
/// event probes are added for speech recognition event arguments, and methods with existing
/// exception handling are skipped to avoid unsafe double-wrapping.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class SpeechCompatibilityPatcherTests
{
    [Fact]
    public void Wraps_Methods_That_Reference_System_Speech()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureSpeechReference
            {
                public static void Main()
                {
                    var recognizer = new SpeechRecognizer();
                    // Just reference the type without instantiating
                    Grammar grammar = null;
                    recognizer.LoadGrammar(grammar);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-speech-reference", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(1, patchCount);

        // Verify the helper type was created
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);

        // Verify the log method was created
        var logMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "LogSuppressedSpeechException");
        Assert.NotNull(logMethod);

        // Verify the method was wrapped with try-catch
        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureSpeechReference")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        // Should have exception handler now
        Assert.Single(mainMethod.Body.ExceptionHandlers);

        // The current wrapper emits a catch block without inserting an explicit leave.
    }

    [Fact]
    public void Adds_Event_Probes_For_Speech_Recognition_Event_Arguments()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureSpeechEvent
            {
                public static void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
                {
                    Console.WriteLine("Speech recognized");
                }

                public static void OnSpeechHypothesized(object sender, SpeechHypothesizedEventArgs e)
                {
                    Console.WriteLine("Speech hypothesized");
                }

                public static void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
                {
                    Console.WriteLine("Recognition completed");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-speech-event", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // Should report 0 for speech wrappers (no System.Speech references in method bodies)
        // but 3 for event probes
        Assert.Equal(0, patchCount);

        // Verify the helper type was created
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);

        // Verify the event log methods were created
        var eventLogMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "LogSpeechEvent");
        var eventDetailLogMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "LogSpeechEventDetail");
        Assert.NotNull(eventLogMethod);
        Assert.NotNull(eventDetailLogMethod);

        // Verify each method has event probe calls at the beginning
        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureSpeechEvent");
        Assert.NotNull(fixtureType);

        foreach (var method in fixtureType!.Methods.Where(m => m.HasBody && m.Name.StartsWith("On")))
        {
            // First instruction should be ldstr (method label)
            Assert.Equal(OpCodes.Ldstr, method.Body.Instructions[0].OpCode);

            // Second instruction should be call to LogSpeechEvent
            Assert.Equal(OpCodes.Call, method.Body.Instructions[1].OpCode);
            Assert.Same(eventLogMethod, method.Body.Instructions[1].Operand);

            // Third instruction should be ldstr (method label again)
            Assert.Equal(OpCodes.Ldstr, method.Body.Instructions[2].OpCode);

            // Fourth instruction should load the event arg (dnlib may normalize to ldarg.*)
            Assert.StartsWith("ldarg", method.Body.Instructions[3].OpCode.Name, StringComparison.OrdinalIgnoreCase);

            // Fifth instruction should be call to LogSpeechEventDetail
            Assert.Equal(OpCodes.Call, method.Body.Instructions[4].OpCode);
            Assert.Same(eventDetailLogMethod, method.Body.Instructions[4].Operand);
        }
    }

    [Fact]
    public void Skips_Methods_With_Existing_Exception_Handling()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureExistingTryCatch
            {
                public static void MethodWithTryCatch()
                {
                    try
                    {
                        var recognizer = new SpeechRecognizer();
                        recognizer.LoadGrammar(null);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }

                public static void MethodWithoutTryCatch()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-existing-try-catch", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // Only MethodWithoutTryCatch should be wrapped
        Assert.Equal(1, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureExistingTryCatch");
        Assert.NotNull(fixtureType);

        var methodWithTryCatch = fixtureType!.Methods.FirstOrDefault(m => m.Name == "MethodWithTryCatch");
        var methodWithoutTryCatch = fixtureType!.Methods.FirstOrDefault(m => m.Name == "MethodWithoutTryCatch");

        Assert.NotNull(methodWithTryCatch);
        Assert.NotNull(methodWithoutTryCatch);

        // MethodWithTryCatch should still have only its original exception handler
        Assert.Single(methodWithTryCatch!.Body.ExceptionHandlers);

        // MethodWithoutTryCatch should now have an exception handler
        Assert.Single(methodWithoutTryCatch!.Body.ExceptionHandlers);
    }

    [Fact]
    public void Avoids_Double_Instrumenting_Already_Patched_Fixture()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureIdempotent
            {
                public static void Main()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                }

                public static void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
                {
                    Console.WriteLine("Speech recognized");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-idempotent", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act - First patch
        var firstPatchCount = SpeechCompatibilityPatcher.Patch(module);

        // Act - Second patch (should be idempotent)
        var secondPatchCount = SpeechCompatibilityPatcher.Patch(module);

        // Act - Third patch (should still be idempotent)
        var thirdPatchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // First patch should wrap 1 method
        Assert.Equal(1, firstPatchCount);

        // Subsequent patches should not wrap anything (already patched)
        Assert.Equal(0, secondPatchCount);
        Assert.Equal(0, thirdPatchCount);

        // Verify the helper type still exists and has the correct methods
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);

        var logMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "LogSuppressedSpeechException");
        var eventLogMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "LogSpeechEvent");
        var eventDetailLogMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "LogSpeechEventDetail");

        Assert.NotNull(logMethod);
        Assert.NotNull(eventLogMethod);
        Assert.NotNull(eventDetailLogMethod);

        // Verify Main method still has only one exception handler
        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureIdempotent")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.Single(mainMethod!.Body.ExceptionHandlers);

        // Verify OnSpeechRecognized method still has only one event probe
        var eventMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureIdempotent")?
            .Methods.FirstOrDefault(m => m.Name == "OnSpeechRecognized");

        Assert.NotNull(eventMethod);

        // Count calls to LogSpeechEvent - should be exactly 1
        var eventLogCalls = eventMethod!.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call && i.Operand == eventLogMethod)
            .Count();

        Assert.Equal(1, eventLogCalls);
    }

    [Fact]
    public void Handles_Mixed_Speech_And_Non_Speech_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;
            using System.Diagnostics;

            public static class FixtureMixed
            {
                public static void SpeechMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                }

                public static void NonSpeechMethod()
                {
                    Console.WriteLine("No speech here");
                    Process.Start("notepad.exe");
                }

                public static void AnotherSpeechMethod()
                {
                    var engine = new SpeechRecognitionEngine();
                    engine.SetInputToDefaultAudioDevice();
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-mixed", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // Should wrap 2 speech methods
        Assert.Equal(2, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureMixed");
        Assert.NotNull(fixtureType);

        var speechMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "SpeechMethod");
        var nonSpeechMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "NonSpeechMethod");
        var anotherSpeechMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "AnotherSpeechMethod");

        Assert.NotNull(speechMethod);
        Assert.NotNull(nonSpeechMethod);
        Assert.NotNull(anotherSpeechMethod);

        // Speech methods should have exception handlers
        Assert.Single(speechMethod!.Body.ExceptionHandlers);
        Assert.Single(anotherSpeechMethod!.Body.ExceptionHandlers);

        // Non-speech method should not have exception handlers
        Assert.Empty(nonSpeechMethod!.Body.ExceptionHandlers);
    }

    [Fact]
    public void Handles_Void_And_Non_Void_Return_Types()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureReturnTypes
            {
                public static void VoidMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                }

                public static int IntMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                    return 42;
                }

                public static string StringMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                    return "test";
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-return-types", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // Should wrap all 3 methods
        Assert.Equal(3, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureReturnTypes");
        Assert.NotNull(fixtureType);

        var voidMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "VoidMethod");
        var intMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "IntMethod");
        var stringMethod = fixtureType!.Methods.FirstOrDefault(m => m.Name == "StringMethod");

        Assert.NotNull(voidMethod);
        Assert.NotNull(intMethod);
        Assert.NotNull(stringMethod);

        // All methods should have exception handlers
        Assert.Single(voidMethod!.Body.ExceptionHandlers);
        Assert.Single(intMethod!.Body.ExceptionHandlers);
        Assert.Single(stringMethod!.Body.ExceptionHandlers);

        // The patcher now adds locals for exception handling and return shaping.
        Assert.True(voidMethod.Body.Variables.Count >= 1);
        Assert.True(intMethod.Body.Variables.Count >= 2);
        Assert.True(stringMethod.Body.Variables.Count >= 2);
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
                    Console.WriteLine("No speech references here");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-empty", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(0, patchCount);

        // Helper type is not emitted when no methods are wrapped.
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.Null(helperType);
    }

    [Fact]
    public void Logs_Patch_Counts_When_Log_Action_Provided()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureLogging
            {
                public static void SpeechMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                }

                public static void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
                {
                    Console.WriteLine("Speech recognized");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-logging", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        var logMessages = new List<string>();
        Action<string> logAction = msg => logMessages.Add(msg);

        // Act
        SpeechCompatibilityPatcher.Patch(module, logAction);

        // Assert
        Assert.Equal(2, logMessages.Count);
        Assert.Contains(logMessages, m => m.Contains("System.Speech safety wrappers applied") && m.Contains("1"));
        Assert.Contains(logMessages, m => m.Contains("System.Speech event probes applied") && m.Contains("1"));
    }

    [Fact]
    public void Handles_Multiple_Return_Statements()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureMultipleReturns
            {
                public static int MethodWithMultipleReturns(bool condition)
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);

                    if (condition)
                    {
                        return 1;
                    }
                    else
                    {
                        return 2;
                    }
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-multiple-returns", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(1, patchCount);

        var method = module.Types
            .FirstOrDefault(t => t.Name == "FixtureMultipleReturns")?
            .Methods.FirstOrDefault(m => m.Name == "MethodWithMultipleReturns");

        Assert.NotNull(method);
        Assert.True(method!.HasBody);

        // The current wrapper keeps the existing return structure intact.
        var retInstructions = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        Assert.Single(retInstructions);

        var leaveInstructions = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Leave).ToList();
        Assert.Empty(leaveInstructions);

        // Should have a single return at the end
        var finalRet = method.Body.Instructions.LastOrDefault(i => i.OpCode == OpCodes.Ret);
        Assert.NotNull(finalRet);
    }

    [Fact]
    public void Handles_Static_And_Instance_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureStaticInstance
            {
                public static void StaticMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                }
            }

            public class InstanceClass
            {
                public void InstanceMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-static-instance", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // Should wrap both static and instance methods
        Assert.Equal(2, patchCount);

        var staticMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureStaticInstance")?
            .Methods.FirstOrDefault(m => m.Name == "StaticMethod");

        var instanceMethod = module.Types
            .FirstOrDefault(t => t.Name == "InstanceClass")?
            .Methods.FirstOrDefault(m => m.Name == "InstanceMethod");

        Assert.NotNull(staticMethod);
        Assert.NotNull(instanceMethod);

        // Both should have exception handlers
        Assert.Single(staticMethod!.Body.ExceptionHandlers);
        Assert.Single(instanceMethod!.Body.ExceptionHandlers);
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
                public static extern void ExternalMethod();
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-no-body", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // Should not wrap methods without bodies
        Assert.Equal(0, patchCount);

        // Helper type should not be created
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.Null(helperType);
    }

    [Fact]
    public void Handles_Value_Type_Return_Defaults()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureValueTypeReturn
            {
                public static int IntMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                    return 42;
                }

                public static bool BoolMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                    return true;
                }

                public static DateTime DateTimeMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                    return DateTime.Now;
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-value-type-return", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(3, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureValueTypeReturn");
        Assert.NotNull(fixtureType);

        foreach (var method in fixtureType!.Methods.Where(m => m.HasBody && m.Name != ".ctor"))
        {
            // Should have exception handler
            Assert.Single(method.Body.ExceptionHandlers);

            // Should have initobj instruction for value type default
            var hasInitobj = method.Body.Instructions.Any(i => i.OpCode == OpCodes.Initobj);
            Assert.True(hasInitobj);
        }
    }

    [Fact]
    public void Handles_Reference_Type_Return_Null()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureReferenceTypeReturn
            {
                public static string StringMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                    return "test";
                }

                public static object ObjectMethod()
                {
                    var recognizer = new SpeechRecognizer();
                    recognizer.LoadGrammar(null);
                    return new object();
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-reference-type-return", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(2, patchCount);

        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureReferenceTypeReturn");
        Assert.NotNull(fixtureType);

        foreach (var method in fixtureType!.Methods.Where(m => m.HasBody && m.Name != ".ctor"))
        {
            // Should have exception handler
            Assert.Single(method.Body.ExceptionHandlers);

            // Should have ldnull instruction for reference type default
            var hasLdnull = method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldnull);
            Assert.True(hasLdnull);
        }
    }

    [Fact]
    public void Preserves_Exception_Handler_Boundaries_With_Event_Probes()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Speech.Recognition;

            public static class FixtureExceptionBoundaries
            {
                public static void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
                {
                    try
                    {
                        Console.WriteLine("Processing");
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
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        // Should not wrap (no System.Speech references in body)
        Assert.Equal(0, patchCount);

        var method = module.Types
            .FirstOrDefault(t => t.Name == "FixtureExceptionBoundaries")?
            .Methods.FirstOrDefault(m => m.Name == "OnSpeechRecognized");

        Assert.NotNull(method);
        Assert.True(method!.HasBody);

        // Should have event probe at the beginning
        Assert.Equal(OpCodes.Ldstr, method.Body.Instructions[0].OpCode);
        Assert.Equal(OpCodes.Call, method.Body.Instructions[1].OpCode);

        // Exception handler should still be valid
        Assert.Single(method.Body.ExceptionHandlers);

        var eh = method.Body.ExceptionHandlers[0];
        Assert.NotNull(eh.TryStart);
        Assert.NotNull(eh.HandlerStart);
        Assert.NotNull(eh.TryEnd);
        Assert.NotNull(eh.HandlerEnd);
    }
}
