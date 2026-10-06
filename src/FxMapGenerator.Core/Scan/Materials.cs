using System.Text.Json;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Scan;

/// <summary>
/// The bundled material tables: hash -> name (<c>data/materials.json</c>) and name -> class
/// (<c>data/material-classes.json</c>). A class index is the class's position in the table; hash 0 is <c>none</c>, a hash
/// without a name <c>unknown</c>, a name in no class <c>other</c>.
/// </summary>
public sealed class Materials
{
    public static Materials Default { get; } = Load();

    public IReadOnlyDictionary<uint, string> Names { get; }
    /// <summary>Class names in index order.</summary>
    public IReadOnlyList<string> Classes { get; }
    readonly Dictionary<string, int> _classOfName;
    readonly Dictionary<string, int> _classIndex;

    Materials(IReadOnlyDictionary<uint, string> names, IReadOnlyList<string> classes, Dictionary<string, int> classOfName)
    {
        Names = names;
        Classes = classes;
        _classOfName = classOfName;
        _classIndex = classes.Select((c, i) => (c, i)).ToDictionary(t => t.c, t => t.i, StringComparer.Ordinal);
        foreach (var required in new[] { "none", "unknown", "other" })
            if (!_classIndex.ContainsKey(required)) throw new InvalidDataException($"material classes: '{required}' missing");
    }

    static Materials Load()
    {
        using var m = JsonDocument.Parse(EmbeddedData.Open("materials.json"));
        var names = new Dictionary<uint, string>();
        foreach (var p in m.RootElement.GetProperty("hashToName").EnumerateObject())
            names[uint.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetString()!;
        using var c = JsonDocument.Parse(EmbeddedData.Open("material-classes.json"));
        var classes = new List<string>();
        var classOfName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cls in c.RootElement.GetProperty("classes").EnumerateObject())
        {
            foreach (var mat in cls.Value.GetProperty("materials").EnumerateArray())
                classOfName[mat.GetString()!] = classes.Count;        // a name listed twice: the later class
            classes.Add(cls.Name);
        }
        return new Materials(names, classes, classOfName);
    }

    public int IndexOf(string cls) => _classIndex.TryGetValue(cls, out var i) ? i : throw new KeyNotFoundException("material class " + cls);

    /// <summary>The class index of one material hash.</summary>
    public int ClassOf(uint hash) =>
        hash == 0 ? _classIndex["none"]
        : !Names.TryGetValue(hash, out var name) ? _classIndex["unknown"]
        : _classOfName.TryGetValue(name, out var c) ? c : _classIndex["other"];

    /// <summary>The class index of every cell of a material grid.</summary>
    public byte[] ClassGrid(uint[] material)
    {
        var cache = new Dictionary<uint, byte>();
        var g = new byte[material.Length];
        for (int i = 0; i < material.Length; i++)
        {
            var h = material[i];
            if (!cache.TryGetValue(h, out var c)) cache[h] = c = (byte)ClassOf(h);
            g[i] = c;
        }
        return g;
    }
}
