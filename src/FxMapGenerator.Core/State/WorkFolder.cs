using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.State;

/// <summary>
/// Layout of a project's work folder:
/// <code>
/// state/     block items present, retake marks, stages done (blocks.json, stages.json)
/// capture/   &lt;block&gt;.png, .cam.txt (camera), .hmap (height grid)
/// scan/      &lt;block&gt;.txt (scan lines)
/// game/      data read from the user's game (never distributed)
/// data/      map data (ground, regions, roads, labels)
/// cells/     &lt;cell&gt;/ vector layers and caches
/// tiles/     &lt;set&gt;/{z}/{x}/{y}.png
/// before/    &lt;set&gt;/{z}/{x}/{y}.png: tiles written over since the last export, as they were before (for comparing)
/// ytd/       &lt;set&gt;/4096/ the minimap texture dictionaries of a map (the sheet size in the folder name)
/// logs/      per-run logs and input snapshots
/// </code>
/// </summary>
public sealed class WorkFolder(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string State => Path.Combine(Root, "state");
    public string Capture => Path.Combine(Root, "capture");
    public string Scan => Path.Combine(Root, "scan");
    public string Game => Path.Combine(Root, "game");
    public string Data => Path.Combine(Root, "data");
    public string Cells => Path.Combine(Root, "cells");
    public string Logs => Path.Combine(Root, "logs");
    public string Tiles(string set) => Path.Combine(Root, "tiles", set);
    public string Before(string set) => Path.Combine(Root, "before", set);
    public string Minimap(string set) => Path.Combine(Root, "ytd", set, FxMapGenerator.Core.Minimap.MinimapSheets.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string CapturePng(BlockId b) => Path.Combine(Capture, b.Name + ".png");
    public string CaptureCamera(BlockId b) => Path.Combine(Capture, b.Name + ".cam.txt");
    public string CaptureHeights(BlockId b) => Path.Combine(Capture, b.Name + ".hmap");
    public string ScanFile(BlockId b) => Path.Combine(Scan, b.Name + ".txt");
}
