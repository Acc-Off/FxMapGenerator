namespace FxMapGenerator.GameData.Gta;

/// <summary>
/// Finds the GTA V (Legacy) installation folder and validates candidate folders. Taken from EmotePreviewer (same author,
/// MIT).
/// </summary>
public static class GtaLocator
{
    public const string ExecutableName = "GTA5.exe";

    /// <summary>Environment variable that overrides the registry lookup (used by developers and tests).</summary>
    public const string FolderVariable = "GTA_FOLDER";

    /// <summary>True when <paramref name="folder"/> contains GTA5.exe.</summary>
    public static bool IsGtaFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, ExecutableName));

    /// <summary>
    /// Detects the install folder: <see cref="FolderVariable"/> first, then the Rockstar Games registry keys
    /// (Steam, Rockstar Launcher, Epic). Returns null when nothing valid is found.
    /// </summary>
    public static string? Detect()
    {
        var env = Environment.GetEnvironmentVariable(FolderVariable);
        if (IsGtaFolder(env)) return Normalize(env!);
        return DetectFromRegistry();
    }

    /// <summary>Registry lookup only (Windows). Null on other platforms or when no valid folder is registered.</summary>
    public static string? DetectFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V");
            if (key == null) return null;
            foreach (var name in new[] { "InstallFolderSteam", "InstallFolder", "InstallFolderEpic" })
            {
                if (key.GetValue(name) is string s && IsGtaFolder(s)) return Normalize(s);
            }
        }
        catch (Exception)
        {
            // registry access can fail under restricted accounts; treat as "not found"
        }
        return null;
    }

    static string Normalize(string folder) => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
