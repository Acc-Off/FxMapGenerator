using FxMapGenerator.Core.Capture;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Landcover;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;

namespace FxMapGenerator.Core.Jobs;

/// <summary>
/// The stages of a build for a project, in order: the visit (when the game can be reached: shots, height grids and scans; for
/// the satellite map it orthorectifies each block beside it, see <see cref="ProvisionalOrtho"/>), then the satellite map
/// (ortho + tiles, the lower zooms), then for the atlas and the road map the game files, the road graph, the landcover, the
/// zones and region colours, the labels, the road shapes, the cells' map data, the cell drawing and the
/// lower zooms of each map, then the minimap's texture dictionaries of the map chosen for it. What is not available yet is
/// named in the notes.
/// </summary>
public static class BuildStages
{
    /// <param name="Game">The game for the visit; null: no visit, the stages use the data at hand.</param>
    /// <param name="Visit">Settings of the visit (the resource version it expects, waits).</param>
    /// <param name="GameFiles">The app's GTA V and key folders for projects that leave them empty.</param>
    public sealed record Options((double Qx, double Qy)? Scale = null, bool Recalibrate = false, IGameAccess? Game = null, VisitStage.Options? Visit = null,
        GameFilesLocation.Defaults? GameFiles = null);

    public static BuildPlan For(Project project, StateStore state, Options? options = null)
    {
        var o = options ?? new Options();
        var stages = new List<Stage>();
        var notes = new List<string>();
        var plan = Planner.Build(project, state, project.File.Parallel, Environment.ProcessorCount);
        var visit = plan.Rows.Single(r => r.Id == "visit");
        bool visitLeft = visit.Needed && visit.Remaining > 0;
        if (visitLeft && o.Game is null)
            notes.Add($"visit: {visit.Remaining} blocks miss data from the game; the game is not part of this run, the other stages use what is there");
        var minimap = project.File.Minimap.Map is { } mm && MapSet.TryParse(mm, out var mset) ? mset : null;
        if (o.Game is not null && visitLeft)
            stages.Add(new VisitStage(o.Game, o.Visit) { Follower = project.Maps.Contains(MapSet.Satellite) ? new ProvisionalOrtho(o.Scale) : null });
        if (project.Maps.Contains(MapSet.Satellite))
        {
            stages.Add(new OrthoStage(new OrthoStage.Options(o.Scale, o.Recalibrate)));
            stages.Add(new SatelliteLowZoomStage());
        }
        if (project.Maps.Any(m => m.IsCellMap))
        {
            stages.Add(new GameFilesStage(o.GameFiles));
            stages.Add(new RoadGraphStage());
            stages.Add(new LandcoverStage());
            stages.Add(new Regions.RegionsStage());
            if (project.Maps.Any(m => m.Kind == MapKind.Atlas)) stages.Add(new Labels.LabelsStage());
            stages.Add(new RoadsStage());
            stages.Add(new Cells.CellPrepStage());
            foreach (var m in project.Maps.Where(m => m.IsCellMap)) stages.Add(new Render.CellDrawStage(m));
            foreach (var m in project.Maps.Where(m => m.IsCellMap)) stages.Add(new Render.CellLowZoomStage(m));
        }
        if (minimap is not null && project.Maps.Contains(minimap))
            stages.Add(new MinimapStage(minimap));
        return new BuildPlan(stages, notes);
    }
}
