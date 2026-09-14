using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class VoskSidecarClientTests
{
    [Fact]
    public void Unreachable_Server_Fails_Closed_Without_Throwing()
    {
        // Port 9 (discard) refuses immediately and hermetically.
        using var client = new VoskSidecarClient("http://127.0.0.1:9/", _ => { });

        Assert.False(client.CheckHealth());
        Assert.False(client.Init(16000f, new[] { "browser" }));
        Assert.False(client.Accept(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(client.GetPartial());
        Assert.Null(client.GetFinal());
    }

    [Fact]
    public void Empty_Pcm_Is_Rejected_Without_Network()
    {
        using var client = new VoskSidecarClient("http://127.0.0.1:9/", _ => { });

        Assert.False(client.Accept(Array.Empty<byte>()));
        Assert.False(client.Accept(null!));
    }

    [Fact]
    public void Base_Url_Resolution_Prefers_Explicit_Then_Env_Then_Default()
    {
        Assert.Equal("http://example:1", VoskSidecarClient.ResolveBaseUrl("http://example:1"));
        Assert.Equal("http://example:2/", VoskSidecarClient.NormalizeBaseUrl("  http://example:2/  "));
        Assert.Equal("http://example:2b/", VoskSidecarClient.NormalizeBaseUrl("http://example:2b"));
        using (var configured = new VoskSidecarClient("http://example:4", _ => { }))
        {
            Assert.Equal("http://example:4/", configured.BaseUrl);
        }

        var previous = Environment.GetEnvironmentVariable(VoskSidecarClient.UrlVariable);
        try
        {
            Environment.SetEnvironmentVariable(VoskSidecarClient.UrlVariable, "http://example:3");
            Assert.Equal("http://example:3", VoskSidecarClient.ResolveBaseUrl(null));
            Assert.Equal("http://example:4", VoskSidecarClient.ResolveBaseUrl("http://example:4"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VoskSidecarClient.UrlVariable, previous);
        }

        Environment.SetEnvironmentVariable(VoskSidecarClient.UrlVariable, null);
        Assert.Equal(VoskSidecarClient.DefaultBaseUrl, VoskSidecarClient.ResolveBaseUrl(null));
        Assert.Equal(VoskSidecarClient.DefaultBaseUrl, VoskSidecarClient.ResolveBaseUrl("  "));
    }

    [Fact]
    public void ExtractField_Parses_Vosk_Shapes()
    {
        Assert.Equal(
            "open the browser",
            VoskSidecarClient.ExtractField("{\"text\":\"open the browser\"}", "text"));
        Assert.Equal(
            "open hulu",
            VoskSidecarClient.ExtractField("{\"partial\":\"open hulu\"}", "partial"));
        Assert.Null(VoskSidecarClient.ExtractField("{}", "text"));
        Assert.Null(VoskSidecarClient.ExtractField("{\"text\":\"\"}", "text"));
        Assert.Null(VoskSidecarClient.ExtractField("{\"text\":\"   \"}", "text"));
        Assert.Null(VoskSidecarClient.ExtractField(null, "text"));
        Assert.Null(VoskSidecarClient.ExtractField("not json", "text"));
    }

    [Fact]
    public void Dispose_Is_Idempotent_And_Never_Throws()
    {
        var client = new VoskSidecarClient("http://127.0.0.1:9/", _ => { });
        var ex = Record.Exception(() =>
        {
            client.Dispose();
            client.Dispose();
        });
        Assert.Null(ex);
    }
}
