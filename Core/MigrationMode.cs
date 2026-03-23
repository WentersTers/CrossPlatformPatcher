namespace CrossPlatformPatcher.Core;

public enum MigrationMode
{
    Stable,
    Probe,
    Full,
}

public static class MigrationModeParser
{
    public static bool TryParse(string? raw, out MigrationMode mode)
    {
        mode = MigrationMode.Stable;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "stable":
                mode = MigrationMode.Stable;
                return true;
            case "probe":
            case "probe64":
                mode = MigrationMode.Probe;
                return true;
            case "full":
            case "full64":
                mode = MigrationMode.Full;
                return true;
            default:
                return false;
        }
    }

    public static string ToCliString(MigrationMode mode)
    {
        return mode switch
        {
            MigrationMode.Stable => "stable",
            MigrationMode.Probe => "probe",
            MigrationMode.Full => "full",
            _ => "stable",
        };
    }

    public static bool Prefers64BitRuntime(MigrationMode mode)
    {
        return mode is MigrationMode.Probe or MigrationMode.Full;
    }
}
