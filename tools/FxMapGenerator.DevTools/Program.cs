using FxMapGenerator.Core.Imaging;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.Satellite;
using FxMapGenerator.Core.State;
using FxMapGenerator.GameData.Textures;
using RageLib.ResourceWrappers;

// devtools <command> [args]
//   ytd-info <file.ytd>...                 textures of each dictionary: name, size, format, levels, data size
//   ytd-decode <file.ytd> <out-folder>     every texture as <out-folder>/<name>.png
//   gfx-empty <in.gfx> <out.gfx> [ids]     a GFX file whose shapes of these ids (default 1,3,5,7: the Cayo Perico island map's)
//                                          draw nothing, written uncompressed (GfxFile.EmptyShapes)
//   island-map <out.gfx> [gta] [keys]     the island map the export makes from this PC's game (IslandMap.Make; the app's
//                                          way of finding GTA V and the keys unless given)
//   interior-maps <out.lua> [gta] [keys]  the list of interior maps the export writes into the minimap resource from this
//                                          PC's game (InteriorMaps.Read; the app's way of finding GTA V and the keys unless
//                                          given)
//   calibrate <project> [workers] [qx,qy]  measures the scale correction from the captured blocks (both passes), stores nothing;
//                                          with qx,qy: one pass at that correction (what is left over)
//   make-icon <favicon.svg> <out.ico>      the executable's icon from the web page's favicon (src/FxMapGenerator.App/app.ico)
//   mdi-icons <@mdi/svg folder> <out.json>  the icons the points of interest can use (data/mdi-icons.json) from the npm package
//                                          @mdi/svg (npm pack @mdi/svg@<version>, unpacked; Docs/development.md)
//   sample-land <folder> <a|b|c> [--no-roads]  the style editor's sample land made anew, as a project in an empty folder
//   sample-land-bundle <data/sample-island> <a|b|c>  the same written for the app to bundle (surface.grid, island.json)
//   sample-island <folder>                  the bundled sample land as a project in an empty folder
//   preview-timings <project> <x> <y> <out-folder> [workers]  the style editor's preview of the windows around (x, y):
//                                          how long each kind of change and each window size takes to draw (and the
//                                          first drawing's tiles)

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: devtools ytd-info <file.ytd>... | ytd-decode <file.ytd> <out-folder> | calibrate <project> [workers] [qx,qy] | make-icon <favicon.svg> <out.ico> | mdi-icons <@mdi/svg folder> <out.json> | sample-land <folder> <a|b|c> | sample-land-bundle <data folder> <a|b|c> | sample-island <folder>");
    return args.Length == 0 ? 64 : 0;
}

