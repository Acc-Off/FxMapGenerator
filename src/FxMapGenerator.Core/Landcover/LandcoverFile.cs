using System.Text.Json.Nodes;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Landcover;

/// <summary>
/// <c>data/landcover/&lt;block&gt;.grid</c>: a block's landcover as a grid file (<see cref="GridFile"/>,
/// <c>Docs/spec/project-format.ja.md</c>), n x n cells every step m, row 0 = the north edge (y0), column 0 = the west
/// edge (x0).
/// <list type="bullet">
/// <item><c>landcover</c> (u8): the ground class to draw (<see cref="GroundClasses.Names"/>; after the beach rule, the
///   majority vote and the merging of small pieces)</item>
/// <item><c>surface</c> (u8): the ground class of the material hit in each cell, before any of that</item>
/// <item><c>water</c>, <c>buildings</c> (bool)</item>
/// <item><c>heightAboveTerrain</c> (f32): the surface height minus the terrain height under it (m; roofs, trees and
///   raised decks stand out, the ground is 0)</item>
/// <item><c>canopy</c> (bool): tree canopy; only when the block's scan has the canopy probe</item>
/// </list>
/// Meta: <c>block</c>, <c>x0</c>, <c>y0</c>, <c>step</c>, <c>classes</c> (the ground class names in value order).
/// </summary>
public static class LandcoverFile
{
    public const string Folder = "landcover";

    public static string PathOf(string dataFolder, BlockId block) => Path.Combine(dataFolder, Folder, block.Name + ".grid");

    public static void Write(string path, BlockSurface block, LandcoverBuilder.Result r)
    {
        var f = new GridFile();
        f.Meta["block"] = block.Block.Name;
        f.Meta["x0"] = block.X0;
        f.Meta["y0"] = block.Y0;
        f.Meta["step"] = block.Step;
        f.Meta["classes"] = new JsonArray(GroundClasses.Names.Select(n => (JsonNode)n).ToArray());
        f.Add("landcover", r.Landcover);
        f.Add("surface", r.Surface);
        f.Add("water", r.Water);
        f.Add("buildings", r.Buildings);
        f.Add("heightAboveTerrain", r.HeightAboveTerrain);
        if (r.Canopy is not null) f.Add("canopy", r.Canopy);
        f.Save(path);
    }
}
