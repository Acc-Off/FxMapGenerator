using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FxMapGenerator.App.Services;

/// <summary>
/// Shows the Windows folder picker on a dedicated STA thread. The browser cannot reveal real paths, so the UI
/// asks the server to pick a folder instead.
/// </summary>
public sealed class FolderDialog
{
    readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Returns the chosen folder, or null when the user cancelled or a dialog is already open.</summary>
    public async Task<string?> PickAsync(string? initial, string? title, CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct)) return null;
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        UseDescriptionForTitle = true,
                        Description = string.IsNullOrWhiteSpace(title) ? "FxMapGenerator" : title,
                        ShowNewFolderButton = false,
                    };
                    if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
                    using var owner = new ForegroundOwner();
                    var result = dialog.ShowDialog(owner);
                    tcs.TrySetResult(result == DialogResult.OK ? dialog.SelectedPath : null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            { IsBackground = true, Name = "folder-dialog" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The <c>kind</c> of <see cref="PickFileAsync"/> that lists a server's road data: a zip or a path node file.</summary>
    public const string RoadData = "roadData";

    /// <summary>The <c>kind</c> of <see cref="PickFileAsync"/> that lists PNG pictures.</summary>
    public const string Picture = "picture";

    /// <summary>
    /// Shows the open (or, with <paramref name="save"/>, the save-as) dialog for project files, or with the
    /// <paramref name="kind"/> <see cref="RoadData"/> for a zip or ynd file, with <see cref="Picture"/> for a PNG
    /// picture. Returns the chosen path (a save path always
    /// ends in <c>.fxmapgen.json</c>), or null when cancelled or a dialog is already open.
    /// </summary>
    public async Task<string?> PickFileAsync(string? initial, string? title, bool save, string? kind, CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct)) return null;
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using FileDialog dialog = save ? new SaveFileDialog { OverwritePrompt = true } : new OpenFileDialog { CheckFileExists = true };
                    dialog.Title = string.IsNullOrWhiteSpace(title) ? "FxMapGenerator" : title;
                    dialog.Filter = kind == RoadData
                        ? "Road data (*.zip; *.ynd)|*.zip;*.ynd|All files (*.*)|*.*"
                        : kind == Picture
                            ? "PNG pictures (*.png)|*.png|All files (*.*)|*.*"
                            : "FxMapGenerator project (*.fxmapgen.json)|*.fxmapgen.json|JSON (*.json)|*.json";
                    dialog.AddExtension = false;
                    if (!string.IsNullOrWhiteSpace(initial))
                    {
                        var folder = Directory.Exists(initial) ? initial : Path.GetDirectoryName(initial);
                        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder;
                        if (!Directory.Exists(initial)) dialog.FileName = Path.GetFileName(initial);
                    }
                    using var owner = new ForegroundOwner();
                    var result = dialog.ShowDialog(owner);
                    string? picked = result == DialogResult.OK ? dialog.FileName : null;
                    if (picked is not null && save && !picked.EndsWith(".fxmapgen.json", StringComparison.OrdinalIgnoreCase))
                        picked = (picked.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? picked[..^5] : picked) + ".fxmapgen.json";
                    tcs.TrySetResult(picked);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            { IsBackground = true, Name = "file-dialog" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// A hidden top-most window that owns the dialog and takes the foreground for it. The app is not the process the user
    /// is working in (the browser is), and Windows opens a background process's windows behind the others and without
    /// the keyboard; the owner joins the input of the thread that has the foreground for the moment it asks for it.
    /// </summary>
    sealed class ForegroundOwner : Form, IWin32Window
    {
        public ForegroundOwner()
        {
            TopMost = true;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new System.Drawing.Size(1, 1);
            Location = new System.Drawing.Point(-2000, -2000);
            Opacity = 0;
            Show();
            TakeForeground();
        }

        void TakeForeground()
        {
            uint own = GetCurrentThreadId(), front = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            bool joined = front != 0 && front != own && AttachThreadInput(own, front, true);
            try
            {
                BringWindowToTop(Handle);
                SetForegroundWindow(Handle);
            }
            finally
            {
                if (joined) AttachThreadInput(own, front, false);
            }
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread, uint to, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
