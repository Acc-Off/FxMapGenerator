using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FxMapGenerator.App.Cli;
using FxMapGenerator.App.Jobs;
using FxMapGenerator.Core.Export;
using FxMapGenerator.Core.Projects;
using Microsoft.Extensions.DependencyInjection;

namespace FxMapGenerator.App.Tests;

/// <summary>The conversion of an edited picture through the API and the command line.</summary>
public sealed class ConvertApiTests
{
    static string NewProject(string folder)
    {
        var p = Project.Create(Path.Combine(folder, "proj", "conv.fxmapgen.json"), "Convert test");
        p.File.Range.Base = "none";
        p.File.Maps.Roadmap = true;
        p.File.Minimap.Map = "roadmap";
        p.Save();
        return p.FilePath;
    }

    /// <summary>A PNG of one colour (colour with alpha, 8 bits a channel, no filter), written row by row.</summary>
    static void WritePng(string path, int width, int height, byte r, byte g, byte b)
    {
        var crcs = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        void Chunk(string type, byte[] data)
        {
            var head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            uint crc = 0xFFFFFFFF;
            foreach (var x in head.AsSpan(4)) crc = crcs[(crc ^ x) & 0xFF] ^ (crc >> 8);
            foreach (var x in data) crc = crcs[(crc ^ x) & 0xFF] ^ (crc >> 8);
            var sum = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(sum, crc ^ 0xFFFFFFFF);
            file.Write(head);
            file.Write(data);
            file.Write(sum);
        }
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 6);
        Chunk("IHDR", header);
        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            for (int x = 0; x < width; x++) (row[1 + 4 * x], row[2 + 4 * x], row[3 + 4 * x], row[4 + 4 * x]) = (r, g, b, 255);
            for (int y = 0; y < height; y++) zlib.Write(row);
        }
        Chunk("IDAT", packed.ToArray());
        Chunk("IEND", []);
    }

    static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    static string[] Codes(JsonElement info) => info.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToArray();

    [Fact]
    public async Task APictureIsCheckedConvertedAndKeptForNextTime()
    {
        await using var host = await TestHost.StartAsync();
        var file = NewProject(host.DataDirectory);
        var dir = Path.GetDirectoryName(file)!;
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/project/open", new { path = file })).StatusCode);

        // nothing kept yet: no picture, and the minimap's map as the map it would be made from
        var info = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
        Assert.Equal(JsonValueKind.Null, info.GetProperty("picture").GetProperty("file").ValueKind);
        Assert.Equal("roadmap", info.GetProperty("picture").GetProperty("map").GetString());

        var none = await Json(await host.Client.PostAsJsonAsync("/api/project/convert/check", new { }));
        Assert.Equal(new[] { "BAD_PICTURE" }, Codes(none));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("picture").ValueKind);

        // a picture of another size, and nothing to write
        var small = Path.Combine(dir, "small.png");
        WritePng(small, 64, 32, 1, 2, 3);
        var bad = await Json(await host.Client.PostAsJsonAsync("/api/project/convert/check", new { file = small, tiles = false, minimap = false }));
        Assert.Equal(new[] { "NOTHING", "BAD_PICTURE" }, Codes(bad));
        var looked = bad.GetProperty("picture");
        Assert.Equal((64, 32, "BAD_SIZE"), (looked.GetProperty("width").GetInt32(), looked.GetProperty("height").GetInt32(), looked.GetProperty("problem").GetString()));
        Assert.Equal(JsonValueKind.Null, looked.GetProperty("zoom").ValueKind);
        var refused = await host.Client.PostAsJsonAsync("/api/project/convert", new { file = small });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("EXPORT_BAD_PICTURE", (await Json(refused)).GetProperty("error").GetProperty("code").GetString());

        // the frame at zoom 6, under the name a paint program gives the picture of the satellite map's layered file
        var picture = Path.Combine(dir, "edited", "satellite-z6 copy.png");
        WritePng(picture, 8192, 12288, 30, 60, 90);
        var fits = await Json(await host.Client.PostAsJsonAsync("/api/project/convert/check", new { file = picture }));
        Assert.Empty(Codes(fits));
        Assert.Equal((6, "satellite", "satellite"), (fits.GetProperty("picture").GetProperty("zoom").GetInt32(), fits.GetProperty("guessedMap").GetString(), fits.GetProperty("map").GetString()));
        Assert.StartsWith(ExportOptions.ResourcePrefix, fits.GetProperty("resourceName").GetString());
        // a name that tells no map: the map asked for, or the minimap's
        var other = await Json(await host.Client.PostAsJsonAsync("/api/project/convert/check", new { file = small, map = "satellite" }));
        Assert.Equal((JsonValueKind.Null, "satellite"), (other.GetProperty("guessedMap").ValueKind, other.GetProperty("map").GetString()));
        Assert.Equal("roadmap", (await Json(await host.Client.PostAsJsonAsync("/api/project/convert/check", new { file = small }))).GetProperty("map").GetString());

        // the web tiles alone (the textures are the core's tests')
        var started = await host.Client.PostAsJsonAsync("/api/project/convert", new { folder = "out", file = picture, map = "satellite", minimap = false, baseUrl = "https://maps.example.net" });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var jobs = host.App.Services.GetRequiredService<JobManager>();
        await jobs.WaitAsync();
        var job = await host.Client.GetFromJsonAsync<JsonElement>("/api/jobs/current");
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal(new[] { "convert.tiles", "convert.files" }, job.GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        var web = Path.Combine(dir, "out", "web-edited", "satellite");
        Assert.True(File.Exists(Path.Combine(web, "tiles", "satellite", "6", "31", "47.png")));
        Assert.True(File.Exists(Path.Combine(web, "index.html")));
        Assert.Contains("https://maps.example.net/tiles/{layer}/{z}/{x}/{y}.png", File.ReadAllText(Path.Combine(web, "lb-phone.lua")));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(dir, "out"), "fxmapgen-minimap-*").Any());
        Assert.False(Directory.Exists(Path.Combine(dir, "out", ".convert")));

        // kept: the picture (relative to the project file) and its map; the export's own choices are as they were
        var saved = Project.Load(file).File.Export;
        Assert.Equal((Path.Combine("edited", "satellite-z6 copy.png"), "satellite"), (saved.Picture.File, saved.Picture.Map));
        Assert.Null(saved.Folder);
        var again = await host.Client.GetFromJsonAsync<JsonElement>("/api/project/export");
        Assert.Equal((picture, "satellite"), (again.GetProperty("picture").GetProperty("file").GetString(), again.GetProperty("picture").GetProperty("map").GetString()));
        // the conversion wrote into another folder than the export's: that folder's record has it
        var record = ExportRecord.Read(Path.Combine(dir, "out"))!;
        var edited = Assert.Single(record.WebEdited!);
        Assert.Equal(("satellite", "satellite", "satellite-z6 copy.png", 6, 2049, true), (edited.Name, edited.Map, edited.Picture, edited.Zoom, edited.Tiles, edited.Complete));
        var shown = await Json(await host.Client.PostAsJsonAsync("/api/project/export/check", new { folder = "out" }));
        Assert.Equal("satellite", shown.GetProperty("last").GetProperty("webEdited")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void TheCommandLineConvertsAPicture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fxmapgen-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var file = NewProject(dir);
            var outDir = Path.Combine(dir, "cli-out");
            var picture = Path.Combine(dir, "roadmap-z6.png");
            WritePng(picture, 8192, 12288, 30, 60, 90);
            var o = new StringWriter();
            var e = new StringWriter();
            Assert.Equal(0, CliCommands.Run(["convert", file, "--picture", picture, "--out", outDir, "--no-minimap", "--workers", "2"], o, e));
            Assert.Contains("made from the roadmap map", o.ToString());
            Assert.Contains("conversion written", o.ToString());
            Assert.True(File.Exists(Path.Combine(outDir, "web-edited", "roadmap", "tiles", "roadmap", "0", "0", "0.png")));
            Assert.False(Directory.EnumerateDirectories(outDir, "fxmapgen-minimap-*").Any());
            Assert.Null(Project.Load(file).File.Export.Picture.File);              // the command line keeps nothing in the project

            Assert.Equal(64, CliCommands.Run(["convert", file, "--out", outDir], new StringWriter(), new StringWriter()));
            Assert.Equal(64, CliCommands.Run(["convert", file, "--picture", picture, "--zoom", "6"], new StringWriter(), new StringWriter()));
            var small = Path.Combine(dir, "small.png");
            WritePng(small, 64, 32, 1, 2, 3);
            var refused = new StringWriter();
            Assert.Equal(65, CliCommands.Run(["convert", file, "--picture", small, "--out", outDir], refused, e));
            Assert.Contains("cannot convert: the picture", refused.ToString());
            Assert.Contains("is 64 x 32 px", refused.ToString());
            refused = new StringWriter();
            Assert.Equal(65, CliCommands.Run(["convert", file, "--picture", picture, "--map", "atlas-postalcodemap-en", "--out", outDir, "--no-tiles", "--no-minimap"], refused, e));
            Assert.Contains("cannot convert: choose the web tiles or the minimap resource", refused.ToString());
            Assert.Contains("is not one of the project's maps", refused.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
