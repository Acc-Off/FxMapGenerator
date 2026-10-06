using System.Diagnostics;

namespace FxMapGenerator.Core.Tests;

/// <summary>
/// Tests that start other programs run alone. A child process inherits the handles that are open and inheritable at that
/// moment (native code such as Skia opens files that way), keeps them until it exits, and so makes other tests fail to
/// delete their files.
/// </summary>
[CollectionDefinition(nameof(ChildProcesses), DisableParallelization = true)]
public sealed class ChildProcesses;

/// <summary>
/// Tests that measure a time or count the workers at work run alone, after the tests that run side by side. Beside
/// tests that draw pictures and run whole jobs, a timer or a worker starts late by more than such a test allows: a wait
/// of 250 ms was measured as 850, and one worker was at work where two were expected.
/// </summary>
[CollectionDefinition(nameof(Timing), DisableParallelization = true)]
public sealed class Timing;

/// <summary>Files of the repository the tests read directly (the FiveM resource, the build properties).</summary>
public static class RepoFiles
{
    static readonly Lazy<string> RootPath = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "FxMapGenerator.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root (FxMapGenerator.slnx) not found above " + AppContext.BaseDirectory);
    });

    public static string Root => RootPath.Value;

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary>
    /// A Lua 5.4 interpreter: FXMAPGEN_LUA, else lua5.4 / lua54 / lua on the PATH that says "Lua 5.4" to <c>-v</c>;
    /// null when there is none.
    /// </summary>
    public static string? Lua54()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("FXMAPGEN_LUA") is { Length: > 0 } set) candidates.Add(set);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "lua5.4", "lua54", "lua" })
                foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe" } : [""])
                    candidates.Add(System.IO.Path.Combine(dir, name + ext));
        foreach (var exe in candidates.Where(File.Exists))
        {
            var (code, output) = Run(exe, ["-v"], Root);
            if (code == 0 && output.Contains("Lua 5.4", StringComparison.Ordinal)) return exe;
        }
        return null;
    }

    /// <summary>Runs a program to the end; exit code and stdout + stderr.</summary>
    public static (int Code, string Output) Run(string exe, IEnumerable<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.Result);
    }
}