switch (args[0])
{
    case "mdi-icons" when args.Length == 3:
    {
        int n = FxMapGenerator.DevTools.MdiIconsMaker.Write(args[1], args[2]);
        Console.WriteLine($"{args[2]}: {n:N0} icons, {new FileInfo(args[2]).Length:N0} bytes");
        return 0;
    }

    case "make-icon" when args.Length == 3:
        FxMapGenerator.DevTools.IconMaker.Write(args[1], args[2]);
        Console.WriteLine($"{args[2]}: {string.Join(", ", FxMapGenerator.DevTools.IconMaker.Sizes)} px, {new FileInfo(args[2]).Length:N0} bytes");
        return 0;

    case "sample-land" when args.Length is 3 or 4:
    {
        // the style editor's sample land: devtools sample-land <empty folder> <a|b|c> [--no-roads]
        if (Directory.Exists(args[1]) && Directory.EnumerateFileSystemEntries(args[1]).Any())
        {
            Console.Error.WriteLine($"{args[1]} is not empty");
            return 1;
        }
        var variant = FxMapGenerator.DevTools.Island.SampleLand.Variants.First(v => v.Name == args[2]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var land = FxMapGenerator.DevTools.Island.SampleLand.Terrain(variant, Console.WriteLine);
        var roads = args.Length == 4 && args[3] == "--no-roads" ? null : FxMapGenerator.DevTools.Island.Roads.Build(land, Console.WriteLine);
        var town = roads is null ? null : FxMapGenerator.DevTools.Island.Town.Build(land, roads, Console.WriteLine);
        roads?.Complete(Console.WriteLine);
        var data = FxMapGenerator.DevTools.Island.SampleLand.Finish(land, roads, town, Console.WriteLine);
        var path = FxMapGenerator.Core.SampleIsland.SampleIsland.Create(args[1], data);
        Console.WriteLine($"{path}: {sw.Elapsed.TotalSeconds:0.0} s");
        return 0;
    }

    case "sample-land-bundle" when args.Length == 3:
    {
        // the style editor's sample land written for the app to bundle: devtools sample-land-bundle <data/sample-island> <a|b|c>
        var variant = FxMapGenerator.DevTools.Island.SampleLand.Variants.First(v => v.Name == args[2]);
        var land = FxMapGenerator.DevTools.Island.SampleLand.Terrain(variant, Console.WriteLine);
        var roads = FxMapGenerator.DevTools.Island.Roads.Build(land, Console.WriteLine);
        var town = FxMapGenerator.DevTools.Island.Town.Build(land, roads, Console.WriteLine);
        roads.Complete(Console.WriteLine);
        var data = FxMapGenerator.DevTools.Island.SampleLand.Finish(land, roads, town, Console.WriteLine);
        FxMapGenerator.Core.SampleIsland.SampleIslandFile.Write(args[1], data);
        foreach (var f in new[] { FxMapGenerator.Core.SampleIsland.SampleIslandFile.Surface, FxMapGenerator.Core.SampleIsland.SampleIslandFile.Shapes })
            Console.WriteLine($"{Path.Combine(args[1], f)}: {new FileInfo(Path.Combine(args[1], f)).Length:N0} bytes");
        return 0;
    }

    case "preview-timings" when args.Length is 5 or 6:
        return FxMapGenerator.DevTools.PreviewTimings.Run(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), args[4], args.Length == 6 ? int.Parse(args[5]) : Environment.ProcessorCount / 2);
    case "sample-island" when args.Length == 2:
    {
        // the bundled sample land as a project in an empty folder: devtools sample-island <folder>
        if (Directory.Exists(args[1]) && Directory.EnumerateFileSystemEntries(args[1]).Any())
        {
            Console.Error.WriteLine($"{args[1]} is not empty");
            return 1;
        }
        Console.WriteLine(FxMapGenerator.Core.SampleIsland.SampleIsland.Create(args[1], FxMapGenerator.Core.SampleIsland.SampleIslandFile.Bundled()));
        return 0;
    }

    case "ytd-info" when args.Length >= 2:
        foreach (var file in args[1..])
        {
            Console.WriteLine($"{Path.GetFileName(file)}: {new FileInfo(file).Length:N0} bytes");
            foreach (var t in YtdFile.Read(file))
                Console.WriteLine($"  {(string)t.Name!,-24} {t.Width}x{t.Height} {(TextureFormat)t.Format} levels {t.Levels} stride {t.Stride} data {t.Data?.FullData.Length:N0} B");
        }
        return 0;

    case "gfx-empty" when args.Length is 3 or 4:
    {
        var ids = (args.Length == 4 ? args[3] : "1,3,5,7").Split(',').Select(int.Parse).ToHashSet();
        var bytes = FxMapGenerator.GameData.Scaleform.GfxFile.EmptyShapes(File.ReadAllBytes(args[1]), ids);
        File.WriteAllBytes(args[2], bytes);
        Console.WriteLine($"{args[2]}: {bytes.Length:N0} bytes, shapes {string.Join(",", ids.Order())} empty");
        return 0;
    }

    case "island-map" when args.Length is 2 or 3 or 4:
    {
        var where = FxMapGenerator.Core.GameFiles.GameFilesLocation.ResolveApp(new(args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null));
        var bytes = FxMapGenerator.Core.Export.IslandMap.Make(where);
        File.WriteAllBytes(args[1], bytes);
        Console.WriteLine($"{args[1]}: {bytes.Length:N0} bytes from {where.GtaFolder} ({where.GtaSource}), keys {where.KeysFolder} ({where.KeysSource})");
        return 0;
    }

    case "interior-maps" when args.Length is 2 or 3 or 4:
    {
        var where = FxMapGenerator.Core.GameFiles.GameFilesLocation.ResolveApp(new(args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null));
        var data = FxMapGenerator.Core.Export.InteriorMaps.Read(where);
        File.WriteAllText(args[1], FxMapGenerator.Core.Export.InteriorMaps.Lua(data, "devtools"), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"{args[1]}: {data.OwnPicture.Count} interiors with a picture of their own ({data.Pictures} picture files), " +
            $"underground {string.Join(", ", data.Underground.Select(r => $"{r.Name} ({r.X}, {r.Y}; {r.Interiors.Count})"))}, " +
            $"missing {(data.Missing.Count == 0 ? "none" : string.Join(", ", data.Missing))}; from {where.GtaFolder} ({where.GtaSource}), keys {where.KeysFolder} ({where.KeysSource})");
        return 0;
    }

    case "ytd-decode" when args.Length == 3:
        foreach (var t in YtdFile.Read(args[1]))
        {
            var rgba = YtdFile.Decompress(t.Data!.FullData, t.Width, t.Height, t.Format);
            var png = Path.Combine(args[2], (string)t.Name! + ".png");
            Images.SavePng(png, rgba, t.Width, t.Height);
            Console.WriteLine($"{(string)t.Name!} -> {png}");
        }
        return 0;

    case "calibrate" when args.Length is 2 or 3 or 4:
    {
        var project = Project.Load(args[1]);
        var folder = new WorkFolder(project.WorkFolderPath);
        var state = StateStore.Open(folder);
        var caps = project.Range.Keys.Where(b => state.Has(b, BlockItem.Shot) && state.Has(b, BlockItem.Height))
            .ToDictionary(b => b, b => new ScaleCalibration.Capture(folder.CapturePng(b), CameraLine.Read(folder.CaptureCamera(b)), folder.CaptureHeights(b)));
        int workers = args.Length >= 3 ? int.Parse(args[2]) : Environment.ProcessorCount;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (args.Length == 4)
        {
            var q = args[3].Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var at = ScaleCalibration.Measure(caps, q[0], q[1], workers);
            foreach (var (axis, a) in new[] { ("x", at.X), ("y", at.Y) })
                Console.WriteLine($"at {q[0]:F5},{q[1]:F5} {axis}: {a.Pairs} pairs, {a.Used} textured, median mismatch {a.MedianMismatch:+0.000;-0.000;0.000} m, 90 % within {a.P90Mismatch:0.000} m -> q {a.Q:F5}");
            Console.WriteLine($"{caps.Count} blocks, {sw.Elapsed.TotalSeconds:n1} s");
            return 0;
        }
        var (first, final) = ScaleCalibration.Calibrate(caps, workers, Console.WriteLine);
        foreach (var (name, r) in new[] { ("pass 1 (from 1, 1)", first), ("pass 2", final) })
            foreach (var (axis, a) in new[] { ("x", r.X), ("y", r.Y) })
                Console.WriteLine($"{name} {axis}: {a.Pairs} pairs, {a.Used} textured, median mismatch {a.MedianMismatch:+0.000;-0.000;0.000} m, 90 % within {a.P90Mismatch:0.000} m -> q {a.Q:F5}");
        Console.WriteLine($"{caps.Count} blocks, {sw.Elapsed.TotalSeconds:n1} s");
        return 0;
    }

    default:
        Console.Error.WriteLine("unknown command or wrong arguments: " + string.Join(' ', args));
        return 64;
}
