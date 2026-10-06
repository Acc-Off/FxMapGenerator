using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Vectors;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Cells;

/// <summary>
/// A cell's map data in the work folder, <c>cells/&lt;cell&gt;/</c>:
/// <list type="bullet">
/// <item><c>layers.json</c>: the vector layers (<see cref="CellLayer"/>) in drawing order, with the block rectangle the
///   pictures are drawn in, the 1 m grid and the blocks.</item>
/// <item><c>shade-&lt;key&gt;.grid</c>: the light of the hill shading made with a set of shade values (<c>light</c>, u8 =
///   round(255 x light)); meta <c>flat</c> (the light of flat ground) and <c>shade</c> (the values; <see cref="ShadeName"/>).</item>
/// <item><c>ground-&lt;style&gt;.png</c>: the 1 m ground picture of a style with a ground raster (RGBA, pixel (0, 0) =
///   the grid's node (0, 0)).</item>
/// <item><c>record.json</c>: what the data was made from and with (see <see cref="CellRecord"/>).</item>
/// </list>
/// </summary>
public static class CellFiles
{
    public const string Layers = "layers.json", Record = "record.json";

    public static string Folder(WorkFolder folder, CellId cell) => Path.Combine(folder.Cells, cell.Name);

    public static string GroundName(string style) => $"ground-{style}.png";

    /// <summary>
    /// The hill shading of a set of shade values: <c>shade-&lt;key&gt;.grid</c>, the key the first 12 hex digits of the
    /// SHA-256 of the values' JSON (<see cref="CellPrepStage.ShadeValues"/>); styles with the same values share it.
    /// </summary>
    public static string ShadeName(JsonNode shadeValues) =>
        $"shade-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(shadeValues.ToJsonString())))[..12]}.grid";

    static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static void WriteLayers(string path, CellArea area, IReadOnlyList<CellLayer> layers)
    {
        var sb = new StringBuilder(1 << 20);
        var r = area.Rect;
        sb.Append("{\"format\":1,\"cell\":\"").Append(area.Id.Name).Append("\",");
        sb.Append("\"rect\":[").Append(N(r.X0)).Append(',').Append(N(r.Y0)).Append(',').Append(N(r.X1)).Append(',').Append(N(r.Y1)).Append("],");
        sb.Append("\"grid\":{\"x0\":").Append(N(area.Gx0)).Append(",\"y0\":").Append(N(area.Gy0)).Append(",\"width\":").Append(area.W).Append(",\"height\":").Append(area.H).Append("},");
        sb.Append("\"blocks\":[").Append(string.Join(",", area.Blocks.Select(b => "\"" + b.Name + "\""))).Append("],\n\"layers\":[");
        for (int k = 0; k < layers.Count; k++)
        {
            var L = layers[k];
            sb.Append(k == 0 ? "\n" : ",\n");
            sb.Append("{\"kind\":\"").Append(L.Kind).Append("\",\"paint\":\"").Append(L.Paint).Append('"');
            if (L.Band >= 0) sb.Append(",\"band\":").Append(L.Band);
            if (L.Set is not null) sb.Append(",\"set\":").Append(JsonSerializer.Serialize(L.Set));
            if (L.Rings is not null)
            {
                sb.Append(",\"cells\":").Append(L.Cells).Append(",\"rings\":[");
                for (int i = 0; i < L.Rings.Count; i++)
                {
                    var ring = L.Rings[i];
                    sb.Append(i == 0 ? "\n" : ",\n").Append("{\"area\":").Append(N(ring.Area)).Append(",\"points\":[");
                    for (int j = 0; j < ring.Points.Length; j++) sb.Append(j == 0 ? "" : ",").Append(N(ring.Points[j]));
                    sb.Append("]}");
                }
                sb.Append("]}");
            }
            else
            {
                sb.Append(",\"lines\":[");
                for (int i = 0; i < L.Lines!.Count; i++)
                {
                    var line = L.Lines[i];
                    sb.Append(i == 0 ? "\n" : ",\n").Append("{\"level\":").Append(N(line.Level)).Append(",\"points\":[");
                    for (int j = 0; j < line.Points.Length; j++) sb.Append(j == 0 ? "" : ",").Append(N(line.Points[j]));
                    sb.Append("]}");
                }
                sb.Append("]}");
            }
        }
        sb.Append("\n]}\n");
        WriteAtomically(path, sb.ToString());
    }

