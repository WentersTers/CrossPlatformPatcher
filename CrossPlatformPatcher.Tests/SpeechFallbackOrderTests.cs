using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Input-chain order: Vosk user model, then Vosk default model, then
/// Windows speech. SAPI type resolution is inert off-Windows (proven here
/// on Linux); the live Windows path is validated on hardware.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class SpeechFallbackOrderTests
{
    private const string ModelPathVar = "PAICOM_VOSK_MODEL_PATH";
    private const string ModelNameVar = "PAICOM_VOSK_MODEL_NAME";

    private static string MakeModelDir(string parent, string name = "some-model")
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(dir, "am"));
        Directory.CreateDirectory(Path.Combine(dir, "conf"));
        Directory.CreateDirectory(Path.Combine(dir, "graph"));
        return dir;
    }

    [Fact]
    public void UserModel_Beats_DefaultModel_In_Order()
    {
        using var temp = new TempDirectory();
        var userModel = MakeModelDir(Path.Combine(temp.Path, "home"), "user-model");

        var priorPath = Environment.GetEnvironmentVariable(ModelPathVar);
        var priorName = Environment.GetEnvironmentVariable(ModelNameVar);
        try
        {
            Environment.SetEnvironmentVariable(ModelPathVar, userModel);
            Environment.SetEnvironmentVariable(ModelNameVar, null);

            var rec = new VoskSpeechRecognizer(_ => { });
            Assert.Equal(userModel, rec.FindUserModel());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelPathVar, priorPath);
            Environment.SetEnvironmentVariable(ModelNameVar, priorName);
        }
    }

    [Fact]
    public void DefaultModel_Found_Beside_App_Base()
    {
        // Uses AppContext.BaseDirectory (process-stable) instead of the
        // process working directory: changing CWD breaks parallel test
        // collections, so no test here ever calls SetCurrentDirectory.
        var baseDir = AppContext.BaseDirectory;
        var modelsDir = Path.Combine(baseDir, "models");
        var probeDir = Path.Combine(modelsDir, "tier-order-probe");
        Directory.CreateDirectory(Path.Combine(probeDir, "am"));
        Directory.CreateDirectory(Path.Combine(probeDir, "conf"));
        Directory.CreateDirectory(Path.Combine(probeDir, "graph"));

        var priorPath = Environment.GetEnvironmentVariable(ModelPathVar);
        var priorName = Environment.GetEnvironmentVariable(ModelNameVar);
        try
        {
            Environment.SetEnvironmentVariable(ModelPathVar, null);
            Environment.SetEnvironmentVariable(ModelNameVar, null);

            var rec = new VoskSpeechRecognizer(_ => { });
            Assert.Equal(probeDir, rec.FindDefaultModel());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelPathVar, priorPath);
            Environment.SetEnvironmentVariable(ModelNameVar, priorName);
            try { Directory.Delete(probeDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FindDefaultModel_Never_Fetches_Patch_Time_Code()
    {
        // The patch-time twin (Core) mirrors the injected tier order minus
        // the first-run fetch: even with a download URL configured, a miss
        // must return null WITHOUT touching the network (no "downloading"
        // line). The injected twin (PAIcom.OWW) carries the same tiers plus
        // a phase-gated fetch (allowFetch=false on pre-warm/wake threads,
        // settled background worker only); its gate is pinned by the
        // ShouldAttemptFetch matrix plus the live "fetch skipped" log line.
        var priorPath = Environment.GetEnvironmentVariable(ModelPathVar);
        var priorName = Environment.GetEnvironmentVariable(ModelNameVar);
        var priorUrl = Environment.GetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(ModelPathVar, null);
            Environment.SetEnvironmentVariable(ModelNameVar, null);
            Environment.SetEnvironmentVariable(
                VoskModelDownloader.UrlOverrideVariable, "http://127.0.0.1:9/no-model.zip");

            var logs = new List<string>();
            var result = new VoskSpeechRecognizer(logs.Add).FindDefaultModel();
            Assert.Null(result);
            Assert.DoesNotContain(logs, m => m.Contains("downloading"));
            Assert.DoesNotContain(logs, m => m.Contains("fetch"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModelPathVar, priorPath);
            Environment.SetEnvironmentVariable(ModelNameVar, priorName);
            Environment.SetEnvironmentVariable(VoskModelDownloader.UrlOverrideVariable, priorUrl);
        }
    }

    [Fact]
    public void Sapi_TryRecognize_Returns_Null_Without_Speech_Stack()
    {
        // This host has no working System.Speech: the fallback must report
        // unavailable (or fail gracefully) instead of throwing. On Windows
        // with the speech stack present, the same call proceeds to listen.
        // (Type resolution alone may succeed via reference assemblies; what
        // matters is TryRecognize never throws and yields null here.)
        var seen = new List<string>();
        var result = SapiFallbackRecognizer.TryRecognize(500, seen.Add);
        Assert.Null(result);
        Assert.NotEmpty(seen);
    }

    [Fact]
    public void Sapi_ReadResultText_Reads_Text_Property()
    {
        Assert.Equal("open browser", SapiFallbackRecognizer.ReadResultText(new { Text = "open browser" }));
        Assert.Null(SapiFallbackRecognizer.ReadResultText(null));
        Assert.Null(SapiFallbackRecognizer.ReadResultText(new { Nope = 1 }));
    }
}
