using FxMapGenerator.Core.Imaging;
using FxMapGenerator.GameData.Textures;

namespace FxMapGenerator.App.Services;

public sealed record LibraryInfo(string Name, string Version);

/// <summary>Versions of the runtime and the bundled libraries, for <c>--version</c> and the diagnostics endpoint.</summary>
public static class Libraries
{
    /// <summary>The vendored gta-toolkit commit (see third_party/gta-toolkit/README-FxMapGenerator.md).</summary>
    public const string GtaToolkitCommit = "ff555267";

    /// <summary>Reading the Skia version loads the native library, so a broken single-file bundle shows up here.</summary>
    public static IReadOnlyList<LibraryInfo> List()
    {
        string skia;
        try { skia = Images.NativeVersion; }
        catch (Exception ex) { skia = "not loaded: " + ex.Message; }
        return new[]
        {
            new LibraryInfo(".NET", Environment.Version.ToString()),
            new LibraryInfo("Skia (native)", skia),
            new LibraryInfo("BCnEncoder.NET", YtdFile.EncoderVersion),
            new LibraryInfo("gta-toolkit", GtaToolkitCommit),
        };
    }
}
