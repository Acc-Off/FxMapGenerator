# API

[日本語](api.ja.md)

The API the executable serves on 127.0.0.1. The screens use it.
How the processing works is in the [development guide](development.md).

A call whose summary does not fit one line has a heading below its table,
with the body (the request's body), the response and the errors.

## Contents

- [Events](#events)
- [Jobs](#jobs)
- [Project](#project)
- [Map view and tiles](#map-view-and-tiles)
- [Road editor](#road-editor)
- [POI](#poi)
- [Styles](#styles)
- [Style preview](#style-preview)
- [Export](#export)
- [Game](#game)
- [App settings and dialogs](#app-settings-and-dialogs)

## Events

The SSE stream `/api/events` sends these.

| Event | Contents |
|---|---|
| `job` | The whole job snapshot. At most four times a second |
| `jobLog` | Each log line |
| `gameUse` | What uses the game by hand. Whenever it changes, and when the stream connects |
| `project` | The open project. On every change, to all tabs |
| `settings` | The recent list |
| `precheck` | The pre-check (`POST /api/game/precheck`) |

For `gameUse`, see "One user of the game at a time"
in the [development guide](development.md#one-user-of-the-game-at-a-time).

## Jobs

Progress also comes as SSE on `/api/events` (`job`, `jobLog`).

| Call | Summary |
|---|---|
| `POST /api/jobs` | Starts a run |
| `GET /api/jobs/current` | The running (or last) run's snapshot. 204 when there was none |
| `POST /api/jobs/current/stop` | Stops the run |
| `PUT /api/jobs/current/workers` | Changes the workers |
| `GET /api/jobs/current/units` | The units per step |

### `POST /api/jobs`

- Body: `{ "project": "<path>", "workers": 12, "scale": [qx, qy], "recalibrate": false }`
  Only `project` is required.
- Response: 202 with the snapshot.
- 409 `RUNNING` / `BUSY`
- 409 `GAME_CHECKING` / `GAME_STARTING`: for a run with work in the game,
  while the pre-check or the start of the resource uses the game.
  No run folder is left.
- 404 / 400: for the project.

### `POST /api/jobs/current/stop`

- Body: `{ "mode": "boundary" }` or `"now"`.
- Response: the snapshot. 409 `NO_JOB`.

### `PUT /api/jobs/current/workers`

- Body: `{ "workers": 7 }`
- Response: the snapshot with new estimates. 409 `NO_JOB`.

### The snapshot

`JobSnapshot` holds these.

- The state (running, stopping, stopped, done, failed).
- The worker limit.
- The workers running and retiring.
- Per step, the units total / done / failed / interrupted, and the time left.
  While waiting: the plan.
  Once units are done: the measured mean unit time over the workers.
- The units being worked on, with their phase.
- The failures.
- The CPU share of the app.
- Free / total memory.
- The inputs in use.

### `GET /api/jobs/current/units`

Gives, per step, the units it found left and which are done, failed or interrupted.
The map colors blocks with it.

A step that is still finding its units says `preparing`.
Also while it measures what it needs first, such as the scale correction.

## Project

| Call | Summary |
|---|---|
| `GET /api/project` | The open project |
| `POST /api/project/open` | Opens a project |
| `POST /api/project/new` | Makes a project |
| `POST /api/project/close` | Closes the project |
| `PATCH /api/project` | Changes the project's values |
| `GET /api/project/blocks` | Every block of the project's frame |
| `POST /api/project/retake` | Sets retake marks, or takes them off |
| `GET /api/project/plan?workers=` | The to-do table |
| `GET /api/project/checks` | The set-up the chosen maps need (ok / not ok / cannot be checked yet) |
| `POST /api/project/postals/fetch` | Fetches the postal codes now |
| `GET /api/projects/recent`, `POST /api/projects/recent/remove` | The recent projects with their names |

### `GET /api/project`

The open project. 204 when none. It includes these.

- `ownStyles`: the project's own styles the maps can use. Id and name.
- `frame`: the project's map frame.
  - `bx0`, `by0`: its first block column and row.
    Negative when cells are added to the left or above.
  - `cols`, `rows`
  - `west`, `north`, `east`, `south`: its edges (m).
- `frameNeeded`: the fewest cells on each side
  that keep the range's blocks inside the frame.
  `top`, `bottom`, `left`, `right`.
- `rangeOutside`: the range's blocks outside the standard frame.
- `rangePresets`: the range presets, each with these.
  - `id`
  - `land`, `water`: its land and water blocks.
  - `missingLand`, `missingWater`: those not in the range yet, by their class once added.
  - `missingOutside`: of them, those outside the project's frame.
  - `needs`: the cells the frame needs on each side of the standard frame
    to hold the preset.
  - `west`, `north`, `east`, `south`: the edges of its blocks (m).

### `POST /api/project/open`

- Body: `{ "path": "<file>" }`
- Response: the project. 404 / 400.

### `POST /api/project/new`

- Body: `{ "path", "name", "workFolder", "preset", "satellite", "atlas", "roadmap", "language" }`
- Response: the project. 409 `EXISTS`.
- Its road edits are a copy of the bundled ones.
  The groups are named in `language` (the screen's).

### `PATCH /api/project`

The body holds any of these: only what is to change.

```
{
  "name", "satellite",
  "atlas": { "enabled", "styles": ["<style id>", ...], "languages": ["en", "ja"] },
  "minimapMap", "minimapOutside", "roadmap", "heightQuality",
  "parallel": 12,
  "serverPreset",
  "range": { "include": [...], "exclude": [...], "reset": true, "preset": "cayoPerico" },
  "stopResources": { "list": [...], "preset": false },
  "serverResources": [...],
  "console": { "host", "port" },
  "postals": "<address or file; \"\" = the default>",
  "extraCells": { "on": true, "top", "bottom", "left", "right" },
  "cayoPerico": true
}
```

- `atlas`: a style is a bundled style's id or one of the project's own styles'.
  English is always among the languages.
- `heightQuality`: `""` goes back to the default.
  A chosen quality that no longer goes with changed maps goes back to it too.
- `range.preset`: puts a range preset's blocks in.
  The ones already in stay as they are.
  It is applied after the other range changes.
- `stopResources`: with `preset: true` it goes back to the preset's list.
- `extraCells`: with `on: false` it adds no cells (`range.extraCells` back to null).
- Paths: paths inside the project's folder are kept relative.
- Minimap: a minimap on a map that goes is taken off.

The response is the project. The errors:

- 400 `INVALID`:
  - A style the project has no readable file of.
  - `extraCells`: numbers over the limits,
    and a frame that leaves blocks of the range outside it
    (take the blocks out of the range first).
  - `range.preset`: an unknown preset, or some of its blocks lie outside the frame
    (the same change's `extraCells` is applied first).
- 409 `LOCKED`: a running step reads the input.

### `GET /api/project/blocks`

Every block of the project's frame.
`cols` x `rows` from its north-west block `bx0`, `by0`.
The standard frame is 32 x 48 from 0, 0.

- A character or number per block, row by row:
  range, default range, data items, retake marks, satellite tiles up to date.
- The cells.
- `satelliteSea`: the color the satellite map's low-zoom tiles painted the open sea with
  (`#rrggbb`, null until they are made).
  The screens put it behind a map showing the satellite map, where it has no tiles.
  Those maps are the plan map, the map view, the road and POI editors.

### `POST /api/project/retake`

- Body: `{ "blocks": [...], "retake": true }`, or `{ "all": true }` (every mark off).
- Response: the project (the SSE event `project` too).
- 400 `INVALID`: a block outside the range.
- 409 `LOCKED`: while a step of the run that reads the range waits or runs
  (as a `PATCH` of `range`).
  Those steps are the capture and scan, ortho, road graph, landcover, region colors,
  road shapes and the satellite map's low-zoom tiles.
- 409 `BUSY`: while another run (window or command line) uses the work folder.

### `GET /api/project/plan?workers=`

- The to-do table.
- The rows a run does, and how many units each could do now.
- What each row had left when the running (or last) run of this project started
  (`atStart`: `runId`, `remaining`).

### `POST /api/project/postals/fetch`

Fetches the postal codes into the work folder now (as the labels step does).
It answers the `postals` prerequisite.
422 `POSTALS` when it cannot.

## Map view and tiles

| Call | Summary |
|---|---|
| `GET /api/project/tiles/{set}/{z}/{x}/{y}.png?p=<key>&v=<n>` | A tile of the work folder |
| `GET /api/project/before` | The maps whose tiles were written over since the last export |
| `GET /api/project/before-tiles/{map}/{z}/{x}/{y}.png` | A tile as it was before it was written over |
| `GET /api/project/point?x=&y=` | What the scans and the landcover hold at a point |

### `GET /api/project/tiles/{set}/{z}/{x}/{y}.png?p=<key>&v=<n>`

A tile of the work folder.
It is always checked again
(see "Map tiles" in the [development guide](development.md#map-tiles)).

### `GET /api/project/before`

The maps whose tiles were written over since the last export (`maps`). Per map:

- `map`
- `tiles`: the tiles kept.
- `z8`: the z8 tiles, as x, y, ...
- `faintZ8`: those whose change does not show.
- `areas`: the places they make.
  West / north / east / south m, tiles, and `faint` (none of its tiles shows the change).
  Those that show first, the largest first.

### `GET /api/project/before-tiles/{map}/{z}/{x}/{y}.png`

The tile as it was before it was written over since the last export.
Else the tile as it is (as `tiles`).

### `GET /api/project/point?x=&y=`

What the scans and the landcover hold at a point. null when not held.

`block`, `scanned`, `material`, `materialClass`, `height`, `water`, `zone`, `zoneName`, `zoneJa`,
`street`, `streetJa`, `onRoad`, `landcover`, `building`

## Road editor

| Call | Summary |
|---|---|
| `GET /api/project/road-editor` | The road editor's state |
| `GET /api/project/road-editor/paths` | The game's path data |
| `GET /api/project/road-editor/node?key=`, `.../link?from=&to=` | The values of a node or a link |
| `GET /api/project/road-editor/ground?x=&y=` | The ground scan at a point |
| `GET /api/project/road-editor/bundled` | The bundled road edits |
| `PUT /api/project/road-editor/edits` | Saves the road edits |
| `POST /api/project/road-editor/preview` | The road shapes the next run would make with the edits |
| `POST /api/project/road-editor/game-files` | Reads the game files now |
| `GET /api/project/road-editor/shapes/{map}/{z}/{x}/{y}.png?v=` | Tiles of the roads alone |

### `GET /api/project/road-editor`

The road editor's state.

- `unavailable`: `noRoadMaps`, `noGameFiles` or null.
- `file`: the project's `roadEdits`.
- `edits`: its contents.
- `problem`
- `notApplied`: `kind`, `key`, `reason`.
- `pathsVersion`
- `shapesVersion`: null means no road shapes yet.
- `shapesLeft`
- `maps`: the maps whose colors the road tiles take.

### `GET /api/project/road-editor/paths`

The game's path data as columns
(see "Road editor" in the [development guide](development.md#road-editor)).
409 `NO_GAME_FILES`.

### `GET /api/project/road-editor/node?key=`, `.../link?from=&to=`

Every value of a node, or of a link's records, as the path data holds them.
404 when there is none.

### `GET /api/project/road-editor/ground?x=&y=`

The ground scan at a point:
`block`, `scanned`, `material`, `materialClass`, `height`, `water`

### `GET /api/project/road-editor/bundled`

The bundled road edits, in the file's form. The screen takes groups of them in.

### `PUT /api/project/road-editor/edits`

- Body: the road edits file's contents.
- Response: saves them, and gives the editor's state.
- A project without an edits file: the first save makes one and names it in the project.
- 400 `INVALID`: with every problem.
- 409 `LOCKED`

### `POST /api/project/road-editor/preview`

- Body: the road edits file's contents.
- Response: `{ "id", "seconds", "provisional" }`.
  The road shapes the next run would make with them.
  `provisional`: there are blocks without landcover yet.
- 409 `SUPERSEDED`: a newer request.
- 409 `NO_GAME_FILES`

### `POST /api/project/road-editor/game-files`

Reads the game files now
(see "Game files" in the [development guide](development.md#game-files)).

- Response: `{ "changed", "pathsChanged", "areas", "nodes", "links", "seconds" }`
- 409 `LOCKED`: the game files step or a map data step is running.
- 409 `GAME_FILES`: why they cannot be read.

### `GET /api/project/road-editor/shapes/{map}/{z}/{x}/{y}.png?v=`

The roads alone, in the map's colors.
`v` is the road shapes' version, or a preview's id.
404 without road shapes.

## POI

| Call | Summary |
|---|---|
| `GET /api/poi/icons` | The MDI icons (`data/mdi-icons.json` as it is) |
| `GET /api/project/poi` | The POI screen's start |
| `PUT /api/project/poi?language=` | Saves the POI as edited |
| `POST /api/project/poi/sample` | A PNG of a POI style drawn |
| `GET /api/project/poi/images?path=` | A PNG a POI style names |
| `POST /api/project/poi/images?name=` | Takes in a PNG for a POI style |
| `POST /api/project/poi/import` | Reads points from a CSV or JSON |

### `GET /api/project/poi`

The POI screen's start.

- `folder`, `stylesFile`: as the project names them. null: none.
- `set`: `folders`, `groups` with `points`, the project's `styles`.
  null when the files cannot be read.
- `bundledStyles`, `bundledGroups`
- `fonts`, `background`: of the first atlas style. `fonts` is by language.
- `problem`

### `PUT /api/project/poi?language=`

- Body: the whole `set` as edited.
- Response: the same as `GET /api/project/poi`.
- When first needed, it makes `poi/` and `poi-styles.json` beside the project file.
  `poi/` is made from the bundled groups, named in `language` (the screen's).
- 400 `INVALID`: with every reason. Nothing is written.
- 409 `LOCKED`: while a running step reads the points.

### `POST /api/project/poi/sample`

- Body: `{ "style", "label", "language", "zoom" }`
- Response: a PNG of the style drawn as the maps draw it (zoom 8, 7 or 6),
  with the point's label in the language.
- 400 `INVALID`

### `GET /api/project/poi/images?path=`

A PNG a POI style names (in the POI styles file's folder).

### `POST /api/project/poi/images?name=`

- Body: the PNG's bytes.
- Response: `{ "image" }`. Its path as a style names it.
- The PNG is kept in `poi-icons/` beside the POI styles file.
  The same picture keeps its name, another gets `-2`...
- 400 `INVALID` when it is not a PNG.

### `POST /api/project/poi/import`

- Body: `{ "name", "text" }`. A CSV when the name ends in `.csv`, else JSON.
- Response: `{ "points", "skipped": [{ "line", "reasons": [{ "code", "value" }] }], "name" }`

## Styles

| Call | Summary |
|---|---|
| `GET /api/styles/schema` | The style editor's table (`data/style-schema.json` as it is) |
| `GET /api/styles/shared` | The shared styles (`id`, `name`, `base`, `bundled`, `maps`, `problem`) |
| `GET /api/fonts` | The font families of this PC, in name order |
| `GET /api/project/styles` | The list of styles |
| `GET /api/project/styles/{id}` | A style's values |
| `PUT /api/project/styles/{id}` | Saves a style |
| `POST /api/project/styles` | Makes a style from another |
| `POST /api/project/styles/import?id=&language=` | Reads a style file in |
| `PUT /api/project/styles/{id}/name` | Renames a style |
| `DELETE /api/project/styles/{id}` | Deletes a style |
| `POST /api/project/styles/{id}/shared?overwrite=` | Copies a style into the shared styles |
| `GET /api/project/styles/{id}/file` | The style's file as it is |

### `GET /api/project/styles`

- `folder`: the project's `styles`.
- `styles`: the bundled atlas styles, then the project's.
  - `id`, `name`
  - `base`: null when bundled.
  - `bundled`
  - `maps`: the maps that use it.
  - `problem`: why a file cannot be read.
- `zoneNames`: the game files' zone names. Code -> `en`, `ja`.
  Empty before they are read.

### `GET /api/project/styles/{id}`

The style's values. 404 when there is none.

- `values`: put together.
- `changes`: the values changed from its base. Empty when bundled.
- `baseValues`: its base's. null when bundled.
- `maps` and the rest.

### `PUT /api/project/styles/{id}`

- Body: the whole style (the `values` the screen edited).
- Response: the style saved. Its file holds what differs from its base.
- 400 `INVALID`: with every problem. Nothing is written.
- 409 `LOCKED`: a running step reads a map drawn with it.
- 404: a bundled style too.

### `POST /api/project/styles`

- Body: `{ "from", "shared", "id", "name" }`
- Response: a style made from another
  (bundled, the project's, a shared one with `shared`).
- The first style makes the `styles` folder and names it in the project.
- 400 `INVALID`: the id's form, no name.
- 409 `ID_TAKEN`
- 404

### `POST /api/project/styles/import?id=&language=`

- Body: a style file.
- Response: the style read in.
- With `id`, it is read in under another id.
- A whole style's name in English and Japanese is taken in `language`.
- 400 `INVALID`: with every problem.
- 409 `ID_TAKEN`

### `PUT /api/project/styles/{id}/name`

- Body: `{ "name" }`
- Response: the style with its new name. Its id stays.

### `DELETE /api/project/styles/{id}`

- Response: the list of styles.
- 409 `IN_USE`: a map of the project uses it.
- 404: a bundled style too.

### `POST /api/project/styles/{id}/shared?overwrite=`

Copies it into the shared styles and answers them.
409 `EXISTS` for a shared style of that id (`overwrite=true` replaces it).

### `GET /api/project/styles/{id}/file`

The style's file as it is, to save. For a bundled style: its bundled file.

## Style preview

| Call | Summary |
|---|---|
| `GET /api/styles/sample` | The sample land's state |
| `POST /api/styles/sample` | Starts making the sample land |
| `GET /api/styles/sample/overview.png` | A small picture of the whole sample land |
| `GET /api/project/styles/preview` | The state of the preview on the open project's data |
| `PUT /api/project/styles/preview/places` | Saves the list of the project's places |
| `POST /api/project/styles/preview` | Draws a window of the sample land or of the project |
| `GET /api/project/styles/preview/tiles/{id}/{x}/{y}.png` | A z8 tile of a window drawn |
| `POST /api/project/styles/preview/pick` | Where a point's color comes from |

### `GET /api/styles/sample`

The sample land's state.

- `state`: `none`, `making`, `ready`, `failed`.
- `progress`: 0 to 1 while made.
- `message`: why it failed.
- `frame`: the land's frame. West, north, east, south, m.
- `place`: the middle of the window first shown.
- `sizes`: the windows' sides offered, m.

### `POST /api/styles/sample`

Starts making the sample land, and answers the sample land's state.
Nothing when it is made or being made.
It uses the open project's `parallel`, else half the CPUs.

### `GET /api/styles/sample/overview.png`

A small picture of the whole sample land.
Its PostalCodeMap ground picture, 600 px wide.
404 before it is made.

### `GET /api/project/styles/preview`

The state of the preview on the open project's data.

- `state`: `none`, `ready`.
- `waiting`: what the project lacks while none
  (the road shapes, the zones, the landcover).
- `frame`: the frame of the blocks it can draw. West, north, east, south, m.
- `blocks`: a character per block of the project's frame,
  row by row from its north-west corner.
  `1` drawn, `0` not.
- `map`: that frame, as `frame` of `GET /api/project`.
- `recommended`, `places`: the recommended places and the project's.
  `name`, `x`, `y`, `ready`.
- `sizes`

### `PUT /api/project/styles/preview/places`

- Body: `[{ "name", "x", "y" }, ...]`. The whole list of the project's places.
- Response: the project's `previewPlaces` and the preview's state.
- Names are trimmed, the middle is rounded to metres. None = null.
- 400 `INVALID` for a place without a name.

### `POST /api/project/styles/preview`

Draws a window of the sample land or of the project.

Body: `{ "values", "x", "y", "size", "language", "side", "source" }`

- `values`: the whole style.
- `x`, `y`: the window's middle (m).
- `size`: the window's side. One of `sizes` (the nearest is taken).
- `language`: the labels' language. `en` or `ja`.
- `source`: what is drawn. `sample` (the default) or `project`.
- `side`: the screen's side. `left` or `right`.

Response: `{ "id", "bounds", "seconds" }`

- `id`: the id its tiles are read by.
- `bounds`: the window's edges. West, north, east, south.
- `seconds`: how long the drawing took.

It draws on the open project's `parallel`.

Errors:

- 400 `INVALID`: every problem of the style.
- 409 `NOT_READY`: the sample land is not made yet.
  With `project`: the project has no road shapes, zones or landcover yet.
- 409 `NO_PROJECT`: with `project`, no project is open.
- 409 `SUPERSEDED`: a newer request for the same side.
- 409 `PREVIEW`: the window cannot be drawn.

### `GET /api/project/styles/preview/tiles/{id}/{x}/{y}.png`

A z8 tile of a window drawn. 404 without it.

- The two newest drawings of each side are kept.
- An id is never used again, so `Cache-Control: private, max-age=3600`.

### `POST /api/project/styles/preview/pick`

Where a point's color comes from.

Body: `{ "values", "x", "y", "cx", "cy", "size", "language", "source" }`

- `values`: the whole style.
- `x`, `y`: the point (m).
- `cx`, `cy`, `size`: the window's middle and side.
- `language`: the labels' language.
- `source`: what is drawn.

Response:

- `color`
- `steps`: from the top down to the one covering it whole.
  - `kind`, `color`
  - `cover`: the part of the pixel.
  - `factor`: the shading.
  - `class`: the road class.
  - `paint`, `band`, `bed`, `text`
  - `rule`: a building color's rule. `zone`, `region`, `default`.
  - `region`
- `point`: `zone`, `zoneEn`, `zoneJa`, `ground`, `water`, `building`.
- `ground`: what the ground picture mixes.
  - `kind`: `ground`, `region`, `tone`, `trees`.
  - `id`, `color`, `share`

Errors:

- 400 `INVALID`
- 409 `NOT_READY`, `NO_PROJECT`
- 409 `PREVIEW`: outside the range's blocks.

## Export

How it works is under "Minimap and export"
in the [development guide](development.md#minimap-and-export).

| Call | Summary |
|---|---|
| `GET /api/project/export` | What can be exported, and the saved choices |
| `POST /api/project/export/check` | Checks choices |
| `POST /api/project/export` | Starts the export run |
| `POST /api/project/export/show` | Opens the folder in Explorer |
| `POST /api/project/convert/check` | Checks the choices of the conversion of an edited picture |
| `POST /api/project/convert` | Starts the conversion run of an edited picture |

### `GET /api/project/export`

- The maps with tiles: count, size, zooms, count and size per zoom, up to date.
- The minimap textures made, of all (`made`, `total`).
  The sheets, the small whole map, `extra` outside the standard frame.
- The island map (`islandMap`, `islandLandMissing`).
- When this PC cannot read what the minimap resource takes from the game's files
  (the list of interior maps, the island map): `game` = `gta` / `keys`.
- The maps that can be written as editable files (`editable.maps`: `map`, `ready`, `upToDate`),
  and the blocks of the frame's sides (`blocksX`, `blocksY`: a picture is 256 px a block at zoom 6).
- The saved choices, with their resource name.
  The maps of the editable files are never among them (`editableMaps` is empty).
- Why they cannot be exported.
- The output folder's record.
- The edited picture last converted (`picture`: `file`, `map`).
  `file` is where the picture is. `map` is the id of the original map.

### `POST /api/project/export/check`

- Body: `{ "folder", "maps": [...], "zip", "baseUrl", "maxZoom", "minimap", "resourceName" }`
  (any).
- For the editable files: `"editableMaps": [...]` (the maps to write; none without it),
  `"editableZoom"` (6 or 7), `"editableFormats"` (a list of `psd`, `svg`), `"language"` (`ja` or `en`: the layers' names).
  - Without `editableFormats`, the saved formats are used.
  - Maps chosen with an empty list of formats cannot be exported (`NO_EDITABLE_FORMAT`).
- Response: the same as `GET /api/project/export`, for these choices.

### `POST /api/project/export`

- Body: the same as for `POST /api/project/export/check`.
- It keeps the choices in the project and starts the export run.
  The response is the snapshot.
- Of the editable files it keeps the zoom level and the formats, not the maps.
- The run's steps are `export`, and with editable files `export.layers` and `export.files`.
- 400 `EXPORT_<reason>` when it cannot be written.

### `POST /api/project/export/show`

- Body: `{ "path" }`
- Opens the folder in Explorer.

### `POST /api/project/convert/check`

- Body: `{ "folder", "file", "map", "tiles", "minimap", "resourceName", "baseUrl" }` (any).
  - `file`: the PNG picture.
  - `map`: the id of the original map.
    Without it: the map the picture's file name tells, else the minimap's map, else the first map.
  - `tiles`, `minimap`: whether the web tiles and the minimap resource are written. Without them: written.
  - `folder`, `baseUrl`: without them, the values the project kept from the last export.
- Response:
  - `picture`: the picture's `file`, `width`, `height`, `zoom` (6 or 7)
    and why it cannot be converted, `problem`.
    `problem` is `NOT_FOUND`, `NOT_PNG`, `INTERLACED` or `BAD_SIZE`.
    Without a picture chosen, `picture` is null.
  - `map`: the map taken.
  - `guessedMap`: the map the picture's file name tells, or null.
  - `problems`: why the conversion cannot run.
  - `resourceName`: the name the minimap resource would get.

### `POST /api/project/convert`

- Body: the same as for `POST /api/project/convert/check`.
- It keeps the picture's place and the original map in the project and starts the conversion run.
  The response is the snapshot.
- The run's steps are `convert.tiles`, `convert.textures` and `convert.files`.
  Without the minimap resource there is no `convert.textures`.
- 400 `EXPORT_<reason>` when it cannot run.

## Game

The API of the pre-check, the graphics settings and the capture resource.
How they work is under "The game: connection, pre-check, capture and scan" in the
[development guide](development.md#the-game-connection-pre-check-capture-and-scan).

| Call | Summary |
|---|---|
| `GET /api/game/precheck` | The newest pre-check |
| `POST /api/game/precheck` | Runs the pre-check |
| `GET /api/game/precheck/files/{name}` | The files of a pre-check |
| `GET /api/game/render` | The graphics settings |
| `POST /api/game/render/apply` | Puts the values for the shots in |
| `POST /api/game/render/restore` | Puts a backup back |
| `POST /api/game/resource/start` | Starts the resource |
| `GET /api/game/fivem` | Whether FiveM runs on this PC |
| `GET /api/capture-resource` | The capture resource's name and version, and the mark |
| `GET /api/capture-resource/found?folder=` | The version of the resource in that folder (null: none) |
| `POST /api/capture-resource/save` | Writes the capture resource |
| `GET /api/capture-resource/zip` | `fxmapgen-capture.zip` (one folder, `fxmapgen-capture/`, inside) |
| `POST /api/project/capture-placed` | Sets or takes away the user's mark |

### `GET /api/game/precheck`

The open project's newest pre-check. 204 when none.

It comes with `resourceStoppedUtc` (the time)
when the app stopped the capture resource after it.
The check then counts as not made.

### `POST /api/game/precheck`

- Runs the pre-check and returns it. About 15 s with the settle measurement.
- 409 `GAME_BUSY`: while a run uses the game.
- 409 `GAME_STARTING`: during a start of the resource.
- 409 `GAME_CHECKING`: during another pre-check.
- SSE `precheck`.

### `GET /api/game/precheck/files/{name}`

Its `shot.png`, `shot-marked.png`, `console.log`, `report.json`.

### `GET /api/game/render`

The graphics settings: path, present, FiveM running, difference, backups.

### `POST /api/game/render/apply`

Backs up, then puts the values in. 409 `FIVEM_RUNNING`, 404 `NO_FILE`.

### `POST /api/game/render/restore`

- Body: `{ "backup": "<name>" }`. Default: the newest.
- Puts the backup back.

### `POST /api/game/resource/start`

Starting the resource
(see "Starting the resource" in the [development guide](development.md#starting-the-resource)).

- `state`: `noConsole` / `running` / `started` / `notStarted`.
- `resource`: the name it answered with. The version comes too.
- `expected`: the executable's version.
- `console`: the lines of the game's console.
- 409 `GAME_BUSY`: while a run uses the game.
- 409 `GAME_CHECKING`: during a pre-check.
- 409 `GAME_STARTING`: during another start.

### `GET /api/game/fivem`

`{ "running" }`: whether FiveM runs on this PC.
It is for the notice. It only looks at the processes.

### `GET /api/capture-resource`

The capture resource's name and version, and the open project's mark (`placed`).

### `POST /api/capture-resource/save`

- Body: `{ "folder", "replace" }`
- Writes it (`written`) and marks the open project.
- 409 `EXISTS` when one is there and `replace` is not set.

### `POST /api/project/capture-placed`

- Body: `{ "placed" }`
- Sets or takes away the user's mark.

## App settings and dialogs

| Call | Summary |
|---|---|
| `GET /api/settings`, `PUT /api/settings` | The app settings (`settings.json`) |
| `GET /api/settings/gamefiles?gta=&keys=` | What the folders give |
| `POST /api/dialog/file` | The chosen file (the Windows dialog) |

### `GET /api/settings`, `PUT /api/settings`

The body and the response are the app settings (`settings.json`).

```
{ "recentProjects", "language", "theme", "jobListWidth", "gtaFolder", "keysFolder", "guides" }
```

### `GET /api/settings/gamefiles?gta=&keys=`

What the folders give.
See "Game files" in the [development guide](development.md#game-files).

### `POST /api/dialog/file`

- Body: `{ "save": true, "initial", "title", "kind" }`
- Response: the chosen file.
- Without `kind`, a project file is chosen.
  With `"kind": "roadData"`, a server's road data (zip, ynd).
  With `"kind": "picture"`, a PNG picture.
