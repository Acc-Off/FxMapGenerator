using FxMapGenerator.Core.Export;

namespace FxMapGenerator.Core.Tests.Export;

/// <summary>
/// The game's interior maps as the minimap resource carries them: read from the names of the game's picture files and
/// from its table of interior maps, written as the Lua file the zoom script reads.
/// </summary>
public sealed class InteriorMapsTests
{
    /// <summary>
    /// A table in the shape of the game's <c>frontend.xml</c>: comments that are runs of hyphens (not well-formed XML),
    /// rows indented with tabs and spaces, places with and without a decimal point, a rotation.
    /// </summary>
    const string Table = """
        <!-- ------------------------------------ -->
        <frontend>
        	<minimap>
        		<data name="golf_courses"   	alignX="L"	alignY="B"	posX="0.003"		posY="-0.038"/>
        	</minimap>

            <!----------------------------------->
            <!-------- minimap interiors -------->
            <!----------------------------------->

            <minimap_interiors>
        	  <interior name="xm_x17DLC_FakeBase" posX="800.0" posY="5500.0" rot="0.0">
                    <content name="fxtest_base_1"/>
                    <content name="fxtest_base_2"/>
                </interior>
                <interior name="V_FakeMetro" posX="-200.0" posY="-1500.0" rot="0.0">
                    <content name="fxtest_metro"/>
                    <content name="fxtest_drain"/>
                    <content name="fxtest_canal"/>
                </interior>
            <interior name="V_FakeTunnel_ID1" posX="770" posY="-2070" rot="0.0">
            </interior>
        	<interior name="V_Michael" posX="-807.343" posY="174.981" rot="-21">
        	    <content name="V_Michael_Garage"/>
        	    <content name="V_Michael"/>
        	</interior>
            </minimap_interiors>
        </frontend>
        """;

    static readonly string[] Files = ["int4242.gfx", "INT117.GFX", "int4242.gfx", "int3273555063.gfx", "minimap_main_map.gfx", "interior.gfx", "int12x.gfx", "frontend.xml"];

    [Fact]
    public void TheHashIsTheGamesNameHashOfAnInterior()
    {
        // the numbers of the game's own file names: int18753073.gfx is V_FakeMetro's picture, int3232302352.gfx the island's
        Assert.Equal(18753073u, InteriorMaps.Hash("V_FakeMetro"));
        Assert.Equal(3232302352u, InteriorMaps.Hash("h4_fake_islandx"));
        Assert.Equal(InteriorMaps.Hash("v_fakemetro"), InteriorMaps.Hash("V_FAKEMETRO"));
    }

    [Fact]
    public void ThePicturesOfTheirOwnComeFromTheFileNamesAndFromTheRowsThatAreNotUndergroundPassages()
    {
        var data = InteriorMaps.Parse(Files, Table);
        // every number of a picture file once (whatever the case of the name), then the interiors the rows of other
        // pictures stand for: the base's two, the house's garage; the house itself has a file (3273555063)
        Assert.Equal(3, data.Pictures);
        Assert.Equal(3273555063u, InteriorMaps.Hash("V_Michael"));
        var expected = new[] { 117u, 4242u, 3273555063u, InteriorMaps.Hash("fxtest_base_1"), InteriorMaps.Hash("fxtest_base_2"), InteriorMaps.Hash("V_Michael_Garage") };
        Assert.Equal(expected.Order(), data.OwnPicture);
        // what the pictures of underground passages stand for is not among them
        Assert.DoesNotContain(InteriorMaps.Hash("fxtest_drain"), data.OwnPicture);
    }

    [Fact]
    public void TheRowsOfUndergroundPassagesKeepTheirPlaceAndTheirInteriors()
    {
        var data = InteriorMaps.Parse(Files, Table);
        Assert.Equal(new[] { "V_FakeMetro", "V_FakeTunnel_ID1" }, data.Underground.Select(r => r.Name));
        var metro = data.Underground[0];
        Assert.Equal((-200.0, -1500.0), (metro.X, metro.Y));
        Assert.Equal(new[] { "fxtest_metro", "fxtest_drain", "fxtest_canal" }, metro.Interiors);
        var tunnel = data.Underground[1];
        Assert.Equal((770.0, -2070.0), (tunnel.X, tunnel.Y));
        Assert.Empty(tunnel.Interiors);
        // the two this table does not have are named, and the resource is written without them
        Assert.Equal(new[] { "V_FakeWaterTunnel", "V_FakeTunnel_SC1" }, data.Missing);

        // a row whose place cannot be read counts as missing; names are matched whatever their case
        var odd = Table.Replace("name=\"V_FakeMetro\" posX=\"-200.0\"", "name=\"v_fakemetro\" posX=\"west\"", StringComparison.Ordinal);
        var without = InteriorMaps.Parse(Files, odd);
        Assert.Equal(new[] { "V_FakeTunnel_ID1" }, without.Underground.Select(r => r.Name));
        Assert.Equal(new[] { "V_FakeMetro", "V_FakeWaterTunnel", "V_FakeTunnel_SC1" }, without.Missing);
        Assert.DoesNotContain(InteriorMaps.Hash("fxtest_drain"), without.OwnPicture);
    }

