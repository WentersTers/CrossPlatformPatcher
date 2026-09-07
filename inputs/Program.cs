using CrossPlatformPatcher.Cli;
using CrossPlatformPatcher.Core;
using System.Reflection;

namespace CrossPlatformPatcher;

/// <summary>
/// Cross-Platform PAIcom Binary-Patch Injector Entry Point.
///
/// Thin dispatcher only: argument parsing lives in <see cref="CliOptions"/>,
/// help text in <see cref="CliHelpText"/>, patch orchestration in
/// <see cref="PatcherRunner"/>, and patch features are discovered dynamically
/// by the module registry. Adding a feature no longer touches this file.
/// </summary>
class Program
{
    private const string BuildFlavor = "Compat";
    private static readonly string ProgramVersion = ResolveProgramVersion();

    private static string ResolveProgramVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            return plusIndex >= 0 ? informational[..plusIndex] : informational;
        }

        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    static int Main(string[] args)
    {
        Console.WriteLine($"PAIcom Binary-Patch Injector ({BuildFlavor}) v{ProgramVersion}");
        Console.WriteLine($"[INFO] CrossPlatformPatcher version: {ProgramVersion}");
        Console.WriteLine("==================================");

        var options = CliOptions.Parse(args);

        return options.Verb switch
        {
            CliVerb.PrepareOnnxNatives => RunPrepareOnnx(),
            CliVerb.TestCommands => RunTestCommands(options),
            CliVerb.Help => PrintHelp(),
            CliVerb.Version => PrintVersion(),
            _ => RunPatch(options),
        };
    }

    private static int RunPrepareOnnx()
    {
        var copied = OnnxNativeLibraryManager.PrepareFromNuGetCache(
            Directory.GetCurrentDirectory(),
            Console.WriteLine);
        return copied > 0 ? 0 : 4;
    }

    private static int RunTestCommands(CliOptions options)
    {
        TestCommandInjection.Run(options.TestCommandsArgs);
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine(CliHelpText.Text);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"CrossPlatformPatcher ({BuildFlavor}) v{ProgramVersion}");
        return 0;
    }

    private static int RunPatch(CliOptions options)
    {
        var outcome = PatcherRunner.Run(options, Console.WriteLine);

        foreach (var warning in outcome.Warnings)
            Console.Error.WriteLine(warning);
        foreach (var error in outcome.Errors)
            Console.Error.WriteLine(error);

        return outcome.ExitCode;
    }
}
