using FxMapGenerator.Core.Preview;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.App.Projects;

/// <summary>The style editor's preview on the open project's own data: its state, its places, the blocks it can draw.</summary>
public sealed partial class ProjectSession
{
    StylePreview? _preview;
    string? _previewKey;

    /// <summary>
    /// The open project's preview: made again when the project file or its work folder's state changed (a run made its
    /// data again, blocks came into the range). <see cref="ProjectException"/> NOT_READY while the project lacks its data.
    /// </summary>
    public StylePreview Preview()
    {
        var project = Require();
        if (StylePreview.Waiting(project) is { } why) throw new ProjectException("NOT_READY", $"the project has no {why} yet");
        var key = PreviewKey(project);
        lock (_sync)
        {
            if (_preview is null || _previewKey != key)
            {
                _preview = new StylePreview(project);
                _previewKey = key;
            }
            return _preview;
        }
    }

    static string PreviewKey(Project project)
    {
        var state = Path.Combine(new WorkFolder(project.WorkFolderPath).State, "stages.json");
        return $"{project.FilePath}|{File.GetLastWriteTimeUtc(project.FilePath).Ticks}|{(File.Exists(state) ? File.GetLastWriteTimeUtc(state).Ticks : 0)}";
    }

    /// <summary>The preview's state on the open project: what it still lacks, the blocks' frame it can draw, its places.</summary>
    public ProjectPreviewDto PreviewStatus()
    {
        var project = Require();
        if (StylePreview.Waiting(project) is { } why)
            return new ProjectPreviewDto("none", why, [], "", Places(project, null, true), Places(project, null, false), StylePreview.Sizes, MapFrameDto.Of(project.Frame));
        var ready = Preview().ReadyBlocks;
        double x0 = ready.Min(b => b.Bx) * WorldGrid.BlockSize + WorldGrid.Left, x1 = (ready.Max(b => b.Bx) + 1) * WorldGrid.BlockSize + WorldGrid.Left;
        double y0 = WorldGrid.Top - ready.Min(b => b.By) * WorldGrid.BlockSize, y1 = WorldGrid.Top - (ready.Max(b => b.By) + 1) * WorldGrid.BlockSize;
        var frame = project.Frame;
        var blocks = new System.Text.StringBuilder(frame.BlocksX * frame.BlocksY);
        foreach (var b in frame.Blocks) blocks.Append(ready.Contains(b) ? '1' : '0');
        return new ProjectPreviewDto("ready", null, [x0, y0, x1, y1], blocks.ToString(), Places(project, ready, true), Places(project, ready, false), StylePreview.Sizes,
            MapFrameDto.Of(frame));
    }

    static List<PreviewPlaceDto> Places(Project project, IReadOnlySet<BlockId>? ready, bool recommended)
    {
        bool Ready(double x, double y) => ready is not null && ready.Contains(BlockId.At(x, y));
        return recommended
            ? StylePreview.Recommended.Select(p => new PreviewPlaceDto(p.Name, p.X, p.Y, Ready(p.X, p.Y))).ToList()
            : (project.File.PreviewPlaces ?? []).Select(p => new PreviewPlaceDto(p.Name, p.X, p.Y, Ready(p.X, p.Y))).ToList();
    }

    /// <summary>The places the project adds to the recommended ones, the whole list (names trimmed, none empty); the preview's state.</summary>
    public ProjectPreviewDto SavePreviewPlaces(IReadOnlyList<PreviewPlace> places)
    {
        Project project;
        lock (_sync)
        {
            var open = _project ?? throw new ProjectException("NO_PROJECT", "no project is open");
            project = Project.Load(open.FilePath);
            var list = places.Select(p => new PreviewPlace { Name = p.Name.Trim(), X = Math.Round(p.X), Y = Math.Round(p.Y) }).ToList();
            if (list.Any(p => p.Name.Length == 0)) throw new ProjectException("INVALID", "a place has no name");
            project.File.PreviewPlaces = list.Count == 0 ? null : list;
            project.Save();
            _project = project;
        }
        Publish(project);
        return PreviewStatus();
    }
}

/// <summary>
/// <c>GET /api/project/styles/preview</c>: the preview on the open project: <c>ready</c>, or <c>none</c> with what the
/// project still lacks (<see cref="Waiting"/>); the frame of the blocks it can draw (west, north, east, south, m), those
/// blocks as a character per block of the project's map frame, row by row from its north-west corner (<c>1</c> drawn,
/// <c>0</c> not; <see cref="Map"/>), the recommended places and the project's own (each whether a block with its data is
/// there), the windows' sides (m).
/// </summary>
public sealed record ProjectPreviewDto(string State, string? Waiting, double[] Frame, string Blocks, IReadOnlyList<PreviewPlaceDto> Recommended,
    IReadOnlyList<PreviewPlaceDto> Places, IReadOnlyList<double> Sizes, MapFrameDto Map);

public sealed record PreviewPlaceDto(string Name, double X, double Y, bool Ready);
