using CrossPlatformPatcher.Core;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Unit tests for ProcessStartCompatibilityPatcher.
/// Verifies that Process.Start calls are correctly rewritten to safe wrapper methods
/// with proper call-site context preservation.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class ProcessStartCompatibilityPatcherTests
{
    [Fact]
    public void Rewrites_Process_Start_String_Calls()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureProcessStartString
            {
                public static void Main()
                {
                    Process.Start("notepad.exe");
                    Process.Start("cmd.exe");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-process-start-string", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        ProcessStartCompatibilityPatcher.Patch(module);

        // Verify the helper type was created
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);

        // Verify the safe method was created
        var safeMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "SafeProcessStart");
        Assert.NotNull(safeMethod);

        // Verify the calls were rewritten
        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureProcessStartString")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        var safeCalls = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Where(i => i.Operand is IMethod method && method.Name == "SafeProcessStart")
            .ToList();

        // Both string overload calls should now point to SafeProcessStart
        Assert.True(safeCalls.Count >= 2);
        Assert.All(safeCalls, instr => Assert.Same(safeMethod, instr.Operand));

        // Verify context strings were inserted before each call
        var ldstrInstructions = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains("@IL_"))
            .ToList();

        Assert.True(ldstrInstructions.Count >= 2);
        Assert.All(ldstrInstructions, instr =>
        {
            var context = (string)instr.Operand;
            Assert.Contains("FixtureProcessStartString::Main@IL_", context);
        });
    }

    [Fact]
    public void Rewrites_Process_Start_ProcessStartInfo_Calls()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureProcessStartPsi
            {
                public static void Main()
                {
                    var psi1 = new ProcessStartInfo("notepad.exe");
                    Process.Start(psi1);

                    var psi2 = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
                    Process.Start(psi2);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-process-start-psi", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        ProcessStartCompatibilityPatcher.Patch(module);

        // Verify the helper type was created
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);

        // Verify the safe method was created
        var safeMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "SafeProcessStartPsi");
        Assert.NotNull(safeMethod);

        // Verify the calls were rewritten
        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureProcessStartPsi")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        var safeCalls = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Where(i => i.Operand is IMethod method && method.Name == "SafeProcessStartPsi")
            .ToList();

        // Both Process.Start(ProcessStartInfo) calls should now point to SafeProcessStartPsi
        Assert.True(safeCalls.Count >= 2);
        Assert.All(safeCalls, instr => Assert.Same(safeMethod, instr.Operand));

        // Verify context strings were inserted before each call
        var ldstrInstructions = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains("@IL_"))
            .ToList();

        Assert.True(ldstrInstructions.Count >= 2);
        Assert.All(ldstrInstructions, instr =>
        {
            var context = (string)instr.Operand;
            Assert.Contains("FixtureProcessStartPsi::Main@IL_", context);
        });
    }

    [Fact]
    public void Preserves_Call_Site_Context_Information()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureContext
            {
                public static void MethodA()
                {
                    Process.Start("app1.exe");
                }

                public static void MethodB()
                {
                    Process.Start("app2.exe");
                }

                public static void Main()
                {
                    MethodA();
                    MethodB();
                    Process.Start("app3.exe");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-context", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        ProcessStartCompatibilityPatcher.Patch(module);

        // Assert
        var fixtureType = module.Types.FirstOrDefault(t => t.Name == "FixtureContext");
        Assert.NotNull(fixtureType);

        // Collect all context strings from all methods
        var allContexts = new List<string>();
        foreach (var method in fixtureType!.Methods.Where(m => m.HasBody))
        {
            var ldstrInstructions = method.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr)
                .Select(i => (string)i.Operand)
                .Where(s => s.Contains("IL_"))
                .ToList();

            allContexts.AddRange(ldstrInstructions);
        }

        // Should have 3 context strings (one for each Process.Start call)
        Assert.Equal(3, allContexts.Count);

        // Verify each context contains the correct method name
        Assert.Contains(allContexts, c => c.Contains("MethodA"));
        Assert.Contains(allContexts, c => c.Contains("MethodB"));
        Assert.Contains(allContexts, c => c.Contains("Main"));

        // Verify each context contains the type name
        Assert.All(allContexts, c => Assert.Contains("FixtureContext", c));

        // Verify each context contains an IL offset
        Assert.All(allContexts, c => Assert.Contains("@IL_", c));
    }

    [Fact]
    public void Does_Not_Rewrite_Unrelated_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureUnrelated
            {
                public static void Main()
                {
                    // These should NOT be rewritten
                    Console.WriteLine("test");
                    var process = new Process();
                    process.Start();
                    var psi = new ProcessStartInfo("test.exe");
                    var fileName = psi.FileName;
                    
                    // This SHOULD be rewritten
                    Process.Start("notepad.exe");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-unrelated", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = ProcessStartCompatibilityPatcher.Patch(module);

        // Assert
        // Only one call should be rewritten (Process.Start(string))
        Assert.Equal(1, patchCount);

        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureUnrelated")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        var safeStringCalls = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Where(i => i.Operand is IMethod method && method.Name == "SafeProcessStart")
            .ToList();

        var instanceCalls = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Callvirt)
            .Where(i => i.Operand is IMethod method && method.Name == "Start")
            .ToList();

        // One string overload should be rewritten; the instance call should remain unchanged
        Assert.Single(safeStringCalls);
        Assert.Single(instanceCalls);

        var rewrittenCall = safeStringCalls.FirstOrDefault();
        Assert.NotNull(rewrittenCall);
    }

    [Fact]
    public void Remains_Idempotent_When_Patched_Multiple_Times()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureIdempotent
            {
                public static void Main()
                {
                    Process.Start("notepad.exe");
                    var psi = new ProcessStartInfo("cmd.exe");
                    Process.Start(psi);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-idempotent", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act - perform multiple patch passes
        var firstPatchCount = ProcessStartCompatibilityPatcher.Patch(module);
        var secondPatchCount = ProcessStartCompatibilityPatcher.Patch(module);
        var thirdPatchCount = ProcessStartCompatibilityPatcher.Patch(module);

        // Assert - at least one rewrite should occur on the first pass
        Assert.True(firstPatchCount >= 1);

        // Verify the helper type still exists and has the correct methods
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);

        var safeMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "SafeProcessStart");
        var safePsiMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "SafeProcessStartPsi");

        Assert.NotNull(safeMethod);
        Assert.NotNull(safePsiMethod);

        // Verify the calls are still pointing to the safe methods
        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureIdempotent")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        var safeCalls = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Where(i => i.Operand is IMethod method && method.Name.Contains("Safe"))
            .ToList();

        Assert.Equal(2, safeCalls.Count);
    }

    [Fact]
    public void Handles_Mixed_Process_Start_Overloads()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureMixed
            {
                public static void Main()
                {
                    // String overload
                    Process.Start("app1.exe");
                    
                    // ProcessStartInfo overload
                    var psi = new ProcessStartInfo("app2.exe");
                    Process.Start(psi);
                    
                    // Another string overload
                    Process.Start("app3.exe");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-mixed", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = ProcessStartCompatibilityPatcher.Patch(module);

        // Assert
        // Should rewrite 3 calls total (2 string + 1 ProcessStartInfo)
        Assert.Equal(3, patchCount);

        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.NotNull(helperType);

        var safeMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "SafeProcessStart");
        var safePsiMethod = helperType?.Methods.FirstOrDefault(m => m.Name == "SafeProcessStartPsi");

        Assert.NotNull(safeMethod);
        Assert.NotNull(safePsiMethod);

        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureMixed")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        // Count calls to each safe method
        var safeStringCalls = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call && i.Operand == safeMethod)
            .Count();

        var safePsiCalls = mainMethod.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Call && i.Operand == safePsiMethod)
            .Count();

        Assert.Equal(2, safeStringCalls);
        Assert.Equal(1, safePsiCalls);
    }

    [Fact]
    public void Does_Not_Rewrite_Non_Process_Start_Methods()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureNonProcess
            {
                public static void Start(string arg)
                {
                    Console.WriteLine(arg);
                }

                public static void Main()
                {
                    // This should NOT be rewritten - it's our own Start method
                    Start("test");
                    
                    // This SHOULD be rewritten
                    Process.Start("notepad.exe");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-non-process", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = ProcessStartCompatibilityPatcher.Patch(module);

        // Assert
        // Only Process.Start should be rewritten
        Assert.Equal(1, patchCount);

        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixtureNonProcess")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        // Find the call to our own Start method
        var ownStartCall = mainMethod.Body.Instructions
            .FirstOrDefault(i => i.OpCode == OpCodes.Call && i.Operand is IMethod m && 
                m.DeclaringType?.Name == "FixtureNonProcess" && m.Name == "Start");

        Assert.NotNull(ownStartCall);

        // Verify it wasn't rewritten (still points to original method)
        Assert.NotEqual("SafeProcessStart", ((IMethod)ownStartCall.Operand!).Name.ToString());
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
                    Console.WriteLine("No Process.Start calls here");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-empty", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        var patchCount = ProcessStartCompatibilityPatcher.Patch(module);

        // Assert
        Assert.Equal(0, patchCount);

        // Helper type should not be created if no patches were applied
        var helperType = module.Types.FirstOrDefault(t => t.Name == "CrossPlatformPatcherCompat");
        Assert.Null(helperType);
    }

    [Fact]
    public void Preserves_Instruction_Order_With_Prefix_OpCodes()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixturePrefix
            {
                public static void Main()
                {
                    Process.Start("notepad.exe");
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-prefix", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        // Act
        ProcessStartCompatibilityPatcher.Patch(module);

        // Assert
        var mainMethod = module.Types
            .FirstOrDefault(t => t.Name == "FixturePrefix")?
            .Methods.FirstOrDefault(m => m.Name == "Main");

        Assert.NotNull(mainMethod);
        Assert.True(mainMethod!.HasBody);

        // Find the context string and the call instruction
        var instructions = mainMethod.Body.Instructions;
        var ldstrIndex = instructions.Select((i, idx) => new { Instruction = i, Index = idx })
            .FirstOrDefault(x => x.Instruction.OpCode == OpCodes.Ldstr && ((string)x.Instruction.Operand).Contains("IL_"))?.Index ?? -1;
        var callIndex = instructions.Select((i, idx) => new { Instruction = i, Index = idx })
            .FirstOrDefault(x => x.Instruction.OpCode == OpCodes.Call && x.Instruction.Operand is MethodDef m && m.Name == "SafeProcessStart")?.Index ?? -1;

        // The context string should be immediately before the call
        Assert.Equal(ldstrIndex + 1, callIndex);
    }

    [Fact]
    public void Logs_Patch_Counts_When_Log_Action_Provided()
    {
        // Arrange
        using var temp = new TempDirectory();
        var source = """
            using System;
            using System.Diagnostics;

            public static class FixtureLogging
            {
                public static void Main()
                {
                    Process.Start("app1.exe");
                    var psi = new ProcessStartInfo("app2.exe");
                    Process.Start(psi);
                }
            }
            """;

        var assemblyPath = FixtureAssemblyBuilder.Build(source, "fixture-logging", temp.Path);
        var module = ModuleDefMD.Load(assemblyPath);

        var logMessages = new List<string>();
        Action<string> logAction = msg => logMessages.Add(msg);

        // Act
        ProcessStartCompatibilityPatcher.Patch(module, logAction);

        // Assert
        Assert.Equal(2, logMessages.Count);
        Assert.Contains(logMessages, m => m.Contains("Process.Start(string)") && m.Contains("1"));
        Assert.Contains(logMessages, m => m.Contains("Process.Start(ProcessStartInfo)") && m.Contains("1"));
    }
}