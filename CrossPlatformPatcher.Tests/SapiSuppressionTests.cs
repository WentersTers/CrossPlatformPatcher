using dnlib.DotNet;
using dnlib.DotNet.Emit;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Product SAPI suppression: void handlers taking a SpeechRecognizedEventArgs
/// get an early return gated on the OWW bridge guard. The fixture fakes the
/// SAPI event-arg type under its real full name (the patcher matches by
/// name); no System.Speech reference needed on the build host.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class SapiSuppressionTests
{
    private const string FixtureSource = """
        using System;

        namespace System.Speech.Recognition
        {
            public class SpeechRecognizedEventArgs : EventArgs
            {
                public SpeechRecognizedEventArgs(string text) { Text = text; }
                public string Text { get; }
            }
        }

        public static class FakeProductSpeech
        {
            public static bool HandlerRan;
            public static bool NonVoidRan;
            public static bool GuardedRan;

            public static void OnSpeechRecognized(object sender, System.Speech.Recognition.SpeechRecognizedEventArgs e)
            {
                HandlerRan = true;
            }

            public static int OnSpeechNonVoid(object sender, System.Speech.Recognition.SpeechRecognizedEventArgs e)
            {
                NonVoidRan = true;
                return 1;
            }

            public static void OnSpeechGuarded(object sender, System.Speech.Recognition.SpeechRecognizedEventArgs e)
            {
                try { GuardedRan = true; }
                catch (Exception) { }
            }

            public static void Unrelated(object sender, EventArgs e)
            {
            }
        }
        """;

    private static ModuleDefMD LoadFixture(string directory)
    {
        var path = FixtureAssemblyBuilder.Build(
            FixtureSource, $"fixture-sapi-suppress-{Guid.NewGuid():N}", directory);
        return ModuleDefMD.Load(path);
    }

    private static MethodDef FindHandler(ModuleDefMD module, string name)
    {
        foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
                if (method.Name == name)
                    return method;
        throw new Xunit.Sdk.XunitException($"handler {name} not found in fixture");
    }

    private static bool CallsGuard(MethodDef method)
    {
        foreach (var instr in method.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Call &&
                instr.Operand is IMethod called &&
                called.Name == "IsProductSpeechSuppressed")
                return true;
        }
        return false;
    }

    [Fact]
    public void Void_Recognized_Handler_Gets_Guard_And_Early_Return()
    {
        using var temp = new TempDirectory();
        using var module = LoadFixture(temp.Path);
        var handler = FindHandler(module, "OnSpeechRecognized");

        var count = SpeechCompatibilityPatcher.ApplySapiSuppression(
            module, module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList());

        Assert.Equal(1, count);
        Assert.True(CallsGuard(handler));
        var instrs = handler.Body.Instructions;
        var callIdx = -1;
        for (int i = 0; i < instrs.Count; i++)
        {
            if (instrs[i].OpCode == OpCodes.Call &&
                instrs[i].Operand is IMethod m && m.Name == "IsProductSpeechSuppressed")
            {
                callIdx = i;
                break;
            }
        }
        Assert.True(callIdx >= 0);
        Assert.Equal(OpCodes.Brfalse_S, instrs[callIdx + 1].OpCode);
        Assert.Equal(OpCodes.Ret, instrs[callIdx + 2].OpCode);
    }

    [Fact]
    public void Second_Pass_Is_Idempotent()
    {
        using var temp = new TempDirectory();
        using var module = LoadFixture(temp.Path);
        var methods = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();

        Assert.Equal(1, SpeechCompatibilityPatcher.ApplySapiSuppression(module, methods));
        Assert.Equal(0, SpeechCompatibilityPatcher.ApplySapiSuppression(module, methods));
    }

    [Fact]
    public void NonVoid_Guarded_And_Unrelated_Handlers_Untouched()
    {
        using var temp = new TempDirectory();
        using var module = LoadFixture(temp.Path);
        var methods = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();

        SpeechCompatibilityPatcher.ApplySapiSuppression(module, methods);

        Assert.False(CallsGuard(FindHandler(module, "OnSpeechNonVoid")));
        Assert.False(CallsGuard(FindHandler(module, "OnSpeechGuarded")));
        Assert.False(CallsGuard(FindHandler(module, "Unrelated")));
    }

    [Fact]
    public void Suppression_Decision_Matrix()
    {
        // The real injected decision unit: Vosk alive suppresses; otherwise
        // only a fresh SAPI proof does.
        var now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc).Ticks;
        Assert.True(SapiSuppression.ShouldSuppress(true, 0, now));
        Assert.False(SapiSuppression.ShouldSuppress(false, 0, now));
        Assert.True(SapiSuppression.ShouldSuppress(false, now - 10 * TimeSpan.TicksPerSecond, now));
        Assert.False(SapiSuppression.ShouldSuppress(false, now - 600 * TimeSpan.TicksPerSecond, now));
    }
}
