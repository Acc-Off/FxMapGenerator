using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Services;

/// <summary>Reads and writes <c>settings.json</c> in the data directory. Saved immediately on every change; no hot reload.</summary>
public sealed class SettingsStore
{
    public const string FileName = "settings.json";
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    readonly ILogger<SettingsStore> _logger;
    readonly object _sync = new();
    AppSettings _current = new();

    public SettingsStore(string dataDirectory, ILogger<SettingsStore> logger)
    {
        DataDirectory = dataDirectory;
        Path = System.IO.Path.Combine(dataDirectory, FileName);
        _logger = logger;
    }

    public string DataDirectory { get; }
    public string Path { get; }

    /// <summary>True when no settings file existed at load time (first run).</summary>
    public bool CreatedDefault { get; private set; }

    /// <summary>A snapshot; mutate a <see cref="AppSettings.Clone"/> and pass it to <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get { lock (_sync) return _current; }
    }

    public event Action<AppSettings>? Changed;

    public void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(Path))
            {
                CreatedDefault = true;
                _current = new AppSettings();
                return;
            }
            try
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(Path, Encoding.UTF8), SettingsJsonContext.Default.AppSettings);
                _current = Normalize(loaded ?? new AppSettings());
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning("Could not read {Path} ({Message}); starting with default settings", Path, ex.Message);
                _current = new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        lock (_sync)
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(normalized, SettingsJsonContext.Default.AppSettings);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, json, Utf8NoBom);
            File.Move(tmp, Path, overwrite: true);
            _current = normalized;
        }
        Changed?.Invoke(normalized);
    }

    /// <summary>
    /// Trims the recent list (no blanks, no duplicates, at most <see cref="AppSettings.MaxRecentProjects"/>), fixes unknown
    /// choices, keeps the panel width in its range (0 or less is the default) and keeps only the guide answers that are
    /// <c>seen</c> or <c>never</c> under an id of letters and digits.
    /// </summary>
    public static AppSettings Normalize(AppSettings s)
    {
        var n = s.Clone();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        n.RecentProjects = n.RecentProjects
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Where(p => seen.Add(p))
            .Take(AppSettings.MaxRecentProjects)
            .ToList();
        n.Language = n.Language is "ja" or "en" ? n.Language : "auto";
        n.Theme = n.Theme is "light" or "dark" ? n.Theme : "system";
        n.GtaFolder = string.IsNullOrWhiteSpace(n.GtaFolder) ? null : n.GtaFolder.Trim();
        n.KeysFolder = string.IsNullOrWhiteSpace(n.KeysFolder) ? null : n.KeysFolder.Trim();
        n.JobListWidth = n.JobListWidth <= 0 ? AppSettings.DefaultJobListWidth
            : Math.Clamp(n.JobListWidth, AppSettings.MinJobListWidth, AppSettings.MaxJobListWidth);
        n.Guides = n.Guides
            .Where(g => g.Key.Length is > 0 and <= 40 && g.Key.All(char.IsAsciiLetterOrDigit) && g.Value is "seen" or "never")
            .ToDictionary(g => g.Key, g => g.Value);
        return n;
    }
}
