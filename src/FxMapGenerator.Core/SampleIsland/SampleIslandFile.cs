using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.SampleIsland;

/// <summary>
/// The made-up land as the app bundles it (<c>data/sample-island/</c>): <c>surface.grid</c> (the surface on its 2 m
/// grid: <c>ground</c>, <c>top</c>, <c>water</c>, <c>probe</c> as f32, NaN where there is none; <c>material</c>,
/// <c>zone</c> as u8 indices; <c>onRoad</c>; <c>street</c> as u32 hashes; the grid's corner and spacing in the meta) and
/// <c>island.json</c> (the material names and zone codes the indices stand for, the names of the zones and the streets,
/// the path nodes and links, the postal codes and the points of interest). Made by the developer tools; the app only
/// reads it.
/// </summary>
public static class SampleIslandFile
{
    public const string Surface = "surface.grid", Shapes = "island.json";
    public const int CurrentFormat = 1;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Write(string folder, IslandData data)
    {
        var s = data.Surface;
        var g = new GridFile();
        g.Meta["x0"] = s.X0;
        g.Meta["y0"] = s.Y0;
        g.Meta["step"] = s.Step;
        g.Add("ground", new Grid<float>(s.Width, s.Height, s.Ground));
        g.Add("top", new Grid<float>(s.Width, s.Height, s.Top ?? s.Ground));
        g.Add("water", new Grid<float>(s.Width, s.Height, s.Water));
        g.Add("probe", new Grid<float>(s.Width, s.Height, s.Probe ?? Enumerable.Repeat(float.NaN, s.Width * s.Height).ToArray()));
        g.Add("material", new Grid<byte>(s.Width, s.Height, s.Material));
        g.Add("zone", new Grid<byte>(s.Width, s.Height, s.Zone));
        g.Add("onRoad", new Grid<bool>(s.Width, s.Height, s.OnRoad ?? new bool[s.Width * s.Height]));
        g.Add("street", new Grid<uint>(s.Width, s.Height, s.Street ?? new uint[s.Width * s.Height]));
        Directory.CreateDirectory(folder);
        g.Save(Path.Combine(folder, Surface), CompressionLevel.SmallestSize);

        static JsonObject Names(IEnumerable<(string Key, string En, string Ja)> names) =>
            new(names.Select(n => KeyValuePair.Create(n.Key, (JsonNode?)new JsonObject { ["en"] = n.En, ["ja"] = n.Ja })));
        var o = new JsonObject
        {
            ["format"] = CurrentFormat,
            ["materials"] = new JsonArray(s.MaterialNames.Select(n => (JsonNode)n).ToArray()),
            ["zoneCodes"] = new JsonArray(s.ZoneCodes.Select(n => (JsonNode)n).ToArray()),
            ["zones"] = Names(data.Zones.Select(kv => (kv.Key, kv.Value.En, kv.Value.Ja))),
            ["streets"] = Names(data.Streets.Select(kv => (kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value.En, kv.Value.Ja))),
            ["nodes"] = new JsonArray(data.Nodes.Select(n => (JsonNode)new JsonObject
            {
                ["key"] = n.Key, ["x"] = n.X, ["y"] = n.Y, ["z"] = n.Z, ["street"] = n.Street, ["junction"] = n.Junction, ["highway"] = n.Highway,
                ["tunnel"] = n.Tunnel, ["unpaved"] = n.Unpaved, ["switchedOff"] = n.SwitchedOff,
                ["junctionArea"] = n.JunctionArea is null ? null : new JsonArray(n.JunctionArea.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
            ["links"] = new JsonArray(data.Links.Select(l => (JsonNode)new JsonObject
            {
                ["from"] = l.From, ["to"] = l.To, ["lanesForward"] = l.LanesForward, ["lanesBack"] = l.LanesBack, ["narrow"] = l.Narrow,
            }).ToArray()),
            ["postals"] = new JsonArray(data.Postals.Select(p => (JsonNode)new JsonObject { ["code"] = p.Code, ["x"] = p.X, ["y"] = p.Y }).ToArray()),
            ["markers"] = new JsonArray(data.Markers.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["x"] = p.X, ["y"] = p.Y }).ToArray()),
            ["dots"] = new JsonArray(data.Dots.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["x"] = p.X, ["y"] = p.Y, ["color"] = p.Color }).ToArray()),
        };
        File.WriteAllText(Path.Combine(folder, Shapes), o.ToJsonString(Json) + "\n");
    }

    /// <summary>The land from its two files, each opened by name.</summary>
    public static IslandData Read(Func<string, Stream> open)
    {
        GridFile g;
        using (var s = open(Surface))
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            g = GridFile.Parse(ms.ToArray(), Surface);
        }
        JsonObject o;
        using (var s = open(Shapes)) o = JsonNode.Parse(s)!.AsObject();
        if ((int)o["format"]! > CurrentFormat) throw new InvalidDataException($"{Shapes}: format {(int)o["format"]!} is newer than this version reads");
        var ground = g.Get<float>("ground");
        static Dictionary<string, (string En, string Ja)> Names(JsonObject n) =>
            n.ToDictionary(kv => kv.Key, kv => ((string)kv.Value!["en"]!, (string)kv.Value!["ja"]!), StringComparer.Ordinal);
        return new IslandData
        {
            Surface = new IslandSurface
            {
                X0 = (double)g.Meta["x0"]!, Y0 = (double)g.Meta["y0"]!, Step = (double)g.Meta["step"]!, Width = ground.Width, Height = ground.Height,
                Ground = ground.Data, Top = g.Get<float>("top").Data, Water = g.Get<float>("water").Data, Probe = g.Get<float>("probe").Data,
                Material = g.Get<byte>("material").Data, MaterialNames = o["materials"]!.AsArray().Select(n => (string)n!).ToList(),
                Zone = g.Get<byte>("zone").Data, ZoneCodes = o["zoneCodes"]!.AsArray().Select(n => (string)n!).ToList(),
                OnRoad = g.Get<bool>("onRoad").Data, Street = g.Get<uint>("street").Data,
            },
            Zones = Names(o["zones"]!.AsObject()),
            Streets = Names(o["streets"]!.AsObject()).ToDictionary(kv => uint.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value),
            Nodes = o["nodes"]!.AsArray().Select(n => new IslandNode((string)n!["key"]!, (double)n["x"]!, (double)n["y"]!, (double)n["z"]!, (uint)n["street"]!,
                (bool)n["junction"]!, (bool)n["highway"]!, (bool)n["tunnel"]!, (bool)n["unpaved"]!, (bool)n["switchedOff"]!,
                n["junctionArea"] is JsonArray ja ? ja.Select(v => (double)v!).ToArray() : null)).ToList(),
            Links = o["links"]!.AsArray().Select(l => new IslandLink((string)l!["from"]!, (string)l["to"]!, (int)l["lanesForward"]!, (int)l["lanesBack"]!, (bool)l["narrow"]!)).ToList(),
            Postals = o["postals"]!.AsArray().Select(p => ((string)p!["code"]!, (double)p["x"]!, (double)p["y"]!)).ToList(),
            Markers = o["markers"]!.AsArray().Select(p => ((string)p!["id"]!, (double)p["x"]!, (double)p["y"]!)).ToList(),
            Dots = o["dots"]!.AsArray().Select(p => ((string)p!["id"]!, (double)p["x"]!, (double)p["y"]!, (string)p["color"]!)).ToList(),
        };
    }

    /// <summary>The land the app bundles.</summary>
    public static IslandData Bundled() => Read(name => EmbeddedData.Open("sample-island/" + name));
}
