using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.GameData.Archives;
using FxMapGenerator.GameData.Gta;
using FxMapGenerator.GameData.Text;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// The game's interior maps, as the minimap resource's script needs them (<c>interiors.lua</c>, read by
/// <see cref="MinimapResource.Zoom"/>). The game draws the radar picture of an interior from a Scaleform file named
/// after it (<c>int&lt;joaat of the interior's name&gt;.gfx</c>), and a table in <c>common\data\ui\frontend.xml</c>
/// (<c>&lt;minimap_interiors&gt;</c>) lets one picture stand for several interiors and says where on the map it lies.
/// Two things are read from the user's game when a resource is written, and go nowhere but into that resource: the name
/// hashes of the interiors that have a picture of their own (the numbers of those file names, and the interiors the
/// table's other rows stand for), and the rows of the four pictures of underground passages (<see cref="Underground"/>)
/// with their places and the interiors each stands for. The four names are all the product holds.
/// </summary>
public static partial class InteriorMaps
{
    /// <summary>The resource's file.</summary>
    public const string FileName = "interiors.lua";

    /// <summary>The game's table of interior maps.</summary>
    public const string TableFile = "frontend.xml";

    /// <summary>Where the game keeps the table it uses (an earlier one is in common.rpf; a later archive's wins).</summary>
    public const string TablePath = @"update\update.rpf\common\data\ui\frontend.xml";

    /// <summary>
    /// The pictures of underground passages, laid over the map instead of replacing it: the metro with the storm
    /// drains, a water tunnel and two road tunnels.
    /// </summary>
    public static readonly IReadOnlyList<string> Underground = ["V_FakeMetro", "V_FakeWaterTunnel", "V_FakeTunnel_SC1", "V_FakeTunnel_ID1"];

    /// <summary>A row of the table: a picture, where it lies (game metres) and the interiors it stands for.</summary>
    public sealed record Row(string Name, double X, double Y, IReadOnlyList<string> Interiors);

    /// <param name="OwnPicture">The name hashes of the interiors that have a picture of their own, ascending.</param>
    /// <param name="Pictures">How many of them are the numbers of the game's picture files (the others come from the table's rows).</param>
    /// <param name="Underground">The rows of <see cref="InteriorMaps.Underground"/> the table has, in its order.</param>
    /// <param name="Missing">The names of <see cref="InteriorMaps.Underground"/> the table does not have.</param>
    public sealed record Data(IReadOnlyList<uint> OwnPicture, int Pictures, IReadOnlyList<Row> Underground, IReadOnlyList<string> Missing);

