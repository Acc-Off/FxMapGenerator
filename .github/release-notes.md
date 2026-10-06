## FxMapGenerator

Makes satellite, atlas and road maps of your FiveM server's world as it is today, with the MLOs you installed, the terrain you changed and the islands you added, and exports them as the in-game map and as a web map. A Windows tool: it photographs the world from inside the game on your PC.

- `FxMapGenerator-<version>-win-x64.exe` — self-contained, no .NET runtime needed
- `FxMapGenerator-<version>-win-x64-slim.exe` — needs the .NET 10 Desktop Runtime and ASP.NET Core Runtime (x64)

What you need and how to start are in the [README](https://github.com/Acc-Off/FxMapGenerator#readme), and [Your first map](https://github.com/Acc-Off/FxMapGenerator/blob/main/Docs/walkthrough.md) goes step by step from the download to the maps in the game. For the atlas and the road map and for the in-game map, create the key files once with the [EmotePreviewer Key Tool](https://github.com/Acc-Off/EmotePreviewerKeyTool/releases). While capturing, every vehicle and NPC on the server is deleted: use a time when nobody else plays, or a copy of the server. No game data and no keys are included.

### 0.1.0

First release.

- Satellite map: photographs taken straight down in the game, put in place with the measured heights and joined.
- Atlas map: ground types, water depth, buildings, roads, street names, zone names and postal codes, in the bundled styles "PostalCodeMap style" and "Regional colors" or in a style of your own.
- Road map: the roads and their street names.
- In-game map: one resource that replaces the radar and the pause map with the map you made. Inside shops and houses it switches to the game's floor plans, and the metro, the storm drains and the road tunnels are laid over your map.
- Web map: 256 px tiles, a viewer that opens straight from the disk, and an example entry for lb-phone.
- Editors for the styles, for the roads (the roads and bridges of an MLO that ships no road data) and for POI (markers on the map).
- Editable files: a map written as a layered PSD or SVG file for Photoshop, GIMP, Inkscape or Illustrator, and the picture you edited there turned into the in-game map and web tiles.
- Land outside the standard map: Cayo Perico, Roxwood and others.
- After the server changed, only the blocks you mark are captured again, and the maps are made again only where they depend on them.
- Server presets for Qbox and QBCore.
