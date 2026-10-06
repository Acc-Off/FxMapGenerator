# Questions and answers

English | [日本語](faq.ja.md)

How-tos by what you want to do, what to do when something fails, and things to know about what the maps contain. The
steps for a first map are in [Your first map](walkthrough.md), the screens are explained in [The screens](screens.md).

**How to**

- [Updating the maps after the server changed](#updating-the-maps-after-the-server-changed)
- [Cayo Perico, Roxwood and other land outside the standard map](#cayo-perico-roxwood-and-other-land-outside-the-standard-map)
- [Japanese maps](#japanese-maps)
- [Adding another kind of map later](#adding-another-kind-of-map-later)
- [Drawing the roads and bridges of an MLO](#drawing-the-roads-and-bridges-of-an-mlo)
- [Editing a map in Photoshop or GIMP (PSD)](#editing-a-map-in-photoshop-or-gimp-psd)
- [Editing a map in Inkscape or Illustrator (SVG)](#editing-a-map-in-inkscape-or-illustrator-svg)
- [A server that ships road data of its own](#a-server-that-ships-road-data-of-its-own)
- [A server that is somewhere else](#a-server-that-is-somewhere-else)
- [A server that is neither Qbox nor QBCore](#a-server-that-is-neither-qbox-nor-qbcore)
- [Publishing the web map](#publishing-the-web-map)
- [Stopping a capture and going on later](#stopping-a-capture-and-going-on-later)

**When something fails**

- ["Start the resource" does not start it](#start-the-resource-does-not-start-it)
- [The game's console cannot be reached](#the-games-console-cannot-be-reached)
- [The test shot has red frames](#the-test-shot-has-red-frames)
- [The capture stopped midway and the character is invisible or cannot move](#the-capture-stopped-midway-and-the-character-is-invisible-or-cannot-move)
- [A notification is in a photograph](#a-notification-is-in-a-photograph)
- [The encryption keys are not found](#the-encryption-keys-are-not-found)
- [The road editor does not open](#the-road-editor-does-not-open)
- [The in-game map does not change](#the-in-game-map-does-not-change)
- [Inside buildings the radar cannot be read and no floor plan shows (Cayo Perico resources)](#inside-buildings-the-radar-cannot-be-read-and-no-floor-plan-shows-cayo-perico-resources)
- [Text on the map shows as boxes or in another font](#text-on-the-map-shows-as-boxes-or-in-another-font)

**About what the maps contain**

- [How far the range goes; the aircraft carrier is sea](#how-far-the-range-goes-the-aircraft-carrier-is-sea)
- [What the roads are drawn from](#what-the-roads-are-drawn-from)
- [Roads the game does not have are drawn, or roads it has are missing](#roads-the-game-does-not-have-are-drawn-or-roads-it-has-are-missing)
- [A road over the sea](#a-road-over-the-sea)
- [A road data file of the server could not be read](#a-road-data-file-of-the-server-could-not-be-read)
- [The sea is see-through on the pause map](#the-sea-is-see-through-on-the-pause-map)

**Other**

- [Where the settings and the logs are](#where-the-settings-and-the-logs-are)

---

## How to

### Updating the maps after the server changed

After adding an MLO or editing terrain, only the changed places need capturing again.

1. On **FiveM setup & check**, prepare the capture (the graphics settings, FiveM joined to the server, **Start the
   resource**, **Check with the game**), as for the first map.
2. On the **Plan & progress** map, mark the blocks of the changed places for retake (**Retake by rectangle**, **Retake
   blocks**; **Clear all retakes** takes every mark off).
3. Press **Run**. The game captures and scans only the marked blocks, and the steps after it make again only what
   those blocks change. The job list says how long it takes before you start.
4. **Map view** → **Compare with before** lays the maps as they were at the last export over the new ones, outlines the
   changed tiles and jumps to the changed places.
5. Export on **Export**. Write the minimap resource under a new name (the name offered will do) and replace the earlier
   resource on the server with it.

When you added or changed a resource that ships road data (`.ynd`), bring **Server resource folders** on **Project
settings** up to date too. Retake the blocks whose roads changed as well, so that the scan and the road shapes agree.

### Cayo Perico, Roxwood and other land outside the standard map

Islands and land that reach outside GTA V's standard map (x −4,140 to 4,860 m, y −5,100 to 8,400 m) can be mapped too.
Capturing them needs the server to load that map.

1. **Project settings** → **Outside the standard map**: turn on **Include areas outside the standard map** and choose
   the cells to add at the top, bottom, left and right (a cell is 2,250 m; up to 2 cells top and bottom together, and 4
   left and right together). Cayo Perico takes a cell below and one to the right, Roxwood a cell above.
2. On the **Plan & progress** map, add the blocks there to the range. **Add a preset** adds the blocks of Cayo Perico
   or Roxwood at once, and asks before widening the map when it is too small. Roxwood's blocks are an estimate from the
   land drawn in RRixxles' Roxwood minimap.
3. When the server loads the Cayo Perico island, also tick **Read the roads of Cayo Perico and replace the game's island
   map** on **Project settings**: the atlas and road maps use the island's road data where it lies, the island's zone
   name (CAYO PERICO) is placed on the map, and the exported minimap resource replaces the game's island map.
4. Run and export. The job list shows the capture time of the added blocks.
5. When you put the minimap resource on the server, remove, besides postal-code-map and other minimaps and an Extra
   Map Tiles resource you already run, the island map resource as well (CayoPericoMinimap, for example). **Keep the
   resource that loads the Cayo Perico island.** The minimap resource does not load the island. How to install it is in
   the resource's own `README.txt`. Some resources that load the island keep the maps of building interiors from
   showing ([Inside buildings the radar cannot be read and no floor plan shows](#inside-buildings-the-radar-cannot-be-read-and-no-floor-plan-shows-cayo-perico-resources)).

Good to know:

- A project with cells added above or to the left numbers its exported web tiles from the north-west corner of the
  added cells; the exported viewer and the lb-phone example are written with those numbers.
- The grid of the game's road data ends at y 8,192 m in the north. Where cells are added above, the part north of it
  (Roxwood's northern edge, for example) gets no roads on the atlas and road maps.
- A project that reads Cayo Perico's roads makes the island map (one that draws nothing) from this PC's GTA V files
  when it exports the minimap resource.

### Japanese maps

The atlas map's text (zone names, street names, the labels of POI) is drawn in English. To make Japanese maps as well:

1. **Project settings** → **Labels (atlas)** → **Map languages**: choose **Japanese**.
2. If a font listed in the same section shows ✗, install it on the PC (the Japanese fonts come with Windows' optional
   feature "Japanese Supplemental Fonts"). A font that is not installed is replaced by the default font.
3. Run on **Plan & progress**. Each output style then makes an English and a Japanese map. The game is not needed.

They are exported into `tiles/atlas-<style>/` and `tiles/atlas-<style>-ja/`. For a Japanese in-game map, choose the
Japanese map under **Minimap** → **Map** on **Plan & progress**.

A project in English only shows no Japanese fields (POI labels, street names) on the screens.

### Adding another kind of map later

Add it under **Output maps** on **Plan & progress** and run. Where data is missing, the game takes it for those blocks
only. The time in the game, for the whole map:

| Maps you have, and their height quality | Map to add | Extra time |
|---|---|---|
| Satellite map only, Speed | Atlas and road map | about 120 min |
| Satellite map only, Balance or Quality | Atlas and road map | about 30 min |
| Atlas and road map only, Balance | Satellite map | about 20 min (about 40 min when going to Quality) |
| Atlas and road map only, Quality | Satellite map | about 20 min |

Adding another atlas style does not need the game.

### Drawing the roads and bridges of an MLO

Roads are drawn from the game's road data (path nodes, `.ynd`). A road or bridge of an MLO that ships no road data is
drawn as ground and buildings, not as a road. Draw it in **Map editing** → **Roads**.

1. On the left map, pick the Draw tool (key 1) and click along the road to place nodes. Near an existing node, the road
   joins that node. Esc or a right click ends it.
2. Under **Selection**, set the lanes, the width, the street name and the unpaved and tunnel flags.
3. Check the road as it will be drawn on the right map.
4. Press **Save** and run on **Plan & progress**. The game is not needed.

These edits count for this tool's maps only. The game's traffic and GPS do not see them.

### Editing a map in Photoshop or GIMP (PSD)

A map you made can be written as a PSD file split into layers, worked on in Photoshop or GIMP, and then turned into the
in-game map and the web map. Every layer is a picture. To keep the roads as shapes and the text as text, see
[the SVG steps](#editing-a-map-in-inkscape-or-illustrator-svg).

1. Under **Editable files** on **Export**, tick the map to edit and **PSD (Photoshop, GIMP)** under **Format**, and
   press **Export**. The folder `editable` of the export folder gets `<map>-z<zoom level>.psd` (for example
   `atlas-postalcodemap-z6.psd`).
   - **Zoom level** is z6 at first: the pixels of the in-game map (8,192 × 12,288 px for a map of the standard size).
   - Choose z7 when the web map should be sharp up to z7. It has four times the pixels, and the paint program needs
     four times the memory (about 1.6 GB a layer).
2. Open the file in Photoshop or GIMP and edit it. The map is split into layers: the ground, the shading, water,
   buildings, roads, postal codes, points of interest, zone names, street names and more. You can add layers and draw
   on them, hide layers and change colors.
   - Save the edited file under another name: exporting again writes over a file of the same name.
3. Write the whole picture out as one PNG picture. Keep the size it had when you opened it, and write it without
   interlacing.
4. Under **Convert an edited picture** on **Export**, give that PNG as the **Picture file**. Check that
   **Original map** is the map you edited, choose the **Output**, and press **Convert**.
5. The export folder then holds the following. They are put in place as what **Export** writes
   ([Your first map](walkthrough.md#10-put-them-in-place)).
   - `web-edited/<map>/`: the web map, with the contents of the folder `web` (the viewer, the tiles, the lb-phone
     example).
   - `fxmapgen-minimap-<date>`: the resource of the in-game map. On the server, it replaces the resource before it.

Good to know:

- The edited picture does not become one of the project's maps. It does not show on **Map view**.
- Making the maps again does not change the edited picture (after adding an MLO and retaking, for example). To have
  the same edits on the new map, export the editable file again and edit it again.
- The picture's size follows the map frame. Changing the cells added under **Outside the standard map** changes the
  size needed. A picture of another size is refused, with the sizes the map takes.
- The in-game map is made with the pixels of z6 (a z7 picture is made smaller for it). The web tiles go from z0 to the
  picture's zoom level.
- With **Blocks not taken** → **Leave transparent** under **Minimap** on **Plan & progress**, what you drew on blocks
  outside the range does not show on the in-game map (it does show on the web tiles).
- GIMP opens the ground and the two shading layers as one group.
- Opened and checked with GIMP 3.2, not with Photoshop.

### Editing a map in Inkscape or Illustrator (SVG)

A map you made can be written as an SVG file split into layers, worked on in Inkscape or Illustrator, and then turned
into the in-game map and the web map. The roads stay shapes, and the postal codes, points of interest, zone names and
street names stay text. To edit every layer as a picture, see
[the PSD steps](#editing-a-map-in-photoshop-or-gimp-psd).

1. Under **Editable files** on **Export**, tick the map to edit and **SVG (Inkscape, Illustrator)** under **Format**,
   and press **Export**. The folder `editable` of the export folder gets two things:
   - `<map>-z<zoom level>.svg` (for example `atlas-postalcodemap-z6.svg`)
   - the folder `<map>-z<zoom level>-svg`: the PNG pictures the SVG file loads. Move this folder together with the SVG
     file.

   **Zoom level** is z6 at first: the pixels of the in-game map (8,192 × 12,288 px for a map of the standard size).
   Choose z7 when the web map should be sharp up to z7. It has four times the pixels, and the program needs four times
   the memory (about 1.6 GB a picture layer).
2. Open the file in Inkscape or Illustrator and edit it.
   - Every road is a shape of its own. The roads are in groups by kind (roads, highways, unpaved tracks, tunnels and
     more).
   - A road is two shapes, its casing and the road itself (in the groups "Road casings" and "Roads", for example).
     Select both to move or delete it.
   - The postal codes, points of interest, zone names and street names are text. You can move them, delete them, type
     them anew and change their color.
   - The layers for the ground, the shading, the tree canopy, water, buildings, contours and the railway are pictures.
   - Text is shown with the fonts installed on the PC. Without the fonts the map's style uses, the letters look
     different.
   - Save the edited file under another name: exporting again writes over the file and the folder of the same name.
3. Write the whole page out as one PNG picture. In the SVG file one pixel is one unit. In Inkscape, the whole page
   written at 96 dpi has the pixels the file started with. Write it without interlacing.
4. Under **Convert an edited picture** on **Export**, give that PNG as the **Picture file**. Check that
   **Original map** is the map you edited, choose the **Output**, and press **Convert**.
5. The export folder then holds the following. They are put in place as what **Export** writes
   ([Your first map](walkthrough.md#10-put-them-in-place)).
   - `web-edited/<map>/`: the web map, with the contents of the folder `web` (the viewer, the tiles, the lb-phone
     example).
   - `fxmapgen-minimap-<date>`: the resource of the in-game map. On the server, it replaces the resource before it.

Good to know:

- The edited picture does not become one of the project's maps. It does not show on **Map view**.
- Making the maps again does not change the edited picture (after adding an MLO and retaking, for example). To have
  the same edits on the new map, export the editable file again and edit it again.
- The picture's size follows the map frame. Changing the cells added under **Outside the standard map** changes the
  size needed. A picture of another size is refused, with the sizes the map takes.
- The in-game map is made with the pixels of z6 (a z7 picture is made smaller for it). The web tiles go from z0 to the
  picture's zoom level.
- With **Blocks not taken** → **Leave transparent** under **Minimap** on **Plan & progress**, what you drew on blocks
  outside the range does not show on the in-game map (it does show on the web tiles).
- The edges of roads and text are drawn by Inkscape or Illustrator, so they differ slightly from the map this tool
  draws.
- With a style whose water is see-through (the bundled "PostalCodeMap style" and "Regional colors" among them), the
  shading of the one or two pixels along the shores is inside the ground layer. It stays there when the shading layers
  are hidden.
- Opened and checked with Inkscape 1.4, not with Illustrator.

### A server that ships road data of its own

When MLO resources or road resources carry road data (`.ynd`), add their folders, zips or ynd files under **Server
resource folders** on **Project settings**, in the order the server starts them. Where two cover the same area, the
later one wins. The maps' roads are then drawn from that road data.

If the server is somewhere else, copy those resources to this PC and give the place of the copies.

### A server that is somewhere else

The captures and scans run in the game on this PC, so it does not matter where the server is.

- Take the capture resource with **Download as zip** on **FiveM setup & check**, unpack it into the server's
  `resources`, and tick **Put on the server**.
- **Start the resource** sends commands to the server through the game's console. If you may not send server commands,
  write `ensure fxmapgen-capture` into `server.cfg` and restart the server.
- Upload the exported minimap resource, the whole folder, into the server's `resources`.

### A server that is neither Qbox nor QBCore

The server preset decides, among other things, the list of resources stopped during capture and scan. The tool has been
tried on Qbox and QBCore servers.

On a server with another framework, choose either and change **Resources stopped during capture and scan** on
**Project settings** to your server's HUD, notification and chat resources. The test shot of **Check with the game**
shows whether anything on the screen is left.

### Publishing the web map

Put the contents of the exported `web` folder on a static web host as they are. The tile address is
`<host>/tiles/<map>/{z}/{x}/{y}.png`.

- Some hosts limit the files of one site (Cloudflare Pages: 20,000 on the free plan). The whole satellite map is 32,769
  files up to zoom 8, 8,193 up to zoom 7 and 2,049 up to zoom 6; choose with **Maximum zoom** on **Export**. The viewer
  and lb-phone stretch the tiles beyond the maximum zoom.
- The tile grid is the one of the loaf-scripts map tiles (a zoom-8 tile is 70.3125 m), so the tiles line up with maps
  made for them. A project with cells added above or to the left has other tile numbers and does not line up.
- For lb-phone, fill in **Address the tiles will be served from** on **Export** before exporting, and add the contents
  of `web/lb-phone.lua` to `Config.CustomMaps` in lb-phone's `config/config.lua`.
- Keep `CREDITS.txt` with the map.

### Stopping a capture and going on later

**Stop after current units** on **Plan & progress** stops once the block being worked on is finished. What is done is
kept, and the next **Run** goes on from there. **Stop now** interrupts the block being worked on (it is done again in
the next run).

After a stop midway, the app does not stop the capture resource (so that you can go on at once). You may close FiveM.
To go on, join the server with FiveM and press **Run**. If **Prerequisites not met** lists something, follow the link
in its row.

---

## When something fails

### "Start the resource" does not start it

What to do for each reason the screen gives:

- **The server has no fxmapgen-capture**: check that the folder is still named `fxmapgen-capture` and is inside the
  server's `resources` (a category folder such as `resources/[fxmapgen]/` is fine).
- **The server refused the command**, **The server said nothing**: you may not send server commands. Write
  `ensure fxmapgen-capture` into `server.cfg` and restart the server.
- **Another version**: after updating the app, the resource has to be the same version. Put it on the server again
  with **Resource for the server**.

**Show the console's output** shows what the game's console answered.

### The game's console cannot be reached

The game's console is the FiveM client's console (the one F8 opens in the game), not the server's console. The app
drives the game through it.

- Check that FiveM is running and has joined the server.
- Only one program can use the game's console at a time. Close other tools that use it (FxDeck, for example).
- The address is **The game's console** on **Project settings**, normally left at `127.0.0.1:29200`.

### The test shot has red frames

Red frames are things on the screen (HUD, text, notifications) that got into the part the map uses (inside the white
lines). Add the resource that shows them to **Resources stopped during capture and scan** on **Project settings**, and
press **Check with the game** again.

Amber frames are things at the edges of the screen that the map does not use. They never reach the map and can stay.

### The capture stopped midway and the character is invisible or cannot move

If the app stopped midway and your character is still invisible or held in place, type this into the game's console
(F8):

```
fxmapgen env off
```

If it answers `already=1`, type:

```
fxmapgen safe
```

The character waits, held in place, until the floor or ground right below where it stood has loaded (up to 10
seconds), and then stands on it. If nothing is found it stays held (`safe=0`): type `fxmapgen safe` again.

### A notification is in a photograph

A notification on screen at the moment of a shot can end up in the photograph. The app finds only txAdmin's (the green
band at the bottom middle), waits and takes the shot again. A framework's notifications (QBCore's sit at the middle of
the right edge and grow to the left with the length of the text) reach the part of the picture the map uses when the
text is long.

Find such blocks on the satellite map in **Map view**, mark them for retake on the **Plan & progress** map and run
again.

### The encryption keys are not found

The atlas and the road map read the road data and the names from GTA V's files, and the export of the minimap resource
reads the list of the maps of building interiors and underground passages. That needs GTA V's installation folder and
the encryption keys of its files.

- The keys are made with [EmotePreviewer Key Tool](https://github.com/Acc-Off/EmotePreviewerKeyTool).
- With **Encryption keys folder** in **⚙ App settings** left empty, the app looks in the environment variable
  `FXMAPGEN_KEYS`, then `%LOCALAPPDATA%\FxMapGenerator\keys`, then `%LOCALAPPDATA%\EmotePreviewer\keys`. If you made the
  keys somewhere else, give that folder.
- If GTA V's installation folder is not found, give the folder that holds `GTA5.exe`.

A satellite map alone, without exporting the minimap resource, needs no keys.

### The road editor does not open

- It says it needs an atlas or a road map among the maps to make: the road editor works in a project that makes an
  atlas or a road map. Choose one under **Output maps** on **Plan & progress**.
- It says the game files are not read yet: press **Read the game files**. They can be read before the capture and scan
  are over, too.

### The in-game map does not change

- **Did you start FiveM again?** The radar keeps the pictures it read when the game started. Joining the server again
  is not enough. The pause map does change when you join again, so only the radar still shows the earlier pictures.
- **Is another minimap resource running?** Remove postal-code-map and other minimap resources, and an Extra Map Tiles
  resource you already run.
- **Did you replace the resource with one of the same name?** FiveM keeps the files of a resource of the same name on
  the players' PCs. Replace it with a resource exported under a new name.
- **Is there only one of them on the server?** Keep just one exported minimap resource on the server.

### Inside buildings the radar cannot be read and no floor plan shows (Cayo Perico resources)

The exported minimap resource changes what the radar and the pause map show by where the player is.

- Inside a building that has a map of its own in the game (shops, houses and others), they switch to that building's
  floor plan.
- In the metro, the storm drains and the road tunnels, the passages are laid over the map, at the same zoom as outside.
- Everywhere else the radar shows the map at its outdoor zoom.

On a server that runs a resource loading the Cayo Perico island, this may not happen. The IPL resource of
[The Cayo Perico Island Available for FiveM](https://forum.cfx.re/t/the-cayo-perico-island-available-for-fivem/1897446)
on the Cfx.re forum calls these two lines every frame, wherever the player is (in its `ipls/cayo_perico.lua`):

```lua
SetRadarAsExteriorThisFrame()
SetRadarAsInteriorThisFrame(`h4_fake_islandx`, vec(4700.0, -5145.0), 0, 0)
```

While they are called, no floor plan shows: inside a building the radar is only the map, enlarged so far that nothing
can be read. The underground passages show only when the minimap resource is started after the island resource. Do one
of these.

**Remove the two lines**

1. Open `ipls/cayo_perico.lua` of the resource that loads the island and put `--` in front of each of the two lines
   above.
2. Start the server again.

Buildings and underground passages then show as described above. With the resource of a project that reads Cayo
Perico's roads, the island shows as the map's own pictures and the two lines are not needed for it. With the resource of
a project that does not read them, the game's own island map, which those lines ask for, no longer shows.

**Keep the two lines and fix the radar's zoom**

1. Add this line to `server.cfg`:

   ```
   setr fxmapgen_minimap_fixed_zoom true
   ```

2. Start the server again.

The radar then shows the map at its outdoor zoom everywhere. Floor plans and underground passages do not show.

### Text on the map shows as boxes or in another font

- When a font the style uses is not installed on the PC, the default font is used (sizes and placement change). The
  fonts are listed under **Labels (atlas)** on **Project settings**. Install the font and run again.
- An English map is drawn with an English font, so Japanese written into a POI's label may show as boxes. Japanese
  labels go into the Japanese fields that appear once **Japanese** is chosen under **Map languages**
  ([Japanese maps](#japanese-maps)).

---

## About what the maps contain

### How far the range goes; the aircraft carrier is sea

The default range is set so that everything the default Qbox recipe loads is drawn. It is 1,045 blocks: the land, the
water within two blocks of it, the sea beyond that is shallower than 200 m, and the three blocks of the aircraft
carrier off the coast south-east of Los Santos. Outside the range, the maps are painted as open sea.

The carrier is not part of the game's own map: a server resource (bob74_ipl, for example) shows it. On a server
without it, those three blocks are sea.

The range can be changed on the **Plan & progress** map.

### What the roads are drawn from

Roads are drawn from the game's road data (path nodes, `.ynd`), and the road scan uses the same data (the game says
whether a point is on a road). Whether a road is paved or unpaved follows from the ground under it.

That is why a road or bridge of an MLO that ships no road data is drawn as ground and buildings, not as a road
([Drawing the roads and bridges of an MLO](#drawing-the-roads-and-bridges-of-an-mlo)).

Server resources that replace the road data of an area change what the road scan sees there as well. Retake the blocks
whose roads changed, so that the scan and the road shapes agree.

### Roads the game does not have are drawn, or roads it has are missing

The airfields' runways, some hiking trails and a number of minor roads, which the game's road data lacks, come with
the app as road edits. So do Cayo Perico's runway with a few of the island's roads (they show on a map whose range
holds the island) and fixes of some road shapes in the city.

The roads of North Yankton (the prologue's town) are in the game's road data although the town is not loaded, and
their street name would show over the sea south-east of the map. The bundled edits hide them. A server that uses North
Yankton reverts **North Yankton roads (hidden)** in the list of edits in **Map editing** → **Roads**.

A new project starts with the bundled edits. Each group can be reverted in the list of edits, and **Import bundled
edits…** takes a reverted group in again.

### A road over the sea

The game's boat routes are left out where they run mostly over water. Where an MLO turns water into land over one, it
can appear as a road (unpaved on natural ground). Hide it in **Map editing** → **Roads**.

### A road data file of the server could not be read

A road data file (`.ynd`) of a server resource that cannot be read (encrypted by the Cfx asset escrow, in GTA V
Enhanced's format, or damaged) is passed over, and its area keeps the game's own roads. The run's log and
`game/sources.json` in the work folder (`serverFileErrors`) name the file and the reason.

### The sea is see-through on the pause map

With a style whose sea is clear (the bundled "PostalCodeMap style" and "Regional colors" beyond 200 m of depth), the
pause map shows the game behind it there, and the radar the small whole map under it.

The blocks outside the range are painted as the map is outside the range (the color and opacity of the sea's deepest
band), so they are clear as well with such a style. With any other style, **Blocks not taken** → **Leave transparent**
under **Minimap** on **Plan & progress** makes them clear.

The web tiles carry the transparency too (the exported viewer puts the sea's color behind them). If you do not want it,
make your own style in **Map editing** → **Styles** and change the water's opacity.

---

## Other

### Where the settings and the logs are

- The app's settings (`settings.json`) and its log are in `%LOCALAPPDATA%\FxMapGenerator`. The bottom of the screen
  shows the place too.
- A project's data (captures, scans, tiles, the records of the runs) is in the project's work folder. The log of each
  run is in `logs/run-<date and time>/` there.
- The options at start-up (`--port`, `--data-dir`, `--gta`, `--keys`, `--no-browser`, `--app`) and the commands without
  the screens are in the [development guide](development.md).
