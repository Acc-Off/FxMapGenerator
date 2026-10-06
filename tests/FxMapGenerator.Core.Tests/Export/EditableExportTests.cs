using System.Xml.Linq;
using FxMapGenerator.Core.Cells;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Grids;
using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Jobs;
using FxMapGenerator.Core.Planning;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Render;
using FxMapGenerator.Core.Roads;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.Core.Styles;
using FxMapGenerator.Core.Tests.Jobs;
using FxMapGenerator.Core.Tests.Render;
using FxMapGenerator.Core.Vectors;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>The layered files of an export: a map's drawing split into layers, as a PSD file of the whole frame.</summary>
public sealed class EditableExportTests
{
    static double[] Square(double x0, double y0, double x1, double y1) => [x0, y0, x1, y0, x1, y1, x0, y1];

    static byte[] Picture(int side, Func<int, int, (byte R, byte G, byte B, byte A)> at)
    {
        var rgba = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                (rgba[(y * side + x) * 4], rgba[(y * side + x) * 4 + 1], rgba[(y * side + x) * 4 + 2], rgba[(y * side + x) * 4 + 3]) = at(x, y);
        return rgba;
    }

    /// <summary>The rows of a plane unpacked: the plane's bytes, row after row.</summary>
    static byte[] Unpacked(IPsdRows rows, int plane, int width)
    {
        var lengths = rows.Lengths(plane);
        using var ms = new MemoryStream();
        rows.Write(plane, ms);
        var packed = ms.ToArray();
        Assert.Equal(lengths.Sum(), packed.Length);
        var o = new byte[width * lengths.Length];
        for (int y = 0, at = 0; y < lengths.Length; at += lengths[y], y++) PackBits.Unpack(packed.AsSpan(at, lengths[y]), o.AsSpan(y * width, width));
        return o;
    }

    [Fact]
    public void BlocksPackedByTheirWorkersAreJoinedRowByRow()
    {
        using var tmp = new TempFolder();
        const int s = 64;
        var store = new PackedBlocks(tmp.File("work"), s);
        var a = Picture(s, (x, y) => ((byte)x, (byte)y, 7, 255));
        var b = Picture(s, (x, y) => (9, (byte)(x + y), (byte)(x / 8), (byte)(y < 32 ? 255 : 100)));
        using (var one = store.Write("cell_0_0"))
        {
            one.Add(3, new BlockId(-1, 5), a);
            one.Add(PackedBlocks.Merged, new BlockId(0, 0), b);
        }
        using (var two = store.Write("cell_0_1")) two.Add(3, new BlockId(1, 5), b);
        Assert.Throws<ArgumentException>(() => { using var w = store.Write("x"); w.Add(0, new BlockId(0, 0), new byte[12]); });

        var blocks = store.Read(3);
        Assert.Equal(new[] { new BlockId(-1, 5), new BlockId(1, 5) }, blocks.Keys.Order());
        Assert.Single(store.Read(PackedBlocks.Merged));
        Assert.Empty(store.Read(9));
        // blocks -1..2 of row 5 and row 6: the first and the third are there, the second is painted, the rest is clear
        var rows = new PackedBlocks.Rows(store, blocks, -1, 5, 4, 2, id => id == new BlockId(0, 5) ? ((byte)1, (byte)2, (byte)3, (byte)4) : null);
        for (int p = 0; p < 4; p++)
        {
            var plane = Unpacked(rows, p, 4 * s);
            Assert.Equal(4 * s * 2 * s, plane.Length);
            for (int y = 0; y < 2 * s; y++)
                for (int x = 0; x < 4 * s; x++)
                {
                    byte want = y >= s ? (byte)0 : (x / s) switch
                    {
                        0 => a[(y * s + x) * 4 + p],
                        1 => (byte)(p + 1),
                        2 => b[(y * s + x - 2 * s) * 4 + p],
                        _ => (byte)0,
                    };
                    Assert.Equal(want, plane[y * 4 * s + x]);
                }
        }
    }

