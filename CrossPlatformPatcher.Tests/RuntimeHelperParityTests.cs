using System.Linq;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[Collection(SequentialTestCollection.CollectionName)]
public sealed class RuntimeHelperParityTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string CoreHelperPath = Path.Combine(RepoRoot, "Core", "OpenWakeWordHelper.cs");
    private static readonly string RuntimeHelperPath = Path.Combine(RepoRoot, "PAIcom.OWW", "OpenWakeWordHelper.cs");

    private sealed record MethodParitySpec(
        string Signature,
        string[] RequiredTokens,
        string[][]? AnyOfTokenGroups = null);

    private static readonly MethodParitySpec[] CriticalMethods =
    {
        new(
            "private static bool TryNormalizeCommandToken(",
            new[]
            {
                "Path.IsPathRooted(token)",
                "token.IndexOfAny(new[] { ';', '&', '|', '`', '>', '<', '\\r', '\\n', '\\t', '\\0', ':' }) >= 0",
                "token.IndexOf('/') >= 0 || token.IndexOf('\\\\') >= 0"
            },
            new[]
            {
                new[]
                {
                    "token.Contains(\"..\", StringComparison.Ordinal)",
                    "token.IndexOf(\"..\", StringComparison.Ordinal) >= 0"
                }
            }),
        new(
            "private static string[] GetPlatformSpecificScriptExtensions(",
            new[]
            {
                "IsRunningUnderWine()",
                "RuntimeInformation.IsOSPlatform(OSPlatform.Windows)",
                "RuntimeInformation.IsOSPlatform(OSPlatform.OSX)"
            }),
        new(
            "private static bool TryResolveScriptPath(",
            new[]
            {
                "TryNormalizeCommandToken(commandToken",
                "GetPlatformSpecificScriptExtensions()",
                "Path.GetFullPath",
                "fullCandidate.StartsWith(rootPrefix"
            }),
        new(
            "private static bool TryStartScript(",
            new[]
            {
                "GetFallbackScriptTimeoutMs()",
                "TryTranslateAndExecuteBatch(scriptPath, timeoutMs, out detail)",
                "WaitForProcessExit(process, timeoutMs, killOnTimeout: false"
            }),
        new(
            "private static bool TryTranslateAndExecuteBatch(",
            new[]
            {
                "PAICOM_BATCH_TO_SHELL_MODE",
                "BatchFileTranslator.TranslateBatchContent(batchContent)",
                "ConvertWinePathToUnix",
                "WaitForProcessExit(process, timeoutMs, killOnTimeout: true"
            }),
        new(
            "private static string ConvertWinePathToUnix(",
            new[]
            {
                "winePath.Replace('\\\\', '/')",
                "unixPath.StartsWith(\"Z:/\", StringComparison.OrdinalIgnoreCase)",
                "Directory.Exists"
            }),
        new(
            "private static bool WaitForProcessExit(",
            new[]
            {
                "process.WaitForExit(timeoutMs)",
                "if (killOnTimeout)",
                "process.Kill()"
            }),
        new(
            "private static int GetFallbackScriptTimeoutMs(",
            new[]
            {
                "PAICOM_FALLBACK_SCRIPT_TIMEOUT_MS",
                "1000",
                "120000"
            }),
        new(
            "private static bool IsRunningUnderWine(",
            new[]
            {
                "PAICOM_RUNTIME_HOST_OS",
                "WINEPREFIX",
                "WINELOADER",
                "WINEDLLPATH"
            }),
        new(
            "private static string? ExtractJsonStringField(",
            new[]
            {
                "json.IndexOf(key, StringComparison.OrdinalIgnoreCase)",
                "builder.Append(current switch",
                "value.Length == 0 ? null : value"
            })
    };

    [Fact]
    public void Critical_Methods_Contain_Required_Parity_Tokens()
    {
        var coreSource = File.ReadAllText(CoreHelperPath);
        var runtimeSource = File.ReadAllText(RuntimeHelperPath);

        foreach (var spec in CriticalMethods)
        {
            var coreMethod = ExtractMethod(coreSource, spec.Signature);
            var runtimeMethod = ExtractMethod(runtimeSource, spec.Signature);

            Assert.False(string.IsNullOrWhiteSpace(coreMethod), $"Core helper missing method: {spec.Signature}");
            Assert.False(string.IsNullOrWhiteSpace(runtimeMethod), $"Runtime helper missing method: {spec.Signature}");

            foreach (var token in spec.RequiredTokens)
            {
                Assert.Contains(token, coreMethod, StringComparison.Ordinal);
                Assert.Contains(token, runtimeMethod, StringComparison.Ordinal);
            }

            if (spec.AnyOfTokenGroups == null)
                continue;

            foreach (var tokenGroup in spec.AnyOfTokenGroups)
            {
                Assert.True(tokenGroup.Any(token => coreMethod.Contains(token, StringComparison.Ordinal)),
                    $"Core helper method '{spec.Signature}' missing any-of token group: {string.Join(" | ", tokenGroup)}");
                Assert.True(tokenGroup.Any(token => runtimeMethod.Contains(token, StringComparison.Ordinal)),
                    $"Runtime helper method '{spec.Signature}' missing any-of token group: {string.Join(" | ", tokenGroup)}");
            }
        }
    }

    [Fact]
    public void ExtractRecognizedText_Uses_Partial_Fallback_In_Both_Helpers()
    {
        var coreSource = File.ReadAllText(CoreHelperPath);
        var runtimeSource = File.ReadAllText(RuntimeHelperPath);

        Assert.Contains("ExtractJsonStringField(trimmed, \"\\\"partial\\\"\")", coreSource, StringComparison.Ordinal);
        Assert.Contains("ExtractJsonStringField(trimmed, \"\\\"partial\\\"\")", runtimeSource, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
            return string.Empty;

        var braceStart = source.IndexOf('{', signatureIndex);
        if (braceStart < 0)
            return string.Empty;

        var depth = 0;
        var inString = false;
        var inChar = false;
        var inLineComment = false;
        var inBlockComment = false;
        var escaped = false;

        for (var i = braceStart; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '\'')
            {
                inChar = true;
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current != '}')
                continue;

            depth--;
            if (depth == 0)
                return source.Substring(signatureIndex, i - signatureIndex + 1);
        }

        return string.Empty;
    }
}
