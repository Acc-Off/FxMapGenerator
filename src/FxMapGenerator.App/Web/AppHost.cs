using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using FxMapGenerator.App.Game;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.App.Logging;
using FxMapGenerator.App.Projects;
using FxMapGenerator.App.Services;
using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace FxMapGenerator.App.Web;

/// <summary>Wires settings, services and the Kestrel loopback listener into one <see cref="WebApplication"/>.</summary>
public static class AppHost
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Builds and starts the host, retrying on higher ports when the requested one is in use.</summary>
    public static async Task<WebApplication> StartAsync(AppOptions options, CancellationToken ct = default)
    {
        var attempts = options.Port == 0 ? 1 : Math.Max(1, options.PortRetries + 1);
        for (var i = 0; ; i++)
        {
            var port = options.Port == 0 ? 0 : options.Port + i;
            var app = Build(options, port);
            try
            {
                await app.StartAsync(ct);
                return app;
            }
            catch (IOException ex) when (i + 1 < attempts && IsAddressInUse(ex))
            {
                app.Logger.LogWarning("Port {Port} is in use; trying {Next}", port, port + 1);
                await app.DisposeAsync();
            }
        }
    }

    static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }) return true;
            if (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)) return true;
            if (e.InnerException == null) break;
        }
        return false;
    }

    public static WebApplication Build(AppOptions options, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.DataDirectory);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "FxMapGenerator",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(options.MinimumLogLevel);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        if (options.ConsoleLogging)
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }
        FileLoggerProvider? fileLogger = null;
        if (options.FileLogging)
        {
            fileLogger = new FileLoggerProvider(Path.Combine(options.DataDirectory, "logs", "fxmapgen.log"), options.MinimumLogLevel);
            builder.Logging.AddProvider(fileLogger);
        }

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.DefaultIgnoreCondition = Json.DefaultIgnoreCondition;
            foreach (var c in Json.Converters) json.SerializerOptions.Converters.Add(c);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new HostPaths(options.DataDirectory, fileLogger?.Path));
        builder.Services.AddSingleton(sp =>
        {
            var store = new SettingsStore(options.DataDirectory, sp.GetRequiredService<ILogger<SettingsStore>>());
            store.Load();
            return store;
        });
        builder.Services.AddSingleton(_ => new EventHub(Json));
        builder.Services.AddSingleton<FolderDialog>();
        builder.Services.AddSingleton<IGameAccess, LocalGame>();
        builder.Services.AddSingleton(_ => RenderSettingsFile.ForUser(options.DataDirectory));
        builder.Services.AddSingleton<IBuildStages, DefaultBuildStages>();
        builder.Services.AddSingleton<IMemoryProbe>(SystemMemory.Instance);
        builder.Services.AddSingleton<JobManager>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobManager>());
        builder.Services.AddSingleton<ProjectSession>();
        builder.Services.AddSingleton<Projects.RoadEditorData>();
        builder.Services.AddSingleton<Projects.SampleLand>();
        builder.Services.AddSingleton<Projects.PreviewDrawings>();
        builder.Services.AddSingleton<ListenerInfo>();
        builder.Services.AddHostedService<StartupService>();

        builder.Services.Configure<KestrelServerOptions>(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            kestrel.Listen(IPAddress.Loopback, port ?? options.Port);
        });

        options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();

        // Only the browser on this PC may talk to us: refuse anything that does not address 127.0.0.1 / localhost.
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal) && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(ApiError.Of("BAD_HOST", "This server only accepts requests addressed to 127.0.0.1"), Json);
                return;
            }
            await next();
        });

        var webRoot = ResolveWebRoot(options, app.Logger);
        if (webRoot is not null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = webRoot,
                OnPrepareResponse = ctx =>
                {
                    var headers = ctx.Context.Response.GetTypedHeaders();
                    headers.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/assets")
                        ? new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365), Extensions = { new NameValueHeaderValue("immutable") } }
                        : new CacheControlHeaderValue { NoCache = true };
                },
            });
        }

        ApiEndpoints.Map(app);
        MapSpaFallback(app, webRoot);
        return app;
    }

    static IFileProvider? ResolveWebRoot(AppOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.WebRootDirectory))
        {
            if (Directory.Exists(options.WebRootDirectory))
            {
                logger.LogInformation("Serving the web UI from {Directory}", options.WebRootDirectory);
                return new PhysicalFileProvider(Path.GetFullPath(options.WebRootDirectory));
            }
            logger.LogWarning("{Variable} directory {Directory} does not exist; falling back to the embedded build", AppOptions.WebRootVariable, options.WebRootDirectory);
        }
        var embedded = new EmbeddedWebRoot();
        if (embedded.FileCount == 0)
        {
            logger.LogWarning("No embedded web UI found (was the frontend built?); only the API is available");
            return null;
        }
        return embedded;
    }

    static void MapSpaFallback(WebApplication app, IFileProvider? webRoot)
    {
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(ApiError.Of("NOT_FOUND", "Unknown API endpoint"), Json);
                return;
            }
            if (webRoot is null || !HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var index = webRoot.GetFileInfo("/index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
    }
}

/// <summary>Paths that endpoints report in diagnostics.</summary>
public sealed record HostPaths(string DataDirectory, string? LogPath);

/// <summary>Resolves the actual listening URL once Kestrel has started (the port may differ from the requested one).</summary>
public sealed class ListenerInfo
{
    readonly IServer _server;
    string? _url;

    public ListenerInfo(IServer server) => _server = server;

    public string Url
    {
        get
        {
            if (_url != null) return _url;
            var address = _server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            if (address == null) return $"http://127.0.0.1:{AppOptions.DefaultPort}/";
            var uri = new Uri(address.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1"));
            _url = $"http://127.0.0.1:{uri.Port}/";
            return _url;
        }
    }

    public int Port => new Uri(Url).Port;
}

/// <summary>Logs the start-up line once Kestrel listens.</summary>
sealed class StartupService : IHostedService
{
    readonly AppOptions _options;
    readonly ListenerInfo _listener;
    readonly IHostApplicationLifetime _lifetime;
    readonly ILogger<StartupService> _logger;

    public StartupService(AppOptions options, ListenerInfo listener, IHostApplicationLifetime lifetime, ILogger<StartupService> logger)
    {
        _options = options;
        _listener = listener;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(() =>
            _logger.LogInformation("FxMapGenerator {Version} listening on {Url} (data: {Data})", AppVersion.Value, _listener.Url, _options.DataDirectory));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
