using System.Text;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Logging;

/// <summary>Small rolling file logger: one file, rotated at 1 MB, three generations kept.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    const long MaxBytes = 1024 * 1024;
    const int Generations = 3;
    static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    readonly string _path;
    readonly object _sync = new();
    readonly LogLevel _minimum;

    public FileLoggerProvider(string path, LogLevel minimum = LogLevel.Information)
    {
        _path = path;
        _minimum = minimum;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public string Path => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    void Write(string line)
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) Rotate();
                File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom);
            }
            catch (IOException)
            {
                // logging must never take the app down
            }
        }
    }

    void Rotate()
    {
        for (var i = Generations - 1; i >= 1; i--)
        {
            var from = $"{_path}.{i}";
            if (File.Exists(from)) File.Move(from, $"{_path}.{i + 1}", overwrite: true);
        }
        File.Move(_path, $"{_path}.1", overwrite: true);
    }

    sealed class FileLogger : ILogger
    {
        readonly FileLoggerProvider _provider;
        readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Level(logLevel)} {_category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            _provider.Write(line);
        }

        static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "????",
        };
    }
}
