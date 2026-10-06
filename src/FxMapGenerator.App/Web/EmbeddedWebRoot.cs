using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace FxMapGenerator.App.Web;

/// <summary>
/// Serves the SPA build embedded as <c>wwwroot/&lt;relative path&gt;</c> manifest resources
/// (see the EmbedWeb target in FxMapGenerator.App.csproj).
/// </summary>
public sealed class EmbeddedWebRoot : IFileProvider
{
    public const string Prefix = "wwwroot/";

    readonly Assembly _assembly;
    readonly Dictionary<string, string> _resources; // "/assets/app.js" -> manifest resource name
    readonly DateTimeOffset _lastModified;

    public EmbeddedWebRoot(Assembly? assembly = null)
    {
        _assembly = assembly ?? typeof(EmbeddedWebRoot).Assembly;
        _resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                var relative = name[Prefix.Length..].Replace('\\', '/');
                _resources["/" + relative] = name;
            }
        }
        // Empty inside a single-file bundle; the start time is then used, which is all the cache headers need.
#pragma warning disable IL3000
        var location = _assembly.Location;
#pragma warning restore IL3000
        _lastModified = !string.IsNullOrEmpty(location) && File.Exists(location) ? File.GetLastWriteTimeUtc(location) : DateTimeOffset.UtcNow;
    }

    public int FileCount => _resources.Count;

    public bool Exists(string subpath) => _resources.ContainsKey(Normalize(subpath));

    public IFileInfo GetFileInfo(string subpath)
    {
        var path = Normalize(subpath);
        return _resources.TryGetValue(path, out var resource)
            ? new ResourceFileInfo(_assembly, resource, Path.GetFileName(path), _lastModified)
            : new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    static string Normalize(string subpath)
    {
        var path = subpath.Replace('\\', '/');
        return path.StartsWith('/') ? path : "/" + path;
    }

    sealed class ResourceFileInfo : IFileInfo
    {
        readonly Assembly _assembly;
        readonly string _resource;

        public ResourceFileInfo(Assembly assembly, string resource, string name, DateTimeOffset lastModified)
        {
            _assembly = assembly;
            _resource = resource;
            Name = name;
            LastModified = lastModified;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            Length = stream.Length;
        }

        public bool Exists => true;
        public long Length { get; }
        public string? PhysicalPath => null;
        public string Name { get; }
        public DateTimeOffset LastModified { get; }
        public bool IsDirectory => false;
        public Stream CreateReadStream() => _assembly.GetManifestResourceStream(_resource)!;
    }
}
