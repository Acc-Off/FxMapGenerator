using System.Globalization;
using System.Text;
using FxMapGenerator.Core.Minimap;
using FxMapGenerator.Core.Projects;
using FxMapGenerator.Core.World;

namespace FxMapGenerator.Core.Export;

/// <summary>
/// The minimap as one FiveM resource: the map's pictures under the game's own names (the 2 x 3 sheets as DXT5 and DXT1,
/// the small whole map under the radar), the 65 empty drawables that hide the game's own minimap lines, and Extra Map
/// Tiles 3.0.1 (MIT, bundled as released) drawing the map beyond the standard frame from the configuration written here
/// (<c>config.lua</c>: one tile per cell of 2250 m, <see cref="MinimapExtraTiles"/>); a small script of its own sets the
/// pause map's zoom levels and the radar's zoom and decides what the radar and the pause map show inside interiors
/// (<see cref="Zoom"/>, with the game's interior maps in <c>interiors.lua</c>, <see cref="InteriorMaps"/>); with Cayo
/// Perico's roads, the island map drawing nothing (<see cref="IslandMap"/>). Then the manifest, the READMEs (English and
/// Japanese) and the credits. The resource name carries a version: FiveM keeps the streamed files of a name in its
/// caches, so a new picture goes out under a new name.
/// </summary>
public static class MinimapResource
{
    const string Emt = "minimap/extra-map-tiles/";

    /// <summary>Extra Map Tiles' scripts in the order its manifest lists them.</summary>
    public static readonly IReadOnlyList<string> EmtScripts = ["utils.lua", "scaleforms.lua", "exports.lua", "client.lua"];

    /// <summary>The version of Extra Map Tiles bundled (its GitHub release).</summary>
    public const string EmtVersion = "3.0.1";

    /// <summary>The empty drawables (bundled data/minimap/ydd/): file name -> bytes.</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> Stubs() =>
        EmbeddedData.Names("minimap/ydd/").Order(StringComparer.Ordinal)
            .Select(n => (Path.GetFileName(n), WebExport.ReadBytes(n))).ToList();

