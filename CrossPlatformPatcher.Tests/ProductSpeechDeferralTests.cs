using CrossPlatformPatcher.Core;
using Xunit;

namespace CrossPlatformPatcher.Tests;

/// <summary>
/// Option A: the bridge defers to the product's own speech loop only when the
/// product's observed result resolves to the SAME command token, with enough
/// confidence to have acted on. Everything else dispatches normally.
/// </summary>
[Collection(SequentialTestCollection.CollectionName)]
public sealed class ProductSpeechDeferralTests
{
    [Theory]
    [InlineData("discord", "discord", 0.9, true)]    // exact agreement, confident: defer
    [InlineData("Discord", "discord", 0.9, true)]    // token match is case-insensitive
    [InlineData(" discord ", "discord", 0.9, true)]  // surrounding whitespace ignored
    [InlineData("discord", "discord", 0.4, true)]    // boundary confidence still counts
    [InlineData("discord", "discord", 0.39, false)]  // below floor: product may not have acted
    [InlineData("discord", "browser", 0.9, false)]   // different command: never suppress
    [InlineData("discord", null, 0.9, false)]        // unmapped product result: dispatch
    [InlineData("discord", "", 0.9, false)]
    [InlineData(null, "discord", 0.9, false)]
    [InlineData("discord", "discord", 0.0, false)]   // unreadable confidence: untrusted
    public void ShouldDefer_Matrix(string? ours, string? product, double confidence, bool expected)
    {
        Assert.Equal(expected, ProductSpeechDeferral.ShouldDefer(ours, product, confidence));
    }

    // Shape-fakes: the reader matches by member name, never by assembly, so
    // plain classes with the right suffixes exercise the real code path.

    private sealed class FakeRecognitionResult
    {
        public string Text { get; set; } = string.Empty;
        public float Confidence { get; set; }
    }

    private sealed class FakeSpeechRecognizedEventArgs
    {
        public FakeSpeechRecognizedEventArgs(FakeRecognitionResult result) { Result = result; }
        public FakeRecognitionResult Result { get; }
    }

    private sealed class UnrelatedPayload
    {
        public string Text { get; set; } = "nope";
    }

    [Fact]
    public void Unwrap_Accepts_Result_And_EventArgs_Rejects_Other()
    {
        var result = new FakeRecognitionResult { Text = "open discord", Confidence = 0.8f };

        Assert.Same(result, SapiFallbackRecognizer.UnwrapRecognitionResult(result));
        var fromArgs = SapiFallbackRecognizer.UnwrapRecognitionResult(new FakeSpeechRecognizedEventArgs(result));
        Assert.Same(result, fromArgs);
        Assert.Null(SapiFallbackRecognizer.UnwrapRecognitionResult(new UnrelatedPayload()));
        Assert.Null(SapiFallbackRecognizer.UnwrapRecognitionResult(null));
        Assert.Null(SapiFallbackRecognizer.UnwrapRecognitionResult("just a string"));
    }

    [Fact]
    public void Read_Text_And_Confidence_From_Result()
    {
        var result = new FakeRecognitionResult { Text = "open discord", Confidence = 0.75f };

        Assert.Equal("open discord", SapiFallbackRecognizer.ReadResultText(result));
        Assert.Equal(0.75, SapiFallbackRecognizer.ReadResultConfidence(result)!.Value, precision: 5);
    }

    [Fact]
    public void Read_Missing_Members_Yields_Null()
    {
        var bare = new object();
        Assert.Null(SapiFallbackRecognizer.ReadResultText(bare));
        Assert.Null(SapiFallbackRecognizer.ReadResultConfidence(bare));
        Assert.Null(SapiFallbackRecognizer.ReadResultText(null));
        Assert.Null(SapiFallbackRecognizer.ReadResultConfidence(null));
    }
}
