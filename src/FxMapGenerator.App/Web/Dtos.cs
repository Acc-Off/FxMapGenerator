using System.Reflection;
using FxMapGenerator.App.Services;

namespace FxMapGenerator.App.Web;

/// <summary>Error envelope of every failed API call: <c>{ "error": { "code": "...", "message": "..." } }</c>.</summary>
public sealed record ApiError(ApiErrorBody Error)
{
    public static ApiError Of(string code, string message) => new(new ApiErrorBody(code, message));
}

public sealed record ApiErrorBody(string Code, string Message);

public static class AppVersion
{
    public static readonly string Value =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}

/// <summary><c>GET /api/status</c> and the SSE <c>status</c> event.</summary>
public sealed record StatusDto(string Version);

/// <summary>
/// <c>GET /api/settings/gamefiles</c>: the GTA V folder (found or not: <c>gtaProblem</c> <c>notFound</c> = none given and none
/// in the registry, <c>notGta</c> = no GTA5.exe there) and the key folder (null when no folder tried is complete), each with
/// where it came from (<c>app</c>, <c>detected</c>, the key search's <c>environment</c> / <c>default</c> / <c>emotePreviewer</c>)
/// and whether the command line set it.
/// </summary>
public sealed record GameFilesStatusDto(string? Gta, string? GtaSource, bool GtaFound, string? GtaProblem, bool GtaFromCommandLine,
    string? Keys, string? KeysSource, bool KeysFound, IReadOnlyList<KeysTriedDto> KeysTried, bool KeysFromCommandLine);

public sealed record KeysTriedDto(string Folder, string Source, IReadOnlyList<string> Missing);

/// <summary><c>GET /api/diagnostics</c>: where things are and what is bundled.</summary>
public sealed record DiagnosticsDto(
    string Version,
    string DataDirectory,
    string SettingsPath,
    string LogPath,
    string Url,
    IReadOnlyList<LibraryInfo> Libraries);
