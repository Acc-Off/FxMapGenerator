using FxMapGenerator.App.Game;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Tests.Capture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Tests.Demo;

/// <summary>
/// The program with a game that plays back an earlier visit, to watch (and take pictures of) the screens of a visit
/// without FiveM. Runs only when <c>FXMAPGEN_DEMO_CAPTURE</c> names a capture folder (<c>&lt;block&gt;.png</c>,
/// <c>.cam.txt</c>, <c>.hmap</c> of the blocks to visit). <c>FXMAPGEN_DEMO_DATA</c> is the app's data directory (default:
/// fxmapgen-demo in the temp folder), <c>FXMAPGEN_DEMO_PORT</c> its port (20399), <c>FXMAPGEN_DEMO_TILE_MS</c> how long a
/// block takes to settle (1500). Open the address, open a project whose range lies in the capture and press Run. The
/// host stops when a file named <c>stop</c> appears in the data directory, or after <c>FXMAPGEN_DEMO_MINUTES</c> (30).
/// </summary>
public sealed class ReplayDemo
{
    [SkippableFact]
    public async Task ServesTheAppWithAGameThatPlaysBackACapture()
    {
        var capture = Environment.GetEnvironmentVariable("FXMAPGEN_DEMO_CAPTURE");
        Skip.If(string.IsNullOrEmpty(capture), "FXMAPGEN_DEMO_CAPTURE is not set");
        Assert.True(Directory.Exists(capture), $"{capture} does not exist");
        var data = Environment.GetEnvironmentVariable("FXMAPGEN_DEMO_DATA") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "fxmapgen-demo");
        int port = Number("FXMAPGEN_DEMO_PORT", 20399);
        int minutes = Number("FXMAPGEN_DEMO_MINUTES", 30);
        Directory.CreateDirectory(data);
        var stop = Path.Combine(data, "stop");
        File.Delete(stop);

        using var game = new FakeGame { Replay = capture, TileMs = Number("FXMAPGEN_DEMO_TILE_MS", 1500), HmapLineMs = 1 };
        var app = await AppHost.StartAsync(new AppOptions
        {
            Port = port,
            PortRetries = 0,
            DataDirectory = data,
            OpenBrowser = false,
            ConsoleLogging = false,
            FileLogging = true,
            MinimumLogLevel = LogLevel.Information,
            WebRootDirectory = null,
            ConfigureServices = services =>
            {
                services.AddSingleton<IGameAccess>(game);
                // graphics settings of the data directory, never this PC's; the fake game plays FiveM, so FiveM counts as
                // running (the notice that the work in the game is over stays on the screen)
                services.AddSingleton(new RenderSettingsFile(Path.Combine(data, "gta5_settings.xml"), Path.Combine(data, "render-backups"), () => true));
            },
        });
        try
        {
            var until = DateTime.UtcNow.AddMinutes(minutes);
            while (!File.Exists(stop) && DateTime.UtcNow < until) await Task.Delay(500);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    static int Number(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out var n) ? n : fallback;
}
