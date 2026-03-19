using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CrossPlatformPatcher.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SequentialTestCollection
{
    public const string CollectionName = "Sequential";
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cpx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

internal sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

internal static class ProgramInvoker
{
    private static readonly object Gate = new();

    public static CommandResult Invoke(string[] args)
    {
        lock (Gate)
        {
            var assembly = typeof(CrossPlatformPatcher.Core.AssemblyPatcher).Assembly;
            var programType = assembly.GetType("CrossPlatformPatcher.Program", throwOnError: true)!;
            var main = programType.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException("Could not locate Program.Main.");

            var originalOut = Console.Out;
            var originalErr = Console.Error;
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;

            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);

            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                Console.SetOut(stdout);
                Console.SetError(stderr);

                var exitCode = (int)(main.Invoke(null, new object[] { args }) ?? throw new InvalidOperationException("Program.Main returned null."));
                return new CommandResult(exitCode, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }
    }
}

internal static class FixtureAssemblyBuilder
{
    public static string Build(string source, string assemblyName, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var references = GetMetadataReferences();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true));

        var outputPath = System.IO.Path.Combine(outputDirectory, $"{assemblyName}.dll");
        var emitResult = compilation.Emit(outputPath);

        if (!emitResult.Success)
        {
            var diagnostics = string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Fixture compilation failed:{Environment.NewLine}{diagnostics}");
        }

        return outputPath;
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies list is unavailable.");

        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
    }
}

internal static class ArtifactAssertions
{
    private static readonly string[] SourceFileExtensions = [".cs", ".csproj", ".sln", ".fs", ".vb"];

    public static void AssertNoSourceArtifacts(string directory)
    {
        var offending = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => SourceFileExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
                           path.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("OpenWakeWordHelper.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offending.Count == 0, "Unexpected source artifacts found:" + Environment.NewLine + string.Join(Environment.NewLine, offending));
    }

    public static void AssertFileContains(string path, params string[] expectedFragments)
    {
        var content = File.ReadAllText(path);
        foreach (var fragment in expectedFragments)
            Assert.Contains(fragment, content, StringComparison.Ordinal);
    }
}
