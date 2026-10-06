namespace FxMapGenerator.Core.Render;

/// <summary>
/// The layers a cell map's drawing is split into for editing in a paint program, bottom first: the ground (the
/// background and the ground picture or ground layers, without the hill shading), the hill shading in two layers (the
/// slopes it darkens, to be multiplied with the ground, and the slopes it lightens, to be colour-dodged; both clipped
/// to the ground, so they change nothing where the ground is not), then what the drawing puts over the ground in its
/// order, the roads, and the labels by kind.
/// </summary>
public enum MapLayer
{
    Ground, ShadeDark, ShadeLight, Canopy, Water, Buildings, Contours, Rail, Roads, Postal, Poi, Zones, Streets,
}

/// <summary>How a layer is put over what lies under it.</summary>
public enum LayerBlend { Normal, Multiply, ColorDodge }

public static class MapLayers
{
    public static IReadOnlyList<MapLayer> All { get; } = Enum.GetValues<MapLayer>();

    /// <summary>
    /// The last of the layers that are pictures in every format. The layers after it (the roads, the labels by kind) are
    /// shapes and text in a format that keeps those (<see cref="Export.SvgFile"/>).
    /// </summary>
    public const MapLayer LastPicture = MapLayer.Rail;

    /// <summary>The layer a step of the drawing belongs to; null for the shading, which has its own two layers.</summary>
    public static MapLayer? Of(StepTag tag) => tag.Kind switch
    {
        "background" or "ground" or "groundLayer" => MapLayer.Ground,
        "canopy" => MapLayer.Canopy,
        "water" or "sea" => MapLayer.Water,
        "building" => MapLayer.Buildings,
        "contour" => MapLayer.Contours,
        "rail" => MapLayer.Rail,
        "tunnel" or "track" or "casing" or "road" => MapLayer.Roads,
        "postal" => MapLayer.Postal,
        "poi" => MapLayer.Poi,
        "zone" => MapLayer.Zones,
        "street" => MapLayer.Streets,
        _ => null,
    };

    /// <summary>
    /// The layer is clipped to the ground layer right under it: it changes the ground's colours where the ground is,
    /// as much as the ground covers a pixel, and nothing else (the two shading layers).
    /// </summary>
    public static bool Clipped(MapLayer layer) => layer is MapLayer.ShadeDark or MapLayer.ShadeLight;

    public static LayerBlend Blend(MapLayer layer) => layer switch
    {
        MapLayer.ShadeDark => LayerBlend.Multiply,
        MapLayer.ShadeLight => LayerBlend.ColorDodge,
        _ => LayerBlend.Normal,
    };

    /// <summary>The layer's name in English.</summary>
    public static string Name(MapLayer layer) => layer switch
    {
        MapLayer.Ground => "Ground",
        MapLayer.ShadeDark => "Shading (dark side)",
        MapLayer.ShadeLight => "Shading (light side)",
        MapLayer.Canopy => "Tree canopy",
        MapLayer.Water => "Water",
        MapLayer.Buildings => "Buildings",
        MapLayer.Contours => "Contours",
        MapLayer.Rail => "Railway",
        MapLayer.Roads => "Roads",
        MapLayer.Postal => "Postal codes",
        MapLayer.Poi => "Points of interest",
        MapLayer.Zones => "Zone names",
        _ => "Street names",
    };

    /// <summary>The layer's name in a language of the screens (<c>ja</c>; any other: English).</summary>
    public static string Name(MapLayer layer, string language) => language != "ja" ? Name(layer) : layer switch
    {
        MapLayer.Ground => "地面",
        MapLayer.ShadeDark => "陰影（暗い側）",
        MapLayer.ShadeLight => "陰影（明るい側）",
        MapLayer.Canopy => "木の茂み",
        MapLayer.Water => "水",
        MapLayer.Buildings => "建物",
        MapLayer.Contours => "等高線",
        MapLayer.Rail => "線路",
        MapLayer.Roads => "道路",
        MapLayer.Postal => "番地",
        MapLayer.Poi => "POI",
        MapLayer.Zones => "地区名",
        _ => "通り名",
    };
}
