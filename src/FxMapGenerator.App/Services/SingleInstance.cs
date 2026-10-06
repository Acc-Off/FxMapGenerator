using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FxMapGenerator.App.Services;

/// <summary>
/// One FxMapGenerator per data directory. The running instance publishes its URL and PID in <c>instance.json</c>;
/// a second launch opens that URL in the browser and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string FileName = "instance.json";

    readonly Mutex _mutex;

    SingleInstance(Mutex mutex, bool isFirst)
    {
        _mutex = mutex;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire(string dataDirectory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToLowerInvariant())))[..16];
        var mutex = new Mutex(initiallyOwned: true, $"Local\\FxMapGenerator-{hash}", out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    public static string InstancePath(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public sealed record InstanceInfo(string Url, int Pid);

    public static void Publish(string dataDirectory, string url)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(InstancePath(dataDirectory), JsonSerializer.Serialize(new InstanceInfo(url, Environment.ProcessId)), new UTF8Encoding(false));
    }

    /// <summary>The URL of the running instance, or null when the file is missing, malformed or points somewhere else than loopback.</summary>
    public static string? ReadUrl(string dataDirectory)
    {
        var path = InstancePath(dataDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            var info = JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(path));
            return info?.Url is { } url && url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) ? url : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public static void Remove(string dataDirectory)
    {
        try { File.Delete(InstancePath(dataDirectory)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