    [Fact]
    public void ABlockIsShrunkAsItsTilesAre()
    {
        // a block's picture at zoom 8 cut into its 16 tiles, and the tiles above them made as the lower zooms are
        using var tmp = new TempFolder();
        var tiles = new TileStore(tmp.Path);
        var block = Picture(1024, (x, y) => ((byte)(x / 4), (byte)(y / 4), (byte)((x * y) % 251), (byte)(x > 600 && y > 300 ? 120 : 255)));
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++) tiles.Write(8, i, j, TileStore.Crop(block, 1024, i * 256, j * 256, 256, 256));
        foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) }) Assert.True(tiles.BuildParent(7, x, y));
        Assert.True(tiles.BuildParent(6, 0, 0));
        var z7 = EditableLayersStage.Shrink(block, 7)!;
        Assert.Equal(512 * 512 * 4, z7.Length);
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++) Assert.Equal(tiles.Read(7, i, j), TileStore.Crop(z7, 512, i * 256, j * 256, 256, 256));
        Assert.Equal(tiles.Read(6, 0, 0), EditableLayersStage.Shrink(block, 6));
        Assert.Null(EditableLayersStage.Shrink(new byte[1024 * 1024 * 4], 6));       // nothing in it
    }

    /// <summary>A road map project of two blocks with their cell's data (ground, a pond, a building), drawn, its lower zooms marked made.</summary>
    sealed class Setup : IDisposable
    {
        readonly TempFolder _tmp = new();
        public Project Project { get; }
        public WorkFolder Folder { get; }
        public StateStore State { get; }
        public TileStore Tiles { get; }
        public MapStyle Style { get; } = MapStyle.Builtin("roadmap");
        public string Out => _tmp.File("out");

        /// <param name="road">Also a road across the two blocks.</param>
        public Setup(bool road = false)
        {
            Project = Project.Create(_tmp.File("t.fxmapgen.json"));
            Project.File.Maps = new MapsSetting { Satellite = false, Roadmap = true };
            Project.File.Range = new RangeSetting { Base = "none", Add = ["z8_0_0", "z8_4_0"] };
            Project.Save();
            Folder = new WorkFolder(Project.WorkFolderPath);
            var cell = CellPlan.For(Project.Range.Keys).Single();
            var dir = CellFiles.Folder(Folder, cell.Id);
            Directory.CreateDirectory(dir);
            double x0 = WorldGrid.Left, y0 = WorldGrid.Top, b = WorldGrid.BlockSize;
            CellFiles.WriteLayers(Path.Combine(dir, CellFiles.Layers), new CellArea(cell),
            [
                new("ground", "grass", CellLayers.Sets.Ground(Style), 100000, [new Ring(100000, Square(x0, y0, x0 + 2 * b, y0 - b))], null),
                new("water", "water", null, 3600, [new Ring(3600, Square(x0 + 60.4, y0 - 60.4, x0 + 120.2, y0 - 120.2))], null),
                new("building", "grey", CellLayers.Sets.Buildings(Style), 900, [new Ring(900, Square(x0 + b + 100.3, y0 - 100.3, x0 + b + 130.7, y0 - 130.7))], null),
            ]);
            Directory.CreateDirectory(Folder.Data);
            var shapes = new GridFile();                        // no roads, or one ribbon
            shapes.Meta["trackWidth"] = 3.0;
            shapes.Add("tracks", new Grid<double>(4, 0));
            foreach (var name in new[] { "ground.class", "raised.level", "raised.class", "cornerPatches.class", "junctionCorners.class" })
                shapes.Add(name, road && name == "ground.class" ? new Grid<byte>(1, 1, [0]) : new Grid<byte>(1, 0));
            foreach (var name in new[] { "ground.casing", "ground.fill", "raised.casing", "raised.fill", "cornerPatches.ring", "junctionCorners.region", "junctionCorners.arc", "junctionCorners.seam", "tunnels.ring" })
            {
                double[] ring = !road ? [] : name == "ground.casing" ? Square(x0 + 20, y0 - 200, x0 + 500, y0 - 212) : name == "ground.fill" ? Square(x0 + 20, y0 - 201, x0 + 500, y0 - 211) : [];
                shapes.Add(name + "Start", ring.Length > 0 ? new Grid<int>(1, 2, [0, ring.Length / 2]) : new Grid<int>(1, 1, [0]));
                shapes.Add(name, ring.Length > 0 ? new Grid<double>(2, ring.Length / 2, ring) : new Grid<double>(2, 0));
            }
            shapes.Add("tunnels.group", new Grid<int>(1, 0));
            shapes.Save(Path.Combine(Folder.Data, RoadShapesFile.FileName));
            State = StateStore.Open(Folder);
            Tiles = new TileStore(Folder.Tiles("roadmap"));
        }

        public async Task<JobSnapshot> Run(IReadOnlyList<Stage> stages) => await JobRunner.Create(new JobSetup
        {
            ProjectPath = Project.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(), State = State,
            Stages = (_, _) => new BuildPlan(stages, []),
        }).RunAsync();

        /// <summary>The map's cell drawn into its tiles (zoom 8 to 5), and the lower zooms counted as made.</summary>
        public async Task Draw()
        {
            Assert.Equal(JobState.Done, (await Run([new CellDrawStage(MapSet.Roadmap)])).State);
            State.SetStageDone(StageKeys.LowZoom, "roadmap", DateTime.UtcNow);
        }

        public ExportOptions Options(int zoom = 6, string language = "en", string map = "roadmap") =>
            new(Out, [], false, null, false, Editable: new EditableChoice([map], zoom, null, language));

        public void Dispose() => _tmp.Dispose();
    }

    [Fact]
    public async Task TheInventorySaysWhichMapsCanBeWrittenInLayers()
    {
        using var s = new Setup();
        var before = ExportInventory.Of(s.Project, s.State);
        Assert.Equal(new EditableMapInfo("roadmap", false, false), Assert.Single(before.Editable.Maps));     // no tiles yet
        Assert.Equal((WorldGrid.BlocksX, WorldGrid.BlocksY), (before.Editable.BlocksX, before.Editable.BlocksY));
        Assert.Equal(new[] { "EDITABLE_NOT_READY" }, before.Check(s.Options()).Select(p => p.Code));
        await s.Draw();
        var inv = ExportInventory.Of(s.Project, s.State);
        Assert.True(Assert.Single(inv.Editable.Maps).Ready);
        Assert.Empty(inv.Check(s.Options()));
        Assert.Empty(inv.Check(s.Options(zoom: 7)));
        Assert.Equal(new[] { "BAD_EDITABLE" }, inv.Check(s.Options(zoom: 8)).Select(p => p.Code));
        Assert.Equal(new[] { "BAD_EDITABLE" }, inv.Check(s.Options() with { Editable = new EditableChoice(["roadmap"], 6, ["tiff"]) }).Select(p => p.Code));
        Assert.Equal(new[] { "EDITABLE_NOT_READY" }, inv.Check(s.Options(map: "satellite")).Select(p => p.Code));       // not a map of the project
        // nothing chosen at all: no web tiles, no minimap, no map in layers
        Assert.Equal(new[] { "NOTHING" }, inv.Check(s.Options() with { Editable = new EditableChoice([]) }).Select(p => p.Code));
        Assert.Equal(new[] { "NOTHING" }, inv.Check(s.Options() with { Editable = null }).Select(p => p.Code));
        // a cell without its data: not ready
        File.Delete(Path.Combine(CellFiles.Folder(s.Folder, new CellId(0, 0)), CellFiles.Layers));
        Assert.False(ExportInventory.Of(s.Project, s.State).Editable.Maps[0].Ready);
        // the steps: the layers only with an atlas or road map among the maps
        Assert.Equal(new[] { "export", "export.layers", "export.files" }, ExportStages.For(s.Options(), "1").Select(x => x.Id));
        Assert.Equal(new[] { "export", "export.files" }, ExportStages.For(s.Options(map: "satellite"), "1").Select(x => x.Id));
        Assert.Equal(new[] { "export" }, ExportStages.For(s.Options() with { Editable = null }, "1").Select(x => x.Id));
    }

    static byte[]?[] ByLayer(PsdReader read) =>
        MapLayers.All.Select(l => read.Layers.FirstOrDefault(x => x.AsciiName == MapLayers.Name(l))?.Rgba).ToArray();

    static int Apart(byte[] a, byte[] b)
    {
        int worst = 0;
        for (int i = 0; i < a.Length; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    [Fact]
    public async Task AMapIsWrittenAsALayeredFileOfTheWholeFrame()
    {
        using var s = new Setup();
        await s.Draw();
        var end = await s.Run(ExportStages.For(s.Options(), "9.9.9"));
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(new[] { (0, 0), (1, 1), (1, 1) }, end.Stages.Select(x => (x.Done, x.Total)));       // nothing else chosen, one cell, one file
        string dir = Path.Combine(s.Out, "editable"), path = Path.Combine(dir, "roadmap-z6.psd");
        Assert.Equal(new[] { "roadmap-z6.psd" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));     // the work folder is gone

        // the two blocks of the range, at the frame's north-west corner
        var read = PsdReader.Read(path, (0, 0, 512, 256));
        Assert.Equal((false, 256 * WorldGrid.BlocksX, 256 * WorldGrid.BlocksY), (read.Big, read.Width, read.Height));
        Assert.Equal(new[] { "Ground", "Water", "Buildings" }, read.Layers.Select(l => l.Name));
        Assert.All(read.Layers, l => Assert.Equal(("norm", 255, 8), (l.Blend, l.Opacity, l.Flags)));
        // a layer takes the rectangle of the blocks it has something in; the water also the open sea outside the range
        Assert.Equal(new[] { (0, 0, 512, 256), (0, 0, read.Width, read.Height), (256, 0, 256, 256) }, read.Layers.Select(l => (l.Left, l.Top, l.Width, l.Height)));
        // the merged picture is the map's tiles
        var tiles = new byte[512 * 256 * 4];
        for (int i = 0; i < 2; i++)
        {
            var tile = s.Tiles.Read(6, i, 0)!;
            for (int y = 0; y < 256; y++) Buffer.BlockCopy(tile, y * 256 * 4, tiles, (y * 512 + i * 256) * 4, 256 * 4);
        }
        Assert.Equal(tiles, read.Merged);
        Assert.Equal((4, true), (read.Channels, read.MergedTransparency));       // the tiles outside the range are not made here: clear
        // the layers put together are the map, to the slack they are made small with (LayeredBlock)
        int slack = (int)LayeredBlock.Slack.Default.Beside + 2;
        Assert.InRange(Apart(LayerTests.Compose(ByLayer(read), 512, 256), tiles), 0, slack);
        var (grass, water, grey) = (s.Style.Paint.Ground["grass"], s.Style.Paint.Water, s.Style.Paint.Buildings["grey"]);
        int pond = (80 * 512 + 80) * 4, house = (105 * 512 + 256 + 105) * 4;
        Assert.Equal((grass.R, grass.G, 255), (read.Layers[0].Rgba[pond], read.Layers[0].Rgba[pond + 1], read.Layers[0].Rgba[pond + 3]));       // the ground whole under the pond
        Assert.Equal((water.R, 255), (read.Layers[1].Rgba[pond], read.Layers[1].Rgba[pond + 3]));
        Assert.Equal((grey.R, 255), (read.Layers[2].Rgba[house], read.Layers[2].Rgba[house + 3]));
        Assert.Equal(0, read.Layers[1].Rgba[house + 3]);

        // outside the range: the style's open sea in the water layer, nothing in the others
        var far = PsdReader.Read(path, (4096, 6144, 8, 8));
        var sea = s.Style.OpenSea;
        Assert.Equal((sea.R, sea.G, sea.B, 255), (far.Layers[1].Rgba[0], far.Layers[1].Rgba[1], far.Layers[1].Rgba[2], far.Layers[1].Rgba[3]));
        Assert.All(far.Layers[0].Rgba.Concat(far.Layers[2].Rgba), v => Assert.Equal(0, v));

        // the folder's record has the file
        var record = ExportRecord.Read(s.Out)!;
        var entry = Assert.Single(record.Editable!);
        Assert.Equal(("roadmap", 6, "psd", "roadmap-z6.psd", new FileInfo(path).Length, read.Width, read.Height),
            (entry.Map, entry.Zoom, entry.Format, entry.File, entry.Bytes, entry.Width, entry.Height));
        Assert.Equal(new[] { "Ground", "Water", "Buildings" }, entry.Layers);
        Assert.Null(record.Web);

        // again in Japanese at zoom 7: another file beside the first, the layers named in Japanese
        Assert.Equal(JobState.Done, (await s.Run(ExportStages.For(s.Options(zoom: 7, language: "ja"), "9.9.9"))).State);
        var fine = PsdReader.Read(Path.Combine(dir, "roadmap-z7.psd"), (0, 0, 1024, 512));
        Assert.Equal((512 * WorldGrid.BlocksX, 512 * WorldGrid.BlocksY), (fine.Width, fine.Height));
        Assert.Equal(new[] { "地面", "水", "建物" }, fine.Layers.Select(l => l.Name));
        Assert.Equal(new[] { "Ground", "Water", "Buildings" }, fine.Layers.Select(l => l.AsciiName));
        var tiles7 = new byte[1024 * 512 * 4];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 4; i++)
            {
                var tile = s.Tiles.Read(7, i, j)!;
                for (int y = 0; y < 256; y++) Buffer.BlockCopy(tile, y * 256 * 4, tiles7, ((j * 256 + y) * 1024 + i * 256) * 4, 256 * 4);
            }
        Assert.Equal(tiles7, fine.Merged);
        Assert.InRange(Apart(LayerTests.Compose(ByLayer(fine), 1024, 512), tiles7), 0, slack);
        Assert.Equal(new[] { "roadmap-z6.psd", "roadmap-z7.psd" }, ExportRecord.Read(s.Out)!.Editable!.Select(e => e.File));
        // a file taken away drops out of the record with the next export
        File.Delete(path);
        Assert.Equal(JobState.Done, (await s.Run(ExportStages.For(s.Options(zoom: 7), "9.9.9"))).State);
        Assert.Equal(new[] { "roadmap-z7.psd" }, ExportRecord.Read(s.Out)!.Editable!.Select(e => e.File));
        Assert.Equal(new[] { "Ground", "Water", "Buildings" }, ExportRecord.Read(s.Out)!.Editable![0].Layers);
    }

    [Fact]
    public async Task ASatelliteMapIsOneLayer()
    {
        using var tmp = new TempFolder();
        var project = Project.Create(tmp.File("p.fxmapgen.json"));
        project.File.Range.Base = "none";
        project.Save();
        var folder = new WorkFolder(tmp.Path);
        var tiles = new TileStore(folder.Tiles("satellite"));
        var photo = Picture(256, (x, y) => ((byte)x, (byte)y, 40, 255));
        tiles.Write(6, 0, 0, photo);
        tiles.Write(6, 5, 9, photo);
        var state = StateStore.Open(folder);
        state.SetStageDone(StageKeys.LowZoom, "satellite", DateTime.UtcNow);
        var options = new ExportOptions(tmp.File("out"), [], false, null, false, Editable: new EditableChoice(["satellite"], 6, null, "ja"));
        Assert.Empty(ExportInventory.Of(project, state).Check(options));
        var end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan(ExportStages.For(options, "9.9.9"), []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        var read = PsdReader.Read(Path.Combine(tmp.File("out"), "editable", "satellite-z6.psd"), (5 * 256, 9 * 256, 256, 256));
        var layer = Assert.Single(read.Layers);
        Assert.Equal(("衛星地図", "Satellite map", 0, 0, read.Width, read.Height), (layer.Name, layer.AsciiName, layer.Left, layer.Top, layer.Width, layer.Height));
        Assert.Equal(photo, layer.Rgba);
        Assert.Equal(photo, read.Merged);
        Assert.False(Directory.Exists(Path.Combine(tmp.File("out"), "editable", ".work")));

        // as an SVG file: the photo as one PNG picture the file links
        var svg = options with { Editable = new EditableChoice(["satellite"], 6, ["svg"], "ja") };
        end = await JobRunner.Create(new JobSetup
        {
            ProjectPath = project.FilePath, Workers = 2, Processors = 2, Memory = new FakeMemory(), State = state,
            Stages = (_, _) => new BuildPlan(ExportStages.For(svg, "9.9.9"), []),
        }).RunAsync();
        Assert.Equal(JobState.Done, end.State);
        string dir = Path.Combine(tmp.File("out"), "editable"), png = Path.Combine(dir, "satellite-z6-svg", "satellite.png");
        Assert.Equal(new[] { "satellite-z6-svg", "satellite-z6.psd", "satellite-z6.svg" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal((read.Width, read.Height), PngRows.Size(png));
        Assert.Equal(photo, PngRegion(png, 5 * 256, 9 * 256, 256, 256));
        Assert.Equal(photo, PngRegion(png, 0, 0, 256, 256));
        Assert.All(PngRegion(png, 256, 0, 256, 256), v => Assert.Equal(0, v));               // no tile there: clear
        var root = XDocument.Load(Path.Combine(dir, "satellite-z6.svg")).Root!;
        var only = Assert.Single(root.Elements());
        Assert.Equal(("Satellite_map", "衛星地図", "layer"), (only.Attribute("id")!.Value, only.Attribute(Inkscape + "label")!.Value, only.Attribute(Inkscape + "groupmode")!.Value));
        var image = Assert.Single(only.Elements());
        Assert.Equal(("satellite-z6-svg/satellite.png", "0", "0", read.Width.ToString(), read.Height.ToString()),
            (image.Attribute(Xlink + "href")!.Value, image.Attribute("x")!.Value, image.Attribute("y")!.Value, image.Attribute("width")!.Value, image.Attribute("height")!.Value));
        var entry = ExportRecord.Read(tmp.File("out"))!.Editable!.Single(e => e.Format == "svg");
        Assert.Equal(("satellite", 6, "satellite-z6.svg", new FileInfo(Path.Combine(dir, "satellite-z6.svg")).Length + new FileInfo(png).Length, read.Width, read.Height),
            (entry.Map, entry.Zoom, entry.File, entry.Bytes, entry.Width, entry.Height));
        Assert.Equal(new[] { "衛星地図" }, entry.Layers);
    }

    static readonly XNamespace Svg = "http://www.w3.org/2000/svg", Xlink = "http://www.w3.org/1999/xlink", Inkscape = "http://www.inkscape.org/namespaces/inkscape";

    /// <summary>A rectangle of a PNG file's pixels (straight RGBA), its rows read one by one.</summary>
    static byte[] PngRegion(string path, int x0, int y0, int w, int h)
    {
        using var png = PngRows.Open(path);
        var row = new byte[png.Width * 4];
        var o = new byte[w * h * 4];
        for (int y = 0; y < y0 + h; y++)
        {
            Assert.Equal(1, png.Read(row));
            if (y >= y0) Buffer.BlockCopy(row, x0 * 4, o, (y - y0) * w * 4, w * 4);
        }
        return o;
    }

    [Fact]
    public async Task AMapIsWrittenAsAnSvgFileWithItsPictures()
    {
        using var s = new Setup(road: true);
        await s.Draw();
        var inventory = ExportInventory.Of(s.Project, s.State);
        var both = s.Options() with { Editable = new EditableChoice(["roadmap"], 6, ["psd", "svg"], "ja") };
        Assert.Empty(inventory.Check(both));
        // maps chosen and no format: nothing can be written; no maps: the formats are not looked at
        Assert.Equal(new[] { "NO_EDITABLE_FORMAT" }, inventory.Check(s.Options() with { Editable = new EditableChoice(["roadmap"], 6, []) }).Select(p => p.Code));
        Assert.Equal(new[] { "NOTHING" }, inventory.Check(s.Options() with { Editable = new EditableChoice([], 6, []) }).Select(p => p.Code));
        Assert.Equal(new[] { "psd" }, new EditableChoice(["roadmap"]).Written);
        Assert.Equal(new[] { "svg", "psd" }, new EditableChoice(["roadmap"], 6, ["svg", "psd", "svg"]).Written);

        var end = await s.Run(ExportStages.For(both, "9.9.9"));
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(new[] { (0, 0), (1, 1), (2, 2) }, end.Stages.Select(x => (x.Done, x.Total)));       // one cell; a file a format
        string dir = Path.Combine(s.Out, "editable"), pictures = Path.Combine(dir, "roadmap-z6-svg");
        Assert.Equal(new[] { "roadmap-z6-svg", "roadmap-z6.psd", "roadmap-z6.svg" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "buildings.png", "ground.png", "water.png" }, Directory.GetFiles(pictures).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // the layers up to the railway: pictures the file links, where the PSD file's layers lie and with their pixels
        var read = PsdReader.Read(Path.Combine(dir, "roadmap-z6.psd"), (0, 0, 512, 256));
        Assert.Equal(new[] { "地面", "水", "建物", "道路" }, read.Layers.Select(l => l.Name));
        var root = XDocument.Load(Path.Combine(dir, "roadmap-z6.svg")).Root!;
        Assert.Equal((read.Width.ToString(), read.Height.ToString()), (root.Attribute("width")!.Value, root.Attribute("height")!.Value));
        var layers = root.Elements(Svg + "g").ToList();
        Assert.Equal(new[] { "Ground", "Water", "Buildings", "Roads" }, layers.Select(g => g.Attribute("id")!.Value));
        Assert.Equal(new[] { "地面", "水", "建物", "道路" }, layers.Select(g => g.Attribute(Inkscape + "label")!.Value));
        string[] files = ["ground.png", "water.png", "buildings.png"];
        for (int k = 0; k < 3; k++)
        {
            var image = Assert.Single(layers[k].Elements());
            var layer = read.Layers[k];
            Assert.Equal(("roadmap-z6-svg/" + files[k], layer.Left, layer.Top, layer.Width, layer.Height), (image.Attribute(Xlink + "href")!.Value,
                int.Parse(image.Attribute("x")!.Value), int.Parse(image.Attribute("y")!.Value), int.Parse(image.Attribute("width")!.Value), int.Parse(image.Attribute("height")!.Value)));
            Assert.Equal((layer.Width, layer.Height), PngRows.Size(Path.Combine(pictures, files[k])));
            // the PSD reader gives the two blocks with the layer's pixels in them; the picture starts at the layer's corner
            int from = Math.Max(layer.Left, 0), wide = Math.Min(layer.Left + layer.Width, 512) - from;
            Assert.Equal(TileStore.Crop(layer.Rgba, 512, from, 0, wide, 256), PngRegion(Path.Combine(pictures, files[k]), from - layer.Left, 0, wide, 256));
        }
        // the road: shapes of the map's data, not a picture
        var road = layers[3];
        Assert.Empty(road.Descendants(Svg + "image"));
        Assert.Equal(new[] { "一般道" }, road.Elements(Svg + "g").Select(g => g.Attribute(Inkscape + "label")!.Value).Where(n => n == "一般道"));
        var ribbon = road.Elements(Svg + "g").Single(g => g.Attribute("id")!.Value == "Roads_Roads");
        Assert.Equal(s.Style.Paint.Road.Fill.ToString(), ribbon.Attribute("fill")!.Value);
        var d = Assert.Single(ribbon.Elements(Svg + "path")).Attribute("d")!.Value;
        double ppm = 256 / WorldGrid.BlockSize;
        Assert.StartsWith(FormattableString.Invariant($"M{Math.Round(20 * ppm, 2)},{Math.Round(201 * ppm, 2)}l"), d);
        Assert.EndsWith("z", d);

        // the folder's record has both files; the SVG file's bytes are its own and its pictures'
        var record = ExportRecord.Read(s.Out)!;
        Assert.Equal(new[] { ("psd", "roadmap-z6.psd"), ("svg", "roadmap-z6.svg") }, record.Editable!.Select(e => (e.Format, e.File)).Order());
        var entry = record.Editable!.Single(e => e.Format == "svg");
        Assert.Equal(("roadmap", 6, new FileInfo(Path.Combine(dir, "roadmap-z6.svg")).Length + Directory.GetFiles(pictures).Sum(f => new FileInfo(f).Length), read.Width, read.Height),
            (entry.Map, entry.Zoom, entry.Bytes, entry.Width, entry.Height));
        Assert.Equal(new[] { "地面", "水", "建物", "道路" }, entry.Layers);

        // the SVG file alone, in English at zoom 7: the same steps, and what the folder of its pictures held before is gone
        Directory.CreateDirectory(Path.Combine(dir, "roadmap-z7-svg"));
        File.WriteAllText(Path.Combine(dir, "roadmap-z7-svg", "left.png"), "from an earlier export");
        var alone = s.Options() with { Editable = new EditableChoice(["roadmap"], 7, ["svg"]) };
        Assert.Equal(new[] { "export", "export.layers", "export.files" }, ExportStages.For(alone, "1").Select(x => x.Id));
        end = await s.Run(ExportStages.For(alone, "9.9.9"));
        Assert.Equal(JobState.Done, end.State);
        Assert.Equal(new[] { (0, 0), (1, 1), (1, 1) }, end.Stages.Select(x => (x.Done, x.Total)));
        Assert.Equal(new[] { "roadmap-z6-svg", "roadmap-z6.psd", "roadmap-z6.svg", "roadmap-z7-svg", "roadmap-z7.svg" },
            Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "buildings.png", "ground.png", "water.png" }, Directory.GetFiles(Path.Combine(dir, "roadmap-z7-svg")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var fine = XDocument.Load(Path.Combine(dir, "roadmap-z7.svg")).Root!;
        Assert.Equal(new[] { "Ground", "Water", "Buildings", "Roads" }, fine.Elements(Svg + "g").Select(g => g.Attribute(Inkscape + "label")!.Value));
        Assert.Equal((2 * read.Width).ToString(), fine.Attribute("width")!.Value);
        // its ground picture is the zoom 7 tiles' ground: the two blocks, 512 pixels each
        Assert.Equal((1024, 512), PngRows.Size(Path.Combine(dir, "roadmap-z7-svg", "ground.png")));
        Assert.Equal(3, ExportRecord.Read(s.Out)!.Editable!.Count);
    }
}
