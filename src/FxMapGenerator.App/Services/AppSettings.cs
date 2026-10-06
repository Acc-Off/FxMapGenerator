using System.Text.Json.Serialization;

namespace FxMapGenerator.App.Services;

/// <summary>Contents of <c>settings.json</c> in the data directory: what is shared by all projects.</summary>
public sealed class AppSettings
{
    public const int MaxRecentProjects = 10;
    public const int DefaultJobListWidth = 440, MinJobListWidth = 320, MaxJobListWidth = 1200;

    /// <summary>Project files (<c>*.fxmapgen.json</c>), most recent first.</summary>
    public List<string> RecentProjects { get; set; } = new();
    /// <summary><c>auto</c>, <c>ja</c> or <c>en</c>.</summary>
    public string Language { get; set; } = "auto";
    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>The project screen's right-hand panel (job list, prerequisites) in CSS pixels, set with its splitter.</summary>
    public int JobListWidth { get; set; } = DefaultJobListWidth;
    /// <summary>The GTA V folder for projects that leave it empty; null = found in the registry.</summary>
    public string? GtaFolder { get; set; }
    /// <summary>The RPF key folder for projects that leave it empty; null = the key search order.</summary>
    public string? KeysFolder { get; set; }
    /// <summary>
    /// The answers about the screens' guides, by guide id: <c>seen</c> (shown, to its end or closed on the way) or
    /// <c>never</c> (not to be asked about again). A screen whose guide is not listed asks when it is first opened.
    /// </summary>
    public Dictionary<string, string> Guides { get; set; } = new();

    public AppSettings Clone() => new()
    {
        RecentProjects = RecentProjects.ToList(),
        Language = Language,
        Theme = Theme,
        JobListWidth = JobListWidth,
        GtaFolder = GtaFolder,
        KeysFolder = KeysFolder,
        Guides = new Dictionary<string, string>(Guides ?? new()),
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
