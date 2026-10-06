using System.Diagnostics;
using System.Globalization;
using System.Text;
using FxMapGenerator.Core.Capture;

namespace FxMapGenerator.App.Game;

/// <param name="Exists">False when FiveM has not written its settings yet (it does on its first start).</param>
/// <param name="FiveMRunning">While FiveM runs the file cannot be changed: FiveM writes its own values back when it closes.</param>
/// <param name="Backups">File names in the backup folder, newest first.</param>
public sealed record RenderStatus(string Path, bool Exists, bool FiveMRunning, IReadOnlyList<RenderSettings.Difference> Differences, IReadOnlyList<string> Backups);

/// <summary>
/// FiveM's graphics settings file (<c>%APPDATA%\CitizenFX\gta5_settings.xml</c>): its difference to the values the shots
/// need, putting them in after a backup, and putting a backup back. Neither while FiveM runs.
/// </summary>
public sealed class RenderSettingsFile(string path, string backupFolder, Func<bool>? fiveMRunning = null)
{
    public string Path { get; } = path;
    public string BackupFolder { get; } = backupFolder;

    public static RenderSettingsFile ForUser(string dataDirectory) => new(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CitizenFX", "gta5_settings.xml"),
        System.IO.Path.Combine(dataDirectory, "render-backups"));

    /// <summary>FiveM's launcher or its game process.</summary>
    public static bool AnyFiveMProcess()
    {
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                try
                {
                    if (p.ProcessName.StartsWith("FiveM", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
            }
        return false;
    }

    public bool FiveMRunning => (fiveMRunning ?? AnyFiveMProcess)();

    /// <exception cref="InvalidDataException">The file is not a settings file.</exception>
    public RenderStatus Read()
    {
        bool exists = File.Exists(Path);
        var diff = exists ? RenderSettings.Compare(ReadText()) : Array.Empty<RenderSettings.Difference>();
        return new RenderStatus(Path, exists, FiveMRunning, diff, Backups());
    }

    public IReadOnlyList<string> Backups() => Directory.Exists(BackupFolder)
        ? Directory.GetFiles(BackupFolder, "gta5_settings-*.xml").Select(f => System.IO.Path.GetFileName(f)!).OrderDescending(StringComparer.Ordinal).ToList()
        : Array.Empty<string>();

    /// <summary>Backs the file up, then puts the values for the shots in. Returns the backup's file name.</summary>
    /// <exception cref="RenderSettingsException">FIVEM_RUNNING, NO_FILE.</exception>
    public string Apply()
    {
        Guard();
        var backup = Backup();
        Write(RenderSettings.Apply(ReadText()));
        return backup;
    }

    /// <summary>Puts a backup back (the newest when none is named); the file as it is now is backed up first.</summary>
    /// <exception cref="RenderSettingsException">FIVEM_RUNNING, NO_BACKUP.</exception>
    public string Restore(string? backup = null)
    {
        if (FiveMRunning) throw new RenderSettingsException("FIVEM_RUNNING", "FiveM is running: close it first (it writes its settings back when it closes)");
        var name = backup ?? Backups().FirstOrDefault() ?? throw new RenderSettingsException("NO_BACKUP", "there is no backup to put back");
        var file = System.IO.Path.Combine(BackupFolder, System.IO.Path.GetFileName(name));
        if (!File.Exists(file)) throw new RenderSettingsException("NO_BACKUP", $"no backup {name}");
        if (File.Exists(Path)) Backup();
        File.Copy(file, Path, overwrite: true);
        return name;
    }

    void Guard()
    {
        if (FiveMRunning) throw new RenderSettingsException("FIVEM_RUNNING", "FiveM is running: close it first (it writes its settings back when it closes)");
        if (!File.Exists(Path)) throw new RenderSettingsException("NO_FILE", $"{Path} does not exist yet: start FiveM once, then close it");
    }

    string Backup()
    {
        Directory.CreateDirectory(BackupFolder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = $"gta5_settings-{stamp}.xml";
        for (int n = 2; File.Exists(System.IO.Path.Combine(BackupFolder, name)); n++) name = $"gta5_settings-{stamp}-{n}.xml";
        File.Copy(Path, System.IO.Path.Combine(BackupFolder, name));
        return name;
    }

    // the file's bytes are UTF-8 (FiveM writes no BOM; one is kept if there)
    string ReadText()
    {
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path));
        return text.StartsWith('﻿') ? text[1..] : text;
    }

    void Write(string text)
    {
        bool bom = File.ReadAllBytes(Path) is [0xEF, 0xBB, 0xBF, ..];
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(bom));
        File.Move(tmp, Path, overwrite: true);
    }
}

public sealed class RenderSettingsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
