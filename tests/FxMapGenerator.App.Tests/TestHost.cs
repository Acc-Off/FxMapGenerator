using FxMapGenerator.App.Game;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Tests;

/// <summary>Starts the real host on a free loopback port with a scratch data directory.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Url => App.Services.GetRequiredService<ListenerInfo>().Url;

    /// <summary>
    /// What a minimap resource takes from the game's files, given in place of this PC's game: an interior with a picture
    /// of its own and one picture of underground passages.
    /// </summary>
    public static readonly InteriorMaps.Data Interiors = new([4242u], 1, [new("V_FakeMetro", -200, -1500, ["fxtest_drain"])],
        ["V_FakeWaterTunnel", "V_FakeTunnel_SC1", "V_FakeTunnel_ID1"]);

    public static async Task<TestHost> StartAsync(Action<AppOptions>? configure = null, Action<string>? prepareDataDirectory = null)
    {
        var host = new TestHost();
        Directory.CreateDirectory(host.DataDirectory);
        prepareDataDirectory?.Invoke(host.DataDirectory);
        var options = new AppOptions
        {
            Port = 0,
            DataDirectory = host.DataDirectory,
            OpenBrowser = false,
            ConsoleLogging = false,
            FileLogging = false,
            MinimumLogLevel = LogLevel.None,
            WebRootDirectory = null,
            // never this PC's game files for a minimap resource (a test that wants a PC's folders sets this to null)
            MinimapGameFiles = MinimapGameFiles.Given(Interiors),
        };
        configure?.Invoke(options);
        // never the game or the graphics settings of this PC: a game that is not running, a settings file in the data
        // directory; a test's own services come after these
        var own = options.ConfigureServices;
        options.ConfigureServices = services =>
        {
            services.AddSingleton<IGameAccess>(new NoGame());
            services.AddSingleton(new RenderSettingsFile(Path.Combine(host.DataDirectory, "gta5_settings.xml"), Path.Combine(host.DataDirectory, "render-backups"), () => false));
            own?.Invoke(services);
        };
        host.App = await AppHost.StartAsync(options);
        host.Client = new HttpClient { BaseAddress = new Uri(host.Url) };
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}

/// <summary>A game that is not running: the console never connects and there is no window.</summary>
public sealed class NoGame : IGameAccess, IGameWindow
{
    public IGameConsole OpenConsole(string host, int port) => new Console();
    public IGameWindow Window => this;
    public (int Width, int Height)? ClientSize() => null;
    public Frame? Capture() => null;

    sealed class Console : IGameConsole
    {
        public bool Connected => false;
        public event Action<string>? LineReceived { add { } remove { } }
        public void Start() { }
        public Task<bool> SendAsync(string command, CancellationToken token = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
