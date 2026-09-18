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

        var count = SpeechCompatibilityPatcher.ApplySapiSuppression(
            module, module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList());

        Assert.Equal(2, count);
        // Looked up after patching: the name now resolves to the trampoline
        // (the pre-patch MethodDef object is the renamed original).
        var handler = FindHandler(module, "OnSpeechRecognized");
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

        // The found handler is the trampoline (gate at the front, no EH);
        // the original body survives intact behind the suffix.
        Assert.Empty(handler.Body.ExceptionHandlers);
        var orig = FindHandler(module, "OnSpeechRecognized" + SpeechCompatibilityPatcher.TrampolineOrigSuffix);
        Assert.False(CallsGuard(orig));
        Assert.Contains(orig.Body.Instructions, i =>
            i.OpCode == OpCodes.Stsfld &&
            i.Operand is IField f && f.Name == "HandlerRan");
    }

    [Fact]
    public void Second_Pass_Is_Idempotent()
    {
        using var temp = new TempDirectory();
        using var module = LoadFixture(temp.Path);
        var methods = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();

        Assert.Equal(2, SpeechCompatibilityPatcher.ApplySapiSuppression(module, methods));
        Assert.Equal(0, SpeechCompatibilityPatcher.ApplySapiSuppression(module, methods));
    }

    [Fact]
    public void NonVoid_Untouched_Guarded_Bodied_Gets_Trampoline()
    {
        // The N=2 refutation promoted the trampoline precisely so EH-bodied
        // handlers are gated (rename + stand a guard in front) instead of
        // skipped. Non-void and unrelated handlers stay untouched.
        using var temp = new TempDirectory();
        using var module = LoadFixture(temp.Path);
        var methods = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();

        var count = SpeechCompatibilityPatcher.ApplySapiSuppression(module, methods);

        Assert.Equal(2, count);
        Assert.False(CallsGuard(FindHandler(module, "OnSpeechNonVoid")));
        Assert.False(CallsGuard(FindHandler(module, "Unrelated")));

        // The guarded handler now resolves to the trampoline (gate first),
        // with the original intact behind the suffix (body + EH kept).
        var trampoline = FindHandler(module, "OnSpeechGuarded");
        Assert.True(CallsGuard(trampoline));
        Assert.Empty(trampoline.Body.ExceptionHandlers);
        var orig = FindHandler(module, "OnSpeechGuarded" + SpeechCompatibilityPatcher.TrampolineOrigSuffix);
        Assert.False(CallsGuard(orig));
        Assert.Single(orig.Body.ExceptionHandlers);
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

    private const string FakeSapiPayloadSource = """
        using System;

        namespace System.Speech.Recognition
        {
            public sealed class RecognitionResult
            {
                public RecognitionResult(string text, float confidence)
                {
                    Text = text;
                    Confidence = confidence;
                }

                public string Text { get; }
                public float Confidence { get; }
            }

            public sealed class SpeechRecognizedEventArgs : EventArgs
            {
                public SpeechRecognizedEventArgs(RecognitionResult result)
                {
                    Result = result;
                }

                public RecognitionResult Result { get; }
            }
        }
        """;

    private const string DoubleSubscribeFixtureSource = """
        using System;

        namespace System.Speech.Recognition
        {
            public class SpeechRecognizedEventArgs : EventArgs
            {
            }
        }

        public static class Gate
        {
            // Mirrors IsProductSpeechSuppressed: true means the bridge owns
            // dispatch, so the product handler must stand down.
            public static bool Suppress;
            public static bool Check() { return Suppress; }
        }

        // GForm1-shaped: one instance handler with a guarded (EH) body,
        // subscribed once at setup and again at restart, never unsubscribed.
        public class FakeProductForm
        {
            public static int HandlerCalls;

            public event EventHandler<System.Speech.Recognition.SpeechRecognizedEventArgs> Recognized;

            public void SetupRecognizer()
            {
                Recognized += new EventHandler<System.Speech.Recognition.SpeechRecognizedEventArgs>(this.OnRecognized);
            }

            public void RestartRecognizer()
            {
                Recognized += new EventHandler<System.Speech.Recognition.SpeechRecognizedEventArgs>(this.OnRecognized);
            }

            public void Fire()
            {
                Recognized?.Invoke(this, null);
            }

            private void OnRecognized(object sender, System.Speech.Recognition.SpeechRecognizedEventArgs e)
            {
                try { HandlerCalls++; }
                catch (Exception) { }
            }
        }
        """;

    [Fact]
    public void Double_Subscribed_Instance_Handler_Gated_Live()
    {
        using var temp = new TempDirectory();
        var path = FixtureAssemblyBuilder.Build(
            DoubleSubscribeFixtureSource, $"fixture-sapi-double-{Guid.NewGuid():N}", temp.Path);
        var module = ModuleDefMD.Load(path);
        var methods = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();

        var gate = module.GetTypes().SelectMany(t => t.Methods).First(m => m.Name == "Check");
        var logs = new List<string>();
        var applied = SpeechCompatibilityPatcher.ApplySapiTrampolines(module, methods, gate, logs.Add);

        // Structural: one trampoline, both subscription sites retargeted.
        Assert.Equal(1, applied);
        Assert.Contains(logs, m => m.Contains("AppliedTrampoline") && m.Contains("OnRecognized") && m.Contains("2 ldftn"));

        var ldftnNames = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Ldftn && i.Operand is IMethod)
            .Select(i => ((IMethod)i.Operand).Name)
            .ToList();
        Assert.Equal(2, ldftnNames.Count);
        Assert.All(ldftnNames, n => Assert.Equal("OnRecognized", n));

        // Idempotent on a fresh list (trampoline + orig both stand down).
        var fresh = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();
        Assert.Equal(0, SpeechCompatibilityPatcher.ApplySapiTrampolines(module, fresh, gate));

        // Live: suppression engaged stands both subscriptions down; released
        // lets both through (which also documents the double-subscription).
        var patchedPath = System.IO.Path.Combine(temp.Path, "fixture-sapi-double.patched.dll");
        module.Write(patchedPath);
        var loaded = System.Reflection.Assembly.LoadFrom(patchedPath);
        var suppressFlag = loaded.GetType("Gate")!.GetField("Suppress")!;
        var formType = loaded.GetType("FakeProductForm")!;
        var callsField = formType.GetField("HandlerCalls")!;
        var form = Activator.CreateInstance(formType)!;
        formType.GetMethod("SetupRecognizer")!.Invoke(form, null);
        formType.GetMethod("RestartRecognizer")!.Invoke(form, null);

        suppressFlag.SetValue(null, true);
        callsField.SetValue(null, 0);
        formType.GetMethod("Fire")!.Invoke(form, null);
        Assert.Equal(0, callsField.GetValue(null));

        suppressFlag.SetValue(null, false);
        formType.GetMethod("Fire")!.Invoke(form, null);
        Assert.Equal(2, callsField.GetValue(null));
    }

    [Fact]
    public void Disposition_Report_Names_Each_Candidate()
    {
        using var temp = new TempDirectory();
        using var module = LoadFixture(temp.Path);
        var methods = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();

        var logs = new List<string>();
        var count = SpeechCompatibilityPatcher.ApplySapiSuppression(module, methods, logs.Add);

        Assert.Equal(2, count);
        Assert.Contains(logs, m => m.Contains("[sapi-suppress] AppliedTrampoline") && m.Contains("OnSpeechRecognized"));
        Assert.Contains(logs, m => m.Contains("[sapi-suppress] AppliedTrampoline") && m.Contains("OnSpeechGuarded"));
        Assert.Contains(logs, m => m.Contains("[sapi-suppress] SkippedNonVoid") && m.Contains("OnSpeechNonVoid"));
        // No SAPI arg, no line: the report stays per-candidate, not per-method.
        Assert.DoesNotContain(logs, m => m.Contains("Unrelated"));
    }

    [Fact]
    public void Observer_Extracts_Product_Sapi_Text()
    {
        // Fakes ship in a fixture assembly (own declaration wins there);
        // they cannot live in the test assembly itself: every fixture
        // compilation references it, which would make each SAPI type
        // ambiguous with the real System.Speech (CS0433).
        using var temp = new TempDirectory();
        var fakePath = FixtureAssemblyBuilder.Build(
            FakeSapiPayloadSource, $"fixture-sapi-fakes-{Guid.NewGuid():N}", temp.Path);
        var fakes = System.Reflection.Assembly.LoadFrom(fakePath);
        var resultType = fakes.GetType("System.Speech.Recognition.RecognitionResult")!;
        var argsType = fakes.GetType("System.Speech.Recognition.SpeechRecognizedEventArgs")!;

        var result = Activator.CreateInstance(resultType, "open discord", 0.9f);
        Assert.True(ProductSapiObserver.TryExtractResult(result, out var text, out var confidence));
        Assert.Equal("open discord", text);
        Assert.Equal(0.9f, confidence);

        var args = Activator.CreateInstance(argsType, result);
        Assert.True(ProductSapiObserver.TryExtractResult(args, out text, out _));
        Assert.Equal("open discord", text);

        var nullArgs = Activator.CreateInstance(argsType, new object?[] { null });
        Assert.False(ProductSapiObserver.TryExtractResult(nullArgs, out _, out _));
        var blank = Activator.CreateInstance(resultType, "   ", 0.1f);
        Assert.False(ProductSapiObserver.TryExtractResult(blank, out _, out _));
        Assert.False(ProductSapiObserver.TryExtractResult(null, out _, out _));
        Assert.False(ProductSapiObserver.TryExtractResult(new object(), out _, out _));
        // A hostile payload must fail closed, never throw.
        Assert.False(ProductSapiObserver.TryExtractResult(new ThrowingPayload(), out _, out _));
    }

    [Fact]
    public void Observer_Deferral_Decision_Matrix()
    {
        var now = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var fresh = now - 2 * TimeSpan.TicksPerSecond;
        var stale = now - 60 * TimeSpan.TicksPerSecond;

        Assert.True(ProductSapiObserver.ShouldDeferToProduct(
            new[] { "open discord", "discord" }, "open discord", fresh, now, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        // Case-insensitive, whitespace-tolerant agreement on any candidate.
        Assert.True(ProductSapiObserver.ShouldDeferToProduct(
            new[] { "open discord" }, "  Open Discord ", fresh, now, out _));
        Assert.True(ProductSapiObserver.ShouldDeferToProduct(
            new[] { "open browser", "discord" }, "discord", fresh, now, out _));

        Assert.False(ProductSapiObserver.ShouldDeferToProduct(
            new[] { "open discord" }, "open browser", fresh, now, out _));
        Assert.False(ProductSapiObserver.ShouldDeferToProduct(
            new[] { "open discord" }, "open discord", stale, now, out _));
        Assert.False(ProductSapiObserver.ShouldDeferToProduct(
            new string?[] { null, "  " }, "open discord", fresh, now, out _));
        Assert.False(ProductSapiObserver.ShouldDeferToProduct(
            new[] { "open discord" }, null, fresh, now, out _));
        Assert.False(ProductSapiObserver.ShouldDeferToProduct(
            null, "open discord", fresh, now, out _));
        Assert.False(ProductSapiObserver.ShouldDeferToProduct(
            new[] { "open discord" }, "open discord", 0, now, out _));
    }

    internal sealed class ThrowingPayload
    {
        public string Text => throw new InvalidOperationException("hostile");
    }
}
