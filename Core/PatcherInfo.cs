namespace CrossPlatformPatcher.Core;

/// <summary>
/// Patcher identity for manifests and logs. Resolved once from the assembly,
/// matching what the CLI banner prints.
/// </summary>
public static class PatcherInfo
{
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(PatcherInfo).Assembly;

        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var fileVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
            .OfType<System.Reflection.AssemblyFileVersionAttribute>()
            .FirstOrDefault()?.Version;

        return !string.IsNullOrWhiteSpace(fileVersion)
            ? fileVersion!
            : assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
