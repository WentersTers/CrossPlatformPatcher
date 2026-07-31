using System.Runtime.InteropServices;

namespace SetupWizardCore.Services;

/// <summary>
/// Detects Wine, Whisky, and Homebrew installations across platforms.
/// Port of Swift DependencyChecker actor.
/// </summary>
public class DependencyChecker
{
    public enum DependencyStatus
    {
        Installed,
        NotInstalled,
        Partial
    }

    public class DependencyResult
    {
        public DependencyStatus Status { get; set; }
        public string? Path { get; set; }
        public string? Details { get; set; }
    }

    /// <summary>
    /// Check if Whisky is installed (macOS only).
    /// </summary>
    public DependencyResult CheckWhisky()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new DependencyResult
            {
                Status = DependencyStatus.NotInstalled,
                Details = "Not applicable (non-macOS)"
            };
        }

        var whiskyPath = "/Applications/Whisky.app";
        if (Directory.Exists(whiskyPath))
        {
            SetupWizardLogger.Instance.Debug($"Found Whisky at: {whiskyPath}");
            return new DependencyResult
            {
                Status = DependencyStatus.Installed,
                Path = whiskyPath,
                Details = "Whisky.app found"
            };
        }

        return new DependencyResult
        {
            Status = DependencyStatus.NotInstalled,
            Details = "Whisky.app not found in /Applications"
        };
    }

    /// <summary>
    /// Check if Wine is installed on Linux or macOS.
    /// On Windows, returns "native" status.
    /// </summary>
    public DependencyResult CheckWine()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DependencyResult
            {
                Status = DependencyStatus.Installed,
                Path = "native",
                Details = "Windows native execution"
            };
        }

        List<string> pathsToCheck = new();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            pathsToCheck.AddRange(new[]
            {
                "/Applications/Whisky.app/Contents/MacOS/wine",
                "/opt/homebrew/bin/wine64",
                "/opt/homebrew/bin/wine",
                "/usr/local/bin/wine64",
                "/usr/local/bin/wine",
                "/usr/bin/wine64",
                "/usr/bin/wine"
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check PATH first, then common paths
            pathsToCheck.AddRange(new[]
            {
                "/usr/bin/wine64",
                "/usr/bin/wine",
                "/usr/local/bin/wine64",
                "/usr/local/bin/wine"
            });
        }

        // Also check PATH via `which` command
        try
        {
            var whichWine = FindOnPath("wine64") ?? FindOnPath("wine");
            if (whichWine != null)
            {
                SetupWizardLogger.Instance.Debug($"Found Wine via PATH at: {whichWine}");
                return new DependencyResult
                {
                    Status = DependencyStatus.Installed,
                    Path = whichWine,
                    Details = "Found via PATH"
                };
            }
        }
        catch
        {
            // Ignore PATH check failures
        }

        // Check common paths
        foreach (var path in pathsToCheck)
        {
            if (File.Exists(path))
            {
                SetupWizardLogger.Instance.Debug($"Found Wine at: {path}");
                return new DependencyResult
                {
                    Status = DependencyStatus.Installed,
                    Path = path,
                    Details = $"Found at {path}"
                };
            }
        }

        return new DependencyResult
        {
            Status = DependencyStatus.NotInstalled,
            Details = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "Wine not found (try installing via Whisky or Homebrew)"
                : "Wine not found (try 'apt install wine' or 'dnf install wine')"
        };
    }

    /// <summary>
    /// Check if Homebrew is installed (macOS only).
    /// </summary>
    public DependencyResult CheckHomebrew()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new DependencyResult
            {
                Status = DependencyStatus.NotInstalled,
                Details = "Not applicable (non-macOS)"
            };
        }

        var paths = new[] { "/usr/local/bin/brew", "/opt/homebrew/bin/brew" };
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                SetupWizardLogger.Instance.Debug($"Found Homebrew at: {path}");
                return new DependencyResult
                {
                    Status = DependencyStatus.Installed,
                    Path = path,
                    Details = "Homebrew found"
                };
            }
        }

        return new DependencyResult
        {
            Status = DependencyStatus.NotInstalled,
            Details = "Homebrew not found"
        };
    }

    /// <summary>
    /// Get current system architecture description.
    /// Port of Swift getArchitecture() using RuntimeInformation instead of uname().
    /// </summary>
    public string GetArchitecture()
    {
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var osDesc = RuntimeInformation.OSDescription;
        return $"{arch} ({osDesc})";
    }

    /// <summary>
    /// Check if running on Apple Silicon.
    /// </summary>
    public bool IsAppleSilicon()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
               RuntimeInformation.OSArchitecture == Architecture.Arm64;
    }

    /// <summary>
    /// Comprehensive dependency check.
    /// Port of Swift checkAll().
    /// </summary>
    public (DependencyResult whisky, DependencyResult wine, DependencyResult homebrew) CheckAll()
    {
        return (CheckWhisky(), CheckWine(), CheckHomebrew());
    }

    /// <summary>
    /// Determine recommended Wine setup for the current platform.
    /// Port of Swift recommendedWineSetup().
    /// </summary>
    public string RecommendedWineSetup()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "native";

        var whisky = CheckWhisky();
        if (whisky.Status == DependencyStatus.Installed)
            return "whisky";

        var wine = CheckWine();
        if (wine.Status == DependencyStatus.Installed)
            return "system_wine";

        return "none";
    }

    private static string? FindOnPath(string executable)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            var paths = pathEnv.Split(Path.PathSeparator);

            foreach (var dir in paths)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var fullPath = Path.Combine(dir, executable);
                if (File.Exists(fullPath))
                    return fullPath;

                // On Windows, try with .exe extension
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var withExt = fullPath + ".exe";
                    if (File.Exists(withExt))
                        return withExt;
                }
            }
        }
        catch
        {
            // Ignore PATH search failures
        }

        return null;
    }
}
