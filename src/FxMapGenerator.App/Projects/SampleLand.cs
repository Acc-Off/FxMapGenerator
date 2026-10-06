using System.Security.Cryptography;
using System.Text;
using FxMapGenerator.App.Web;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Preview;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.SampleIsland;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;
using Microsoft.Extensions.Logging;

namespace FxMapGenerator.App.Projects;

/// <summary>
/// The style editor's sample land as a project of the app's own (<c>sample-island/</c> in the settings folder): made
/// from the bundled data the first time the preview needs it (again when the app's version or its bundled land
/// changed), its steps without the game run up to the cells' data (no tiles), then drawn by <see cref="StylePreview"/>.
/// </summary>
public sealed class SampleLand
{
    public const string FolderName = "sample-island";
    const string MarkerName = "sample.json";
    /// <summary>The place first shown: the middle of the land's town.</summary>
    public static readonly (double X, double Y) DefaultPlace = (-3072, 7117);
    /// <summary>The overview's width (pixels).</summary>
    const int OverviewPx = 600;

    readonly string _folder;
    readonly ILogger<SampleLand> _logger;
    readonly object _sync = new();
    string _state = "none";
    double _progress;
    string? _message;
    StylePreview? _preview;
    int _workers = Math.Max(1, Environment.ProcessorCount / 2);
    byte[]? _overview;

    public SampleLand(HostPaths paths, ILogger<SampleLand> logger)
    {
        _folder = Path.Combine(paths.DataDirectory, FolderName);
        _logger = logger;
        if (IsCurrent()) _state = "ready";
    }

    string ProjectPath => Path.Combine(_folder, SampleIsland.ProjectName + ProjectFile.Extension);

    /// <summary>What the land was made with: the app's version and the bundled land's files (another of either makes it again).</summary>
    static string MarkerText()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in EmbeddedData.Names("sample-island/").Order(StringComparer.Ordinal))
        {
            using var s = EmbeddedData.Open(name);
            var buffer = new byte[1 << 16];
            for (int n; (n = s.Read(buffer, 0, buffer.Length)) > 0;) sha.AppendData(buffer, 0, n);
        }
        return $"{{\"version\":\"{AppVersion.Value}\",\"data\":\"{Convert.ToHexStringLower(sha.GetHashAndReset())}\"}}";
    }

    bool IsCurrent()
    {
        var marker = Path.Combine(_folder, MarkerName);
        // the cells' data too (a land marked made without it is made again)
        try { return File.Exists(marker) && File.Exists(ProjectPath) && Directory.Exists(new WorkFolder(_folder).Cells) && File.ReadAllText(marker, Encoding.UTF8) == MarkerText(); }
        catch (IOException) { return false; }
    }

    /// <summary>Whether the land is made (the preview can draw).</summary>
    public bool Ready
    {
        get { lock (_sync) return _state == "ready"; }
    }

    public SampleStatusDto Status()
    {
        lock (_sync) return new SampleStatusDto(_state, Math.Round(_progress, 3), _message, Frame(), [DefaultPlace.X, DefaultPlace.Y], StylePreview.Sizes);
    }

    /// <summary>The land's frame: its cell (west, north, east, south, m).</summary>
    static double[] Frame()
    {
        var r = SampleIsland.Rect;
        return [r.X0, r.Y0, r.X1, r.Y1];
    }

    /// <summary>Starts making the land (unless it is made or being made) with this many workers; its status.</summary>
    public SampleStatusDto Start(int workers)
    {
        lock (_sync)
        {
            if (_state is "ready" or "making") return Status();
            _state = "making";
            _progress = 0;
            _message = null;
            _workers = Math.Max(1, workers);
        }
        _ = Task.Run(Make);
        return Status();
    }

    async Task Make()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
            var path = SampleIsland.Create(_folder, SampleIslandFile.Bundled());
            var runner = JobRunner.Create(new JobSetup
            {
                ProjectPath = path,
                Workers = _workers,
                // the steps up to the cells' data: the preview draws the places itself
                Stages = (p, s) =>
                {
                    var plan = BuildStages.For(p, s, new BuildStages.Options());
                    return plan with { Stages = plan.Stages.Where(st => st is not CellDrawStage and not CellLowZoomStage and not MinimapStage).ToList() };
                },
                Version = AppVersion.Value,
            });
            using var progress = new System.Threading.Timer(_ =>
            {
                var snap = runner.Snapshot();
                double done = snap.Stages.Sum(st => st.State == StageState.Done ? 1.0 : st.Total > 0 ? (double)st.Done / st.Total : 0);
                lock (_sync) _progress = snap.Stages.Count == 0 ? 0 : done / snap.Stages.Count;
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
            var end = await runner.RunAsync();
            if (end.State != JobState.Done || end.FailureCount > 0)
                throw new InvalidOperationException(end.Error ?? string.Join("; ", end.Failures.Select(f => $"{f.Stage} {f.Unit}: {f.Message}")));
            // a step left waiting ends the run too: the land is ready only with every cell's data made
            var land = Project.Load(path);
            var state = StateStore.Open(new WorkFolder(land.WorkFolderPath));
            if (CellPlan.For(land.Range.Keys).Any(c => state.StageDone(StageKeys.CellPrep, c.Id.Name) is null))
                throw new InvalidOperationException("the steps of the sample land did not make its cells' data");
            File.WriteAllText(Path.Combine(_folder, MarkerName), MarkerText(), new UTF8Encoding(false));
            lock (_sync)
            {
                _state = "ready";
                _progress = 1;
                _preview = null;
                _overview = null;
            }
            _logger.LogInformation("Sample land made in {Folder} ({Seconds:0.0} s)", _folder, end.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sample land could not be made");
            lock (_sync)
            {
                _state = "failed";
                _message = ex.Message;
            }
        }
    }

    /// <summary>The preview of the land (made once it is ready).</summary>
    public StylePreview Preview()
    {
        lock (_sync)
        {
            if (_state != "ready") throw new ProjectException("NOT_READY", "the sample land is not made yet");
            return _preview ??= new StylePreview(Project.Load(ProjectPath));
        }
    }

    /// <summary>
    /// A small picture of the whole land for choosing a place: its ground picture of the PostalCodeMap style made
    /// smaller (the frame's width <see cref="OverviewPx"/> pixels), null before the land is made.
    /// </summary>
    public byte[]? Overview()
    {
        lock (_sync)
        {
            if (_state != "ready") return null;
            if (_overview is not null) return _overview;
        }
        var project = Project.Load(ProjectPath);
        var ground = Path.Combine(CellFiles.Folder(new WorkFolder(project.WorkFolderPath), SampleIsland.Cell), CellFiles.GroundName(AtlasPresets.PostalCodeMap));
        var rgba = Images.LoadRgba(ground, out int w, out int h);
        int ow = OverviewPx, oh = (int)Math.Round((double)OverviewPx * h / w);
        var png = Images.EncodePng(Images.Resize(rgba, w, h, ow, oh), ow, oh, 80);
        lock (_sync) _overview = png;
        return png;
    }
}

/// <summary>
/// <c>GET /api/styles/sample</c>: the sample land's state (<c>none</c>, <c>making</c> with its progress 0 to 1,
/// <c>ready</c>, <c>failed</c> with the message), its frame (west, north, east, south, m), the place first shown and the
/// windows' sides the preview offers (m).
/// </summary>
public sealed record SampleStatusDto(string State, double Progress, string? Message, double[] Frame, double[] Place, IReadOnlyList<double> Sizes);