    /// <summary>Reads them from the game of <paramref name="where"/> (its GTA V and key folders).</summary>
    /// <exception cref="InvalidOperationException">No GTA V or no keys, or the game has no picture files or no table.</exception>
    public static Data Read(GameFilesLocation where, CancellationToken token = default)
    {
        if (!where.GtaFound) throw new InvalidOperationException("the interior maps are read from GTA V's files, and GTA V was not found on this PC");
        if (!where.KeysFound) throw new InvalidOperationException("the interior maps are read from GTA V's files, and the RPF keys were not found");
        GtaKeys.Install(where.KeysFolder!);
        using var ix = RpfIndex.Open(where.GtaFolder!, n => n.Equals(TableFile, StringComparison.OrdinalIgnoreCase) || PictureFile().IsMatch(n), token);
        var table = ix.Entries.Where(e => e.Path.EndsWith(@"\data\ui\" + TableFile, StringComparison.OrdinalIgnoreCase)).MaxBy(e => e.Order)
            ?? throw new InvalidOperationException($"the game has no {TableFile} ({TablePath})");
        try
        {
            return Parse(ix.Entries.Select(e => e.Name), Encoding.UTF8.GetString(RpfIndex.Read(table.File)));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"the interior maps cannot be read from the game ({where.GtaFolder}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The data from the names of the game's files (those of picture files count, each number once) and the text of the
    /// table's file. The file is not well-formed XML (its comments hold runs of hyphens), so the rows are taken by their
    /// tags.
    /// </summary>
    /// <exception cref="InvalidDataException">No picture file among the names, or no table in the text.</exception>
    public static Data Parse(IEnumerable<string> fileNames, string table)
    {
        var own = new SortedSet<uint>();
        foreach (var name in fileNames)
            if (PictureFile().Match(name) is { Success: true } m && uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hash))
                own.Add(hash);
        if (own.Count == 0) throw new InvalidDataException("no interior map files (int<number>.gfx) were found");
        int pictures = own.Count;

        int from = table.IndexOf("<minimap_interiors>", StringComparison.OrdinalIgnoreCase);
        int to = from < 0 ? -1 : table.IndexOf("</minimap_interiors>", from, StringComparison.OrdinalIgnoreCase);
        if (to < 0) throw new InvalidDataException($"{TableFile} has no table of interior maps (<minimap_interiors>)");
        var rows = new List<Row>();
        foreach (Match row in TableRow().Matches(table[from..to]))
        {
            var attributes = TagAttribute().Matches(row.Groups[1].Value).ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            if (!attributes.TryGetValue("name", out var name) || name.Length == 0) continue;
            var interiors = Content().Matches(row.Groups[2].Value).Select(c => c.Groups[1].Value).Where(c => c.Length > 0).ToList();
            if (!Underground.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var interior in interiors) own.Add(Hash(interior));
                continue;
            }
            // a row without a place cannot be laid over the map: it counts as missing
            if (attributes.TryGetValue("posX", out var x) && attributes.TryGetValue("posY", out var y)
                && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
                rows.Add(new Row(name, px, py, interiors));
        }
        var missing = Underground.Where(u => !rows.Any(r => r.Name.Equals(u, StringComparison.OrdinalIgnoreCase))).ToList();
        return new Data(own.ToList(), pictures, rows, missing);
    }

    /// <summary>The hash the game names an interior by: joaat of its name in lower case.</summary>
    public static uint Hash(string interior) => Gxt2File.Joaat(interior.ToLowerInvariant());

    static string N(double v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

    static string Quoted(string s) => "'" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>The resource's <c>interiors.lua</c>: the two tables the zoom script reads.</summary>
    public static string Lua(Data data, string version)
    {
        var sb = new StringBuilder();
        sb.Append("-- The game's interior maps (FxMapGenerator ").Append(version).Append("), read from the game files of the PC that wrote this resource.\n");
        sb.Append("-- zoom.lua reads them: what the radar and the pause map show depends on the interior the player is in.\n\n");
        sb.Append("-- The name hashes of the interiors that have a radar picture of their own: ").Append(data.Pictures)
          .Append(" from the names of the game's\n-- int<hash>.gfx files, ").Append(data.OwnPicture.Count - data.Pictures)
          .Append(" more that rows of the game's table of interior maps (frontend.xml) stand for.\n");
        sb.Append("RADAR_OWN_PICTURE = {\n");
        for (int i = 0; i < data.OwnPicture.Count; i += 8)
            sb.Append("    ").AppendJoin(' ', data.OwnPicture.Skip(i).Take(8).Select(h => $"[{h.ToString(CultureInfo.InvariantCulture)}] = true,")).Append('\n');
        sb.Append("}\n\n");
        sb.Append("-- The pictures of underground passages: where the game's table puts each (x, y in game metres) and the interiors\n");
        sb.Append("-- it stands for.\n");
        sb.Append("RADAR_UNDERGROUND = {\n");
        foreach (var row in data.Underground)
        {
            sb.Append("    { name = ").Append(Quoted(row.Name)).Append(", x = ").Append(N(row.X)).Append(", y = ").Append(N(row.Y)).Append(", content = {\n");
            for (int i = 0; i < row.Interiors.Count; i += 6)
                sb.Append("        ").AppendJoin(", ", row.Interiors.Skip(i).Take(6).Select(Quoted)).Append(",\n");
            sb.Append("    } },\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    [GeneratedRegex(@"^int(\d+)\.gfx$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PictureFile();

    [GeneratedRegex(@"<interior\b([^>]*)>(.*?)</interior>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"(\w+)\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex TagAttribute();

    [GeneratedRegex(@"<content\b[^>]*?\bname\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Content();
}

/// <summary>
/// What a minimap resource takes from the game's files when it is written: the interior maps (<see cref="InteriorMaps"/>)
/// and, for a project that reads Cayo Perico's roads, the island map drawing nothing (<see cref="IslandMap"/>). They come
/// from this PC's game (<see cref="Of"/>); tests, which have no game, give them (<see cref="Given"/>).
/// </summary>
public sealed class MinimapGameFiles
{
    readonly GameFilesLocation? _where;
    readonly InteriorMaps.Data? _interiors;
    readonly byte[]? _islandMap;

    MinimapGameFiles(GameFilesLocation? where, InteriorMaps.Data? interiors, byte[]? islandMap)
    {
        _where = where;
        _interiors = interiors;
        _islandMap = islandMap;
    }

    /// <summary>This PC's game: the app's GTA V and key folders (null: found on this PC).</summary>
    public static MinimapGameFiles Of(GameFilesLocation.Defaults? gameFiles = null) => new(GameFilesLocation.ResolveApp(gameFiles ?? new()), null, null);

    /// <summary>The parts themselves, in place of a game.</summary>
    /// <param name="islandMap">The island map for a project that reads Cayo Perico's roads.</param>
    public static MinimapGameFiles Given(InteriorMaps.Data interiors, byte[]? islandMap = null) => new(null, interiors, islandMap);

    /// <summary>Why the game cannot be read: <c>gta</c> (GTA V not found), <c>keys</c> (the RPF keys not found); null = it can.</summary>
    public string? Missing => _where is null ? null : !_where.GtaFound ? "gta" : !_where.KeysFound ? "keys" : null;

    /// <summary>
    /// What stops an export or a conversion that writes the minimap resource, by <see cref="Missing"/>
    /// (<c>MINIMAP_NO_GTA</c>, <c>MINIMAP_NO_KEYS</c>); null = nothing.
    /// </summary>
    public static ExportProblem? Problem(string? missing) => missing switch
    {
        "gta" => new("MINIMAP_NO_GTA", "the minimap resource takes the game's interior maps (and Cayo Perico's island map) from GTA V's files, and GTA V was not found on this PC (see the app's settings)"),
        "keys" => new("MINIMAP_NO_KEYS", "the minimap resource takes the game's interior maps (and Cayo Perico's island map) from GTA V's files, and the RPF keys were not found (see the app's settings)"),
        _ => null,
    };

    public InteriorMaps.Data Interiors(CancellationToken token = default) => _interiors ?? InteriorMaps.Read(_where!, token);

    public byte[] IslandMap(CancellationToken token = default) =>
        _where is not null ? Export.IslandMap.Make(_where, token) : _islandMap ?? throw new InvalidOperationException("no island map was given");

    /// <summary>
    /// What the resource of the unit's project carries: the interior maps, and the island map when the project reads Cayo
    /// Perico's roads (else null). A picture of underground passages the game's table does not have is logged: its
    /// passages are then not laid over the map.
    /// </summary>
    public (InteriorMaps.Data Interiors, byte[]? IslandMap) Read(UnitContext ctx)
    {
        ctx.Report("interior maps", 0);
        var interiors = Interiors(ctx.Token);
        ctx.Log($"interior maps: {interiors.OwnPicture.Count} interiors with a picture of their own ({interiors.Pictures} picture files), " +
            $"{interiors.Underground.Count} pictures of underground passages for {interiors.Underground.Sum(r => r.Interiors.Count)} interiors");
        foreach (var name in interiors.Missing)
            ctx.Log($"the game's table of interior maps has no usable row for {name}: its passages are not laid over the map");
        if (!ctx.Project.File.CayoPerico) return (interiors, null);
        ctx.Report("island map", 0.1);
        return (interiors, IslandMap(ctx.Token));
    }
}