    [Fact]
    public void AGameWithoutPictureFilesOrWithoutTheTableIsRefused()
    {
        Assert.Contains("no interior map files", Assert.Throws<InvalidDataException>(() => InteriorMaps.Parse(["minimap_main_map.gfx", "frontend.xml"], Table)).Message);
        Assert.Contains("no table of interior maps", Assert.Throws<InvalidDataException>(() => InteriorMaps.Parse(Files, "<frontend></frontend>")).Message);
        Assert.Contains("no table of interior maps", Assert.Throws<InvalidDataException>(() => InteriorMaps.Parse(Files, "<minimap_interiors> never closed")).Message);
        // a PC without GTA V or without keys
        using var tmp = new TempFolder();
        Assert.Equal("gta", TestGame.NoGame(tmp.Path).Missing);
        Assert.Equal("keys", TestGame.NoKeys(tmp.Path).Missing);
        Assert.Null(TestGame.Given().Missing);
        Assert.Contains("GTA V was not found", Assert.Throws<InvalidOperationException>(() => TestGame.NoGame(tmp.Path).Interiors()).Message);
        Assert.Contains("the RPF keys were not found", Assert.Throws<InvalidOperationException>(() => TestGame.NoKeys(tmp.Path).Interiors()).Message);
        Assert.Equal(("MINIMAP_NO_GTA", "MINIMAP_NO_KEYS"), (MinimapGameFiles.Problem("gta")!.Code, MinimapGameFiles.Problem("keys")!.Code));
        Assert.Null(MinimapGameFiles.Problem(null));
    }

    [Fact]
    public void TheLuaFileHoldsTheTwoTablesTheZoomScriptReads()
    {
        var lua = InteriorMaps.Lua(TestGame.Interiors, "9.9.9");
        var home = InteriorMaps.Hash("fxtest_home");
        var garage = InteriorMaps.Hash("fxtest_garage");
        Assert.Contains("RADAR_OWN_PICTURE = {\n    " + string.Join(' ', new[] { 4242u, home, garage }.Order().Select(h => $"[{h}] = true,")) + "\n}\n", lua);
        Assert.Contains("2 from the names of the game's\n-- int<hash>.gfx files, 1 more that rows", lua);
        Assert.Contains("""
            RADAR_UNDERGROUND = {
                { name = 'V_FakeMetro', x = -200.0, y = -1500.0, content = {
                    'fxtest_metro',
                } },
                { name = 'V_FakeWaterTunnel', x = -1500.0, y = -875.0, content = {
                    'fxtest_drain', 'fxtest_canal',
                } },
            }

            """.ReplaceLineEndings("\n"), lua);
        Assert.DoesNotContain('\r', lua);

        // many entries wrap: eight hashes a line, six names a line; a place keeps its decimals
        var many = new InteriorMaps.Data(Enumerable.Range(1, 17).Select(i => (uint)i).ToList(), 17,
            [new("V_FakeMetro", -807.343, 174.981, Enumerable.Range(1, 7).Select(i => $"n{i}").ToList())], []);
        var wrapped = InteriorMaps.Lua(many, "9.9.9");
        Assert.Contains("    [1] = true, [2] = true, [3] = true, [4] = true, [5] = true, [6] = true, [7] = true, [8] = true,\n    [9] = true,", wrapped);
        Assert.Contains("    [17] = true,\n}\n", wrapped);
        Assert.Contains("x = -807.343, y = 174.981, content = {\n        'n1', 'n2', 'n3', 'n4', 'n5', 'n6',\n        'n7',\n    } },\n", wrapped);
    }
}
