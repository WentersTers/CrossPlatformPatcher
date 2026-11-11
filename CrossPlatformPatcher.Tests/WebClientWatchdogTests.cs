using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using dnlib.DotNet;
using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// The watchdog must (a) redirect every WebClient construction, (b) leave a
/// fast successful download untouched, and (c) cut a stalled download short
/// instead of blocking the calling thread.
/// </summary>
public class WebClientWatchdogTests
{
    private const string FixtureSource = """
        using System.Net;

        public static class FixtureNet
        {
            public static string Fetch(string url)
            {
                var client = new WebClient();
                try
                {
                    return client.DownloadString(url);
                }
                catch (WebException)
                {
                    throw;
                }
            }
        }
        """;

    private static string BuildPatchedFixture(string directory)
    {
        var built = FixtureAssemblyBuilder.Build(FixtureSource, $"fixture-net-{Guid.NewGuid():N}", directory);
        var patched = Path.Combine(directory, $"patched-net-{Guid.NewGuid():N}.dll");

        using var module = ModuleDefMD.Load(built);
        int redirected = WebClientTimeoutPatcher.Patch(module);
        Assert.Equal(1, redirected);
        module.Write(patched);

        return patched;
    }

    [Fact]
    public void RedirectsCallsAndCreatesHelpers()
    {
        using var temp = new TempDirectory();
        var built = FixtureAssemblyBuilder.Build(FixtureSource, $"fixture-net-{Guid.NewGuid():N}", temp.Path);

        using var module = ModuleDefMD.Load(built);
        int redirected = WebClientTimeoutPatcher.Patch(module);

        Assert.Equal(1, redirected);

        var helper = module.GetTypes().Single(t => t.Name == WebClientTimeoutPatcher.HelperTypeName);
        var safe = helper.Methods.Single(m => m.Name == "SafeDownloadString");
        Assert.Contains(safe.Body.Instructions,
            i => i.OpCode == dnlib.DotNet.Emit.OpCodes.Ldc_I4 &&
                 (int)i.Operand == WebClientTimeoutPatcher.WatchdogTimeoutMilliseconds);

        var job = module.GetTypes().Single(t => t.Name == WebClientTimeoutPatcher.JobTypeName);
        var run = job.Methods.Single(m => m.Name == "Run");
        Assert.Single(run.Body.ExceptionHandlers);

        // The fixture's call site must now target the helper.
        var fetch = module.GetTypes().Single(t => t.Name == "FixtureNet")
            .Methods.Single(m => m.Name == "Fetch");
        Assert.Contains(fetch.Body.Instructions,
            i => i.OpCode == dnlib.DotNet.Emit.OpCodes.Call &&
                 i.Operand is IMethod m && m.Name == "SafeDownloadString");
    }

    [Fact]
    public void SecondPatchIsIdempotent()
    {
        using var temp = new TempDirectory();
        var built = FixtureAssemblyBuilder.Build(FixtureSource, $"fixture-net-{Guid.NewGuid():N}", temp.Path);

        using var module = ModuleDefMD.Load(built);
        Assert.Equal(1, WebClientTimeoutPatcher.Patch(module));
        Assert.Equal(0, WebClientTimeoutPatcher.Patch(module));
        Assert.Single(module.GetTypes().Where(t => t.Name == WebClientTimeoutPatcher.HelperTypeName));
        Assert.Single(module.GetTypes().Where(t => t.Name == WebClientTimeoutPatcher.JobTypeName));
    }

    [Fact]
    public void SuccessfulDownloadReturnsBody()
    {
        using var temp = new TempDirectory();
        var patched = BuildPatchedFixture(temp.Path);

        using var server = new HangingServer(respond: true);
        var result = InvokeFetch(patched, server.Url);

        Assert.Equal("ok", result);
    }

    [Fact]
    public void StalledDownloadIsCutOffByTheWatchdog()
    {
        using var temp = new TempDirectory();
        var patched = BuildPatchedFixture(temp.Path);

        using var server = new HangingServer(respond: false);

        var sw = Stopwatch.StartNew();
        var ex = Record.Exception(() => InvokeFetch(patched, server.Url));
        sw.Stop();

        var web = Assert.IsType<WebException>(ex);
        Assert.Contains("compatibility watchdog", web.Message);
        Assert.InRange(sw.Elapsed.TotalSeconds, 25.0, 45.0);
    }

    private static string InvokeFetch(string assemblyPath, string url)
    {
        var asm = Assembly.LoadFrom(assemblyPath);
        var fetch = asm.GetType("FixtureNet")!.GetMethod("Fetch", BindingFlags.Public | BindingFlags.Static)!;
        try
        {
            return (string)fetch.Invoke(null, new object[] { url })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private sealed class HangingServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly bool _respond;
        private volatile bool _stop;

        public HangingServer(bool respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/watchdog";

            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public string Url { get; }

        private void Serve()
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                using var stream = client.GetStream();
                // Read the request head so the client is in its response wait.
                var buffer = new byte[4096];
                stream.ReadTimeout = 10000;
                try { stream.Read(buffer, 0, buffer.Length); } catch { }

                if (_respond)
                {
                    var body = "ok"u8.ToArray();
                    var head = System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(head);
                    stream.Write(body);
                    stream.Flush();
                }
                else
                {
                    // Accept and never answer; the watchdog has to break the wait.
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    while (!_stop && DateTime.UtcNow < deadline)
                        Thread.Sleep(100);
                }
            }
            catch
            {
                // Best effort; the assertions decide.
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
        }
    }
}
