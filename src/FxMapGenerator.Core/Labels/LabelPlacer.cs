using System.Text.RegularExpressions;
using FxMapGenerator.Core.GameFiles;
using FxMapGenerator.Core.Geometry;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Poi;
using FxMapGenerator.Core.Regions;
using FxMapGenerator.Core.RoadGraph;
using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Labels;

/// <summary>
/// A label placed on the map: <c>postal</c>, <c>zone</c> or <c>street</c>; its centre, rotation (degrees), size (em, m),
/// font, weight and letter spacing (m); a street name also its glyphs along the line, and the route number written into
/// it (not kept in the labels file; the record counts them).
/// </summary>
public sealed record PlacedLabel(string Kind, string Text, double X, double Y, double Rotation, double Size, string Font, string Weight, double Spacing,
    IReadOnlyList<LabelLines.Glyph>? Chars = null, string? Route = null);

/// <summary>What the placement of one language reads.</summary>
public sealed class LabelInput
{
    /// <summary>The label frame: left, top, right, bottom (m); labels stay inside it.</summary>
    public required (double X0, double Y0, double X1, double Y1) Frame { get; init; }
    public required IReadOnlyList<PostalCode> Postals { get; init; }
    /// <summary>The points of interest drawn on this map (they take their space first).</summary>
    public required IReadOnlyList<ResolvedPoi> Pois { get; init; }
    public required ZoneGrid Zones { get; init; }
    public required GameNames Names { get; init; }
    public required IReadOnlyList<RoadGraphFile.Road> Roads { get; init; }
    public required IReadOnlyList<Route> Routes { get; init; }
    public required LabelsStyle Style { get; init; }
    public required string Language { get; init; }
    public required ITextMetrics Metrics { get; init; }
    /// <summary>The zones (their codes) whose names are not placed.</summary>
    public IReadOnlyCollection<string> UnnamedZones { get; init; } = [];
    /// <summary>Called with every street name and its parts (for checks and figures).</summary>
    public Action<string, IReadOnlyList<StreetPart>>? OnParts { get; init; }
}

/// <summary>
/// Places the labels of a map in one language, once for the whole map (the drawing only draws them): postal codes and
/// points of interest where they are; zone names at the middle of each zone's largest patch (moved up to 90 m, made
/// smaller when there is no room; none for the zones the input leaves unnamed); street names along their lines, as many
/// as the class's repeat length allows, never over anything placed before (a 1 m occupancy grid with a clearance around
/// every label).
/// </summary>
public static class LabelPlacer
{
    const double FallbackScale = 0.8;

    /// <summary>The places tried for a zone name around its centre, nearest first (the order of equal distances is fixed).</summary>
    static readonly (int Dx, int Dy)[] ZoneOffsets =
    [
        (0, 0), (0, 30), (30, 0), (-30, 0), (0, -30), (-30, -30), (30, 30), (-30, 30), (30, -30), (-60, 0), (0, 60), (60, 0), (0, -60), (-30, -60),
        (60, -30), (-60, 30), (30, 60), (60, 30), (-30, 60), (30, -60), (-60, -30), (60, -60), (-60, 60), (60, 60), (-60, -60), (0, 90), (0, -90),
        (-90, 0), (90, 0), (90, 30), (-90, -30), (30, 90), (30, -90), (90, -30), (-90, 30), (-30, 90), (-30, -90), (90, 60), (-90, -60), (90, -60),
        (-60, 90), (-60, -90), (60, 90), (60, -90), (-90, 60), (-90, 90), (-90, -90), (90, 90), (90, -90),
    ];

