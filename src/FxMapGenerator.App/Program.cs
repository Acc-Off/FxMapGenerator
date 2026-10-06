using FxMapGenerator.App;
using FxMapGenerator.App.Cli;
using FxMapGenerator.App.Services;
using FxMapGenerator.App.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// FxMapGenerator.exe [--port 20400] [--data-dir <dir>] [--gta <dir>] [--keys <dir>] [--no-browser] [--app] [--verbose] [--version] [--help]
// FxMapGenerator.exe new | import | plan ...   (subcommands without the UI, see CliCommands)

if (args.Length > 0 && CliCommands.IsCommand(args[0]))
    return CliCommands.Run(args, Console.Out, Console.Error);

var options = new AppOptions();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            if (!int.TryParse(Next("--port"), out var port) || port < 0 || port > 65535) return Fail("--port needs a number between 0 and 65535");
            options.Port = port;
            break;
        case "--data-dir":
            options.DataDirectory = Path.GetFullPath(Next("--data-dir"));
            break;
        case "--gta":
            options.GtaFolderOverride = Path.GetFullPath(Next("--gta"));
            break;
        case "--keys":
            options.KeysFolderOverride = Path.GetFullPath(Next("--keys"));
            break;
        case "--no-browser":
            options.OpenBrowser = false;
            break;
        case "--app":
            options.AppWindow = true;
            break;
        case "--verbose":
        case "-v":
            options.MinimumLogLevel = LogLevel.Debug;
            break;
        case "--version":
            Console.WriteLine($"FxMapGenerator {AppVersion.Value}");
            foreach (var lib in Libraries.List()) Console.WriteLine($"  {lib.Name,-20} {lib.Version}");
            return 0;
        case "--help":
        case "-h":
            Console.WriteLine(Usage());
            return 0;
        default:
            return Fail("unknown argument " + args[i]);
    }

    string Next(string name)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"{name} needs a value");
            Console.Error.WriteLine(Usage());
            Environment.Exit(64);
        }
        return args[++i];
    }
}

Directory.CreateDirectory(options.DataDirectory);
using var instance = SingleInstance.Acquire(options.DataDirectory);
if (!instance.IsFirst)
{
    var existing = SingleInstance.ReadUrl(options.DataDirectory);
    if (existing != null)
    {
        Console.WriteLine($"FxMapGenerator is already running at {existing}; opening it in the browser.");
        BrowserLauncher.Open(existing, options.AppWindow);
    }
    else
    {
        Console.Error.WriteLine("FxMapGenerator is already running for this data directory.");
    }
    return 0;
}

WebApplication app;
try
{
    app = await AppHost.StartAsync(options);
}
catch (Exception ex)
{
    Console.Error.WriteLine("FxMapGenerator could not start: " + ex.Message);
    return 4;
}

var url = app.Services.GetRequiredService<ListenerInfo>().Url;
SingleInstance.Publish(options.DataDirectory, url);
PrintBanner(app.Services, url);

if (options.OpenBrowser && !BrowserLauncher.Open(url, options.AppWindow))
    app.Logger.LogWarning("Could not open a browser; open {Url} manually", url);

// Ctrl+C / closing the console / POST /api/quit all end up in ApplicationStopping.
await app.WaitForShutdownAsync();
SingleInstance.Remove(options.DataDirectory);
await app.DisposeAsync();
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(Usage());
    return 64;
}

static string Usage() => """
    usage: FxMapGenerator [options]
           FxMapGenerator new | import | plan ... (run "FxMapGenerator plan" for their options)

      --port <n>        listening port (default 20400; the next 10 ports are tried when busy)
      --data-dir <dir>  settings and logs (default %LOCALAPPDATA%\FxMapGenerator)
      --gta <dir>       GTA V folder for projects that leave it empty (default: the settings, else the registry)
      --keys <dir>      RPF key folder for projects that leave it empty (default: the settings, else
                        %FXMAPGEN_KEYS%, %LOCALAPPDATA%\FxMapGenerator\keys, %LOCALAPPDATA%\EmotePreviewer\keys)
      --no-browser      do not open the browser at start-up
      --app             open the UI as an app window (Edge / Chrome --app=) instead of a tab
      --verbose, -v     debug logging
      --version         print the version and the versions of the bundled libraries
      --help, -h        this text
    """;

static void PrintBanner(IServiceProvider services, string url)
{
    var options = services.GetRequiredService<AppOptions>();
    var settings = services.GetRequiredService<SettingsStore>();
    var paths = services.GetRequiredService<HostPaths>();

    Console.WriteLine();
    Console.WriteLine($"============ FxMapGenerator {AppVersion.Value} ============");
    Console.WriteLine($"  UI        : {url}");
    Console.WriteLine($"  data      : {options.DataDirectory}");
    Console.WriteLine($"  settings  : {settings.Path}{(settings.CreatedDefault ? "  (new)" : "")}");
    if (paths.LogPath != null) Console.WriteLine($"  log       : {paths.LogPath}");
    Console.WriteLine("  Press Ctrl+C to quit.");
    Console.WriteLine("=================================================");
    Console.WriteLine();
}

/// <summary>Entry point marker so tests and tooling can reference the assembly's program type.</summary>
public partial class Program { }
