using FxMapGenerator.Core.Scan;

namespace FxMapGenerator.Core.Landcover;

/// <summary>The ground classes of the map, in the order of their value in the landcover grids.</summary>
public enum GroundClass : byte { None, Urban, Grass, Dirt, Sand, Beach, Rock, Vegetation, Snow, WaterMaterial, DefaultMaterial }

/// <summary>Ground classes and how the material classes (<c>data/material-classes.json</c>) map onto them.</summary>
public static class GroundClasses
{
    public const int Count = 11;

    /// <summary>Names in value order, as the landcover file lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["none", "urban", "grass", "dirt", "sand", "beach", "rock", "vegetation", "snow", "waterMaterial", "defaultMaterial"];

    /// <summary>
    /// The ground class of every material class (index = material class): no hit is none, wet sand is beach, the water
    /// material and the DEFAULT material (the material class default: what the game puts on terrain, rocks and props it
    /// gives no material of their own; it says nothing of what the ground is, so a style paints and blends it as it
    /// chooses) are classes of their own, every other hard or unknown surface is urban.
    /// </summary>
    public static byte[] OfMaterialClasses(Materials materials) => materials.Classes.Select(c => (byte)(c switch
    {
        "none" => GroundClass.None,
        "rock" => GroundClass.Rock,
        "grass" => GroundClass.Grass,
        "dirt" => GroundClass.Dirt,
        "sand" => GroundClass.Sand,
        "sand_wet" => GroundClass.Beach,
        "vegetation" => GroundClass.Vegetation,
        "snow" => GroundClass.Snow,
        "water" => GroundClass.WaterMaterial,
        "default" => GroundClass.DefaultMaterial,
        _ => GroundClass.Urban,
    })).ToArray();

    /// <summary>Material classes of natural ground (a height sample of the terrain, and on the terrain never a roof).</summary>
    static readonly string[] NaturalGround = ["rock", "grass", "dirt", "sand", "sand_wet", "vegetation", "snow"];

    /// <summary>Per material class: natural ground.</summary>
    public static bool[] NaturalGroundOf(Materials materials) => materials.Classes.Select(c => NaturalGround.Contains(c)).ToArray();

    /// <summary>Per material class: never taken as a roof material (natural ground, water, no hit).</summary>
    public static bool[] NotRoofOf(Materials materials) => materials.Classes.Select(c => NaturalGround.Contains(c) || c is "water" or "none").ToArray();
}
