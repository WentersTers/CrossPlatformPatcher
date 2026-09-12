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
        Assert.Equal(3, logMessages.Count);
        Assert.Contains(logMessages, m => m.Contains("System.Speech safety wrappers applied") && m.Contains("1"));
        Assert.Contains(logMessages, m => m.Contains("System.Speech event probes applied") && m.Contains("1"));
        Assert.Contains(logMessages, m => m.Contains("System.Speech emulate hardening applied") && m.Contains("0"));
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

    [Fact]
    public void Emulate_Call_Is_Rewritten_To_Resilient_Helper()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System.Speech.Recognition;

            public static class FixtureEmulate
            {
                public static RecognitionResult Dispatch(SpeechRecognitionEngine engine, string text)
                {
                    return engine.EmulateRecognize(text);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-emulate", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = SpeechCompatibilityPatcher.Patch(module);

        // Assert: the method still counts as wrapped (backstop installed).
        Assert.Equal(1, patchCount);

        var method = module.Types
            .FirstOrDefault(t => t.Name == "FixtureEmulate")?
            .Methods.FirstOrDefault(m => m.Name == "Dispatch");

        Assert.NotNull(method);
        Assert.True(method!.HasBody);

        // No direct EmulateRecognize call may remain.
        Assert.DoesNotContain(method.Body.Instructions, i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is IMethod called &&
            called.Name == "EmulateRecognize");

        // The resilient helper exists and is the new callee.
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);
        var helper = helperType!.Methods.FirstOrDefault(m => m.Name == "EmulateRecognizeResilient");
        Assert.NotNull(helper);

        Assert.Contains(method.Body.Instructions, i =>
            i.OpCode == OpCodes.Call && Equals(i.Operand, helper));

        // Backstop catch is still in place.
        Assert.Single(method.Body.ExceptionHandlers);
    }

    [Fact]
    public void Emulate_Hardening_Is_Idempotent()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System.Speech.Recognition;

            public static class FixtureEmulateIdem
            {
                public static RecognitionResult Dispatch(SpeechRecognitionEngine engine, string text)
                {
                    return engine.EmulateRecognize(text);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-emulate-idem", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var first = SpeechCompatibilityPatcher.Patch(module);
        var second = SpeechCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(1, first);
        Assert.Equal(0, second);

        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);
        Assert.Single(helperType!.Methods.Where(m => m.Name == "EmulateRecognizeResilient"));
    }

    [Fact]
    public void Hardened_Dispatch_Completes_With_Null_Engine()
    {
        // Arrange: the missing-recognizer case must complete (not throw),
        // returning null so callers degrade exactly like the old skip path.
        // The null-engine branch routes the phrase through the bridge, which
        // fails closed here (no PAIcom.OWW beside the fixture) and logs it.
        using var temp = new TempDirectory();
        var source = """
            using System.Speech.Recognition;

            public static class FixtureEmulateNull
            {
                public static RecognitionResult Dispatch(SpeechRecognitionEngine engine, string text)
                {
                    return engine.EmulateRecognize(text);
                }

                // Forces framework assembly references (Console, Runtime)
                // so the injected helpers resolve outside mscorlib-only modules.
                public static string TouchFramework()
                {
                    System.Console.WriteLine("touch");
                    var assembly = typeof(object).Assembly;
                    var type = assembly.GetType("System.Object");
                    return type?.FullName ?? "?";
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-emulate-null", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);
        Assert.Equal(1, SpeechCompatibilityPatcher.Patch(module));

        var patchedPath = System.IO.Path.Combine(temp.Path, "fixture-emulate-null.patched.dll");
        module.Write(patchedPath);

        // The fixture references System.Speech, which .NET 8 does not resolve
        // from the Framework GAC at runtime. Stage the reference beside the
        // patched copy so signature/type resolution succeeds; the test never
        // executes speech code (null engine short-circuits first).
        var speechPath = FindSystemSpeechAssembly();
        Assert.True(File.Exists(speechPath), "System.Speech reference assembly not found for execution.");
        File.Copy(speechPath, System.IO.Path.Combine(temp.Path, "System.Speech.dll"), overwrite: true);

        // Act: null engine must not throw (this also JIT-verifies the
        // injected helper bodies, including their exception tables).
        // Capture console: the bridge failure path logs its label.
        var originalOut = Console.Out;
        using var stdout = new System.IO.StringWriter();
        Console.SetOut(stdout);
        object? result = null;
        Exception? ex = null;
        try
        {
            var loaded = System.Reflection.Assembly.LoadFrom(patchedPath);
            var dispatch = loaded.GetType("FixtureEmulateNull")!.GetMethod("Dispatch")!;
            ex = Record.Exception(() => result = dispatch.Invoke(null, new object?[] { null, "hello" }));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert
        Assert.Null(ex);
        Assert.Null(result);
        var logged = stdout.ToString();
        Assert.True(
            logged.Contains("BridgeTextDispatch") || logged.Contains("Bridged text dispatch"),
            "expected the bridge attempt to be logged, got: " + logged);
    }

    [Fact]
    public void Injected_Framework_Refs_Resolve_Within_Module_Refs()
    {
        // Regression guard: every framework MemberRef the patcher injects
        // must live in an assembly the target module actually references.
        // A wrong scope is a runtime TypeLoad that can even silence console
        // output itself (observed as a startup death with an empty log).
        using var temp = new TempDirectory();
        var source = """
            using System.Speech.Recognition;

            public static class FixtureScopes
            {
                public static RecognitionResult Dispatch(SpeechRecognitionEngine engine, string text)
                {
                    return engine.EmulateRecognize(text);
                }

                public static string TouchFramework()
                {
                    System.Console.WriteLine("touch");
                    var assembly = typeof(object).Assembly;
                    var type = assembly.GetType("System.Object");
                    return type?.FullName ?? "?";
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-scopes", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);
        SpeechCompatibilityPatcher.Patch(module);

        var refNames = module.GetAssemblyRefs().Select(a => a.Name.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        foreach (var type in module.Types.Where(t => t.Name == "CrossPlatformPatcherCompat"))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is IMethod called &&
                        called.DeclaringType?.Scope is dnlib.DotNet.AssemblyRef scope &&
                        !refNames.Contains(scope.Name))
                    {
                        bad.Add($"{method.Name}: {called.DeclaringType?.FullName} scope {scope.Name}");
                    }
                }
            }
        }

        Assert.True(bad.Count == 0, "Unresolvable injected refs:" + Environment.NewLine + string.Join(Environment.NewLine, bad));
    }

    [Fact]
    public void Bridge_Fails_Closed_Without_Oww()
    {        using var temp = new TempDirectory();
        var source = """
            using System.Speech.Recognition;

            public static class FixtureBridge
            {
                public static RecognitionResult Dispatch(SpeechRecognitionEngine engine, string text)
                {
                    return engine.EmulateRecognize(text);
                }

                public static string TouchFramework()
                {
                    System.Console.WriteLine("touch");
                    var assembly = typeof(object).Assembly;
                    var type = assembly.GetType("System.Object");
                    return type?.FullName ?? "?";
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-bridge", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);
        SpeechCompatibilityPatcher.Patch(module);
        var patchedPath = System.IO.Path.Combine(temp.Path, "fixture-bridge.patched.dll");
        module.Write(patchedPath);
        var speechSrc = FindSystemSpeechAssembly();
        if (File.Exists(speechSrc))
            File.Copy(speechSrc, System.IO.Path.Combine(temp.Path, "System.Speech.dll"), overwrite: true);
        var loaded = System.Reflection.Assembly.LoadFrom(patchedPath);
        var bridge = loaded.GetTypes().SelectMany(t => t.GetMethods()).FirstOrDefault(m => m.Name == "BridgeTextDispatch");
        Assert.NotNull(bridge);
        // No PAIcom.OWW beside the fixture: must fail closed (null, no throw).
        // The miss path must name its step (Type.GetType miss marker) rather
        // than surfacing a bare NullReferenceException downstream.
        var originalOut = Console.Out;
        using var stdout = new System.IO.StringWriter();
        Console.SetOut(stdout);
        object? result = "sentinel";
        Exception? bridgeEx = null;
        try
        {
            bridgeEx = Record.Exception(() => result = bridge!.Invoke(null, new object?[] { "hello" }));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        Assert.Null(bridgeEx);
        Assert.Null(result);
        var logged = stdout.ToString();
        // The outcome line prints in every environment (miss or real
        // dispatch). Which miss-step markers appear depends on whether the
        // test process already has PAIcom.OWW loaded (full suite) or not
        // (isolation): with OWW loaded the bridge resolves the real handler
        // and dispatches through it, still failing closed to null for
        // an unmatched phrase. Step-marker presence is verified
        // structurally in Bridge_Body_Contains_Step_Diagnostics.
        Assert.Contains("Bridged text dispatch done:", logged);
    }

    [Fact]
    public void Bridge_Body_Contains_Step_Diagnostics()
    {
        // Structural guard: the injected bridge must resolve Type.GetType
        // first and name each miss step, so a guest run logs which step
        // failed instead of a bare NullReferenceException. Verified on the
        // emitted IL so the check holds regardless of whether the test
        // process itself has PAIcom.OWW loaded.
        using var temp = new TempDirectory();
        var source = """
            using System.Speech.Recognition;

            public static class FixtureBridgeSteps
            {
                public static RecognitionResult Dispatch(SpeechRecognitionEngine engine, string text)
                {
                    return engine.EmulateRecognize(text);
                }

                public static string TouchFramework()
                {
                    System.Console.WriteLine("touch");
                    var assembly = typeof(object).Assembly;
                    var type = assembly.GetType("System.Object");
                    return type?.FullName ?? "?";
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-bridge-steps", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);
        SpeechCompatibilityPatcher.Patch(module);

        var bridge = module.Types
            .FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat")?
            .Methods.FirstOrDefault(m => m.Name == "BridgeTextDispatch");
        Assert.NotNull(bridge);
        Assert.True(bridge!.HasBody);

        var strings = bridge.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string)
            .Select(i => (string)i.Operand)
            .ToList();
        Assert.Contains(strings, s => s.Contains("Type.GetType miss"));
        Assert.Contains(strings, s => s.Contains("type null"));
        Assert.Contains(strings, s => s.Contains("method null"));

        // Type.GetType(string) static resolution must precede Assembly.Load.
        var calls = bridge.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m &&
                m.Name == "GetType" && m.MethodSig is not null && !m.MethodSig.HasThis)
            .ToList();
        Assert.NotEmpty(calls);
    }

    private static string FindSystemSpeechAssembly()
    {
        const string gacSpeechPath = @"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll";
        if (File.Exists(gacSpeechPath))
            return gacSpeechPath;

        try
        {
            var loaded = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("System.Speech"));
            if (!string.IsNullOrWhiteSpace(loaded.Location) && File.Exists(loaded.Location))
                return loaded.Location;
        }
        catch
        {
            // Fall through to the failure message below.
        }

        return string.Empty;
    }
}
