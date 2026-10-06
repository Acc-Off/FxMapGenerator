using System.Security.Cryptography;
using FxMapGenerator.Core.Preview;

namespace FxMapGenerator.App.Projects;

/// <summary>
/// The style editor's windows drawn (of the sample land or of the open project), kept for the browser to read their
/// z8 tiles by the drawing's id: the two last drawings per side of the screen (the browser still reads the one before
/// while the next comes in).
/// </summary>
public sealed class PreviewDrawings
{
    readonly Dictionary<string, List<(string Id, StylePreview.Drawing Drawing)>> _drawings = new();
    /// <summary>Starts the drawings' ids, so a browser never takes an earlier run's tiles for this one's.</summary>
    static readonly string RunId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    int _drawn;

    /// <summary>Keeps a drawing for a side of the screen (the side's two last ones stay); its id.</summary>
    public string Keep(string side, StylePreview.Drawing drawing)
    {
        lock (_drawings)
        {
            var id = $"{RunId}-{++_drawn}";
            if (!_drawings.TryGetValue(side, out var list)) _drawings[side] = list = new();
            list.Add((id, drawing));
            if (list.Count > 2) list.RemoveAt(0);
            return id;
        }
    }

    /// <summary>A z8 tile of a kept drawing as PNG, or null (another drawing, a tile outside it or without a block).</summary>
    public byte[]? Tile(string id, int x, int y)
    {
        lock (_drawings)
            foreach (var list in _drawings.Values)
                foreach (var (i, d) in list)
                    if (i == id) return d.Tiles.GetValueOrDefault((x, y));
        return null;
    }
}
