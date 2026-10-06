using System.Text.Json;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Projects;

/// <summary>
/// A server preset (bundled <c>data/presets/&lt;id&gt;.json</c>): the resources stopped on the game client while the capture
/// environment is on and started again after it. They draw on the screen or set the weather, the clock or the traffic
/// density every frame, which the capture resource does itself during a visit.
/// </summary>
public sealed record ServerPreset(string Id, string Name, IReadOnlyList<string> StopResources);

public static class ServerPresets
{
    const string Folder = "presets/";
    static readonly Lazy<IReadOnlyList<ServerPreset>> Bundled = new(Load);

    /// <summary>The bundled presets, by file name.</summary>
    public static IReadOnlyList<ServerPreset> All => Bundled.Value;

    public static ServerPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// A name the game console takes as one resource: no spaces, quotes or semicolons (a semicolon would start another
    /// console command).
    /// </summary>
    public static bool IsResourceName(string? name) => !string.IsNullOrEmpty(name) && name.All(c => c > ' ' && c is not (';' or '"'));

    static IReadOnlyList<ServerPreset> Load()
    {
        var list = new List<ServerPreset>();
        foreach (var name in EmbeddedData.Names(Folder).Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(EmbeddedData.Open(name));
            var r = doc.RootElement;
            var preset = new ServerPreset(r.GetProperty("id").GetString()!, r.GetProperty("name").GetString()!,
                r.GetProperty("stopResources").EnumerateArray().Select(e => e.GetString()!).ToArray());
            if (name != Folder + preset.Id + ".json") throw new InvalidDataException($"data/{name}: the id '{preset.Id}' is not the file name");
            if (preset.StopResources.FirstOrDefault(n => !IsResourceName(n)) is { } bad) throw new InvalidDataException($"data/{name}: '{bad}' is not a resource name");
            list.Add(preset);
        }
        return list;
    }
}
