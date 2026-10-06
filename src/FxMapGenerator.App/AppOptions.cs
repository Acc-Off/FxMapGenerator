using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App;

/// <summary>Command line and host options. Tests construct this directly; <see cref="Program"/> fills it from the arguments.</summary>
public sealed class AppOptions
{
    public const int DefaultPort = 20400;
    public const string WebRootVariable = "FXMAPGEN_WEBROOT";

    /// <summary>Requested port; 0 lets Kestrel pick one. When the port is busy, <see cref="PortRetries"/> higher ports are tried.</summary>
    public int Port { get; set; } = DefaultPort;
    public int PortRetries { get; set; } = 10;

    /// <summary>Settings and logs. Default: %LOCALAPPDATA%\FxMapGenerator.</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    /// <summary><c>--gta</c>: the GTA V folder for projects that leave it empty, before the settings and the registry.</summary>
    public string? GtaFolderOverride { get; set; }
    /// <summary><c>--keys</c>: the RPF key folder for projects that leave it empty, before the settings and the search order.</summary>
    public string? KeysFolderOverride { get; set; }

    /// <summary>
    /// What a minimap resource takes from the game's files, in place of this PC's game (null: this PC's game). Tests,
    /// which have no game, set it.
    /// </summary>
    public Core.Export.MinimapGameFiles? MinimapGameFiles { get; set; }

    public bool OpenBrowser { get; set; } = true;
    /// <summary>Open the UI as an app window (<c>msedge --app=</c>) instead of a tab.</summary>
    public bool AppWindow { get; set; }

    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;
    public bool ConsoleLogging { get; set; } = true;
    public bool FileLogging { get; set; } = true;

    /// <summary>Serve the SPA from this directory instead of the embedded build (<see cref="WebRootVariable"/>).</summary>
    public string? WebRootDirectory { get; set; } = Environment.GetEnvironmentVariable(WebRootVariable);

    /// <summary>Runs after all registrations; tests use it to swap in fakes.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FxMapGenerator");
}
