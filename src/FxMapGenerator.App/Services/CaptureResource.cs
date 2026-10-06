using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FxMapGenerator.App.Services;

/// <summary>
/// The capture resource (<c>fxmapgen-capture</c>) inside the exe: its files and version (<c>fxmanifest.lua</c>), written
/// out as a folder into the server's resources, or as a zip holding that one folder. The folder name is the resource
/// name: the app starts and stops the resource by it.
/// </summary>
public static partial class CaptureResource
{
    public const string Name = "fxmapgen-capture";
    const string Prefix = "capture/";

    static readonly Lazy<IReadOnlyList<(string Path, byte[] Data)>> Bundled = new(Load);

    /// <summary>The resource's files: the path inside the resource folder (with '/') and the bytes.</summary>
    public static IReadOnlyList<(string Path, byte[] Data)> Files => Bundled.Value;

    /// <summary>The version in its <c>fxmanifest.lua</c>.</summary>
    public static string Version =>
        VersionOf(Encoding.UTF8.GetString(Files.First(f => f.Path == "fxmanifest.lua").Data)) ?? "?";

    static IReadOnlyList<(string, byte[])> Load()
    {
        var assembly = typeof(CaptureResource).Assembly;
        var list = new List<(string, byte[])>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            list.Add((name[Prefix.Length..].Replace('\\', '/'), ms.ToArray()));
        }
        if (!list.Any(f => f.Item1 == "fxmanifest.lua")) throw new InvalidDataException("the capture resource is not in the exe");
        return list;
    }

    /// <summary>The <c>version '...'</c> of a manifest; null without one.</summary>
    public static string? VersionOf(string manifest) => ManifestVersion().Match(manifest) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// What a folder holds under the resource's name: null when nothing, else the version of the manifest there ("?" when
    /// the folder has no readable manifest).
    /// </summary>
    public static string? Found(string parent)
    {
        var folder = Path.Combine(parent, Name);
        if (!Directory.Exists(folder)) return File.Exists(folder) ? "?" : null;
        var manifest = Path.Combine(folder, "fxmanifest.lua");
        try { return File.Exists(manifest) ? VersionOf(File.ReadAllText(manifest)) ?? "?" : "?"; }
        catch (IOException) { return "?"; }
    }

    /// <summary>Writes the resource folder into <paramref name="parent"/>, in place of one already there; returns the folder.</summary>
    public static string WriteTo(string parent)
    {
        var folder = Path.Combine(parent, Name);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        foreach (var (path, data) in Files)
        {
            var file = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, data);
        }
        return folder;
    }

    /// <summary>A zip with the resource folder in it (<c>fxmapgen-capture/...</c>), for hosts that take a zip to unpack.</summary>
    public static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in Files)
            {
                var entry = zip.CreateEntry($"{Name}/{path}", CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    [GeneratedRegex(@"^\s*version\s+['""]([^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex ManifestVersion();
}
