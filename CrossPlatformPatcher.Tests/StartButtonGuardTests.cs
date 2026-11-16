using dnlib.DotNet;
using dnlib.DotNet.Emit;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// The Start guard must (a) hook the menu constructor once, (b) inject a
/// helper whose bodies are exception-guarded, and (c) be a strict no-op on a
/// module that has no WinForms control tree. Live behaviour (four buttons with
/// the gist blackholed) is verified on a guest, not here.
/// </summary>
public class StartButtonGuardTests
{
    private const string StubWinForms = """
        namespace System.Windows.Forms
        {
            public class Control
            {
                public string Text { get; set; }
                public bool Visible { get; set; }
                public ControlCollection Controls { get; } = new ControlCollection();
                public class ControlCollection
                {
                    public int Count => 0;
                    public Control this[int index] => null;
                }
            }
            public class Timer
            {
                public int Interval { get; set; }
                public event System.EventHandler Tick;
                public void Start() { Tick?.Invoke(null, System.EventArgs.Empty); }
                public void Stop() { }
                public void Dispose() { }
            }
        }
        """;

    private const string MenuFixture = """
        using System.Net;
        using System.Windows.Forms;

        public class GForm1 : Control
        {
            public GForm1()
            {
                var client = new WebClient();
                var text = client.DownloadString("https://example.invalid/menu");
                Controls.GetType();
                if (text == null) { Text = "empty"; }
            }
        }
        """;

    private const string NoMenuFixture = """
        public static class Plain
        {
            public static int Add(int a, int b) => a + b;
        }
        """;

    private static string BuildMenuFixture(string directory)
    {
        var stub = FixtureAssemblyBuilder.Build(
            StubWinForms, $"winforms-stub-{Guid.NewGuid():N}", directory);
        return FixtureAssemblyBuilder.Build(
            MenuFixture, $"menu-fixture-{Guid.NewGuid():N}", directory, stub);
    }

    [Fact]
    public void HooksMenuConstructorAndCreatesGuardedHelper()
    {
        using var temp = new TempDirectory();
        var built = BuildMenuFixture(temp.Path);

        using var module = ModuleDefMD.Load(built);
        int guarded = StartButtonGuardPatcher.Patch(module);

        Assert.Equal(1, guarded);

        var helper = module.GetTypes().FirstOrDefault(t => t.Name == StartButtonGuardPatcher.HelperTypeName);
        Assert.NotNull(helper);
        Assert.Contains(helper!.Methods, m => m.Name == "Attach" && m.HasBody);
        Assert.Contains(helper.Methods, m => m.Name == "OnTick" && m.HasBody);
        Assert.Contains(helper.Methods, m => m.Name == "Apply" && m.HasBody);

        // Every helper body must catch its own exceptions: a layout surprise
        // must never take the app down.
        foreach (var method in helper.Methods.Where(m => m.HasBody))
        {
            Assert.Contains(method.Body.ExceptionHandlers,
                h => h.HandlerType == ExceptionHandlerType.Catch);
        }

        var menuCtor = module.GetTypes()
            .First(t => t.Name == "GForm1")
            .Methods.First(m => m.Name == ".ctor");
        Assert.Contains(menuCtor.Body.Instructions, i =>
            i.OpCode == OpCodes.Call && i.Operand is IMethod im &&
            im.Name == "Attach" &&
            im.DeclaringType?.Name == StartButtonGuardPatcher.HelperTypeName);

        // The re-check is one-shot: exactly one Tick subscription in Attach.
        var attach = helper.Methods.First(m => m.Name == "Attach");
        Assert.Single(attach.Body.Instructions.Where(i =>
            (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) &&
            i.Operand is IMethod im && im.Name == "add_Tick"));
    }

    [Fact]
    public void PatchIsIdempotent()
    {
        using var temp = new TempDirectory();
        var built = BuildMenuFixture(temp.Path);

        using var module = ModuleDefMD.Load(built);
        Assert.Equal(1, StartButtonGuardPatcher.Patch(module));
        Assert.Equal(0, StartButtonGuardPatcher.Patch(module));
    }

    [Fact]
    public void NoControlTreeMeansNoPatch()
    {
        using var temp = new TempDirectory();
        var built = FixtureAssemblyBuilder.Build(
            NoMenuFixture, $"plain-fixture-{Guid.NewGuid():N}", temp.Path);

        using var module = ModuleDefMD.Load(built);
        Assert.Equal(0, StartButtonGuardPatcher.Patch(module));
        Assert.DoesNotContain(module.GetTypes(),
            t => t.Name == StartButtonGuardPatcher.HelperTypeName);
    }

    [Fact]
    public void FlagDefaultsOff()
    {
        Assert.False(StartButtonGuardPatcher.IsEnabled(null));
        Assert.False(StartButtonGuardPatcher.IsEnabled(new Dictionary<string, string>()));
        Assert.True(StartButtonGuardPatcher.IsEnabled(
            new Dictionary<string, string> { [StartButtonGuardPatcher.OptionKey] = "true" }));
    }
}
