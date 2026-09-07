namespace CrossPlatformPatcher.Cli;

/// <summary>Top-level command the CLI can dispatch to.</summary>
public enum CliVerb
{
    /// <summary>Patch a PAIcom.exe (default).</summary>
    Patch,

    /// <summary><c>-h</c> / <c>--help</c></summary>
    Help,

    /// <summary><c>-V</c> / <c>--version</c></summary>
    Version,

    /// <summary>First arg is <c>--prepare-onnx-natives</c> (immediate run, then exit).</summary>
    PrepareOnnxNatives,

    /// <summary>First arg is <c>--test-commands</c> (delegated to TestCommandInjection).</summary>
    TestCommands,
}
