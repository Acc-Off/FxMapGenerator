using System.Text.Json;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Capture;

/// <summary>
/// That the program stopped the capture resource on the project's server (<c>state/capture-stopped.json</c>): a visit
/// that took every block stops it at its end. What was found with the game before holds no longer: the next visit needs
/// the resource started and the connection check made again, so a check older than this counts as not made
/// (<see cref="PrecheckReport.LatestFor"/>).
/// </summary>
/// <param name="Resource">The name the resource answered under.</param>
public sealed record ResourceStop(DateTime AtUtc, string Resource)
{
    public const string FileName = "capture-stopped.json";

    static string PathOf(WorkFolder folder) => Path.Combine(folder.State, FileName);

    public static ResourceStop? Read(WorkFolder folder)
    {
        var path = PathOf(folder);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ResourceStop>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }

    public void Write(WorkFolder folder)
    {
        Directory.CreateDirectory(folder.State);
        var path = PathOf(folder);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Project.Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