    public static List<PlacedLabel> Place(LabelInput input, Action<string>? log = null)
    {
        var st = input.Style;
        var (x0, y0, x1, y1) = input.Frame;
        var occ = new Occupancy(x0, y0, x1, y1, st.Clearance);
        var o = new List<PlacedLabel>();
        bool Inside(double x, double y) => x0 <= x && x < x1 && y1 < y && y <= y0;
        string lang = input.Language, font = st.Font(lang), fontEn = st.Font("en");
        var m = input.Metrics;

        // 1. postal codes, where they are
        foreach (var p in input.Postals)
        {
            if (!Inside(p.X, p.Y)) continue;
            double em = p.Size ?? st.PostalSizeIn(ZoneAt(input.Zones, p.X, p.Y));
            var (w, h) = m.Size(p.Code, fontEn, "bold", em);
            o.Add(new PlacedLabel("postal", p.Code, p.X, p.Y, 0.0, em, fontEn, "bold", 0.0));
            occ.Take([new LabelBox(p.X, p.Y, w, h, 0.0)]);
        }
        // 2. points of interest take their space (they are drawn from the points themselves)
        foreach (var poi in input.Pois)
        {
            double x = poi.Point.X, y = poi.Point.Y;
            if (!Inside(x, y)) continue;
            double w, h;
            var (text, textLang) = PoiLabel(poi, lang);
            if (poi.Style.Look is "dot" or "icon") (w, h) = PoiLayout.Space(poi);
            else
            {
                (w, h) = m.Size(text, st.Font(textLang), poi.Style.Weight, poi.Size);
                if (poi.Style.Look == "badge") w = h = Math.Max(w, h) + 2 * (0.2 * poi.Size);
            }
            var boxes = new List<LabelBox> { new(x, y, w, h, 0.0) };
            // the label beside a dot or icon
            if (poi.Style.ShowLabel && text.Length > 0)
            {
                var (nw, nh) = m.Size(text, st.Font(textLang), poi.Style.Weight, poi.LabelSize);
                boxes.Add(new LabelBox(PoiLayout.LabelLeft(poi) + nw / 2, y, nw, nh, 0.0));
            }
            occ.Take(boxes);
        }
        // 3. zone names
        int nZone = 0;
        var zg = input.Zones;
        var present = new bool[zg.Codes.Count];
        for (int i = 0; i < zg.Zone.Count; i++)
            if (zg.Inside.Data[i]) present[zg.Zone.Data[i]] = true;
        for (int code = 1; code < zg.Codes.Count; code++)
        {
            if (!present[code] || input.UnnamedZones.Contains(zg.Codes[code])) continue;
            var mask = new Grid<bool>(zg.Zone.Width, zg.Zone.Height);
            for (int i = 0; i < mask.Count; i++) mask.Data[i] = zg.Inside.Data[i] && zg.Zone.Data[i] == code;
            var lab = Components.Label(mask, 4);
            if (lab.Count == 0) continue;
            int k = 1;
            for (int j = 2; j <= lab.Count; j++)
                if (lab.Area[j] > lab.Area[k]) k = j;
            if (lab.Area[k] * Num.Pow2(zg.Step) < st.ZoneMinArea) continue;
            var (cx, cy) = Components.Centroids(lab)[k];
            double x = x0 + cx * zg.Step, y = y0 - cy * zg.Step;
            string name = input.Names.Zone(zg.Codes[code], lang) ?? zg.Codes[code];
            string text = lang == "en" ? name.ToUpperInvariant() : name;
            bool placed = false;
            foreach (double scale in (ReadOnlySpan<double>)[1.0, 0.8, 0.65])
            {
                double em = st.ZoneSize * scale, sp = st.ZoneSpacing * scale;
                var (w, h) = m.Size(text, font, st.ZoneWeight, em, sp);
                foreach (var (dx, dy) in ZoneOffsets)
                {
                    LabelBox[] box = [new(x + dx, y + dy, w, h, 0.0)];
                    if (!occ.Inside(box) || !occ.Free(box)) continue;
                    o.Add(new PlacedLabel("zone", text, x + dx, y + dy, 0.0, em, font, st.ZoneWeight, sp));
                    occ.Take(box);
                    placed = true;
                    nZone++;
                    break;
                }
                if (placed) break;
            }
            if (!placed) log?.Invoke($"zone label {text}: no free place");
        }
        // 4. street names along their lines
        int nStreet = PlaceStreets(input, occ, o);
        log?.Invoke($"labels ({lang}): postal {o.Count(l => l.Kind == "postal")}, zone {nZone}, street {nStreet}");
        return o;
    }