    /// <summary>Writes the resource folder (made fresh; an existing folder of that name is replaced).</summary>
    /// <param name="extra">The cells beyond the standard frame whose textures the minimap stage made (<see cref="MinimapExtraTiles.Cells"/>).</param>
    /// <param name="interiors">The game's interior maps (<see cref="InteriorMaps.Read"/>).</param>
    /// <param name="islandMap">The empty island map (<see cref="IslandMap.Make"/>), or null when the project does not read Cayo Perico's roads.</param>
    /// <param name="cellMapCredits">The credits of an atlas or road map (<see cref="WebExport.CellMapCredits"/>), or empty.</param>
    /// <param name="title">The map's name for people (<see cref="MapSet.Title"/>).</param>
    public static void Write(string folder, string name, string ytdFolder, MapSet map, string title, string project, string version,
        IReadOnlyList<CellId> extra, InteriorMaps.Data interiors, byte[]? islandMap, string cellMapCredits = "")
    {
        var dir = Path.Combine(folder, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        var stream = Path.Combine(dir, "stream");
        Directory.CreateDirectory(stream);
        foreach (var s in MinimapSheets.All)
            foreach (var tex in new[] { s.SeaTexture, s.Texture })
                File.Copy(Path.Combine(ytdFolder, tex + ".ytd"), Path.Combine(stream, tex + ".ytd"));
        File.Copy(Path.Combine(ytdFolder, MinimapLod.Texture + ".ytd"), Path.Combine(stream, MinimapLod.Texture + ".ytd"));
        foreach (var cell in extra)
            File.Copy(Path.Combine(ytdFolder, MinimapExtraTiles.Texture(cell) + ".ytd"), Path.Combine(stream, MinimapExtraTiles.Texture(cell) + ".ytd"));
        foreach (var (file, bytes) in Stubs()) File.WriteAllBytes(Path.Combine(stream, file), bytes);
        foreach (var file in new[] { "minimap_main_map.gfx", "radar_masks.ytd" })
            File.WriteAllBytes(Path.Combine(stream, file), WebExport.ReadBytes(Emt + "stream/" + file));
        foreach (var script in EmtScripts) File.WriteAllBytes(Path.Combine(dir, script), WebExport.ReadBytes(Emt + script));
        File.WriteAllBytes(Path.Combine(dir, "extra-map-tiles-LICENSE.txt"), WebExport.ReadBytes(Emt + "LICENSE"));
        if (islandMap is not null) File.WriteAllBytes(Path.Combine(stream, IslandMap.FileName), islandMap);
        Text(Path.Combine(dir, "config.lua"), Config(extra, project, version));
        Text(Path.Combine(dir, InteriorMaps.FileName), InteriorMaps.Lua(interiors, version));
        Text(Path.Combine(dir, "zoom.lua"), Zoom(version));
        Text(Path.Combine(dir, "fxmanifest.lua"), Manifest(name, title, project, version, islandMap is not null));
        // north of the standard frame is beyond the game's road network data (its grid ends at y 8192)
        bool north = extra.Any(c => c.R < 0);
        Text(Path.Combine(dir, "README.txt"), Readme(name, title, extra.Count > 0, north, islandMap is not null));
        Text(Path.Combine(dir, "README.ja.txt"), ReadmeJa(name, title, extra.Count > 0, north, islandMap is not null));
        Text(Path.Combine(dir, "CREDITS.txt"), Credits(map, project, version, extra.Count > 0, islandMap is not null, cellMapCredits));
    }

    static string N(double v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Extra Map Tiles' configuration: its own settings as released, the blur of the tilted radar replaced only when there
    /// are tiles, and one tile per cell beyond the standard frame, by its north-west corner at half a sheet's scale.
    /// </summary>
    public static string Config(IReadOnlyList<CellId> extra, string project, string version)
    {
        var sb = new StringBuilder();
        sb.Append("-- Extra Map Tiles ").Append(EmtVersion).Append(" configuration written by FxMapGenerator ").Append(version).Append(" for ").Append(project).Append(":\n");
        sb.Append("-- the map beyond the game's 2 x 3 minimap sheets, one tile per cell of 2250 m (half a sheet),\n");
        sb.Append("-- placed by its north-west corner (x, y in game metres).\n");
        sb.Append("config = {}\n");
        sb.Append("config.scaleform_minimap_main_map = \"minimap_main_map\"\n");
        sb.Append("config.scaleform_minimap = \"minimap\"\n");
        sb.Append("config.offset = 0.1\n\n");
        sb.Append("config.remove_blur = ").Append(extra.Count > 0 ? "true" : "false").Append('\n');
        sb.Append("config.radar_masks = \"radar_masks\"\n\n");
        sb.Append("config.tiles = {\n");
        foreach (var cell in extra)
        {
            var name = MinimapExtraTiles.Texture(cell);
            var (x, y) = MinimapExtraTiles.Corner(cell);
            sb.Append("    ['").Append(name).Append("'] = {\n");
            sb.Append("        txd = \"").Append(name).Append("\",\n");
            sb.Append("        txn = \"").Append(name).Append("\",\n");
            sb.Append("        x = ").Append(N(x)).Append(",\n");
            sb.Append("        y = ").Append(N(y)).Append(",\n");
            sb.Append("        x_scale = 0.5,\n");
            sb.Append("        y_scale = 0.5,\n");
            sb.Append("        rotation = 0.0,\n");
            sb.Append("        alpha = 100,\n");
            sb.Append("        centered = false,\n");
            sb.Append("        visible = true\n");
            sb.Append("    },\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>The server's setting that keeps the radar's zoom at 1100 everywhere (<see cref="Zoom"/>).</summary>
    public const string FixedZoomSetting = "fxmapgen_minimap_fixed_zoom";

    /// <summary>
    /// The resource's own script: the pause map's zoom levels (the values of the postal code minimaps), and what the
    /// radar and the pause map show by where the player is, so that the two agree. The shape was looked at in the game:
    /// <list type="bullet">
    /// <item>outside, and inside an interior without a radar picture: the radar's zoom is 1100, on foot and in a vehicle
    /// alike (the postal code minimaps' value);</item>
    /// <item>inside an interior that has a radar picture of its own (<see cref="InteriorMaps.Data.OwnPicture"/>): the
    /// zoom is given back to the game (0) and the game shows that picture at its closer zoom; while the pause map is in
    /// its interior view the outside map is hidden there, so that it shows the picture alone as well;</item>
    /// <item>inside an interior one of the pictures of underground passages stands for
    /// (<see cref="InteriorMaps.Data.Underground"/>): the zoom stays 1100, the outside map is kept and that picture is
    /// added at its place: the map with the passages laid over it, on the radar and on the pause map.</item>
    /// </list>
    /// A zoom set stays until it is set again, so the script sets it every frame and gives it back (0) when the resource
    /// stops. With the server's setting <see cref="FixedZoomSetting"/> (a replicated convar) the script only keeps the
    /// zoom at 1100 everywhere: for servers whose island script asks the game for the island map every frame everywhere,
    /// which keeps the interior pictures from showing (the READMEs say so).
    /// </summary>
    public static string Zoom(string version) => $$"""
        -- Minimap zoom (FxMapGenerator {{version}}): the pause map's zoom levels, and what the radar and the pause map
        -- show by where the player is, so that the two agree.
        --   Outside, and inside an interior without a radar picture: the radar's zoom is 1100, on foot and in a vehicle
        --     alike, as the postal code minimaps have it.
        --   Inside an interior that has a radar picture of its own (interiors.lua, RADAR_OWN_PICTURE: shops, houses ...):
        --     the zoom is given back to the game (0), so that the radar shows that picture at the game's closer zoom.
        --     Leaving the zoom alone is not enough: a zoom set stays until it is set again. While the pause map is in its
        --     interior view the outside map is hidden, so that the pause map shows the picture alone too. It is not
        --     hidden for the radar: nothing tells a script that the radar shows an interior (IsPausemapInInteriorMode()
        --     is false whenever the pause menu is closed).
        --   Inside an interior one of the pictures of underground passages stands for (RADAR_UNDERGROUND: the metro, the
        --     storm drains, road tunnels): the zoom stays 1100, the outside map is kept and that picture is added at its
        --     place, so that the radar and the pause map both show the map with the passages laid over it.
        -- With "setr {{FixedZoomSetting}} true" in server.cfg the zoom is 1100 everywhere and nothing else is
        -- done (README.txt says when that is wanted). The game's own zoom and zoom levels come back when the resource
        -- stops.

        CreateThread(function()
            SetMapZoomDataLevel(0, 0.96, 0.9, 0.08, 0.0, 0.0)
            SetMapZoomDataLevel(1, 1.6, 0.9, 0.08, 0.0, 0.0)
            SetMapZoomDataLevel(2, 8.6, 0.9, 0.08, 0.0, 0.0)
            SetMapZoomDataLevel(3, 12.3, 0.9, 0.08, 0.0, 0.0)
            SetMapZoomDataLevel(4, 22.3, 0.9, 0.08, 0.0, 0.0)
        end)

        local setting = GetConvar('{{FixedZoomSetting}}', 'false')
        local fixedZoom = setting == 'true' or setting == '1'

        local underground = {}
        for _, row in ipairs(RADAR_UNDERGROUND) do
            row.hash = GetHashKey(row.name)
            for _, name in ipairs(row.content) do
                underground[GetHashKey(name) & 0xFFFFFFFF] = row
            end
        end

        local radarZoomActive = true
        local lastInterior = 0
        local ownPicture, undergroundRow = false, nil

        -- What the interior the player is in has: a picture of its own, a picture of underground passages that stands
        -- for it, or neither; asked of the game when the interior changes.
        local function look(ped)
            local interior = GetInteriorFromEntity(ped)
            if interior == lastInterior then
                return
            end
            lastInterior, ownPicture, undergroundRow = interior, false, nil
            if interior ~= 0 then
                local _, hash = GetInteriorLocationAndNamehash(interior)
                if type(hash) == 'number' then
                    hash = hash & 0xFFFFFFFF
                    ownPicture = RADAR_OWN_PICTURE[hash] == true
                    undergroundRow = underground[hash]
                end
            end
        end

        CreateThread(function()
            while radarZoomActive do
                Wait(0)
                local ped = PlayerPedId()
                if not fixedZoom then
                    look(ped)
                end
                if IsPedOnFoot(ped) or IsPedInAnyVehicle(ped, true) then
                    SetRadarZoom(ownPicture and 0 or 1100)
                end
                if ownPicture then
                    if IsPauseMenuActive() and IsPausemapInInteriorMode() then
                        HideMinimapExteriorMapThisFrame()
                    end
                elseif undergroundRow then
                    SetRadarAsExteriorThisFrame()
                    SetRadarAsInteriorThisFrame(undergroundRow.hash, undergroundRow.x, undergroundRow.y, 0, 0)
                end
            end
        end)

        AddEventHandler('onClientResourceStop', function(resourceName)
            if GetCurrentResourceName() ~= resourceName then
                return
            end
            radarZoomActive = false
            SetRadarZoom(0)
            for i = 0, 4 do
                ResetMapZoomDataLevel(i)
            end
        end)

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string Manifest(string name, string title, string project, string version, bool islandMap)
    {
        var sb = new StringBuilder();
        sb.Append("-- Minimap of ").Append(project).Append(": ").Append(title).Append(", ").Append(MinimapSheets.Size).Append(" x ").Append(MinimapSheets.Size)
          .Append(" px per 4500 m sheet. Made with FxMapGenerator ").Append(version).Append(".\n");
        sb.Append("-- Extra Map Tiles ").Append(EmtVersion).Append(" (MIT, Alex Licuriceanu) draws the map beyond the game's minimap: see CREDITS.txt.\n");
        sb.Append("fx_version 'cerulean'\n");
        sb.Append("game 'gta5'\n");
        sb.Append("lua54 'yes'\n");
        sb.Append("this_is_a_map 'yes'\n\n");
        sb.Append("name '").Append(name).Append("'\n");
        sb.Append("description 'Minimap made with FxMapGenerator'\n");
        sb.Append("version '").Append(version).Append("'\n\n");
        sb.Append("client_scripts {\n");
        foreach (var s in EmtScripts) sb.Append("    '").Append(s).Append("',\n");
        sb.Append("    '").Append(InteriorMaps.FileName).Append("',\n");
        sb.Append("    'zoom.lua',\n");
        sb.Append("}\n\n");
        sb.Append("shared_scripts {\n    'config.lua',\n}\n\n");
        sb.Append("files {\n");
        sb.Append("    'stream/*.ytd',\n");
        sb.Append("    'stream/minimap_main_map.gfx',\n");
        if (islandMap) sb.Append("    'stream/").Append(IslandMap.FileName).Append("',\n");
        sb.Append("}\n");
        if (islandMap)
            sb.Append("\n-- Cayo Perico's island map, drawing nothing: the island shows as this map's own pictures.\n")
              .Append("data_file 'SCALEFORM_DLC_FILE' 'stream/").Append(IslandMap.FileName).Append("'\n");
        return sb.ToString();
    }

    /// <summary>The forum thread of the island resource the READMEs name.</summary>
    public const string IslandResourceAddress = "https://forum.cfx.re/t/the-cayo-perico-island-available-for-fivem/1897446";

    /// <summary>
    /// The two lines that resource calls every frame wherever the player is, as its <c>ipls/cayo_perico.lua</c> writes
    /// them: while they are called, the game shows the island map in place of every interior's picture.
    /// </summary>
    public static readonly IReadOnlyList<string> IslandResourceLines =
        ["SetRadarAsExteriorThisFrame()", "SetRadarAsInteriorThisFrame(`h4_fake_islandx`, vec(4700.0, -5145.0), 0, 0)"];

    /// <param name="north">The map reaches north of the standard frame (beyond the game's road network data).</param>
    public static string Readme(string name, string title, bool extraTiles, bool north, bool islandMap)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append(": the in-game minimap and pause map as the ").Append(title)
          .Append(" (").Append(MinimapSheets.Size).Append(" x ").Append(MinimapSheets.Size).Append(" px per 4500 m sheet)")
          .Append(extraTiles ? ", with the map beyond the game's minimap" : "").Append(".\n\n");
        sb.Append("Install:\n");
        sb.Append("1. Copy this folder into the server's resources folder and add \"ensure ").Append(name).Append("\" to server.cfg\n");
        sb.Append("   (or start it from the console: refresh, then ensure ").Append(name).Append(").\n");
        sb.Append("2. Remove the resources this one replaces:\n");
        sb.Append("   - postal-code-map (PostalCodeMap) and other minimap resources (for example Roxwood-Minimap's map folder);\n");
        if (islandMap) sb.Append("   - the Cayo Perico island map resource (for example CayoPericoMinimap);\n");
        sb.Append("   - an Extra Map Tiles resource you already run: move your own tiles (their lines in its config.lua and their\n");
        sb.Append("     files in its stream folder) into this one.\n");
        if (islandMap)
        {
            sb.Append("3. Keep the resource that loads the Cayo Perico island (for example the IPL resource of \"The Cayo Perico Island\n");
            sb.Append("   Available for FiveM\" on the Cfx.re forum): this one does not load the island. The island shows as this map's\n");
            sb.Append("   own pictures.\n");
        }
        sb.Append(islandMap ? "4" : "3").Append(". Players see the new minimap after starting FiveM again: the radar keeps the pictures it loaded when the\n");
        sb.Append("   game started, also over a reconnect.\n");
        sb.Append(islandMap ? "5" : "4").Append(". When you make the map again, use the new resource (a new name) and remove this one: FiveM keeps the\n");
        sb.Append("   files of a resource name in its caches.\n\n");
        sb.Append("Inside buildings and underground:\n");
        sb.Append("- Inside a building that has a map of its own in the game (shops, houses and others), the radar and the pause map\n");
        sb.Append("  switch to that building's floor plan.\n");
        sb.Append("- In the metro, the storm drains and the road tunnels, the passages are laid over the map, at the same zoom as\n");
        sb.Append("  outside.\n");
        sb.Append("- Everywhere else the radar shows the map at its outdoor zoom.\n\n");
        sb.Append("If you run a resource that loads the Cayo Perico island:\n");
        sb.Append("The IPL resource of \"The Cayo Perico Island Available for FiveM\" on the Cfx.re forum\n");
        sb.Append("(").Append(IslandResourceAddress).Append(") calls these two lines every frame,\n");
        sb.Append("wherever the player is (in its ipls/cayo_perico.lua):\n");
        foreach (var line in IslandResourceLines) sb.Append("    ").Append(line).Append('\n');
        sb.Append("While they are called, no floor plan shows: inside a building the radar is only the map, enlarged so far that\n");
        sb.Append("nothing can be read. The underground passages show only when this resource is started after that one. Do one of\n");
        sb.Append("these:\n");
        sb.Append("- Remove the two lines (put -- in front of each).\n");
        sb.Append("  Buildings and underground passages then show as described above. ");
        sb.Append(islandMap
            ? "The island shows as this map's own\n  pictures: the two lines are not needed for it.\n"
            : "The game's own island map, which\n  those lines ask for, no longer shows.\n");
        sb.Append("- Keep the two lines and add this line to server.cfg:\n");
        sb.Append("    setr ").Append(FixedZoomSetting).Append(" true\n");
        sb.Append("  The radar then shows the map at its outdoor zoom everywhere. Floor plans and underground passages do not show.\n\n");
        sb.Append("What is inside:\n");
        sb.Append("- stream/minimap_sea_*.ytd (the pause map) and stream/minimap_*.ytd (the radar): the map's pictures under the game's\n");
        sb.Append("  own names; stream/minimap_lod_128.ytd: the small whole map under the radar.\n");
        sb.Append("- stream/minimap_*.ydd: empty drawables that hide the game's own minimap lines.\n");
        if (extraTiles) sb.Append("- stream/fxmapgen_extra_*.ytd: the map beyond the game's minimap, drawn by Extra Map Tiles as config.lua says.\n");
        if (islandMap) sb.Append("- stream/int3232302352.gfx: Cayo Perico's island map drawing nothing, made from your own game files.\n");
        sb.Append("- Extra Map Tiles ").Append(EmtVersion).Append(" (client.lua, exports.lua, scaleforms.lua, utils.lua, stream/minimap_main_map.gfx,\n");
        sb.Append("  stream/radar_masks.ytd), bundled as released under the MIT license (extra-map-tiles-LICENSE.txt).\n");
        sb.Append("- zoom.lua: the pause map's zoom levels, the radar's zoom, and what the radar and the pause map show inside\n");
        sb.Append("  buildings and underground.\n");
        sb.Append("- interiors.lua: the list of the game's interior maps that zoom.lua reads, made from your own game files.\n");
        if (north)
            sb.Append("\nThe map north of y 8192 is beyond the game's road network data: whether the GPS finds roads there is not known.\n");
        return sb.ToString();
    }

    public static string ReadmeJa(string name, string title, bool extraTiles, bool north, bool islandMap)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append(": ゲーム内のミニマップと ESC の地図を「").Append(title).Append("」にするリソースです（4500 m ごとに ")
          .Append(MinimapSheets.Size).Append(" × ").Append(MinimapSheets.Size).Append(" px")
          .Append(extraTiles ? "。ゲームのミニマップの外の地図も含みます" : "").Append("）。\n\n");
        sb.Append("入れ方:\n");
        sb.Append("1. このフォルダをサーバーの resources に置き、server.cfg に「ensure ").Append(name).Append("」を足します\n");
        sb.Append("   （コンソールからなら refresh の後に ensure ").Append(name).Append("）。\n");
        sb.Append("2. このリソースが置き換えるものを外します:\n");
        sb.Append("   - postal-code-map（PostalCodeMap）と、ほかのミニマップのリソース（Roxwood-Minimap の地図のフォルダなど）\n");
        if (islandMap) sb.Append("   - カヨ・ペリコの島の地図のリソース（CayoPericoMinimap など）\n");
        sb.Append("   - すでに動かしている Extra Map Tiles（自分で足したタイルがあれば、その config.lua の行と stream のファイルを、\n");
        sb.Append("     このリソースに移します）\n");
        if (islandMap)
        {
            sb.Append("3. カヨ・ペリコの島を読み込むリソース（Cfx.re フォーラムの「The Cayo Perico Island Available for FiveM」の IPL の\n");
            sb.Append("   リソースなど）は残します。このリソースは島を読み込みません。島は、このリソースの地図の絵で出ます。\n");
        }
        sb.Append(islandMap ? "4" : "3").Append(". プレイヤーには、FiveM を起動し直した後に新しいミニマップが見えます（レーダーは、ゲームを起動したときに\n");
        sb.Append("   読んだ絵を、入り直しても使い続けます）。\n");
        sb.Append(islandMap ? "5" : "4").Append(". 地図を作り直したときは、新しいリソース（新しい名前）を使い、これは外します（FiveM は同じ名前のリソースの\n");
        sb.Append("   ファイルを覚えておくため）。\n\n");
        sb.Append("建物の中と地下の地図:\n");
        sb.Append("- 専用の地図がある建物（店、家など）の中では、レーダーと ESC の地図が、その建物の間取りに切り替わります。\n");
        sb.Append("- 地下鉄、水路、道路のトンネルの中では、地上と同じ拡大の地図に、通路の絵が重なります。\n");
        sb.Append("- それ以外の場所では、レーダーは地上の拡大の地図です。\n\n");
        sb.Append("カヨ・ペリコの島を読み込むリソースを使っているとき:\n");
        sb.Append("Cfx.re フォーラムの「The Cayo Perico Island Available for FiveM」\n");
        sb.Append("（").Append(IslandResourceAddress).Append("）の IPL のリソースは、\n");
        sb.Append("ipls/cayo_perico.lua で、どこにいても毎フレーム次の 2 行を呼びます。\n");
        foreach (var line in IslandResourceLines) sb.Append("    ").Append(line).Append('\n');
        sb.Append("この 2 行が呼ばれている間、建物の間取りは出ません。建物の中のレーダーは、大きく拡大された地図だけになり、読めません。\n");
        sb.Append("地下の通路の絵は、このリソースを島のリソースより後に起動したときだけ出ます。次のどちらかを行ってください。\n");
        sb.Append("- 2 行を消す（行の先頭に -- を付ける）。\n");
        sb.Append("  建物の中と地下の地図が、上のとおりに出ます。");
        sb.Append(islandMap
            ? "島は、このリソースの地図の絵で出ます（2 行は要りません）。\n"
            : "この 2 行が出していたゲームの島の地図は、出なくなります。\n");
        sb.Append("- 2 行を残し、server.cfg に次の 1 行を足す。\n");
        sb.Append("    setr ").Append(FixedZoomSetting).Append(" true\n");
        sb.Append("  レーダーは、どこにいても地上の拡大の地図になります。建物の間取りと地下の通路の絵は出ません。\n\n");
        sb.Append("中身:\n");
        sb.Append("- stream/minimap_sea_*.ytd（ESC の地図）と stream/minimap_*.ytd（レーダー）: ゲームと同じ名前の地図の絵。\n");
        sb.Append("  stream/minimap_lod_128.ytd: レーダーの下に敷く全体図。\n");
        sb.Append("- stream/minimap_*.ydd: ゲームのミニマップの線を消す空のもの。\n");
        if (extraTiles) sb.Append("- stream/fxmapgen_extra_*.ytd: ゲームのミニマップの外の地図。Extra Map Tiles が config.lua に従って描きます。\n");
        if (islandMap) sb.Append("- stream/int3232302352.gfx: 何も描かないカヨ・ペリコの島の地図。あなたのゲームのファイルから作ったものです。\n");
        sb.Append("- Extra Map Tiles ").Append(EmtVersion).Append("（client.lua・exports.lua・scaleforms.lua・utils.lua・stream/minimap_main_map.gfx・\n");
        sb.Append("  stream/radar_masks.ytd）: 公開されたものをそのまま、MIT ライセンスで同梱しています（extra-map-tiles-LICENSE.txt）。\n");
        sb.Append("- zoom.lua: ESC の地図の拡大の段と、レーダーの拡大、建物の中と地下で出す地図を決めます。\n");
        sb.Append("- interiors.lua: zoom.lua が読む、ゲームの内装の地図の一覧。あなたのゲームのファイルから作ったものです。\n");
        if (north)
            sb.Append("\ny 8192 より北は、ゲームの道のデータの外です。そこで GPS が道をたどれるかは分かっていません。\n");
        return sb.ToString();
    }

    /// <param name="extraTiles">The resource carries textures beyond the standard frame.</param>
    /// <param name="islandMap">The resource carries the empty island map.</param>
    public static string Credits(MapSet map, string project, string version, bool extraTiles, bool islandMap, string cellMapCredits = "")
    {
        var sb = new StringBuilder();
        var pictures = extraTiles ? "stream/minimap_*.ytd, stream/fxmapgen_extra_*.ytd" : "stream/minimap_*.ytd";
        sb.Append("Made with FxMapGenerator ").Append(version).Append(" (MIT License), https://github.com/Acc-Off/FxMapGenerator\n\n");
        if (map.Kind == MapKind.Satellite)
            sb.Append("Map pictures (").Append(pictures).Append("): the world of Grand Theft Auto V (Rockstar Games), taken in FiveM on the server of \"")
              .Append(project).Append("\".\n\n");
        else sb.Append("Map pictures (").Append(pictures).Append("): ").Append(cellMapCredits);
        sb.Append("The empty minimap drawables (stream/minimap_*.ydd) and the zoom values (zoom.lua) come from the\n");
        sb.Append("postal code map minimap by Virus_City:\n");
        sb.Append("https://forum.cfx.re/t/release-postal-code-map-minimap-new-improved-v1-3/147458\n\n");
        sb.Append("The list of interior maps (").Append(InteriorMaps.FileName).Append("): the names of the interior maps of Grand Theft Auto V (Rockstar Games)\n");
        sb.Append("and the places of its maps of underground passages, read from the game files of the PC that wrote this resource.\n\n");
        if (islandMap)
        {
            sb.Append("The island map (stream/").Append(IslandMap.FileName).Append("): Cayo Perico's island map of Grand Theft Auto V (Rockstar Games),\n");
            sb.Append("read from the game files of the PC that wrote this resource, its shapes made to draw nothing.\n\n");
        }
        sb.Append("Extra Map Tiles ").Append(EmtVersion).Append(" (client.lua, exports.lua, scaleforms.lua, utils.lua, stream/minimap_main_map.gfx,\n");
        sb.Append("stream/radar_masks.ytd) by Alex Licuriceanu (L1CKS), MIT License (extra-map-tiles-LICENSE.txt), bundled as released:\n");
        sb.Append("https://github.com/alexlicuriceanu/extra-map-tiles/releases/tag/v").Append(EmtVersion).Append('\n');
        return sb.ToString();
    }

    static void Text(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
}
