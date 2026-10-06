using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Jobs;

/// <summary>
/// <c>logs/run-&lt;yyyyMMdd-HHmmss&gt;/</c> of one run: <c>inputs/</c> (the project files the run reads, copied at its
/// start: the project file, its road edits file when it has one, and in <c>styles/</c> the project's own styles its maps
/// use), <c>run.log</c> (what happened), <c>units.csv</c> (one line per unit: stage, unit, start, seconds, result,
/// worker limit; to the microsecond, the time the unit held its worker) and <c>run.json</c> (the last snapshot, the plan
/// at the start and the level changes).
/// </summary>
public sealed class RunFolder : IDisposable
{
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    readonly object _sync = new();
    readonly StreamWriter _log;
    readonly StreamWriter _units;

    RunFolder(string id, string path)
    {
        Id = id;
        Path = path;
        Directory.CreateDirectory(Inputs);
        _log = new StreamWriter(new FileStream(System.IO.Path.Combine(path, "run.log"), FileMode.Append, FileAccess.Write, FileShare.Read), Utf8NoBom) { AutoFlush = true };
        _units = new StreamWriter(new FileStream(System.IO.Path.Combine(path, "units.csv"), FileMode.Append, FileAccess.Write, FileShare.Read), Utf8NoBom) { AutoFlush = true };
        _units.WriteLine("stage,unit,started_utc,seconds,result,workers");
    }

    /// <summary><c>run-20260926-153012</c> (local time; <c>-2</c>, <c>-3</c>... when two runs start in the same second).</summary>
    public string Id { get; }
    public string Path { get; }
    public string Inputs => System.IO.Path.Combine(Path, "inputs");
    public string RecordPath => System.IO.Path.Combine(Path, "run.json");

    public static RunFolder Create(WorkFolder folder, DateTime localNow)
    {
        var baseId = "run-" + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (int n = 1; ; n++)
        {
            var id = n == 1 ? baseId : $"{baseId}-{n}";
            var path = System.IO.Path.Combine(folder.Logs, id);
            if (Directory.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return new RunFolder(id, path);
        }
    }

    /// <summary>Copies a file the run reads into <c>inputs/</c> and returns the copy's path.</summary>
    public string CopyInput(string file, bool again = false)
    {
        var target = System.IO.Path.Combine(Inputs, System.IO.Path.GetFileName(file));
        File.Copy(file, target, overwrite: again);
        return target;
    }

    public void Log(string line)
    {
        lock (_sync) _log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}");
    }

    public void Unit(string stage, string unit, DateTime startedUtc, double seconds, string result, int workers)
    {
        var inv = CultureInfo.InvariantCulture;
        lock (_sync) _units.WriteLine(string.Join(',', stage, unit, startedUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", inv), seconds.ToString("0.000000", inv), result, workers.ToString(inv)));
    }

    /// <summary>Writes run.json through a temporary file.</summary>
    public void WriteRecord(object record)
    {
        lock (_sync)
        {
            var tmp = RecordPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json), Utf8NoBom);
            File.Move(tmp, RecordPath, overwrite: true);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _log.Dispose();
            _units.Dispose();
        }
    }
}

/// <summary>
/// <c>state/run.lock</c>, held open while a run uses the work folder, so a second run (another window, or the command
/// line) cannot write the same state at the same time. The file names the holder; it is released when the process ends.
/// </summary>
public sealed class WorkFolderLock : IDisposable
{
    readonly FileStream _stream;

    WorkFolderLock(FileStream stream) => _stream = stream;

    static string PathOf(WorkFolder folder) => System.IO.Path.Combine(folder.State, "run.lock");

    /// <summary>The lock, or null with the holder's description when another run holds it.</summary>
    public static WorkFolderLock? TryAcquire(WorkFolder folder, out string? holder)
    {
        Directory.CreateDirectory(folder.State);
        try
        {
            var stream = new FileStream(PathOf(folder), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            stream.SetLength(0);
            var text = Utf8NoBom.GetBytes($"pid {Environment.ProcessId}, since {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            stream.Write(text);
            stream.Flush();
            holder = null;
            return new WorkFolderLock(stream);
        }
        catch (IOException)
        {
            holder = Holder(folder) ?? "another run";
            return null;
        }
    }

    /// <summary>Who holds the lock now, or null when nobody does.</summary>
    public static string? Holder(WorkFolder folder)
    {
        var path = PathOf(folder);
        if (!File.Exists(path)) return null;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return null;
        }
        catch (IOException)
        {
            try
            {
                using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(read);
                return reader.ReadToEnd().Trim();
            }
            catch (IOException) { return "another run"; }
        }
    }

    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public void Dispose() => _stream.Dispose();
}
