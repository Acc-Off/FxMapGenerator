using System.Diagnostics;

namespace FxMapGenerator.App.Services;

/// <summary>Opens the UI in the default browser, or as an app window (<c>--app=</c>) in Edge / Chrome.</summary>
public static class BrowserLauncher
{
    /// <summary>Returns false when nothing could be started.</summary>
    public static bool Open(string url, bool appWindow)
    {
        if (appWindow)
        {
            var exe = FindChromiumBrowser();
            if (exe != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { ArgumentList = { $"--app={url}" }, UseShellExecute = false });
                    return true;
                }
                catch (Exception) { /* fall through to the default browser */ }
            }
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static string? FindChromiumBrowser()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        var candidates = new[]
        {
            @"Microsoft\Edge\Application\msedge.exe",
            @"Google\Chrome\Application\chrome.exe",
        };
        foreach (var rel in candidates)
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                var p = Path.Combine(root, rel);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