    public static (CellLayerFileInfo Info, List<CellLayer> Layers) ReadLayers(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var rect = root.GetProperty("rect").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var grid = root.GetProperty("grid");
        var info = new CellLayerFileInfo(root.GetProperty("cell").GetString()!, (rect[0], rect[1], rect[2], rect[3]),
            grid.GetProperty("x0").GetDouble(), grid.GetProperty("y0").GetDouble(), grid.GetProperty("width").GetInt32(), grid.GetProperty("height").GetInt32(),
            root.GetProperty("blocks").EnumerateArray().Select(e => BlockId.Parse(e.GetString()!)).ToList());
        var layers = new List<CellLayer>();
        foreach (var L in root.GetProperty("layers").EnumerateArray())
        {
            string kind = L.GetProperty("kind").GetString()!, paint = L.GetProperty("paint").GetString()!;
            string? set = L.TryGetProperty("set", out var s) ? s.GetString() : null;
            int band = L.TryGetProperty("band", out var b) ? b.GetInt32() : -1;
            if (L.TryGetProperty("rings", out var rings))
                layers.Add(new CellLayer(kind, paint, set, L.GetProperty("cells").GetInt32(),
                    rings.EnumerateArray().Select(r => new Ring(r.GetProperty("area").GetDouble(), r.GetProperty("points").EnumerateArray().Select(v => v.GetDouble()).ToArray())).ToList(), null, band));
            else
                layers.Add(new CellLayer(kind, paint, set, 0, null,
                    L.GetProperty("lines").EnumerateArray().Select(l => new ContourLine(l.GetProperty("level").GetDouble(), l.GetProperty("points").EnumerateArray().Select(v => v.GetDouble()).ToArray())).ToList(), band));
        }
        return (info, layers);
    }

    public static void WriteShade(string path, Grid<byte> light, double flat, JsonNode shadeParams)
    {
        var f = new GridFile();
        f.Meta["flat"] = flat;
        f.Meta["shade"] = shadeParams.DeepClone();
        f.Add("light", light);
        var tmp = path + ".tmp";
        f.Save(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteGround(string path, byte[] rgba, int w, int h)
    {
        var tmp = path + ".tmp.png";
        Images.SavePng(tmp, rgba, w, h);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteRecord(string path, CellRecord record) =>
        WriteAtomically(path, JsonSerializer.Serialize(record, Projects.Project.Json) + "\n");

    public static CellRecord? ReadRecord(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CellRecord>(File.ReadAllText(path), Projects.Project.Json); }
        catch (JsonException) { return null; }
    }

    static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed record CellLayerFileInfo(string Cell, (double X0, double Y0, double X1, double Y1) Rect, double GridX0, double GridY0, int Width, int Height, IReadOnlyList<BlockId> Blocks);

/// <summary>
/// <c>cells/&lt;cell&gt;/record.json</c>: the blocks, where the heights came from (<c>grid</c> = the height data,
/// <c>scan</c> = the ground scan's surface, per block), the layer sets made, the sets of shade values (a shading each)
/// and the ground values (per style id), counts and seconds. A style whose values are not here makes the cell's data
/// again. <see cref="Inputs"/>: per block the digests of the files read (<see cref="State.ContentDigest"/>);
/// <see cref="Regions"/>: per region field its ground pictures read (<see cref="Regions.RegionField.KeyOf"/>) the digest of
/// the part read (null without them); <see cref="Outputs"/>: the digest of each file written (a map's drawing compares
/// those it reads, <see cref="CellPrepStage.OutputFor"/>).
/// </summary>
public sealed record CellRecord(IReadOnlyList<string> Blocks, IReadOnlyDictionary<string, string> Heights, IReadOnlyList<string> Sets,
    IReadOnlyList<JsonNode> Shades, IReadOnlyDictionary<string, JsonNode> Grounds, int Layers, int Rings, double Seconds,
    IReadOnlyDictionary<string, CellBlockInputs>? Inputs = null, IReadOnlyDictionary<string, string>? Regions = null, IReadOnlyDictionary<string, string>? Outputs = null);

/// <summary>The digests of what a cell's data read of a block: its landcover, its scan and (with the height data) its height grid.</summary>
public sealed record CellBlockInputs(string? Landcover, string? Scan, string? Height);
