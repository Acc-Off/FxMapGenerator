using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FxMapGenerator.Core.Jobs;

/// <summary>Physical memory of the PC in bytes.</summary>
public readonly record struct MemoryStatus(long Available, long Total);

public interface IMemoryProbe
{
    MemoryStatus Read();
}

/// <summary>
/// Free and total physical memory: GlobalMemoryStatusEx on Windows, /proc/meminfo on Linux, otherwise what the
/// garbage collector knows (total only; everything counts as free).
/// </summary>
public sealed partial class SystemMemory : IMemoryProbe
{
    public static readonly SystemMemory Instance = new();

    public MemoryStatus Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref s)) return new MemoryStatus((long)s.AvailPhys, (long)s.TotalPhys);
        }
        else if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            long total = 0, available = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) continue;
                if (parts[0] == "MemTotal:") total = kb * 1024;
                else if (parts[0] == "MemAvailable:") available = kb * 1024;
            }
            if (total > 0) return new MemoryStatus(available, total);
        }
        var gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new MemoryStatus(gc, gc);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>This process's share of all processors since the previous sample (0..1), and its memory.</summary>
public sealed class ProcessLoad
{
    readonly object _sync = new();
    readonly Process _process = Process.GetCurrentProcess();
    TimeSpan _cpu;
    long _at;
    double _last;

    public ProcessLoad()
    {
        _cpu = _process.TotalProcessorTime;
        _at = Stopwatch.GetTimestamp();
    }

    /// <summary>CPU share since the last call (calls closer than 0.5 s apart repeat the last value).</summary>
    public double Cpu()
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            var wall = Stopwatch.GetElapsedTime(_at, now);
            if (wall < TimeSpan.FromMilliseconds(500)) return _last;
            _process.Refresh();
            var cpu = _process.TotalProcessorTime;
            _last = Math.Clamp((cpu - _cpu) / (wall * Environment.ProcessorCount), 0, 1);
            _cpu = cpu;
            _at = now;
            return _last;
        }
    }

    public long WorkingSet()
    {
        lock (_sync)
        {
            _process.Refresh();
            return _process.WorkingSet64;
        }
    }

    public long PeakWorkingSet()
    {
        lock (_sync)
        {
            _process.Refresh();
            return _process.PeakWorkingSet64;
        }
    }
}