    /// <summary>The label a point shows in a language and the language of that label (English when the language's label is empty).</summary>
    public static (string Text, string Language) PoiLabel(ResolvedPoi poi, string lang) =>
        poi.Point.Label.TryGetValue(lang, out var n) && n.Length > 0 ? (n, lang) : (poi.Point.Label.GetValueOrDefault("en") ?? "", "en");

    /// <summary>The zone code at a point (the 4 m zone cell under it), or "" outside the zones.</summary>
    public static string ZoneAt(ZoneGrid z, double x, double y)
    {
        double c = Math.Floor((x - z.X0) / z.Step), r = Math.Floor((z.Y0 - y) / z.Step);
        if (c < 0 || r < 0 || c >= z.Zone.Width || r >= z.Zone.Height) return "";
        int ci = (int)c, ri = (int)r;
        return z.Inside[ri, ci] ? z.Codes[z.Zone[ri, ci]] : "";
    }

    static int PlaceStreets(LabelInput input, Occupancy occ, List<PlacedLabel> o)
    {
        var st = input.Style;
        string lang = input.Language, font = st.Font(lang);
        var expand = lang == "en" ? st.Expand : [];
        // chains per street hash (first seen first), then per displayed name
        var byHash = new List<(uint Hash, List<StreetChain> Chains)>();
        var hashIndex = new Dictionary<uint, int>();
        foreach (var r in input.Roads)
        {
            if (r.Street == 0 || !st.StreetClasses.ContainsKey(r.Class) || r.Points.Count < 2) continue;
            if (!hashIndex.TryGetValue(r.Street, out int hi))
            {
                hi = byHash.Count;
                hashIndex[r.Street] = hi;
                byHash.Add((r.Street, new List<StreetChain>()));
            }
            byHash[hi].Chains.Add(new StreetChain(r.Class, r.Street, r.Points.Select(p => new LinePoint(p.X, p.Y)).ToList(), r.Length, r.Divided, r.OneWay));
        }
        var byName = new List<(string Name, List<StreetChain> Chains)>();
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (hash, chains) in byHash)
        {
            var nm = input.Names.Street(hash, lang);
            if (string.IsNullOrEmpty(nm)) continue;
            if (!nameIndex.TryGetValue(nm, out int ni))
            {
                ni = byName.Count;
                nameIndex[nm] = ni;
                byName.Add((nm, new List<StreetChain>()));
            }
            byName[ni].Chains.AddRange(chains);
        }
        var routeOf = new Dictionary<string, Route>(StringComparer.Ordinal);
        if (st.RouteNumbers)
            foreach (var route in input.Routes)
                foreach (var s in route.Streets) routeOf[s] = route;
        double SumLength(List<StreetChain> cs)
        {
            double t = 0;
            foreach (var c in cs) t += c.Length;
            return t;
        }
        var order = byName.OrderBy(kv => -kv.Chains.Max(r => StreetParts.Rank(r.Class))).ThenBy(kv => -SumLength(kv.Chains)).ToList();
        // the main lines of every name; a highway name gives no label where another name's highway or major main line runs alongside,
        // unless it fits nowhere else
        var trunksByName = byName.Select(kv => (kv.Name, Trunks: StreetParts.Trunks(kv.Chains))).ToList();
        var otherTrunks = new Dictionary<string, (List<StreetChain> Chains, List<string> Names)>(StringComparer.Ordinal);
        foreach (var (nm2, chs2) in byName)
        {
            if (StreetParts.TopClass(chs2) != RoadClass.Highway) continue;
            var ts = new List<StreetChain>();
            var tn = new List<string>();
            foreach (var (nm3, tl) in trunksByName)
            {
                if (nm3 == nm2) continue;
                foreach (var c in tl)
                    if (c.Class is RoadClass.Highway or RoadClass.Major) { ts.Add(c); tn.Add(nm3); }
            }
            otherTrunks[nm2] = (ts, tn);
        }
        var trunkNames = trunksByName.Where(t => t.Trunks.Count > 0).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        int n = 0;
        foreach (var (nm, chains) in order)
        {
            string plain = Expand(nm, expand);
            var texts = routeOf.TryGetValue(nm, out var rt) ? new[] { Routes.Format(rt, plain), plain } : new[] { plain };
            var cls = StreetParts.TopClass(chains);
            var cs = st.StreetClasses[cls];
            var ot = otherTrunks.TryGetValue(nm, out var otv) ? otv : ([], []);
            var partsAll = StreetParts.Build(chains, ot.Chains, ot.Names);
            input.OnParts?.Invoke(nm, partsAll);
            int top = partsAll.Max(p => StreetParts.Rank(p.Class));
            List<List<StreetPart>> tiers = trunkNames.Contains(nm)
                ? [partsAll.Where(p => p.Shared is null && !p.Ramp && StreetParts.Rank(p.Class) == top).ToList(),
                   partsAll.Where(p => p.Shared is not null && !p.Ramp).ToList(),
                   partsAll.Where(p => p.Shared is null && !p.Ramp && StreetParts.Rank(p.Class) < top).ToList(),
                   partsAll.Where(p => p.Ramp).ToList()]
                : [partsAll.Where(p => p.Shared is null).ToList(), partsAll.Where(p => p.Shared is not null).ToList()];
            var placedHere = new List<(double X, double Y)>();
            foreach (var parts in tiers.Where(t => t.Count > 0))
            {
                foreach (var text in texts)
                {
                    foreach (double em in (ReadOnlySpan<double>)[cs.Size, cs.Size * FallbackScale])
                    {
                        var chars = ITextMetrics.Glyphs(text);
                        foreach (var part in parts)
                        {
                            var cum = LabelLines.Cum(part.Points);
                            double L = cum[^1];
                            var advs = input.Metrics.Advances(text, font, cs.Weight, em);
                            if (cs.LetterSpacing != 0)
                                for (int i = 0; i < advs.Length - 1; i++) advs[i] += em * cs.LetterSpacing;
                            double w = 0;
                            foreach (var a in advs) w += a;
                            if (L < w + 6.0) continue;
                            double gap = cs.SameNameGap;
                            int k = Math.Max(1, (int)Num.PyFloorDiv(L, Num.PyMax(cs.Repeat, gap)));
                            for (int i = 0; i < k; i++)
                            {
                                double ideal = (i + 0.5) * L / k;
                                double lo = w / 2 + 2.0, hi = L - w / 2 - 2.0;
                                foreach (double s in Num.NpArange(lo, hi + 1e-6, 5.0).OrderBy(s => Math.Abs(s - ideal)))
                                {
                                    var gl = LabelLines.GlyphsAlong(part.Points, cum, s, chars, advs, cs.MaxGlyphTurn);
                                    if (gl is null) continue;
                                    var mid = gl[gl.Count / 2];
                                    if (placedHere.Any(p => Num.Hypot(mid.X - p.X, mid.Y - p.Y) < gap)) continue;
                                    var boxes = gl.Select(g => new LabelBox(g.X, g.Y, g.Advance, em * 0.8, g.Rotation)).ToList();
                                    if (!occ.Inside(boxes) || !occ.Free(boxes)) continue;
                                    o.Add(new PlacedLabel("street", text, mid.X, mid.Y, mid.Rotation, em, font, cs.Weight, 0.0, gl, rt is not null && text != plain ? rt.Number : null));
                                    occ.Take(boxes);
                                    placedHere.Add((mid.X, mid.Y));
                                    n++;
                                    break;
                                }
                            }
                        }
                        if (placedHere.Count > 0) break;
                    }
                    if (placedHere.Count > 0) break;
                }
                if (placedHere.Count > 0) break;
            }
        }
        return n;
    }

    /// <summary>Short forms written out (whole words only, in the table's order).</summary>
    public static string Expand(string name, IReadOnlyList<KeyValuePair<string, string>> table)
    {
        foreach (var (k, v) in table) name = Regex.Replace(name, @"\b" + Regex.Escape(k) + @"\b", v.Replace("$", "$$", StringComparison.Ordinal));
        return name;
    }
}
