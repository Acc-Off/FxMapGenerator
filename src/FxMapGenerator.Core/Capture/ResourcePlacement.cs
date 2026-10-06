using System.Text.Json;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Capture;

/// <summary>
/// That the capture resource of a version is on the project's server (<c>state/capture-resource.json</c>): the app wrote
/// it into a folder (<c>saved</c>), the resource answered with it (<c>answered</c>), or the user said it is there
/// (<c>user</c>: put there some other way, such as a zip uploaded to a rented server). The app cannot see the server's
/// folders, so this is the only way it knows; a new version of the app wants the new resource again.
/// </summary>
public sealed record ResourcePlacement(string Version, string How, DateTime AtUtc, string? Folder = null)
{
    public const string FileName = "capture-resource.json";
    public const string Saved = "saved", Answered = "answered", User = "user";

    static string PathOf(WorkFolder folder) => System.IO.Path.Combine(folder.State, FileName);

    public static ResourcePlacement? Read(WorkFolder folder)
    {
        var path = PathOf(folder);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ResourcePlacement>(File.ReadAllText(path), Project.Json); }
        catch (JsonException) { return null; }
    }

    public void Write(WorkFolder folder)
    {
        Directory.CreateDirectory(folder.State);
        var path = PathOf(folder);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Project.Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static void Clear(WorkFolder folder) => File.Delete(PathOf(folder));
}
