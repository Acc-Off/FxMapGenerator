# Development

[日本語](development.ja.md)

The layout of the source, the build, the commands, and how the processing works.
The API is in [api.md](api.md), the file formats are in [spec/](spec/) (in Japanese).

## Contents

Getting started:

- [Requirements](#requirements)
- [Layout](#layout)
- [Build, test, run](#build-test-run)
- [Command line without the UI](#command-line-without-the-ui)

How a run works, and the steps:

- [Jobs](#jobs)
- [Game files](#game-files)
- [Grids and scans](#grids-and-scans)
- [Road graph](#road-graph)
- [Landcover](#landcover)
- [Roads](#roads)
- [Road editor](#road-editor)
- [Zones and region colors](#zones-and-region-colors)
- [Cell map data](#cell-map-data)
- [Points of interest](#points-of-interest)
- [Labels](#labels)
- [Cell drawing](#cell-drawing)
- [Minimap and export](#minimap-and-export)

The game:

- [The FiveM resource](#the-fivem-resource)
- [The game: connection, pre-check, capture and scan](#the-game-connection-pre-check-capture-and-scan)

The screens, and the rest:

- [Screens and the open project](#screens-and-the-open-project)
- [Hover help](#hover-help)
- [Guides](#guides)
- [Single-file executables](#single-file-executables)
- [Style editor](#style-editor)
- [The style editor's sample land](#the-style-editors-sample-land)
- [The style editor's preview](#the-style-editors-preview)

## Requirements

- .NET SDK 10. `global.json` pins the 10.0 feature band.
- Node.js 22 or later. The UI is built by the App project's `BuildWeb` target.
- Lua 5.4 (`lua` and `luac`), used by the two tests below.
  Without Lua, `dotnet test` skips both.
  - The FiveM resource's syntax check and tests.
  - The test of the scripts of the exported minimap resource.

## Layout

- `src/FxMapGenerator.App`: the executable.
  Kestrel on 127.0.0.1, the command line, settings, jobs, the game connection.
- `src/FxMapGenerator.Core`: the processing itself. No Windows dependency.
  Processing steps, grids, vectors, drawing (SkiaSharp), tiles, estimates, the project format.
- `src/FxMapGenerator.GameData`: game file formats.
  - Texture dictionaries (`.ytd`), DDS block compression.
  - The RPF archives and their keys.
  - Path nodes (`.ynd`).
  - Text tables (`.gxt2`).
  - Scaleform files (`.gfx`: making shapes draw nothing).
- `src/FxMapGenerator.Web`: the UI. Vite + React + TypeScript + Zustand, embedded into the executable.
- `tools/FxMapGenerator.DevTools`: developer commands.
  `devtools ytd-info`, `ytd-decode`, `calibrate`, `make-icon`, `mdi-icons`, `sample-land`,
  `sample-land-bundle`, `sample-island`, `preview-timings`, `gfx-empty`, `island-map`, `interior-maps`.
- `resource/fxmapgen-capture`: the FiveM resource (Lua) the executable drives through the game's console.
  See "The FiveM resource" below.
  The game's console is the FiveM client's console (the one F8 opens in the game).
  It is not the server's console.
- `data/`: bundled data.
  - The default range and the range presets (`range-presets.json`).
  - The estimate defaults (`timings.json`).
  - The server presets.
  - The material names and classes (`materials.json`, `material-classes.json`).
  - The bundled road edits (`road-edits-default.json`).
    What they hold is under "Bundled edits" below.
  - The atlas and road map styles (`styles/`).
  - The style editor's table of items (`style-schema.json`) and its sample land (`sample-island/`).
  - The POI and their styles (`poi/`, `poi-styles.json`).
  - The MDI icons of the POI styles (`mdi-icons.json`).
  - The route numbers (`routes.json`).
  - The parts of the minimap resource (`minimap/`).
  - The exported viewer (`viewer/`).
- `tests/`: xUnit tests. They need neither GTA V nor FiveM.
  - `tests/resource`: the resource's tests, run in a fake game.
  - `tests/minimap`: the exported minimap resource's scripts, run in a small fake game.
- `Docs/spec/`: the format specifications (in Japanese).
  - The lines exchanged with the resource (`link-protocol.ja.md`).
  - The project file and the work folder (`project-format.ja.md`).
  - The styles (`style-format.ja.md`).
  - The POI (`poi-format.ja.md`).
  - The path nodes (`ynd-format.ja.md`).
  - The text tables (`gxt2-format.ja.md`).
- `third_party/gta-toolkit`: vendored (MIT). See its `README-FxMapGenerator.md`.

## Build, test, run

```
dotnet build FxMapGenerator.slnx -c Release
dotnet test FxMapGenerator.slnx -c Release
dotnet run --project src/FxMapGenerator.App -c Release -- --no-browser
```

The App build runs `npm ci` (first time) and `npm run build` in `src/FxMapGenerator.Web`
whenever a UI file changed.
`-p:SkipWebBuild=true` builds without Node (the UI is then not embedded).

### Start-up options

```
FxMapGenerator [--port <n>] [--data-dir <folder>] [--gta <folder>] [--keys <folder>]
    [--no-browser] [--app] [--verbose] [--version] [--help]
```

| Argument | What it does |
|---|---|
| `--port` | The listening port. Default 20400 |
| `--data-dir` | The folder of the settings and the logs. Default `%LOCALAPPDATA%\FxMapGenerator` |
| `--gta` | The GTA V folder |
| `--keys` | The key folder |
| `--no-browser` | Does not open the browser at start-up |
| `--app` | Opens the UI as an app window instead of a tab |
| `--verbose`, `-v` | Debug logging |
| `--version` | Prints the version and the versions of the bundled libraries |
| `--help`, `-h` | Prints the list of arguments |

- `--port`: when that port is busy, a free one of the next 10 ports is used.
- `--gta`, `--keys`: they win over the app settings.
  How the folders are found is under "The GTA V and key folders" below.
- `--app`: opened with Edge's or Chrome's `--app=`.
- `--version`: see also "Single-file executables" below.

### Working on the UI with hot reload

1. Start the executable with `--no-browser`.
2. Run `npm run dev` in `src/FxMapGenerator.Web`.
3. Open http://localhost:5173. API calls are proxied to 127.0.0.1:20400.

`FXMAPGEN_WEBROOT=<folder>` makes the executable serve the UI from that folder instead of the embedded build.

## Command line without the UI

| Command | What it does |
|---|---|
| `new` | Makes a project file |
| `import` | Copies captures and scans taken earlier into the work folder |
| `plan` | Prints the to-do table and the estimated times |
| `build` | Runs what is left as a job |
| `precheck` | Runs the pre-check |
| `export` | Runs an export as a job |
| `convert` | Runs the conversion of an edited picture as a job |
| `retake` | Puts retake marks on blocks of the range, or takes them off |

### new

```
FxMapGenerator new <file.fxmapgen.json> [--name <name>]
    [--maps satellite,atlas-postalcodemap,atlas-postalcodemap-ja,roadmap,...] [--work <folder>]
```

Makes a project file.

| Argument | What it does |
|---|---|
| `--name` | The project's name. Without it, the name comes from the file name |
| `--maps` | The maps to make, separated by `,`. Without it, the satellite map alone |
| `--work` | The work folder. Without it, the folder of the project file |

What `--maps` takes:

- `satellite`: the satellite map.
- `roadmap`: the road map.
- `atlas-<style>`: an atlas. The style is the bundled `postalcodemap` or `regional`.
  Its maps are made in English.
- `atlas-<style>-ja`: Japanese maps are made too. The languages are the same for every atlas style.

### import

```
FxMapGenerator import <file.fxmapgen.json> [--capture <folder>] [--scan <folder>]
```

Copies captures and scans taken earlier into the project's work folder.
It records which data items each block has.
The source folders are only read.

| Argument | Copied | Files |
|---|---|---|
| `--capture` | The captures of that folder | `<block>.png`, `.cam.txt`, `.hmap` |
| `--scan` | The scans of that folder | `<block>.txt` |

At least one of `--capture` and `--scan` is needed.

### plan

```
FxMapGenerator plan <file.fxmapgen.json> [--workers <n> | --level full|strong|normal|light]
    [--processors <n>] [--json]
```

Prints the to-do table.
The table says what is left per step and how long it takes with the chosen workers.
How the time is worked out is under "Estimated times" below.

The workers are given in one of two ways.
Without either, the project's value is used.

- `--workers`: as a number.
- `--level`: as a share of the processors. full is 100 %, strong 70 %, normal 50 %, light 30 %.

The other arguments:

- `--processors`: the number of logical processors to count with. Without it, this PC's.
  The share of `--level` is taken of this number.
- `--json`: prints the table as JSON.

### build

```
FxMapGenerator build <file.fxmapgen.json> [--workers <n> | --level full|strong|normal|light]
    [--scale <qx>,<qy>] [--recalibrate] [--no-game] [--gta <folder>] [--keys <folder>]
```

Runs what is left as a job ("Jobs" below).
It goes in this order.

1. **Capture and scan**: done first when blocks miss a data item their maps need
   (shot, height grid, scans).
   It happens in the game ("The game: connection, pre-check, capture and scan" below).
   - With `--no-game` there is no capture and scan: the maps are made from the data at hand.
   - During the capture and scan, each block taken is orthorectified at once
     with a provisional scale correction.
2. **Satellite map**: after the capture and scan, these are made.
   - The scale correction.
     It is measured once from the overlap of neighbouring captures and kept in `state/scale.json`.
     `--recalibrate` measures again, `--scale` sets it.
     With nothing to measure, the bundled default is used.
   - The orthorectified z8 tiles.
     Of every block whose tiles are older than its shot
     or the heights its photo is placed with (`PhotoItems`).
   - The open sea outside the range.
   - The low-zoom tiles.
3. **Map data**: follows when an atlas or the road map is made, in this order (the sections below).
   Game files -> road graph -> landcover -> zones and region colors -> labels -> roads.
   The labels only when an atlas is made.
4. **Cell rendering and low-zoom tiles**: made after the map data.
   Cell map data -> cell drawing -> low-zoom tiles, in this order (the sections below).
   The cell drawing and the low-zoom tiles run once per map.
5. **Minimap**: made last, when the project has a minimap ("Minimap and export" below).

While it runs, and stopping it:

- It prints progress every 5 seconds.
- Ctrl+C once stops after the running units. Twice stops at once.
- `build` again continues from there.

### precheck

```
FxMapGenerator precheck <file.fxmapgen.json>
```

Runs the pre-check ("The game: connection, pre-check, capture and scan" below).
It prints what it found, and where the report and the test shot are.
The exit code is 0 when all is fine and 1 when there is something to fix.

### export

```
FxMapGenerator export <file.fxmapgen.json> [--out <folder>] [--maps satellite,...|none] [--zip]
    [--base-url <url>] [--max-zoom 6|7|8] [--no-minimap] [--resource-name <name>]
    [--editable <map>,...] [--editable-zoom 6|7] [--editable-format psd,svg]
    [--workers <n>] [--gta <folder>] [--keys <folder>]
```

Runs an export ("Minimap and export" below) as a job.

| Argument | What it does |
|---|---|
| `--out` | The output folder |
| `--maps` | The maps to export, separated by `,`. `none`: no map |
| `--zip` | Writes the web part as a zip |
| `--base-url` | The address the tiles will be served from. Used in the lb-phone example |
| `--max-zoom` | The maximum zoom of the web tiles |
| `--no-minimap` | Does not write the minimap resource |
| `--resource-name` | The minimap resource's name |
| `--editable` | The maps to write as editable files, separated by `,` |
| `--editable-zoom` | The zoom level of the editable files |
| `--editable-format` | The formats of the editable files: `psd`, `svg`. Separated by `,` |
| `--workers` | The workers |
| `--gta`, `--keys` | The GTA V and key folders |

- What is not given comes from the choices the project kept from the last export.
  Without those, the defaults:
  every map with tiles, `export` next to the project file, maximum zoom 8.
- The editable files are written only with `--editable`.
  `--editable-zoom` and `--editable-format` go with `--editable`. Not given, they are as last written.
- The minimap resource is written whenever the project has a minimap,
  unless `--no-minimap` is given.
- Choices that cannot be exported print why, and the exit code is 65.
- The command line does not keep its choices in the project.
- The minimap resource reads the list of interior maps from the game's files.
  For a project that reads Cayo Perico's roads, it reads the island map too.
  They come from the game of `--gta` and `--keys`, as `build`'s game files do.
  Without them, the game of the app's settings is used.

### convert

```
FxMapGenerator convert <file.fxmapgen.json> --picture <png> [--map <map>] [--out <folder>]
    [--no-tiles] [--no-minimap] [--resource-name <name>] [--base-url <url>]
    [--workers <n>] [--gta <folder>] [--keys <folder>]
```

Runs the conversion of an edited picture ("Export: converting an edited picture" below) as a job.

| Argument | What it does |
|---|---|
| `--picture` | The PNG picture to convert. Needed |
| `--map` | The id of the map the picture was made from |
| `--out` | The output folder |
| `--no-tiles` | Does not write the web tiles |
| `--no-minimap` | Does not write the minimap resource |
| `--resource-name` | The minimap resource's name |
| `--base-url` | The address the tiles will be served from. Used in the lb-phone example |
| `--workers` | The workers |
| `--gta`, `--keys` | The GTA V and key folders |

- Without `--map`, the map comes from the picture's file name.
  A name that starts with the name of an editable file (`<map>-z<zoom>`) gives that map.
  Otherwise it is the minimap's map, and without one the project's first map.
- Without `--out` and `--base-url`, the values the project kept from the last export are used.
- A picture that cannot be converted prints why, and the exit code is 65.
- The project file is not changed.
- What the minimap resource reads from the game's files
  comes from the game of `--gta` and `--keys`, as for `export`.

### retake

```
FxMapGenerator retake <file.fxmapgen.json>
    [--blocks z8_<x>_<y>,... | --rect <west>,<south>,<east>,<north> | --all] [--clear]
```

Puts retake marks on blocks of the range.
Used after a change of the server, such as an added MLO.

The capture and scan of the next `build` takes again, for the marked blocks,
every data item their maps need.
The steps after it make again what uses the data taken again.

| Argument | Blocks |
|---|---|
| `--blocks` | The blocks named. Each has to be in the range |
| `--rect` | The blocks of the range that a rectangle in game metres touches |
| `--clear` | Takes the mark off instead. `--clear --all` takes it off every block |
| None | Lists the marked blocks |

While another run uses the work folder, it does nothing and ends with exit code 65.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Done. For `precheck`: all fine |
| 1 | Units or a step failed. For `precheck`: something to fix |
| 130 | Stopped |
| 64 | Wrong arguments |
| 65 | Bad project, or the work folder is used by another run |

### Estimated times

The estimates start from the values of `data/timings.json`.
They were measured on a whole-map run; the file's `about` explains them.

An estimate is shown as a range.
The range is the sum times `spread`.
`spread` is 0.9-1.2 for the capture and scan and 0.8-1.3 for the other steps.

**Capture and scan**

Time is added per block and per item, for land and for water blocks.

| Value | What it counts |
|---|---|
| `shot`, `height` | The shot image, the height grid |
| `scanGround`, `scanRoads` | A scan right after the camera |
| `scanGroundAlone`, `scanRoadsAlone` | A scan without the camera |
| `scanCanopy` | The canopy scan |
| `cameraMove` | Once per block taken with the camera |
| `farMove` | Once per group of neighbouring blocks taken with the camera |

`farMove` is the move to a block that is not next to the last one.
The times of the preparation, the clean-up and the check once connected are added as well.

**Steps without the game**

A value is one worker's time per unit.
The units are spread over the workers to get the time.

- Ortho and landcover: per block.
- The cell map data (`cells.prepPerBlock`)
  and the cell drawing (`cells.drawPerBlock`: `atlas`, `roadMap`):
  per land and per water block of the cell. Its margin blocks count too.
  A water block draws quickly, but its map data is the heavier: the sea's depth bands.
- The minimap (`ytd.perSheet`): the sheets side by side.

The next two are steps that work in parallel inside.
Their value is one worker's time, divided by the workers.

- `lowZoom`: the low-zoom tiles. One value per kind of map.
  It is measured on the standard frame, so it grows with the frame's area.
- `ortho.scalePerBlock`: the first measurement of the scale correction.
  Per block of the range (captured by then).
  Counted only while the project has no measured correction.

With more than one worker, the blocks shot are orthorectified beside the capture and scan.
So the ortho time of those blocks is part of the capture and scan's time.

### Estimates for a partial update

For a partial update, before the steps of the whole map run again, the plan expects the following.

| Expected | Which |
|---|---|
| Landcover | The blocks taken again and their neighbours |
| Cells | Those holding the blocks taken again, their neighbours, or blocks whose road edits changed |
| Minimap textures | Those of these cells |

**When the landcover is every block**

- When the list of server resources, the frame or the Cayo Perico choice changed.
  The road data read changes the road graph.
  A ynd changed inside a listed folder is not foreseen.
- When the road edits changed.

**The blocks whose road edits changed**

Found by comparing with the per-block hashes of the edits in the road graph's record.

**The minimap textures of those cells**

- Of the six sheets, those of the cell.
- With any sheet, the small whole map.
- The textures of the cells outside the standard frame.
- With the satellite map as the minimap, all of them whenever an ortho block is left.

**Known only later**

- The cells that these changes add are known once those steps ran:
  the landcover (made again for another road graph), the road shapes, the labels, the region colors.
- The running steps are estimated from what they really have left.

## Jobs

Making the maps runs as a job, the same from the command line and from the UI.
The app runs one job at a time. It goes on when the browser is closed.
Code: `src/FxMapGenerator.Core/Jobs/`. API: [api.md](api.md#jobs).

### Steps and units

- A step (`Stage`) first finds the units left.
  A unit is a block, a cell, a map set, or one unit for the whole world.
- The job hands the units out, one by one, to free workers.
- A step done once for the whole world runs its loops through `StageContext.Parallel`.
  The calling worker works through the items itself, and free workers join as helpers.
- Steps run in order.

### Workers

- Steps without the game run with the chosen number of workers.
  It is 1 to the number of logical processors; the project keeps a default in `parallel`.
- The number can change during the run.
  - Raising it starts more units at once.
  - Lowering it lets the running units finish and starts no new one until fewer run.
    In a loop: from its next item.
- Beyond the first worker, another one only starts when the free memory is enough.
  Enough is one more unit of the step (its `MemoryPerUnit`) plus 1 GB for the rest of the PC.
  The snapshot then says at how many it holds (`memoryLimitedAt`).

### Steps in the game

- They run their units one at a time, whatever the workers.
- Beside them they may run helper work
  on up to `Stage.Helpers` workers (`StageContext.RunBesideAsync`).
  - It waits for a free worker, then runs.
  - It is listed with the units in progress.
  - It goes into `units.csv` under its own name.
  - It does not run while the number of workers is 1.
- The limit is the game's one worker plus the helpers, or the number of workers if lower.
- The capture and scan runs the provisional orthorectification of the blocks it took as helper work.

### Stopping

- At a boundary (default): running units finish, no new one starts.
- Now: running units are cancelled. The step's clean-up still runs.
- Every unit done is recorded in `state/stages.json`,
  at the latest every 2 seconds and at the end of a step.
- The next run continues with what is left.

### The run folder

Each run puts the following into `logs/run-<yyyyMMdd-HHmmss>/` in the work folder.

| File | Contents |
|---|---|
| `inputs/` | Copies of the inputs, taken at the start |
| `run.log` | The log |
| `units.csv` | Per unit: step, unit, start, seconds, result, worker limit |
| `run.json` | The last snapshot, the worker changes, the failures, the to-do table at the start |

Copied into `inputs/`:

- The project file.
- Its road edits file.
- The project's own styles its maps use (`styles/`).
- The POI folder (`poi/`) and `poi-styles.json`, with the PNG files it names.

The run reads only these copies.
So edits during the run go to the next one.

The road edits file alone is copied again as the first step reading it, the road graph, starts.
Edits saved until then go into this run.

### Locks on the inputs in use

Each step names the project inputs it reads (`StageInputs`).
While the step is not over, those inputs cannot be changed
(`JobRunner.LockedBy`, `locks` in the snapshot).

The names of the inputs:

range, frame, maps, server, the game's console address, game files, Cayo Perico, postals, languages,
style and its parts, POI, road edits, minimap.

How the inputs differ:

- Road edits: locked only from the start of the first step reading them
  to the end of the last (`JobRunner.Held`).
  They can be saved while the capture and scan or the ortho step runs.
- The frame (`frame`: the cells added around the standard frame):
  read by the steps covering the whole frame.
  Game files, region colors, road shapes, low-zoom tiles (the satellite map's too).
  It can change while the steps reading the range's blocks run
  (capture and scan, ortho, road graph, landcover).
  A new frame leaves the range's blocks as they are.
- The Cayo Perico choice (`cayoPerico`): has its own lock.
  It is read by the game files step (the island's road files)
  and the labels step (the name of the island's zone).
- The server's resource folders: stay with the game files' lock.

### One run per work folder

`state/run.lock` is held open during a run.
Another run (another app or the command line) names the holder's process id and does not start.

## Game files

In the screen's job list this step is "Game file import".

A project with an atlas or the road map runs this step
after the capture and scan and the satellite map.
`GameFilesStage`, one unit for the whole map.

### What it reads and writes

1. It indexes the GTA V archives once.
2. It reads the ynd of the areas under the project's frame: path nodes, links, junction records.
   - 494 areas for the standard frame.
   - The areas' grid ends at y 8192, so the part of a frame north of it has no areas.
   - With `cayoPerico` on, the island's road files (area number + 1024) are read
     in place of their areas' own files.
3. It reads the English and Japanese GXT2 tables.
4. It lets the server's ynd replace whole areas.
   - `gameFiles.serverResources` in order, a later one winning.
     Inside a folder: dictionary order.
   - The road scan asks the game whether a point is on a road,
     so it follows the road data the server sends as well.
     The blocks of a replaced area keep the road scan of the earlier road data
     until they are taken again.
5. It writes `game/`. The formats are in `Docs/spec/ynd-format.ja.md` and `gxt2-format.ja.md`.

| File | Contents |
|---|---|
| `paths.json` | The vehicle nodes and links |
| `names.json` | Street and zone names |
| `sources.json` | Where they came from |

### What it waits for, reading again, time

- The street and zone names the scans met are looked up in the name tables too.
  So the step waits while a block of the range has no road scan.
  A capture and scan earlier in the same run takes it.
- It reads again when the frame or `cayoPerico` differs
  from what `sources.json` was read for (`extraCells`, `cayoPerico`, `islandAreas`).
- It takes about 4 s on one worker.
  That is with the files in the disk cache; the first time takes about 13 s.
- The job list shows the step with its two parts:
  `gameFiles.paths` (the path areas) and `gameFiles.names` (the name tables).

### Reading now, outside a run

The files can also be read now, outside a run.
The ways in are `GameFilesStage.ReadNow`, "Read the game files" on the road editor,
and `POST /api/project/road-editor/game-files`.

- It writes the files the step writes, with the names of the scans there are so far.
- It is there for two things.
  - To use the road editor during a first capture and scan.
  - To read the files again after GTA V or a server's road data changed.
    The step does not notice that change.
- When `paths.json` and `names.json` come out the same bytes, nothing changes.
- Otherwise they are replaced.
  - A step that had run before counts as run now. The next run makes the road steps again.
  - A step that never ran stays to do. It runs once the scans are in.
- Refused (409 `LOCKED`) while the game files step or a map data step of a run is running.

### The GTA V and key folders

The GTA V and key folders are this PC's. A project has none.
They are found in this order (`GameFilesLocation.Resolve`).

1. The app's start arguments `--gta` and `--keys`. `build` has the same arguments.
2. The app settings: `gtaFolder` and `keysFolder` in `settings.json`.
3. What can be found.
   The search is described under "App settings" in `Docs/spec/project-format.ja.md`.

The command-line `build` reads the settings of the default data folder too.

### Prerequisites

- "GTA V installation folder, encryption keys": one line on the project settings screen.
  It is there for a project that makes an atlas or a road map, and for one with a minimap map chosen.
  It checks that the folders found in the order above hold `GTA5.exe` and the four key files.
  Reported as `gameFiles` of `GET /api/project/checks`, with these values:
  `gta`, `gtaSource`, `gtaOk`, `gtaProblem` (`notFound` / `notGta`),
  `keys`, `keysSource`, `keysMissing`.
- "Server resource folders (optional)": checks that every entry
  of the project's `gameFiles.serverResources` is there.
  With none given, it is fine. One per line.
  Reported as `serverResources`, with the values `count`, `entries`, `missing`.

### The app settings window

The folders of the settings are chosen in the app settings window.
The header's "⚙ App settings" opens it.
The project settings' "Open the app settings" opens it too.

- It shows what the folders typed give (`GET /api/settings/gamefiles?gta=&keys=`):
  the folder found, where it came from, whether `GTA5.exe` and the keys are there,
  the key folders tried and their missing files.
  `--gta` / `--keys` win and are shown as such.
- It saves them into the settings.

### Tests

- The tests never read the game's files.
  They use a small ynd written with gta-toolkit and hand-made GXT2 tables.
- The command-line `build` test has no scans, so the game files and road graph steps wait.

## Grids and scans

### The grid parts

`src/FxMapGenerator.Core/Grids/` holds the grid parts of the map data. It uses no outside library.

| Part | Contents |
|---|---|
| `Grid<T>` | The grid. Row-major |
| `Kernel` | Kernels. OpenCV's elliptic shape, rectangles, the cross |
| `Morphology` | Binary and grey dilation, erosion and opening |
| `Components` | Connected components. Numbered by their first cell in row order |
| `DistanceTransform` | The nearest cell and the distance |
| `Filters` | Window counts. Mirrored edges |
| `PointIndex` | Point neighbourhoods |
| `GridFile` | The file. Format in `Docs/spec/project-format.ja.md` |

- Cells outside the grid come in two conventions. Each caller picks one.
  Dilation is the same in both.
  - `Erode` / `Open`: do not count them (nothing erodes from the edge).
  - `ErodeEdgeOff` / `OpenEdgeOff`: treat them as off (the edge erodes).
- Of two or more equally near cells, the lower row and then the lower column is kept.
- Grey erosion and dilation with a kernel whose rows are centred runs
  (ellipses, boxes, the cross) widen each row's minimum / maximum one cell at a time.
  Width r comes from width r - 1 at the two neighbours, several cells at once.
  The 151 m elliptic opening of an 846 x 846 grid takes about 0.06 s.
- Thick polylines (`ThickLines`) set the same cells as OpenCV's `polylines`
  (8-connected, thickness 2 and up).
  Per segment, a quadrilateral half the thickness to either side
  is filled in 1/65536-cell fixed point.
  A disc goes at the first point and at every segment's end.
- All run on one thread. The steps parallelise over their units.

### Reading the scans

`src/FxMapGenerator.Core/Scan/` holds the following.

- `ScanFile`: reads the scans. Lines to grids.
- `Materials`: classifies the materials.
- `ScanArea`: looks the range's scans up by position, as one grid.

### Line and number parts

`src/FxMapGenerator.Core/Geometry/` holds line and number parts.

- `Polyline`: arc lengths, stations every 4 m, Douglas-Peucker simplification,
  cutting gentle corners.
- `Num`: calculations that give the same bits as Python 3.11 and numpy.
  `math.hypot`, `round`, `numpy.interp` / `median`, a window mean.
  They are there so that every threshold falls on the same side.
  `Math.Sqrt(x * x + y * y)` differs in the last digit for about one length in ten.

## Road graph

The step after the game files. `RoadGraphStage`, one unit for the whole map.
Code: `src/FxMapGenerator.Core/RoadGraph/`.

It turns the path links of the game files (`game/paths.json`) into map roads
(one chain per street stretch) and writes `data/roads.json`.
The format is in `Docs/spec/project-format.ja.md`.
The widths come from the on-road samples of the range's road scans.
So, like the game files, it waits for the scans.

### Applying the road edits

The project's road edits (`roadEdits`) are applied to the path links as read,
before the graph is built (`RoadEditsFile.PathsOf`).
Code: `src/FxMapGenerator.Core/RoadEdits/`. The road shapes apply them the same way.

- Added nodes and links go in after the game's.
- Nodes are moved.
- Values are changed.
- Hidden nodes (with their links) and links are taken out.

When an edit applies:

- A game node is found by its key.
  Its edit applies only while the node is still where its `original` says.
- A game link's edit applies only while the link still holds its original lanes.
- Edits that do not fit (the server's road data changed, for example) are not applied.
  They are listed with the reason in the log and the record.

### Order of work

1. **Chains** (`Chains`)
   - The vehicle nodes and links within 60 m of the range. Shortcut links are left out.
   - At every node the links are paired by the straightest continuation.
     - Through a node of two: unless it turns back.
     - At three or more: up to 35 degrees.
       Never between two different named streets.
       Never where the one-way directions do not continue.
   - The pairs are walked into chains.
   - Junction hubs (flagged, three or more links) are taken out of the line.
2. **Width and class** (`SurfaceWidth`, `RoadGraphBuilder.Classify`)
   - Width: every 4 m, at least 15 m from the ends,
     the on-road samples across the line (±25 m every 0.25 m).
     The median width of the run around the centre is taken.
   - A station on a link whose width the road edits fix takes that width instead of a measurement.
     Such links are those the edits give a width, and those they add.
     An added link is not on the road scan, so it takes the width of its lanes.
   - A road too short for 3 stations takes the width of its longest such link.
     The road keeps the result to the end.
   - Name: the one most named nodes carry.
     A node the road edits add votes for no name too.
     So a road added from a named node takes its added nodes' name.
   - Class: from the highway and offroad flags, the name, the lanes and the width.
3. **Bundles** (`Bundles`)
   - Parallel chains of one street are moved onto one midline.
     The conditions: within 20 m, nearly the same direction, heights less than 3 m apart.
   - The width is measured once across the whole bundle.
   - The shift is carried along the chain and averaged over 5 stations each way.
4. **Finishing the lines**
   - Lines are simplified (1.5 m, bundles 2.5 m). Gentle corners are cut.
   - A `street` under 120 m that meets a `major` of the same street becomes `major`.
   - Bundled roads get one width per street.
   - Groups of `minor` roads that meet the other roads at one node or none
     are parking aisles (`parking`).
   - Short fragments are dropped.

### Record, building again, time

- `data/roads-record.json` keeps the blocks it was built from, the counts
  and what the road edits did (`roadEdits`).
- It is built again in these cases.
  - The game files or a road scan are newer.
  - The range differs.
  - The road edits' contents differ from the record's (`roadEdits.digest`).
    Another file or none counts too. The same bytes saved again does not.
  - A file is gone.
- An edits file that cannot be read fails the step with every problem named.
- About 9 s on one worker for 986 blocks. 7 of them go to reading the blocks' scans.
- The loops inside (the width per chain, the bundle stations) run on `StageContext.Parallel`.
  The result does not depend on the number of workers.

## Landcover

In the screen's job list this step is "Land cover".

The step after the road graph. `LandcoverStage`, one unit per block.
Code: `src/FxMapGenerator.Core/Landcover/`.

- Reads: the ground scans of the block and its 8 neighbours, the block's road scan,
  the road graph.
- Writes: the ground classes, water, buildings and tree canopy,
  to `data/landcover/<block>.grid`.
  The format and the meaning of each array are in `Docs/spec/project-format.ja.md`.
- The heights follow the height quality
  (`SurfaceHeights.LandcoverItems`; the height quality under "The game" below).
  - Balance: the ground scan.
  - Quality: per point, the higher of the ground scan and the height grid.

### Order of work

1. **The block's surface** (`BlockSurface`)
   - The heights (as above).
     Of the ground scan: the hit or the water surface, the higher.
     Holes are filled from the nearest cell. Single precision.
   - The material classes, open water, the on-road samples, the canopy.
   - The same for the neighbours.
     `LandcoverStage` keeps five rows of blocks for the units around them.
2. **Ground classes** (`LandcoverBuilder`)
   - Material class to ground class (`GroundClasses`).
   - Sand near water becomes beach.
   - A 5 x 5 majority vote (`Majority`).
   - Small pieces are merged into their surroundings (`MergeSmall`).
   - The `DEFAULT` material is a ground class of its own, `defaultMaterial`, not urban ground.
     `DEFAULT` is terrain, rocks and props the game gives no material of their own.
   - The material `DEFAULT` says nothing of what the ground is,
     so each style chooses its paint and whether to blend it.
     The bundled PostalCodeMap style blends it with the natural ground,
     so that such patches on mountain slopes do not stand out with sharp edges.
     Paving and concrete stay urban and sharp.
3. **Terrain** (`GroundModel`)
   - Made on the 3 x 3 mosaic of blocks.
   - The natural-ground and tarmac seeds are those on large wall-bounded pieces of the surface.
   - They are grown by walking over small height steps.
     A walked piece is ground only with at least two seeds next to each other in it
     (`MinSeedGroup`).
     So a single seed on an eave does not make a roof ground.
   - Every other cell takes the height of the nearest walked cell.
4. **Buildings**
   - Pieces of roof material more than 3 m above the terrain.
     Not the ones on roads, not rock.
   - The parking rule (`GarageRule`): tarmac 6 m above the 151 m opening.
     Not what lies under the road graph's streets, major roads and highways
     drawn with their widths, and not what is surrounded by water.
5. **Pieces that are not buildings are taken off** (`FalseBuildings`)
   - Rock faces: pieces that meet the three conditions below.
     They are too steep to walk, so not ground;
     but a real building has walls or stands among paving.
     - At least half of the cells are `DEFAULT` or natural ground.
       `DEFAULT` is a hit without a material name, such as a rock prop.
     - Walls (an edge whose outside is 2.5 m or more lower) are on less than 60 % of the edges.
     - At least 90 % of the 5 m ring (not water) is natural ground or `DEFAULT`.
   - Pieces of the parking rule (joined over corners) with either of these.
     - Within 10 m, at least 15 % natural ground no more than 3 m below their median height.
       A hill, a bank, a quay's land side:
       the 151 m opening was pulled down by the slope or the sea next to it.
     - At least 20 % water. A bridge, a pier, a dam.
   - A cell of a piece taken off stays a building when a kept piece of the other rule covers it.
   - The step's log counts the cells taken off.

### What it waits for, making again, time

- A block waits in these cases.
  - While it or a neighbour in the range lacks its scans. With quality: also its height grid.
    The capture and scan takes them first.
  - While the road graph is out of date.
- It is made again in these cases.
  - An input of the block or its neighbours is newer.
  - Its file is gone.
- In these cases every block is made again.
  The step's records are cleared first. A waiting block is made once it can be.
  - The height quality asks for other heights
    than `data/landcover-record.json` holds (`scan`, `both`). Or there is no record.
  - The road graph's contents differ from the hash the record holds (`roadGraph`).
    The parking rule reads the road graph.
    A road graph made again with the same contents counts as the same.
- A record without the hash (made before it was kept)
  takes a road graph newer than a block as another one.
- 986 blocks take 29 s on 17 workers. About 0.5 s a unit; about 0.2 s on one worker.

## Roads

The step after the landcover. `RoadsStage`, one unit for the whole map.
Code: `src/FxMapGenerator.Core/Roads/`.

From the path links and street names of the game files,
and the water and surface layers of the range's landcover,
it writes the shapes to draw the roads with to `data/road-shapes.grid`.
The format is in `Docs/spec/project-format.ja.md`.

The numbers use functions that give the same bits as numpy and CPython (`Geometry/Num`).
numpy's `hypot` form, `linspace`, pairwise sums, `interp` with a guess, CPython's `math.hypot`.

### Order of work

1. **The graph** (`RoadNet`)
   - The vehicle links, without the shortcut links.
     With the road edits applied ("Road graph" above).
   - The landcover's water and surface are laid out on one world 1 m grid.
     In file-name order. Where blocks overlap: water from either, the surface of the later.
   - Boat routes are dropped:
     runs between junctions lying on water for half their length or more.
     - No boat mark is read from the road data.
       So a route an MLO turns into land stays as a road.
       It is unpaved where natural ground lies under it.
     - No landcover counts as water.
   - Highways are these.
     - Freeway names.
     - Flagged stretches of 60 m or more.
     - Unnamed flagged ramps.
     - Unnamed unflagged connectors between freeways.
     - A link that is highway only by the freeway name at one end,
       and whose other end meets an ordinary road, is a road.
   - Unpaved: per run, natural ground under half or more of the 2 m samples.
     Runs without a vote and highways keep the nodes' unpaved flag.
2. **The drawn links** (`RoadLinks`)
   - Width = (lanes both ways + 2 x lane offset) x 5.5 m.
     4.0 m on narrow links. A track is 5 m.
     A width the road edits give wins over both.
   - Junction connectors are not drawn; the arriving road is extended instead.
     - Towards the road going on.
     - Two facing ones become one link through the junction.
     - To the junction, when the road beyond starts there.
     - Past a connector between two junctions: to the next junction.
   - Forks (one link splitting into two, or two joining) start their branches
     side by side on the trunk and push them apart where they overlap.
   - Straight-on pairs are marked at nodes with 3+ links.
3. **Levels** (`RoadLevels`)
   - A link whose ribbon (casing + 2 m) overlaps a link 3 m or more below,
     not joined to it within 60 m along the links, is 1 + that link's level.
   - Roads over tunnels stay on the ground.
   - At ends of the same height the higher level carries on for 15 m.
4. **Shapes** (`RoadShapes`)
   - Per piece, a curve and widths.
     - Curve: centripetal Catmull-Rom through the nodes, every 2 m.
     - Widths: averaged over 30 m. A fork's trunk narrows to its branches.
   - Ground ribbons. Real dead ends are rounded.
   - Raised runs.
   - Corner patches where ends stop.
   - Junction corners rounded (`RoadLinks.Joins`): 6 m, from the drawn edges.
     Also where a link the road edits add meets two or more others
     at a node without a junction record, in a 10 m square around it.
   - Tunnels (`TunnelOutlines`): the segments are painted on a 0.5 m raster
     (a cell is inside when its centre is).
     The outline is traced and simplified to 0.15 m.

### What it waits for, making again, time

- It waits in these cases.
  - While the game files or the road graph are out of date.
  - While a block of the range has no landcover yet.
- It is made again in these cases.
  - The game files or a block's landcover are newer.
  - The range changed.
  - The road edits' contents differ from the record's.
  - The file is gone.
- About 2 s on one worker for 986 blocks, reading their landcover included.
- The computation itself (`RoadsStage.Make`) is shared
  with the road editor's right map ("Road editor" below).

## Road editor

The roads screen of the "Map editing" tab.
It edits the project's road edits file (`roadEdits`).
The format is in `Docs/spec/project-format.ja.md`.
The edits are a difference from the game's path data.
The road graph and the road shapes apply them.

- The screen: `src/edit/`
- The API's code: `src/FxMapGenerator.App/Web/RoadEditorEndpoints.cs`
- What it reads: `Projects/RoadEditorData.cs`

### The path data and the drawing

- The path data goes to the screen once, as columns (`GET /api/project/road-editor/paths`).
  About 4 MB for the whole map.
  - The nodes with their flags.
  - One link a connection.
    It goes the way its first record in the file goes, as the edits compare lanes.
  - The street names in English and Japanese.
- The screen applies the edits itself, as the server does (`src/edit/roads/model.ts`).
  The reasons for edits that do not apply are the same too.
- It draws the nodes and links on canvas tiles (`pathsLayer.ts`).
  - Coloured by kind.
    Normal, highway, tunnel, unpaved, no regular traffic (the game's switched-off flag).
  - Nodes from zoom 6. Junction nodes are larger, with a dark rim.
  - One-way arrows from zoom 7.
  - Hit tests go over square buckets.
- Every change is a new edits object. Undo keeps the last 200.

### What the left map shows

- The legend under the map holds a check box a kind.
  The game's five classes, one box for all of them, added, changed, hidden.
- Each has its own hover help.
- The choice is kept while the app is open (`shown` in `store.ts`, not saved).
- A kind unchecked is neither drawn nor picked. It also leaves the selection.
  Not picked covers clicks and the hover, select area, whole road,
  draw (no snapping to it, no splitting it) and move.
  `pick`, `inRectangle` and `inPolygon` take the kinds.
- A game item changed or hidden goes with "changed" or "hidden", not with its class.

### Tools

- Draw, move, select, select area, whole road. The keys 1 to 5 choose them.
  Not while typing into a field, nor with a window open.
- Select area is a rectangle.
  With Ctrl+drag: the nodes inside the outline traced while dragging.
  Either way the links with both ends inside are selected. Shift adds.
- While select area is on, the map's own dragging is off (a left drag selects).
  A drag with the wheel button moves the map.

### Whole road

`roadOf` in `pathsLayer.ts`.

- From the link clicked, it goes both ways, on through the nodes where exactly two meet.
  Up to the next branch (a node where three or more roads meet) or dead end.
- It takes the links, the nodes on the way and the dead ends.
  The branch nodes are not taken: they are other roads' nodes too.
- Shortcut links, hidden items and kinds the left map leaves out are not roads here.
- A branch node, or an item that is no road, is taken alone.
- Ctrl+click: every node of the street name (after the edits)
  and the links with both ends of it.
  An unnamed item: its road.
- Hovering marks in white what a click would take. It follows Ctrl.

### The right map

Transparent tiles with the roads alone, in the colors of an atlas or road map.
The server draws them with the cells' road drawing
(`RoadTiles` over a `RoadShapesIndex`, `RoadDrawing`).

- Half a second after an edit, the server computes the road shapes
  the next run would make with the edits not saved yet (`RoadEditorData.PreviewOf`).
  - It runs `RoadsStage.Make`, which the step uses too,
    on a copy of the path data with the edits.
  - The landcover's water and surface layers are read once and kept. About 250 MB.
  - A newer request cancels one being computed (409 `SUPERSEDED`).
  - The right map shows its tiles (`v=preview-<n>`). 0.8-1.2 s for the whole map.
- With the edits as saved and the road shapes made from them,
  the right map shows the shapes of the last run.
- While blocks of the range have no landcover yet (before the capture and scan are over),
  the preview is provisional (`provisional`).
  - Such a block is land without a class (`notMade` of `RoadNet.WorldGrids`).
    It is not the open sea.
  - So the roads on it are not left out as boat routes.
    Their paving is the path data's flag.
  - The screen says so.
  - The landcover's layers are read again as more blocks get theirs.
    While provisional, the same ones are kept for 30 s.

### Saving

- Saving writes the file through the edits' checks (`RoadEditsFile.Write`).
  Every problem is named, and nothing is written on one.
- For a project without an edits file (`roadEdits` null), the first save makes the file.
  It makes `road-edits.json` beside the project file and names it in the project.
  When the name is taken: `road-edits-2.json`, ... (`RoadEditsFile.FreeName`).
- During a run, saving is refused (409 `LOCKED`)
  from the start of the first step reading the edits to the end of the last.
  Those steps are the road graph, the road shapes and the labels.
  Edits saved before that (during the capture and scan, for example) go into that run.

### Bundled edits

`data/road-edits-default.json`. Embedded into the executable (`RoadEditsFile.BundledBytes`).
It holds, in the edits file's form, edits that every server's map can use.

- The contents: the airfields' runways, hiking trails, North Yankton's roads hidden,
  Cayo Perico's roads, additional minor roads, fixes of road shapes in the city.
- Each set is a group (`groups`, and `group` on its nodes and links).
- The groups' names are in English and Japanese (`ItemName`).
- A test checks that the file reads, that it is written as the app writes edits,
  and that every item belongs to a named group.

**A new project**

A new project (the screen's `ProjectSession.Create` and the command `new`)
starts from a copy of it.

- `RoadEditsFile.StartFromBundled` writes it beside the project file under a free name,
  whatever maps are chosen.
- The groups are named in the screen's language, one name each (`RoadEditsFile.Copied`).
  The command: English.
- It sets `roadEdits`.

**Taking groups into an existing project**

An existing project takes groups in on the screen.
"Import bundled edits…", `GET /api/project/road-editor/bundled`, `takeIn` in `model.ts`.

- It is one change of the unsaved edits (one undo).
- Added nodes are numbered on from the project's.
- An added node at the same place (1 cm each way) as one of the edits is used instead.
- A link between two nodes the edits link already is not added.
- An edit of a game node or link is left out in these cases. The counts show in the window.
  - Where the project's edits change the same one. Theirs stays.
  - Where the project's game data does not hold it as the bundle found it.
- A street name the bundle adds is matched by its English name.
- A group the edits hold already (an item of it) cannot be chosen again.
- Later versions of the app do not change a project's edits by themselves.

**A group that applies where found** (`whereFound`)

North Yankton's roads hidden is such a group.
It hides the 395 nodes of the prologue town's roads.
A frame reads only some of their areas, and Cayo Perico's files replace others.

- It is taken in whole. Its edits the game data does not hold are not counted.
- Applying leaves out what does not fit, silently
  (`RoadEditSet.Match`; on the screen `applyEdits`' `leftOut`).
- What is left out is not listed under "Edits not applied".

### Groups

- The list of edits shows each group as one row: its name and its items.
  The ends of its link edits are not counted.
- A name is one text.
  A bundled group's name is in the screen's language.
  A group taken in keeps its name in the screen's language then.
- "Revert" takes the whole group back.
  - Added nodes and links go, with the links of one's own ending at them.
  - Edits of game nodes and links go.
- An item of a group stays in it when changed (hidden, moved, split).
- The map does not read groups.
  The road graph, the road shapes and the labels take the edits without them.

### Street names of one's own

Kept in the edits file's `streets`: hash -> English, Japanese or none.
They are labels, so the screen asks for the Japanese one
only when the project makes Japanese maps.

- A new name's hash is the game's hash (joaat) of its English name in lower case.
- When that hash is in use, it is the next number no street uses.
  Not 0, none of the game's names or nodes, no other added name.
- Its English name must differ from the game's names and the other added names.
- The screen puts the name and sets it on the selected nodes in one change.
  It drops a name no node has any more (`prune`).
- The road graph works with the hashes alone.
- The road shapes (`RoadNet`: freeway names, a street going on) and the labels
  read the names beside the game's.
  `RoadEditSet.ApplyTo` puts them into `PathFile.Streets`. `GameNames.WithStreets`.

## Zones and region colors

In the screen's job list this step is "Region colors".

The step after the landcover. `RegionsStage`, one unit for the whole map.
Code: `src/FxMapGenerator.Core/Regions/`.

- `ZoneGrid`: the zones of the range's road scans,
  on one 4 m grid of the whole map (`data/zones.grid`).
  It holds the grid's frame too (the blocks' rectangle in whole metres).
  The region colors and the labels read it.
- `RegionField`: the region colors.
  Made only when a map's style takes its ground from the region colors.
  Those styles are the bundled `regional` and a project's style made from it.
  - The region of each zone comes from the style's table.
  - The share of land of each region is blurred with SciPy's Gaussian into colors.
  - One field per `regions` section of the maps' styles (`data/regions-<key>.grid`).
    The key is the first 12 hex digits of the section's SHA-256.
    Styles with the same section share it.
- Made again in these cases.
  - A block's road scan or landcover is newer.
  - The range or the frame changed.
  - A style needs a field not made.
- The record keeps, per cell of the range, the hash of the part of the field
  the cell's ground reads (`RegionField.WindowDigest`).
  That part is the 4 m points around the nodes of the cell's 1 m grid,
  and one more on each side.
  So newer region colors make only the cells whose part changed again.

## Cell map data

In the screen's job list this step is "Preprocessing", under "Cell rendering".

The step after the region colors. `CellPrepStage`, one unit per cell.
Code: `src/FxMapGenerator.Core/Cells/`.

For every cell it makes the map data the map sets share, in `cells/<cell>/`.
The format is in `Docs/spec/project-format.ja.md`.

### What it makes

- **The landcover layers**
  - The landcover masks are made as masks first,
    then traced into outlines (`Vectors/`, potrace's polygon stage).
    The masks: ground kinds, water, depth bands, canopy, building colors.
  - Every layer carries a key of the style values it was made with (`set`).
    Styles with the same values share the layer.
- **Shade and contours**
  - They take the landcover's heights.
    With balance: the ground scan's surface.
    With quality: per point the higher of it and the height grid.
    `heights` in the record: `scan`, `both`.
    So the buildings are taken out of, and the shade is made from, the same surface.
  - The shade uses SciPy's blur and numpy's gradient.
    The contours are the lines of contourpy's serial generator.
  - One shading per set of shade values (`shade-<key>.grid`).
    The key is the first 12 hex digits of the SHA-256 of the values. `shades` in the record.
    Styles with the same values share it.
- **The ground pictures** (`ground-<style>.png`)
  - They follow the style's `groundRaster`:
    material colors blended, or region colors with the materials as tones.

### Making again

It is made again in these cases. A record without the hashes counts as changed.

- A block's landcover, scans or height grid are newer,
  and their contents differ from what the record holds (`inputs`).
  `inputs` is, per block, the hashes of the files read.
  `ContentDigest` keeps a file's hash in memory while its length and time stay the same.
- A block's heights in the record are not the ones the height quality takes now.
- The style values differ from the record.
- The region colors are newer and the cell's part of them changed (`regions`).

The record also holds the hash of each file written (`outputs`):
`layers.json`, the shadings, the ground pictures.

A map's drawing compares one hash of the files its style reads (`CellPrepStage.OutputFor`).
So another style's files changing leave it.

## Points of interest

The POI the maps draw. Code: `src/FxMapGenerator.Core/Poi/`.
The format is in `Docs/spec/poi-format.ja.md`.

### How they are read

- The project's `poi` (a folder of points) is read. null: the bundled `data/poi/`.
- Its folders on disk are the POI folders (settings in `_.json`),
  each other `.json` file a group of points.
  Every point gets its values from its group and the folders above (`PoiData.Resolve`).
  Those values are its POI style, maps, visibility and lock.
- The POI styles are the bundled `data/poi-styles.json` plus the project's `poiStyles`.
- Bundled points: the 26 Highway One markers A-Z (`highway-markers.json`)
  and the 5 colored dots (`colored-dots.json`).
  They are at the positions of the postal code map's picture, and locked.

### How they are drawn

- Looks: text, text in a circle, circle, icon.
- An icon is one of two.
  - An MDI icon (`icon`): a name in `data/mdi-icons.json`.
    `MdiIcons` draws one path in a 24 x 24 box with `SKPath.ParseSvgPathData`.
  - A PNG beside the POI styles file (`image`).
    Its SHA-256 goes into what the records compare.
- `badgeColor` puts a circle 1.5 times the icon behind it.
- `showLabel` writes the label to the right of a circle or icon.
  Its size is `labelSize`, else 0.6 times the size.
  An icon over a circle writes it in the circle's color.
- `PoiLayout` holds these measures.
  The drawing (`CellPainter`) and the space the labels leave (`LabelPlacer`) use them.
- What the records compare is `ResolvedPoi.Drawn`.
  Labels, place, look, maps, visibility.
  A label missing in a language is written as an empty one.
  It is compared for the labels and per cell (`CellInputs.PoisDigest`).
- A cell also draws the points whose label beside reaches it from the west.

### Names and labels

- A point's `name`: one text. The lists' and the search's; never drawn.
- A point's `label`: the text the maps draw. Per language (`LabelPlacer.PoiLabel`).
- The names of folders, groups and POI styles are one text too (`ItemName`).
- The bundled groups and POI styles carry English and Japanese names,
  shown in the screen's language.
  The first save (`PUT /api/project/poi?language=`, `PoiFiles.Copied`)
  keeps them in that language.

### The maps they show on, and a run

- The maps a point shows on: the atlas maps and the road map.
  The minimap is made from its map's tiles, points and all.
- A run copies the POI folder into `inputs/poi/`
  and the POI styles file into `inputs/poi-styles.json`, and reads the copies.
  The PNGs it names are copied beside it (`JobRunner.CopyPoi`, `Project.UsePoiCopy`).

### The POI screen's files

`PoiFiles` handles them.

- They are read into a `PoiEditSet` (folders, groups with their points,
  the project's own styles) and written back file by file.
  - A file whose text says the same (as the app writes it) is left alone.
  - A new folder starts from the bundled groups. Their names are in the screen's language.
  - `.json` files and folders the set no longer has are removed.
- Paths are checked for the disk (`PathProblems`, `SafeName`).
- `PoiImport` reads points from a CSV or JSON.
  The CSV's named columns: `x`, `y`, `label`, `labelJa`, `name`, `color`, `size`.
- `PoiSample` draws a style as the cells draw it. Zoom 8, made smaller for 7 and 6.

### The MDI data

The MDI data is made from the npm package `@mdi/svg`. It is not a dependency of the build.

```
npm pack @mdi/svg@7.4.47        # then unpack the .tgz (tar -xzf) into a folder
dotnet run --project tools/FxMapGenerator.DevTools -c Release -- mdi-icons <unpacked>/package data/mdi-icons.json
```

It keeps the icons that are not deprecated,
with their aliases and tags for the screen's search, and the version.

## Labels

The step after the zones and region colors. `LabelsStage`, one unit per language.
Code: `src/FxMapGenerator.Core/Labels/`.

- For every language of the atlas maps,
  the labels of the whole map are placed once (`LabelPlacer`).
  The drawing only draws them.
- A partial update places the whole map's labels again.
  The same inputs give the same labels, and a change moves only the labels near it.
- Only the cells whose labels changed are drawn again.

### What is placed, and the order

- Postal codes: the project's `postals` (null: nearest-postal's table at its URL)
  are fetched once and copied into `data/postals.json`.
  Their size is the code's own `size`, else the style's table by zone.
- Order: postal codes and POI (no overlap check), then zone names,
  then street names (higher classes first).
- Overlaps are checked on a 1 m grid (`Occupancy`, the cells of Pillow's polygon fill).
  `clearance` is kept around every label.
- Text is measured with the fonts of this PC (SkiaSharp, no hinting).
  The ink box is taken as Skia measures it, not rounded out to whole metres.

### The name of the Cayo Perico island's zone

- The name of the Cayo Perico island's zone (`ISHEIST`) is placed
  only when the project reads the island's roads (`cayoPerico`).
  `LabelsStage.UnnamedZones` goes into `LabelInput.UnnamedZones`.
  The style editor's preview follows the same rule.
- That zone reaches over the sea around the island,
  to the aircraft carrier the default range holds.
  So on a map without the island its name would stand on the water.
- The zone itself (region colors, postal code sizes, the zone of a clicked point) stays.
- The labels are placed again when the record's `cayoPerico` differs from the project's
  (or is missing).

### Street names

- The lines of a street name (`StreetParts`) are made in this order.
  1. Its roads are chained again by the ends that go on straightest.
  2. Boulevards whose pieces never meet get centre lines from their shape
     (2 m grid, closed, thinned).
  3. The carriageways are paired into a midline.
  4. The lines are cut at corners over 50 degrees.
  5. They are cut where another name's highway runs alongside.
- The numbers use functions that give the same bits as C, numpy and Python (`Num`).
  C's pow for `x ** 2`, numpy's arange and round, Python's `//` and round.
- The skeleton pixels are cut into lines
  in the order CPython walks a set of them (`PixelSet`).
- Street names: the game's (`game/names.json`)
  and those the road edits add (`streets`, English and Japanese).
  A name without a Japanese one is written in English on a Japanese map too.
- The step reads the edits while it runs.
  While a step reading the road edits runs, the screen cannot save them.

### Files, making again, time

- One file per placement key.
  The key is the SHA-256 of the style's `labels` without colors and outlines.
  So maps placed alike (the bundled `postalcodemap` and `regional`) share one file.
- Made again in these cases.
  - The road graph, the zones or the game names are newer.
  - A placement key has no file.
  - The postal code copy, the POI or the street names the road edits add (`streetsSha256`)
    differ from the record.
- About 5 s a language for the whole map.

## Cell drawing

In the screen's job list these steps are "Cell rendering" and "Low-zoom tiles".

The step after the cell map data and the labels. `CellDrawStage`, one unit per cell.
One per atlas or road map, as `cells.<map>`.
Code: `src/FxMapGenerator.Core/Render/`.

A cell is drawn at the z8 scale (1,024 px a block), its own blocks are cut into z8 tiles,
and the z7 to z5 tiles above them are made.
The tiles are reduced with the same Lanczos as the satellite map.

### Drawing order

`CellPainter` draws in this order.

1. The background.
2. The ground picture (enlarged bilinearly) or the ground layers.
3. The hill shading over what is drawn so far (`ShadeLayer`).
   The 1 m illumination is enlarged with the values of SciPy's `ndimage.zoom` (order 1)
   and multiplied in float32.
4. The other layers, in their order.
   Filled even-odd, with a 0.6 px outline of the fill color that hides the seams.
   The canopy as a tint or a pattern. Contours as lines.
5. The roads.
   Tunnels, unpaved tracks, the ground-level ribbons with their casings, corner patches,
   rounded junction corners, the raised runs level by level.
6. Last, the labels.
   Postal codes, POI, zone names. Street names glyph by glyph.

The opacity of water:

- Water and sea bands whose opacity (`paint.waterOpacity`, `paint.sea.opacity`) is under 1
  replace what is under them with their color at that opacity.
  What is drawn after them stays opaque.
- Only a style with such water keeps the picture's transparency (`MapStyle.SeeThroughWater`).
  Its tiles are RGBA where clear.
  Every other picture is made opaque.

### What is chosen to draw

- Only the layers whose style-value key (`set`) is the style's own are drawn.
  The colors come from the style (the labels file holds none).
- Road shapes, labels and POI are taken when they come near the cell's rect.
  What lies outside does not reach the picture, so the choice does not change it.
- The paths are made once in the cell picture's pixels
  and drawn block by block, shifted by 1,024 px. One block of memory.
  The blocks put together are the cell drawn at once.
- What a cell takes of the whole map's data is chosen in one place (`CellInputs`).
  The painter draws exactly these.
  - Road shapes: those within 50 m of the cell's block rectangle.
  - Labels and POI: those whose point lies within 200 m of it.

### Records, drawing again, time

- The road shapes' and the labels' steps keep the hash of each cell's part
  in their records (`cells`).
- `cells/<cell>/draw-<map>.json` keeps the hashes of what the cell was drawn from.
  - `prep`: the files of its map data the map reads. From the cell record's `outputs`.
  - `roads`, `labels`, `pois`.
- Drawn again in these cases.
  - The style differs from `data/draw-<map>.json` (every cell).
  - The cell has no drawing record.
  - What it takes changed.
    Each of the map data, road shapes and labels is compared
    only when its step ran after the drawing.
    Road shapes or labels made again the same around the cell draw nothing again.
- About 30 s a map for the 23 cells (12 workers).

### Low-zoom tiles

`CellLowZoomStage`, `lowZoom.<map>`.

- The z8 tiles of the project's frame outside the range
  are painted with the style's open sea.
  - Colour: `MapStyle.OpenSea`, the last color of `paint.sea.sand`. The deepest band's.
  - Opacity: that band's opacity (the last of `paint.sea.opacity`).
    Clear in the bundled atlas styles.
  - So the sea of the edge blocks runs on without a step.
    The same as the satellite map's open sea.
- Then z7 to z0 are all made again.
- They are made again too when the frame differs
  from the one recorded (`data/low-zoom-<map>.json`).
  Tiles outside a smaller frame are removed.
- A parent of four same flat opaque tiles is written as their bytes.
  Lanczos keeps a flat picture flat, so that is what the reduction gives.
- About 10 s a map for the whole map.
- The minimap (ytd) is made from the atlas and road maps
  the same way as from the satellite map.
- The export's viewer shows the same open sea behind the tiles.

## Minimap and export

### Minimap (ytd)

Code: `src/FxMapGenerator.Core/Minimap/`.
With a minimap chosen in the project (`minimap`, a map), it is the last step of a build.

**The six sheets**

The standard frame is cut into 2 × 3 sheets of 4500 m (row 0 north, column 0 west).
For each sheet:

1. One picture of 4096 px is put together from the map's tiles.
   - 16 × 16 z6 tiles. A z6 tile is one block. Missing tiles stay transparent.
   - When the project's `minimap.outside` is `transparent`,
     the blocks outside the range stay transparent too.
   - There is no choice of 8192 px sheets (z7).
     They looked no different in the game,
     and FiveM warns that their DXT5 textures of 64 MiB are oversized.
2. It is compressed in two forms, with one mip level.
   - DXT5 (`minimap_sea_<row>_<col>`).
   - DXT1 with 1-bit alpha (`minimap_<row>_<col>`).
     Every pixel that is not fully transparent is made opaque first, see-through water too.
     The radar draws the DXT1 ones
     and shows the small whole map under them only where the map is transparent.
   - The game's minimap looks the textures up by these names.
   - With smaller levels in the file, the game's "normal" texture quality draws those,
     and the map blurs when zoomed in.
   - The compression runs in bands of 256 rows on the free workers.
     Blocks are encoded on their own,
     so the bands put together equal the whole picture compressed at once.
3. Both go to `ytd/<map>/4096/`, through temporary files.

Record and making again:

- The record is `ytd` / `<map>@4096/<row>_<col>`.
  `data/minimap-<map>-<row>_<col>.json` holds the hash of the 256 tiles the sheet was made of.
- A sheet older than the map's low-zoom record is left again only when those tiles changed.
  A sheet is 2 × 2 cells.
  So a partial update makes at most the sheets of the cells it drew,
  and of those only the ones whose tiles came out different.
- With all six made, the outside they were made with goes into `data/minimap-<map>.json`.
  When it is not the project's, all six are left again.
  A run that starts to make them with the other outside removes the record first.
  So a run stopped half-way does not leave it beside sheets of this one.

**The small whole map**

One more unit, `<map>@4096/lod`, makes the small whole map the game lays under the radar.
`minimap_lod_128`, `MinimapLod`.

- The game's own is the standard frame squeezed into 128 × 128 px
  (the same 9000 × 13500 m as the six sheets).
- So this one is the map's six z2 tiles of the standard frame side by side,
  shrunk to that square (Lanczos).
- It is laid over the style's open sea (the satellite map: black),
  opaque, as DXT5 with one level.
- It is left when never made, or when its tiles changed since.
  `data/minimap-<map>-lod.json` holds their hash.
- The job list counts it with the sheets.

**The cells outside the standard frame**

The map outside the standard frame (a frame with added cells, such as Cayo Perico's)
cannot go into the game's six sheets.
Every cell outside it that holds blocks of the range becomes a texture of its own.
`MinimapExtraTiles`, unit `<map>@4096/cell_<row>_<col>`.

- Its 8 × 8 z6 tiles side by side.
- 2048 px for its 2250 m (the sheets' density). DXT5 with one level.
  Transparent where the sheets are.
- Named `fxmapgen_extra_<row>_<col>`, with `m` for a minus (`fxmapgen_extra_m1_0`).
- A frame grows by whole cells.
  So a cell is either in a sheet or wholly outside the standard frame.
- Extra Map Tiles draws them in the game ("Export: the minimap resource" below).
- Each is left again as the sheets are (`data/minimap-<map>-cell_<row>_<col>.json`).
- The job list counts them with the sheets.

**Time**

A sheet takes about 14 s.
The sheets run side by side, their bands on the workers left over.
Six take 15 s on 12 workers (`data/timings.json`).

### Export

Code: `src/FxMapGenerator.Core/Export/`. API: [api.md](api.md#export).

It writes what was made into an output folder.
It runs as a job, so progress, stopping and the run folder are those of a build.
The step is `export`.
Its units are the tiles by zoom and column bands, the viewer, the resource.
When editable files are written, two more steps follow it, `export.layers` and `export.files` ("Export: editable files" below).
`ExportStages.For` gives the steps in their order.

```
<output>/
  fxmapgen-export.json          the record (the web part, and the resources, editable files and
                                web tiles of edited pictures written); a folder with it may be
                                written again
  web/                          or web.zip with the same contents (PNGs stored uncompressed)
    index.html, leaflet/        the viewer (Leaflet; the tiles are read by relative path,
                                so it opens from the disk)
    tiles/<map>/{z}/{x}/{y}.png
    lb-phone.lua                an entry for lb-phone's Config.CustomMaps
    README.txt, CREDITS.txt
  fxmapgen-minimap-<date>/      the minimap resource
    fxmanifest.lua, config.lua, zoom.lua, README.txt, README.ja.txt, CREDITS.txt
    interiors.lua               the list of the game's interior maps
    client.lua, exports.lua, scaleforms.lua, utils.lua, extra-map-tiles-LICENSE.txt
                                Extra Map Tiles 3.0.1
    stream/                     minimap_sea_*.ytd and minimap_*.ytd (12), minimap_lod_128.ytd,
                                the empty minimap_*.ydd (65), fxmapgen_extra_*.ytd (the cells
                                outside the standard frame), minimap_main_map.gfx and
                                radar_masks.ytd (Extra Map Tiles), int3232302352.gfx (Cayo
                                Perico's island map drawing nothing, only when the project
                                reads the island's roads)
  editable/                     the editable files (only for the maps an export chooses)
    <map>-z<zoom>.psd           a map split into layers; .psb when it passes what a PSD holds
    <map>-z<zoom>.svg           a map split into layers; the roads and the text stay shapes and text
    <map>-z<zoom>-svg/          the PNG pictures that SVG file links (the layers that are pictures)
  web-edited/<map>/             the web tiles of an edited picture (written by the conversion
                                of an edited picture, apart from an export)
    index.html, leaflet/, tiles/<map>/{z}/{x}/{y}.png, lb-phone.lua, README.txt, CREDITS.txt
```

### Export: the web part

- The web part is written fresh each time. Tiles of an earlier export do not stay.
- A folder that holds other files and no record is refused.
- The web tiles go up to the maximum zoom (`maxZoom`, 6-8, default 8).
  - Each level less is about a quarter of the files.
    A whole satellite map: 32,769 up to z8, 8,193 up to z7, 2,049 up to z6.
  - Some static hosts limit the files of a site.
  - The viewer enlarges the last level beyond it.
- With cells added above or to the left, the export numbers the tiles
  from the frame's north-west corner (`WebExport.Renumbers`).
  - z3 to z8 are copied under shifted numbers. z2 to z0 are made again from the z3 written.
  - So they do not line up with maps made for that grid.
  - The viewer, the lb-phone example and the README are written for the frame.
  - Only the tiles inside the frame are exported.

The lb-phone example:

- lb-phone works out the scale and zoom levels from `resolution`.
  `resolution` is the whole map at the maximum zoom.
  For the standard frame: 32768 × 49152 px at z8, 16384 × 24576 at z7.
- So the example's `resolution` and `zoom.max` follow the maximum zoom,
  and its `topLeft` / `bottomRight` the project's frame.
- The tiles use the grid of the loaf-scripts map tiles. A z8 tile is 70.3125 m.
  An export of the standard frame up to z7 has loaf's `resolution`.

### Export: the tiles before a change

The maps' tiles written over since the last export keep what they were (`TileStore.Keeping`).

- A tile written with the same bytes is not written again.
- The first time other bytes replace it,
  the old ones go to the work folder's `before/<map>/{z}/{x}/{y}.png`.
- This covers the cell drawings, the low-zoom tiles, the ortho
  and the provisional ortho during a capture.
- With no export yet, "before" is the map before the first run that wrote over its tiles.
- An export that completes removes `before/<map>` of the maps it wrote.
  A map left out keeps its earlier tiles.
- The map view's "Compare with before" reads them
  ("Screens and the open project" below).

### Export: the minimap resource

- The resource name carries the date, with `-2`, `-3` when the name exists.
  FiveM keeps a resource name's files in its caches.
- The screen and the command line can set the name (lower-case letters, digits, `-`, `_`).
- The six sheets replace the game's by name: the resource streams files of the game's names.
- Extra Map Tiles 3.0.1, bundled, draws the textures outside the standard frame.
  MIT, bundled as released (`data/minimap/README.md`).
  - The export writes its `config.lua` in its own form, with one tile per cell.
    - `txd` = `txn` = the texture.
    - `x`, `y` = the cell's north-west corner in game metres.
    - `x_scale` = `y_scale` = 0.5, as a tile of 1 is 4500 m.
    - `alpha` 100.
  - `remove_blur` (its own masks for the tilted radar) is on only when there are tiles.
  - It places invisible blips at the corners of its tiles, so the pause map reaches them.
  - A project of the standard frame gets the same resource with no tiles.
- `zoom.lua` sets the pause map's zoom levels
  and the radar's zoom (1100, on foot and in a vehicle).
  They are the values of the original postal code map script (Virus_City).
  What it calls depends on the interior the player is in ("Interior maps" below).
  The game's radar zoom and zoom levels come back when the resource stops.
- The empty drawables (ydd) are those of the postal code map's first release
  (`data/minimap/README.md`).
- The radar keeps the pictures it loaded when the game started until the game closes.
  A reconnect does not change them.
  The pause map changes at once.
- The resource's README (English and Japanese) tells players two things.
  - To start FiveM again after putting it in.
  - Which resources to remove:
    PostalCodeMap, other minimaps, an Extra Map Tiles already running.
    With the island map, also the island map resource such as CayoPericoMinimap.
    The resource loading the island stays.
  - What shows inside buildings and underground.
  - The two lines a resource loading the island calls every frame, and what to do about them
    ("Interior maps" below).
- `tests/minimap/run-resource.lua` runs a written resource's scripts
  in a small fake game (Lua 5.4) for the tests.
  It looks at where Extra Map Tiles draws the tiles and puts its blips, the zoom calls,
  and what the stop gives back.
  It puts the player outside, in an interior without a picture, in one with a picture of its own
  and in an underground one in turn, and looks at what is called.

Interior maps:

- The game has a radar picture per interior. Its file is `int<joaat of the interior's name>.gfx`.
- `<minimap_interiors>` in `common\data\ui\frontend.xml` is a table of pictures and interiors.
  A row is a picture's name, its place, and the names of the interiors it stands for.
- The resource carries a list made from the two, `interiors.lua` (`InteriorMaps`).
  - `RADAR_OWN_PICTURE`: the name hashes of the interiors that have a picture of their own.
    They are the numbers of the picture files' names
    and the interiors the table's rows stand for (but for the four underground pictures).
  - `RADAR_UNDERGROUND`: the rows of the pictures of underground passages.
    Each has the picture's name, its place and the names of the interiors it stands for.
- There are four pictures of underground passages:
  `V_FakeMetro` (the metro and the storm drains), `V_FakeWaterTunnel`, `V_FakeTunnel_SC1`, `V_FakeTunnel_ID1`.
  These four names are all the product holds.
- The list is read from this PC's GTA V when the resource is written (`MinimapGameFiles`).
  - The table is the last `data\ui\frontend.xml` in the game's order (the one in `update.rpf`).
  - The table's file is not well-formed XML (its comments hold runs of hyphens). The rows are taken by their tags.
- The export needs GTA V and the keys (`MINIMAP_NO_GTA`, `MINIMAP_NO_KEYS`).
- A game without any picture file, or without the table, is an error.
- A picture of the four that has no row in the table is left out. The log says so.
- The resource the conversion of an edited picture writes is the same.

`zoom.lua` calls one of three things, by the interior the player is in.
It tells the interior by `GetInteriorFromEntity` and `GetInteriorLocationAndNamehash`,
and asks the game only when the interior changes.

| Where the player is | What is called | What shows |
|---|---|---|
| Outside; an interior without a picture | `SetRadarZoom(1100)` | The map at the outdoor zoom |
| An interior with a picture of its own | `SetRadarZoom(0)` | The floor plan in place of the map |
| An interior an underground picture stands for | `SetRadarZoom(1100)`, `SetRadarAsExteriorThisFrame()`, `SetRadarAsInteriorThisFrame(picture, x, y, 0, 0)` | The map at the outdoor zoom with the passages laid over it |

- A zoom set by `SetRadarZoom` stays until it is set again. 0 gives it back to the game.
  So it is called every frame, and only on foot or in a vehicle.
- In an interior with a picture of its own, `HideMinimapExteriorMapThisFrame()` is called
  only while the pause map is in its interior view (`IsPauseMenuActive() and IsPausemapInInteriorMode()`).
  The pause map then shows the floor plan alone too.
- The outside map is not hidden for the radar.
  While the pause menu is closed, nothing tells a script whether the game shows an interior.
- In an underground interior, `SetRadarAsExteriorThisFrame()` keeps the outside map.
  The pause map does not go to its interior view either.
- This shape was settled by looking at it in the game.

The server's setting `fxmapgen_minimap_fixed_zoom`:

- With `setr fxmapgen_minimap_fixed_zoom true` in `server.cfg`,
  `zoom.lua` only calls `SetRadarZoom(1100)` everywhere.
- It is a replicated convar. The script reads it when the resource starts.
- It is for servers whose island resource calls these two lines every frame, wherever the player is:
  `SetRadarAsExteriorThisFrame()` and `SetRadarAsInteriorThisFrame(h4_fake_islandx, ...)`.
  The IPL resource of "The Cayo Perico Island Available for FiveM" on the Cfx.re forum does.
- While those two lines are called, the game shows no interior's picture.
  The game shows one such picture a frame.
  When two resources ask, the picture of the one started later shows.
- On such a server, the radar inside an interior with a picture of its own is only the map, enlarged
  (the picture does not show, and the zoom alone goes back to the game).
- The READMEs say to remove the two lines or to use this setting, with what each gives.

Cayo Perico's island map:

- With Cayo Perico's roads read (`cayoPerico`),
  the resource carries the island map drawing nothing (`IslandMap`).
  It is the game's `int3232302352.gfx` from this PC's GTA V with its four shapes emptied,
  registered as `SCALEFORM_DLC_FILE`.
- So the island shows as the map's own pictures
  (the part in a sheet and the cells outside).
- The island shows also when the server's island scripts do not ask for the island map
  (`SetRadarAsInteriorThisFrame` with `h4_fake_islandx`).
  The resource does not ask.
- Like the list of interior maps, it is read from this PC's GTA V when the resource is written.
- It counts the island's land blocks the range lacks.
  The screen says so; the island is missing there in the game.
- The file goes only into the resource.

### Export: editable files

A map is written as one picture split into layers.
It is for opening in another program and working on the map there.
There are two formats.

| Format | Programs | Contents |
|---|---|---|
| PSD | Photoshop, GIMP | Every layer is a picture |
| SVG | Inkscape, Illustrator | The roads, the text and the points of interest are shapes and text. The other layers are PNG pictures |

They were opened and checked in GIMP 3.2 and Inkscape 1.4.

The code is in these places.

- `Core/Export`: `EditableStages.cs` (the two steps), `LayeredBlock.cs` (making the layers small), `PackedBlocks.cs` (the temporary files).
- `Core/Export`: `PsdFile.cs` and `PackBits.cs` (PSD), `SvgFile.cs` (SVG).
- `Core/Imaging`: `PngWriter.cs` (the PNG pictures an SVG file links).
- `Core/Render`: `CellPainter.DrawLayers` (drawing layer by layer), `MapLayer.cs` (the list of layers).

**When they are written**

- They are written only for the maps an export chooses.
- Which maps were written is not kept in the project. `export.editable` keeps the zoom level and the formats.
- The `export` command writes them only with `--editable <map>,...`.
- Every map chosen is written in every format chosen (`EditableChoice.Written`).
  Maps chosen without any format cannot be exported (`NO_EDITABLE_FORMAT`).

**Size and file**

- The picture is the project's whole frame.
- The zoom level is 6 or 7 (`EditableChoice`). A block is 256 px at z6 and 512 px at z7.
- The standard frame at z6 is 8192×12288 px: the pixels of the minimap's six sheets.
- `<map>` is the name of the map's web tile folder (`MapSet.ExportName`).
- The PSD file is `editable/<map>-z<zoom>.psd`.
  A side over 30,000 px or a file over 2 GB makes it a PSB file (`.psb`).
- The SVG file is `editable/<map>-z<zoom>.svg`.
  The PNG pictures it links are in the folder `<map>-z<zoom>-svg/` beside it ("The shape of the SVG file" below).
- A file or folder of the same name is written over.

**Layers**

Bottom first (`MapLayer`). A layer the map has nothing for is not written.

| Layer | Contents | Blend mode |
|---|---|---|
| Ground | The background colour and the ground picture or ground layers. No shading | Normal |
| Shading (dark side) | Where the shading darkens the ground | Multiply |
| Shading (light side) | Where the shading lightens the ground | Color Dodge |
| Tree canopy | The tree canopy | Normal |
| Water | The water and the sea's depth bands | Normal |
| Buildings | The buildings | Normal |
| Contours | The contours | Normal |
| Railway | The railway | Normal |
| Roads | Tunnels, tracks, roads with their casings, raised roads | Normal |
| Postal codes | The postal codes | Normal |
| Points of interest | The points of interest | Normal |
| Zone names | The zone names | Normal |
| Street names | The street names | Normal |

- The two shading layers are clipped to the ground (`MapLayers.Clipped`). They do nothing where the ground is not.
  SVG has nothing that stands for clipping ("The shape of the SVG file" below).
- The blocks outside the range hold the style's open sea in the water layer (as the tiles do; nothing when the style's open sea is clear).
- A satellite map is one layer, its tiles as they are ("Satellite map").
- The layers are named in the language of the screens (`MapLayers.Name`).
  The command names them in Japanese only when the app's settings say `ja`.
  A PSD file's old name field holds the English name.
- A layer is as large as the rectangle of the blocks it has something in.
- From the ground to the railway (up to `MapLayers.LastPicture`) a layer is a picture in every format.
  The roads, postal codes, points of interest, zone names and street names above them are pictures in a PSD file, shapes and text in an SVG file.

**Drawing layer by layer** (`CellPainter.DrawLayers`)

- The steps `DrawArea` draws a map's tiles with go, by their kind (`StepTag`), to the pictures of their layers (`MapLayers.Of`).
  `DrawArea` is as it was.
- The shading is the factor the ground is multiplied by (`ShadeLayer.Factor`), in two layers.
  - Where it is under 1, the dark side holds the grey of that factor. Multiply is "the colour under it × the grey".
  - Where it is over 1, the light side holds the grey for which "1 ÷ (1 − grey)" is the factor. Color Dodge is "the colour under it ÷ (1 − grey)".
- See-through water takes the place of what is under it (`FilledLayer`). So the ground is cut where the water is.
  - The water's steps are also drawn on a picture that starts opaque.
    That picture's opacity less the water's is the part of the ground the water leaves.
  - The ground's opacity = that part ÷ (1 − the water layer's opacity): the value that gives the map's pixel under the water layer.
  - Under opaque water the ground stays.
- The layers put over each other bottom first are the picture `DrawArea` draws. They differ by the rounding of 8 bits, 3 to 4 steps at most where the shading acts.

**Making the layers small** (`LayeredBlock.Shrink`)

Layers made small one by one and put together are not the tiles.

- A tile is the whole picture put together first and made small then. Making a picture smaller strengthens the contrast along the edges of shapes a little.
- At a shore of see-through water, ground and water made small apart leave the pixel between them more see-through than the tile.

So the small layers are found backwards, from pictures made small.

1. At z8 the layers are put over each other one by one, bottom first.
   The order is the ground, the ground with the shading, then every layer above, one at a time.
2. Every such picture is made small the way the tiles are
   (`EditableLayersStage.Shrink`: four tiles of z8 to one of z7, and for z6 those four to one; Lanczos).
3. A layer is what turns the small picture "up to the one before" into the small picture "up to here".

Each layer is found as follows.

- The ground is the first picture itself.
- The two shading layers are the factor between the small ground and the small "ground with the shading".
  - A pixel is grey where one factor gives its three colours to a step.
  - Elsewhere it holds a factor per colour.
- Any other layer starts from the opacity it has when made small alone.
  Its colours are those that give the picture "up to here".
  - Inside a shape it keeps the paint's colour.
  - Along a shape's edge it carries the contrast that making a picture smaller adds.
- Its opacity moves only where the picture needs it.
  - Over a see-through picture it becomes what gives the opacity of the picture "up to here". A shore of see-through water is such a place.
  - Where colours between 0 and 1 cannot give the picture "up to here", the opacity goes up.
    A layer moves a pixel only as far as it is opaque.
- Where the layer made small alone has nothing, it gets a pixel only when both of these hold.
  Such a pixel is the lighter or darker rim outside a shape, a little white or black.
  - The two small pictures differ there.
  - The layers put together so far are not the picture "up to here" yet.

How close it gets is the three values of `LayeredBlock.Slack`.

| Value | Default | Meaning |
|---|---|---|
| `Inside` | 3 | The difference that may stay where the layer has something. In steps of 255 |
| `Beside` | 3 | The difference that may stay where the layer has nothing |
| `BesideOpacity` | 0.25 | The most opacity a pixel gets where the layer has nothing |

- The smaller the difference, the more opaque the added pixels.
- A pixel near white or black takes an opaque pixel to be moved even a little.
  Example: lifting a pixel 10 steps under white by 5 steps takes white at 50 % opacity.

What stays between the layers put together and the tiles is the following.

- The difference that may stay, above.
- The places that would take a pixel more opaque than the limit.
- What the layers and the picture are apart at z8.
- The pixels just outside an opaque shape over see-through water.
  Making the picture smaller leaves the tile a little more see-through there. No layer put over it can do that.

**Putting them together**

Two steps make the files.

1. `export.layers` (`EditableLayersStage`). Its units are the cells of the atlas and road maps.
   - A cell's blocks are drawn layer by layer.
   - A block's layers are made as small as the zoom level asks ("Making the layers small" above).
   - The small layers are kept as packed rows (`PackBits`) in temporary files (`PackedBlocks`),
     in `editable/.work/` of the output folder.
   - It needs about the memory of the cell drawing.
   - Each layer takes two shrinkings: of the layer alone, and of the picture put together up to it.
   - With SVG as the only format, the layers above the railway are not made small: an SVG file holds no pictures of them.
2. `export.files` (`EditableFilesStage`). Its units are the pairs of a map and a format.
   - A PSD file and an SVG file are written from the same temporary files. The layers are drawn once.
   - The written file goes into the export's record (`ExportRecord.AddEditable`).
   - The temporary files go at the end of the step.

A PSD file is written this way.

- The merged picture (the whole map as one picture, at the end of a PSD file) is made from the map's tiles.
- A layer's rows are the rows of its blocks joined from left to right.
  Packed rows joined are that row packed.
- The row lengths are counted first (`PsdFile.Measure`), PSD or PSB is decided, then the file is written.
  The whole picture is never in memory.

An SVG file is written this way.

- Every layer that is a picture is written as a PNG file (`PngWriter`). The layers are written side by side.
  A row of blocks is read at a time (`PackedBlocks.Pixels`) and written as it is. The whole picture is never in memory.
- The roads, the text and the points of interest are written from the map's data (`SvgFile`): `data/road-shapes.grid`, the placed labels, the points of interest.
- The PNG files and the SVG file are made in `editable/.work/` and put in place when they are whole.
  A folder of the same name is removed first.

- When a cell fails or the export is stopped, no file is made and the temporary files go.

**The shape of the PSD file** (`PsdFile`)

- 8 bits, RGB. A layer has four planes: alpha, red, green, blue. Every plane is PackBits row by row.
- A layer's name is written in the Unicode field (`luni`) and in the old name field.
- The only image resource is the resolution (72 ppi).
- When the merged picture has see-through pixels, its fourth plane is their opacity and the layer count is written negative.
  The colours are written over white (as paint programs write them; a reader takes the white out).
- A PSB file has lengths of 8 bytes and row lengths of 4.

**The shape of the SVG file** (`SvgFile`)

- One unit is one pixel of the zoom level. `width`, `height` and `viewBox` are the picture's pixels.
  A PNG written from it at one pixel a unit has the size the conversion of an edited picture reads.
- A layer is a `<g>` right under the root, with Inkscape's layer mark (`inkscape:groupmode="layer"`).
  - Its `id` is made from the English name (`SvgFile.Id`; `Shading_dark_side`, for one).
  - Its `inkscape:label` is the name in the language of the screens.
- Coordinates have two decimals. A shape's points are written as steps from the point before.
- The SVG file is only written. The product does not read it.

The layers that are pictures:

- An `<image>` links the PNG file in the folder beside the SVG file (`xlink:href`, a relative path).
- The PNG files are named after the layers' English names (`EditableChoice.PictureName`).
  `ground`, `shading-dark`, `shading-light`, `tree-canopy`, `water`, `buildings`, `contours`, `railway`.
  A satellite map's is `satellite`.
- The two shading layers carry `mix-blend-mode` in the layer's `style`.
  `multiply` for the dark side, `color-dodge` for the light side.
- SVG has nothing that stands for clipping to the layer under a layer.
  Left as they are, the shading pictures show as grey where the ground is not, and make a pixel the ground covers in part opaque.
  So where water is see-through, the PNG pixels change in two places
  (`EditableFilesStage.ShadeInGround`, `KeepToWholeGround`).
  - The shading pictures keep only the pixels the ground covers whole.
  - A pixel the ground covers in part gets its colour after the shading in the ground picture.
  - Put together, the pictures are what the PSD file's layers put together are.

The roads (in the order `RoadDrawing` draws them, a `<path>` a shape):

| Group | Contents |
|---|---|
| Tunnels | A shape a tunnel: all of its outlines. A see-through fill and a dashed edge |
| Unpaved tracks | A line a run of joined segments. A line with a width |
| Road casings, Highway casings | The casings of the roads, of the corner patches and of the junction corners |
| Roads, Highways | The roads and the corner patches |
| Junction corners | The rounded corners and their seams |
| Raised roads 1, Raised roads 2, … | By level. Casings, unpaved tracks, roads, highways, in that order |

- A group's colour is in the group's `fill` (`stroke` for the unpaved tracks). The shapes carry no colour.
- What the drawing strokes (the casings of the corner patches, the casings and seams of the junction corners) is written as the outline of the stroke
  (`SKPaint.GetFillPath`). Every shape of a group is then a filled shape of one colour.
- A junction corner's seam is cut to the corner's region (`SKPath.Op`).
- A shape that reaches a block of the range is written whole. The tiles cut shapes at the range's edge; the SVG file does not.

The text:

- A postal code, a zone name or a label of a point of interest is one `<text>`.
  Its place comes from the numbers the drawing uses (`CellPainter.SetText`): the middle of its width and of its ink's height lie on its point.
- A street name is one `<text>` with lists of `x`, `y` and `rotate`, a value a character.
- An outline is the text's `stroke`. `paint-order` puts it under the fill.
- Kerning and ligatures are off (`font-feature-settings:'kern' 0`, `font-variant-ligatures:none`).
  The drawing sets glyph after glyph by its advance.
- The font is the style's font name and weight. The text is shown with the fonts installed on the PC that opens the file.

The points of interest:

- A point of interest is one `<g>`. Its `inkscape:label` is the point's name (its label when it has none).
- A circle is a `<circle>`, an MDI icon a `<path>`, a PNG icon an `<image>`.
  The PNG is copied into the folder beside the SVG file as `poi-<POI style id>.png`.

### Export: converting an edited picture

An editable file edited in a paint program and written out as one PNG picture of the whole map
becomes web tiles and a minimap resource.

- Nothing is added to the project's maps. Nothing is written into the work folder.
- It is a job of its own, apart from an export.
  On the screens it is "Convert an edited picture" of the export screen. The command is `convert`.

The code:

- `Core/Imaging/PngRows.cs`: reads a PNG row by row.
- `Core/Export/PictureConvert.cs`: the choices (`ConvertOptions`), the look at the picture (`EditedPicture`),
  why it cannot run (`ConvertStages.Check`).
- `Core/Export/ConvertStages.cs`: the three steps.
- `Core/Minimap/MinimapTextures.cs`: makes the textures. Shared with the minimap step.

**The picture**

- A PNG of the project's whole frame, of the size of an editable file (`EditedPicture.Size`).
  For the standard frame: 8192×12288 px at z6, 16384×24576 px at z7.
- The zoom level comes from the picture's size (`EditedPicture.ZoomOf`).
  A picture of neither size is not converted (`BAD_SIZE`).
- It is read row by row (`PngRows`). The whole picture is never held in memory.
  - 8 and 16 bits. Colour, grey, palette.
  - Alpha, a transparent colour of `tRNS`, the opacities of a palette.
  - 16 bits keep their upper 8.
  - An interlaced PNG is not read (`INTERLACED`): its rows do not come in order.
  - Colour profiles and gamma are not looked at. The check sums of the chunks are not verified.
- While it is read, the file cannot be written over.

**The original map**

One map is chosen as the map the picture was made from (`ConvertOptions.Map`).
The output takes these from it:

- The tile folder's name (`MapSet.ExportName`) and the map's title.
- The colour of its sea: behind the web tiles and under the small whole map (`MinimapTextures.Under`).
- Its credits.

Not chosen, it is found in this order (`EditedPicture.DefaultMap`):

1. The map whose editable file's name (`<map>-z<zoom>`) the picture's file name starts with.
2. The minimap's map.
3. The project's first map.

**The steps** (`ConvertStages.For`)

1. `convert.tiles` (`ConvertTilesStage`). Its unit is the one picture.
   - The picture is read a row of tiles at a time and cut into the tiles of its zoom level
     (`ConvertTilesStage.Cut`). The picture's pixels are the tiles' pixels.
   - The zooms below it are made down to z0, shrunk as every map's are (`LowZooms.BuildParents`).
   - The tiles go into `.convert/tiles/` of the output folder, numbered as a work folder's tiles.
2. `convert.textures` (`ConvertTexturesStage`). Only when the minimap resource is written.
   - Its units are the six sheets, the small whole map and the cells outside the standard frame.
   - They are made by what the minimap step uses (`MinimapTextures`): the same tiles give the same files.
   - With `minimap.outside` = `transparent`, the blocks outside the range are clear,
     whatever the picture has there.
   - The textures go into `.convert/ytd/` of the output folder.
3. `convert.files` (`ConvertFilesStage`). Its units are `web` and `resource`.
   - `web`: the tiles are written into `web-edited/<map>/tiles/<map>/`,
     numbered as an export numbers them (`ExportGrid`).
   - `web`: the viewer, the lb-phone example, a README and the credits are written too.
     The maximum zoom of the lb-phone example is the picture's zoom level.
   - `web`: a folder of the same name is made again. Folders of other names stay.
   - `resource`: the minimap resource is written (`MinimapResource.Write`).
     It is an export's resource with the textures made from the picture, named by the same rule.
   - The record gets both, and `.convert/` is removed at the end.

- A conversion that is stopped or fails removes `.convert/` too.
  What a stop between two steps leaves is removed first by the next conversion or export.
- The export's `web/` is not touched. An export does not touch `web-edited/`.

**The record** (`fxmapgen-export.json`)

- `webEdited`: the outputs in `web-edited/` (`ExportWebEdited`).
  Those whose folder is gone drop out at the next writing.
- `fromPicture` of `resources`: true for a resource made from a picture.
- A folder without a record gets one when a conversion writes into it.

**What is kept**

- A conversion from the screen keeps the picture's place and the original map in the project
  (`export.picture`). The outputs chosen are not kept.
- The command line keeps nothing.

**Why it cannot run** (`ConvertStages.Check`)

| Code | Meaning |
|---|---|
| `NO_FOLDER` | No output folder |
| `NOT_EMPTY` | The output folder holds other files |
| `NOTHING` | No output is chosen |
| `BAD_MAP` | The original map is not one of the project's maps |
| `BAD_PICTURE` | The picture cannot be converted |
| `MINIMAP_NO_GTA`, `MINIMAP_NO_KEYS` | GTA V or the keys the minimap resource needs are missing |
| `BAD_NAME` | The resource name cannot be used |
| `BAD_URL` | The address is not http(s) |

What `BAD_PICTURE` stands for is in the look at the picture (`PictureInfo.Problem`).
It is one of `NOT_FOUND`, `NOT_PNG`, `INTERLACED`, `BAD_SIZE`.

### Export: credits and map names

- The credits name the source of the map (the server captured and scanned),
  then the `credit` of every piece of data the map was made with (`WebExport.DataCredits`).
  Each text once.
  - The style.
  - The postal codes and the route numbers, when the labels record counts any.
  - The groups of POI it shows.
  - The icons (`MdiIcons.Credit`), when a point it shows draws an MDI icon.
- The bundled data carries its texts.
  - The PostalCodeMap look, and the markers A-Z and colored dots (Virus_City).
  - The route numbers (GTA Wiki).
- The default postal codes' text (nearest-postal, MIT)
  is the program's own (`PostalCodes.DefaultCredit`).
  It is used when the postal codes come from the default address.
- Map names come from the style's name and, for a map not in English,
  its language (`MapSet.Title`).
  A bundled style's English name, a project style's as written.
- An English atlas map's tile folder has no language (`MapSet.ExportName`).

## The FiveM resource

`resource/fxmapgen-capture` goes onto the server. It comes inside the executable.
`capture/<path>` in `FxMapGenerator.App.csproj`, `Services/CaptureResource.cs`.

- The screens write it as `fxmapgen-capture/` into a folder chosen,
  or hand out a zip holding that one folder.
  When a folder of that name is there, they show the version there and ask before replacing it.
- The executable also starts it (`refresh`, `ensure`)
  and stops it once everything is taken (`stop`),
  through the game's console ("The game" below).
- The executable drives it through the game's console socket with `fxmapgen <sub> ...`
  and reads the lines it prints back.
  Each line starts with `[fxmapgen] `.
- The commands and their lines (protocol 1) are listed at the top of `client/link.lua`.
  `Docs/spec/link-protocol.ja.md` has the details.

### Files

| File | Contents |
|---|---|
| `client/link.lua` | Loaded first. Shared state, the game's console command, requests to the server side |
| `client/env.lua` | The capture environment, and setting the ped down |
| `client/camera.lua` | The camera straight down, and the waits for loading |
| `client/sample.lua` | `hmap`: the height grid of the visible surface |
| `client/scan.lua` | The scans of a block |
| `server/main.lua` | Clearing the world, `hello`, filling up hunger and thirst after a capture |

**`client/link.lua`**

`hello`, `status`, `res`, `ping`.

**`client/env.lua`**

- `env on` / `env off`: the capture environment.
  No NPCs, no HUD, noon, clear weather, no clouds, no fog, fixed exposure, dry ground,
  the ped hidden and frozen.
- `safe`: sets the ped down on the ground.
  - The ped is set down once a ray straight down meets the ground's collision.
  - Where the height it stood at is known, the landing waits up to 10 s
    for the collision or the water surface just below it (within 2 m).
    A floor's or roof's collision can come after the ground's under it.
  - When none comes, it takes a hit found lower.
  - With no hit at all it leaves the ped frozen and answers `safe=0`
    (`fxmapgen safe` tries again).
  - The game's `HasCollisionLoadedAroundEntity` is not used.
    Once the script focus is cleared it stays false for the player's ped,
    even on loaded ground.

**`client/camera.lua`**

`tile` / `cam`.

- The camera straight down.
- Waiting for the scene.
  Cut at 2.5 s over a block of open water (25 of 25 points of a 5 x 5 grid have water).
- Waiting for the collision under the centre.
  On the ground: until a ray down meets it. Over water: none.
- Settling: the game's streaming requests at 0 for `quietMs` in a row,
  and at least 10 frames.
  `quietMs` is the tile command's last argument. Default 1500.
- The `READY` / `FAIL` lines.
  READY carries `quiet=` and the frames per second while settling, `fps=`.
- The beacon's sequence number.

**`client/scan.lua`**

`scan ground` / `scan roads` / `scan stop`. The scans of a block.

- Ground: per 2 x 2 section, the scene and collision are streamed, then it is taken.
  What is taken: 2 rays down every metre (3 with the canopy) and the water surface.
  The streaming goes in this order.
  1. A ray down meets the collision.
  2. The game reports the collision around the ped loaded. 3 s at most.
  3. No streaming requests for 0.3 s. 5 s at most.

  Buildings can come after the ground.
  Right after a jump, they can come even after the game said loaded.
- Roads: streamed at the centre, then on-road every metre and street / zone every 4 m.
- The lines are `MSCAN` lines (format v=1).
  They carry the scan's request number `seq` and the block's name.
- A row's values come in parts of at most 900 characters.
  The game cuts a line the client prints at 1,023.

**`server/main.lua`**

- Clears the world whenever the camera is placed over a block
  (`tile`: for a shot or the height grid).
  Only with the permission `command.fxmapgen` and nobody else on.
  The scans alone do not clear it.
- `hello`.
- Fills up hunger and thirst after a capture.

### Resources stopped while capturing

The resources to stop on the client while capturing come from the server preset.
A preset is `data/presets/<id>.json`, embedded in Core and read as `ServerPresets`.
A project can replace the list (`server.stopResources`).

### Tests without the game

```
luac -p resource/fxmapgen-capture/fxmanifest.lua resource/fxmapgen-capture/client/*.lua resource/fxmapgen-capture/server/*.lua
lua tests/resource/run-tests.lua
```

`tests/resource/fakegame.lua` runs the resource's scripts under plain Lua 5.4.
The natives they call are replaced by a small model. The model holds these.

- A virtual clock of 16 ms frames.
- Threads as coroutines.
- Net events after 30 ms.
- The client's printed lines cut at 1,023 characters, as the game cuts them.
- A made-up world.
  Land with buildings, sea, a pond, a mountain, a patch where streaming never settles.
  - The sea bottom is 40 m down within 500 m of land, 300 m within 1 km and 600 m beyond.
    One block has a sloping deep bottom.
  - On the land: concrete buildings, tarmac roads every 100 m, grass and trees between.
    Street and zone names too.
- Streaming requests that run out 20 frames after the ped moved.
  Bursts of them can be put in.
- Collision that loads a moment after the ped arrives.
  1.5 s after a jump of more than 350 m.
- Buildings' collision that comes after the ground's, when a test asks.
  Also after the game said the collision around was loaded.
- Rays straight down.
- A `HasCollisionLoadedAroundEntity` that stays false once the script focus is cleared,
  as in the game.
- A check that the ped is never unfrozen where it would fall.

Running the tests:

- `run-tests.lua` checks the commands, the values and the form of every line.
  A scan's every point is compared against the made-up world.
- `dotnet test` runs it too when Lua 5.4 is on the PATH (or `FXMAPGEN_LUA` names it).
  It then reads the capture and the scans it emits (`--emit <folder>`)
  with the program's capture and scan readers.
- The version in `fxmanifest.lua` must be the `<Version>` of `Directory.Build.props`
  (a test checks).
- CI runs the syntax check and the tests on Ubuntu with Lua 5.4.
- A release attaches the executables and `SHA256SUMS.txt` only
  (the resource is inside the executables).

## The game: connection, pre-check, capture and scan

The program reaches the game only through the game's console socket (127.0.0.1:29200)
and FiveM's window.

- `src/FxMapGenerator.Core/Capture/`: the part with no Windows dependency.
- `src/FxMapGenerator.App/Game/`: the game of this PC.
- API: [api.md](api.md#game).

### Connection

- `src/FxMapGenerator.App/FxConsole/` is the client copied from FxDeck 0.7.0.
  Only the namespace differs.
- The tests are FxDeck's too.
  Their emulator got a hook (`Responder`) that answers commands with scripted lines.
- The game takes one connection at a time.
  So the program opens one connection to the game's console at a time (`LocalGame`).
  A pre-check and a run's capture and scan do not overlap.

### Lines

`GameLink` sends a command and waits for the lines that answer it.

- While waiting, it sends `fxmapgen ping` when nothing went out for 3 s.
  The game drops the connection after about 5 s.
- The conversation goes into a transcript.
  Height-grid rows only by BEGIN and END (the rows are in the files).
- A tile takes only these as its answer.
  - A READY with its `block=`, with a request number above those of earlier requests.
  - A FAIL.
- An ERROR line ends the wait too.
- Before another try it asks `status` for the number now.
  So a READY of the try before that came after its wait is not taken.
- A scan and a height grid are checked as they arrive.
  A token that is no value, rows that never came, rows without all their points.
- One that came incomplete fails the try.
  The capture and scan takes that item of the block again.

### Window

`GameWindow` handles the window of `FiveM_…GTAProcess`.

- The size of its drawing area, and that area as RGBA by PrintWindow
  (drawing area, full content).
- It works behind other windows and does not take the focus.
- A window moved off the screen is not composed and comes out black.

### Frame checks

`FrameChecks` looks at these.

- The beacon: 4 x 4 px top left. Ready in green, and the low 3 bits of the request number.
- A notification band: flat green in the bottom middle.
- What stands out on open sea: pixels more than 40 off the frame's median,
  gathered in 16 px cells and boxed.
  The sea itself stays within about 15.

Each box knows whether it touches the part of the frame the map uses (`MapArea`).
`MapArea` is the block's square in the middle and 64 px around it.
The sides of the frame (FiveM's own watermark, for example) never reach the map.

### The capture and scan step

`VisitStage`. It is a step in the game, so it runs on one worker.

**Before the blocks**

- The connection.
- hello: the protocol, the same version as the program, the server side's answer,
  the permission, other players.
- A 1920 x 1080 drawing area.
- The resources to stop: the project's list, else its preset's.
  Only those running get `stop`.
- `cl_drawPerf 0`.
- `fxmapgen env on`.

**The settle wait**

It comes from `state/settle.json` (the pre-check's measurement, below), else 1.5 s.
It goes into the run log and is sent with every tile.

**Per block**

The blocks go from the north, each row the other way round.
Only the items a block misses are taken, in turn.

1. For the shot or the height grid: `fxmapgen tile` -> READY.
2. The shot: after 300 ms a frame.
   Up to 4, until the beacon is ready with the request's number.
   A notification band is waited out 5 s at a time, 3 times.
3. The height grid: `fxmapgen hmap 1`.
4. For the scans, then: `fxmapgen scan ground` -> `fxmapgen scan roads`.
   - With `canopy` when a map draws the canopy.
     The canopy alone is taken with the whole ground scan.
   - A block that only misses scans needs no camera and no tile.
     The resource moves the character itself.
5. The files are each written whole before they replace the old.
   - `capture/<block>.png`
   - `.cam.txt` (the READY line)
   - `.hmap`
   - `scan/<block>.txt` (the MSCAN lines).
     When only one scan is taken again, the other's lines stay from the file.
6. The items taken are recorded.
   - A ground scan taken again without the canopy drops the canopy item.
   - On a block marked for retake the old items go and the mark is cleared.

A failed try is repeated twice, with the capture environment put on again.
Without shots, the window size is not checked and `cl_drawPerf` is not sent.

**The height quality**

The project's `heightQuality`. Code: `SurfaceHeights`.

| Value | What is taken |
|---|---|
| `speed` | The height grid only |
| `balance` | The ground scan only (the higher of the hit and the water) |
| `quality` | Both |

- `speed` is for the satellite map alone. With an atlas or road map it is a project error.
- The photos (orthorectification, scale measurement, provisional tiles)
  are placed with `PhotoItems`.
  `quality`: the higher per point.
- The landcover's ground and buildings and the cells' shade and contours
  take `LandcoverItems`.
  `quality`: likewise the higher per point. `balance`: the ground scan.
- null is the default for the maps.
  Satellite alone: `speed`. With an atlas or road map: `quality`.
- It is `quality` also without the satellite map.
  The height grid serves the landcover,
  and with the camera streaming the block first the scans come quicker.
  So the capture and scan takes about as long as the scans alone.
- What the capture and scan takes follows from `Planner.NeedsItem`.
  The satellite map: the shot and `PhotoItems`.
  Atlas and road map: the scans and `LandcoverItems`.

**The estimates**

- They tell two kinds of scans apart.
  - Scans right after the camera (`scanGround`, `scanRoads`).
  - Scans alone (`scanGroundAlone`, `scanRoadsAlone`).
    Slower, as the camera has not streamed the block first.
- They add the camera's move once per block taken with the camera (`cameraMove`).
  On the row of the block's first camera item.
- The blocks taken with the camera are split into groups of neighbouring blocks (the 8 around).
  The move to a block that is not next to the last one is added once per group (`farMove`).
  On the row of the first camera item.
  The scans alone were measured on scattered blocks already.

**After the blocks**

This runs also when stopped at once.

- `fxmapgen env off`. The resource sets the character down.
- `ensure` of the stopped resources, in reverse order.

**Stopping the capture resource**

- When every block of the range is taken, the clean-up ends by stopping the capture resource.
  Taken means none failed and not stopped (`StageEnd.Complete` in `Finish`).
  - `stop` under the name HELLO gave as `res`.
  - Then a `fxmapgen hello` that must go unanswered.
  - `Options.StopAtEnd`. The wait is `StopCheck`.
- A run stopped half-way or with failures leaves it running,
  so the rest can follow at once.
- Without the permission it keeps running, which does no harm: it only acts on commands.
- Either way the run announces `gameDone`
  (`JobSnapshot.announcements`, `StageContext.Announce`).
  - `resource`.
  - `stopped`: `1` stopped / `0` still answering / `kept` not asked to stop /
    `unknown` the connection to the game's console gone.
  - `restarted`: the resources started again.

**When the run stops, and what it leaves**

- In these cases the run stops at the boundary,
  with the reason in the snapshot's `stopReason`.
  The blocks left do not fail one by one.
  - Refused (another player, no permission).
  - The window gone or resized.
  - The connection not coming back.
- The run's folder gets these.
  - The transcript `console.log`, without the rows of the height grids and scans.
  - The list of stopped resources, `stopped-resources.txt`.

**The provisional orthorectification**

For the satellite map the blocks are orthorectified beside the capture.
`ProvisionalOrtho`, the capture step's `Follower`.

- Whenever a block's shot and the heights its photo is placed with (`PhotoItems`) are in,
  a helper worker does two things.
  - It writes the block's z8 tiles and the tiles above it: 4 at z7, one each at z6..z0.
  - It records its orthorectification. `ortho.provisional` in `units.csv`.
- The scale correction is found in this order.
  1. The given one (`--scale`).
  2. The project's (`state/scale.json`).
  3. The bundled default (`ScaleStore.Default`).
     Measured over the whole default range. Field of view 2°.
- After the capture, the ortho step measures the scale as before.
  - When it differs from the `tiles` correction by less than 3 cm at a block's edge,
    those tiles stay and that correction becomes the project's.
    3 cm is a tenth of a z8 pixel (`ScaleStore.Agrees`).
  - Otherwise every block is made again.
- The sea outside the range and the whole of the low-zoom tiles
  are made by the low-zoom step after it.
- When the capture step ends, the helper finishes the block in hand and stops.
  The ortho step makes the rest.

### Pre-check

`Precheck`. The screen's "Check with the game".

- Per check: the result, the values, a sentence (English),
  and `codes` for screens to use their own words (see `PrecheckItem`).
- The checks go in this order.
  1. Connection.
  2. hello.
  3. Window.
  4. The state of the resources to stop.
  5. A test shot. Set as for the capture and scan,
     over the sea at the standard frame's north-west corner (`z8_0_0`).
     - Anything standing out is boxed.
       It fails only in the part the map uses.
       Boxes outside it are counted in `values.outside` and shown.
     - The notification band.
  6. Weather, time and nearby NPCs (`status`).
  7. The settle wait (below).
  8. The game is put back: env off, `ensure` of the stopped resources.
- The results go to the work folder's `logs/precheck-<time>/`.
  `report.json`, `shot.png`, `shot-marked.png` with the map's part and the boxes,
  `console.log`, `settle-<block>-first.png` / `-last.png`.
- The game rows of the prerequisite checks come from the newest one.

**After the capture resource was stopped**

- When a capture and scan has taken every block of the range
  and stopped the capture resource, it keeps the time (`ResourceStop`).
  In the work folder's `state/capture-stopped.json`.
- A pre-check older than that counts as not made.
  - `PrecheckReport.LatestFor` adds `resourceStoppedUtc` to the report.
  - The game rows of the checks go back to no result.
  - The screen shows no report, and asks to start the resource and check again.
- Nothing is kept when the resource still answers after the stop (no permission),
  or when the run was stopped half-way.

**When the run needs no shots**

That is: no map takes them, or every block of the range has them.

- The window, the test shot and the settle wait are not checked.
  Result none, code `notNeeded`.
  The report is fine without them. The screen leaves them out.
- Weather / time / NPCs are not checked either.
  They only show in the photos; the scans' rays meet the map's own collision.
- The check then ends after the resources' states,
  without stopping anything or putting the capture environment on.

### Starting the resource

`ResourceStart`, the screen's "Start the resource".

- When HELLO is answered, nothing is done.
- Otherwise: `refresh`, 3 s, `ensure fxmapgen-capture`, 3 s, HELLO again.
- These are server commands. Only a player with the permission can send them.
- The lines of the game's console during the attempt (sent and received)
  come back as they are.
  The screen shows them, with the likely reasons, when it did not start.
  The likely reasons: another folder name, not on the server, no permission.

### One user of the game at a time

`JobManager.UseGame`.

- The game's console takes one connection.
  So a run's work in the game (the capture and scan), the pre-check
  and the start of the resource never overlap.
- The pre-check and the start take the game when they begin
  and give it back at their end.
- When they cannot, they answer 409.
  - `GAME_BUSY` while a run still has work in the game.
  - `GAME_CHECKING` during a pre-check.
  - `GAME_STARTING` during a start.
- Meanwhile a run with work in the game does not start either
  (`POST /api/jobs` answers the same 409).
  A run without work in the game starts.
- What uses the game by hand goes out as the SSE event `gameUse`.
  `{ "kind": "precheck" | "resourceStart" | null }`.
- The screens grey "Run", "Check with the game" and "Start the resource" meanwhile,
  with the reason.

### The mark that it is on the server

`ResourcePlacement`. `state/capture-resource.json` of the work folder.

- The executable cannot see the server's folders.
  So a mark says the capture resource is there.
- The mark is set in these cases.
  - When the screen wrote the resource (`saved`).
  - When it answered with the executable's version: while starting it,
    in the pre-check, or at the start of a capture and scan (`answered`).
  - By the user (`user`).
- A mark of another version counts as none.
- The `capture` prerequisite (while a capture and scan is left) reads it.

### Measuring the settle wait

`SettleProbe`. It runs in the pre-check.

It measures how long this PC's game still changes the picture
after its streaming requests reached 0.
Distant detail fading in, textures sharpening.

1. The place is the town farther from where the player stood before the pre-check.
   North: Paleto Bay. South: downtown Los Santos. A place not loaded yet.
2. The candidate blocks (built-up, no water, next to each other)
   are shot with `quietMs` 0.
3. Frames are taken every 0.2 s for 3 s after READY.
4. Each frame is compared with the last one, over the part the map uses.
   The block's time is the first frame from which on
   fewer than 0.05 % of the pixels differ by more than 24 levels.
   The transcript also has the shares for 8 and 48 levels.
5. A block that still changes at the end, has water or gives no READY
   makes way for the next candidate.
6. With two blocks measured, the wait is 1.5 times the longer time (rounded up to 10 ms).
   It goes to `state/settle.json`.
7. Otherwise that file is removed, and the capture and scan waits the default 1.5 s.

### Graphics settings

- `RenderSettings`: the values the shots need, the difference,
  and the change of only those values.
  - The values: quality levels, extended distance scaling,
    high-detail streaming while flying, a 1920 x 1080 window, VSync on, ...
  - With VSync the game draws at the screen's rate and leaves the GPU room.
    So its frame rate does not change when other programs use the GPU,
    and the measured settle wait holds.
  - Only the values change. Other lines, their order and the line ends stay.
- `RenderSettingsFile`: handles `%APPDATA%\CitizenFX\gta5_settings.xml`.
  - A backup goes to `%LOCALAPPDATA%\FxMapGenerator\render-backups\` before a change.
  - A backup can be put back.
  - Neither while FiveM runs. It writes its own values back when it closes.

### Tests

`tests/FxMapGenerator.Core.Tests/Capture/FakeGame.cs` is a fake game.
It answers the commands with the resource's lines,
and draws the made-up world with the beacon in its window.

- Faults can be put on single blocks.
  Black frame, the frame of the block before, a notification, a resized window, no answer,
  a late READY, a scene that does not settle, a refusal, HMAP ABORT,
  a scan line cut short as the game cuts a long one.
- For the settle wait it can do the following.
  The measurement has to find the coarse time and ignore the few pixels.
  - Hold READY for the request's `quietMs`.
  - Keep the picture coarse for a while after the streaming ended.
  - Keep a block's picture changing.
  - Change a few pixels in every frame.
- A capture and scan and then the ortho, through the job on the fake game,
  give tiles byte for byte equal to those of the same frames
  written as capture files directly.
- The orthorectification beside the capture gives the same tiles
  as the ortho step with the same correction.
- Tests never use the game of this PC.
  The test host's game is one that is not running.
  The CLI `build` tests pass `--no-game`.
- The window capture test shows a small window
  in the top left corner of the screen for a moment.

### Playing back a capture

The fake game can play back an earlier capture (`Replay` = a capture folder).
It returns the block's frame, READY line and height grid.

`tests/FxMapGenerator.App.Tests/Demo/ReplayDemo.cs` runs the program
with its screens on that game.
Only when the environment variable `FXMAPGEN_DEMO_CAPTURE` is set.
Normal test runs skip it.

```
FXMAPGEN_DEMO_CAPTURE=<capture folder> dotnet test tests/FxMapGenerator.App.Tests --filter ReplayDemo
```

At `http://127.0.0.1:20399/` (`FXMAPGEN_DEMO_PORT`),
open a project whose range lies in that capture and press "Run".
This shows the screens of a capture without the game.

- The data directory is `FXMAPGEN_DEMO_DATA`. Default: `fxmapgen-demo` in the temp folder.
- A block settles in `FXMAPGEN_DEMO_TILE_MS`. Default 1500.
- A file named `stop` in the data directory stops it.

## Screens and the open project

The app has one open project at a time, the same for all browser tabs (`ProjectSession`).
Every change is sent to all tabs as the SSE event `project`.
The recent list goes as `settings`.
API: [api.md](api.md).

With a project open, tabs in the header switch between six screens.
Project settings, plan & progress, FiveM setup & check, map editing, map view, export.
It is plan & progress when opened.
A screen with prerequisites not met shows how many in its tab.

Screen texts: items in a formal written style, help texts polite.
No internal words (the code's stage names, the app's versions).

### The start screen

`src/start/`.

- New project: name, project file, work folder, server preset, maps.
  The maps are satellite map, atlas, road map.
  Its road edits start from the bundled ones.
- Open: the Windows file dialog, shown by the executable.
- Recent projects.

### App settings

`src/shared/AppSettingsDialog.tsx`.

- The window of the header's "⚙ App settings". On the start screen and the project screens.
- The language, the theme, the GTA V and key folders are saved together.
  The folders show what they give as they are typed ("Game files" above).

### The plan & progress screen

`src/project/`.

**The left panel**

- The output maps: satellite map, atlas, road map.
  The atlas has its list of styles. Each is made in the project's map languages.
- "Height quality": under the output maps. Radio buttons speed / balance / quality.
  Those that do not go with the maps are greyed out, with the reason.
  The heading's help carries the whole-map times and the times of adding a map later.
- Minimap.
- Workers: a slider up to the logical processors.

**The map in the middle**

- Leaflet. Game metres as a simple CRS.
- The base is PostalMap or the project's own satellite tiles.
- The block grid is colored by the data held or, after a run, by what the run did.
- The range tools: add or exclude blocks by rectangle or one by one,
  and go back to the default range.
- The list "Add a preset": there while the project adds areas outside the standard map.
  Cayo Perico, Roxwood. One whose blocks are all in the range is shown but cannot be chosen.
  Its window shows the following. Once the blocks are in, the map moves to them.
  - How many blocks go in, land and water.
  - Widening the frame when the preset needs it, asked with the sides and their cells.
    Not when that would pass the limits: then only why.
  - Cayo Perico's roads, offered while the project does not read them.
- The retake tools, on a row of their own:
  mark blocks of the range for retake, by rectangle or one by one.
  A click takes the mark off again. Another tool takes every mark off.

**The run status**

Under the map.

- One line that is always there: what a run would do.
  Or the running step with progress, time left, errors, CPU, memory, workers
  and the run / stop buttons.
- Sections that open and close:
  units in progress (one line per worker), errors with the reason, the log.

**The right panel**

- The job list.
  - Rows open to their parts.
  - A click outlines the row's blocks or cells.
  - What is left: the count and, on a second line, its split into land and water.
  - "in game" and "manual" are marks next to the step.
- The prerequisites not met. The ✗ ones only.
  Each has a link to where it is set: project settings, FiveM setup & check, app settings.
  With none: "Every prerequisite is met".
- The right-hand panel's width is dragged at its edge and kept in the app settings.
  Double-click: the default 440 px.

**The rest**

- Inputs the running steps read are locked, with the reason.
- Help texts show lines starting with `|` as a table (`HelpLayer`).

### The job list during a run

While it follows a run, the to-do table (`plan`) is read again every 5 s too.

A step's row:

| State | Shown |
|---|---|
| While waiting | What the table has left, and the run's time for it. "Waiting" when nothing is left yet |
| Preparing | "Preparing" |
| While running | A bar with done / total and the errors. "Running" for a step of one whole-map unit |
| After that | Done, stopped or error |

- The capture and scan's items (not steps) count down.
  - The bar: what the row had left when the run started (`atStart` of `plan`),
    less what it has left now.
  - The time: the capture and scan's time left (measured),
    shared in the proportion of the items' estimates.
- A row with parts sums them up. Units when they count the same thing, else steps.
- The total is the run's time left (as the run status).
- The two rows of work by hand (before starting FiveM, the check with the game)
  show their state from the prerequisites.

### The run's map

- Steps on blocks (the capture and scan, the orthorectification, the landcover)
  color the blocks.
- During the cells' steps (the preprocessing, the cell rendering) and the minimap,
  the cells or sheets are framed and colored instead.
  The phase of one being worked on is named in its frame. The blocks are drawn faintly.
  After the run: the frames of the last such step.
- The atlas or road map being drawn gets a layer for each cell as soon as the cell is done,
  bounded to the cell.
  The drawing writes z8 to z5, so lower zooms show z5 smaller.
- Once its low-zoom tiles are being made, the layer is the whole map.
- The list of units being worked on moves the map to a cell or sheet too.

### During a capture

- In the run view of a project that makes the satellite map,
  the satellite tiles are laid over the base.
  Blocks appear as they are taken. Finished blocks are only outlined.
- While the job list still has satellite work (orthorectification or low-zoom tiles),
  a map showing the satellite tiles has a note in its lower left corner:
  "Satellite map: provisional (scale not confirmed yet)". The map view too.
- During a run the tiles and the blocks' state are read again every 5 s.

### Map tiles

- Their address carries the project's key
  and a number that grows with each reload (`?p=<key>&v=<n>`).
  The project's key is a short value from its work folder.
- Another project gets new layers.
- A reload lays the new layer over the old one
  and takes the old one away once the new tiles are in (no blinking).
- The app answers tiles with `Cache-Control: no-cache` and an ETag (304 when unchanged).
  Missing tiles are answered with `no-store`.
  The same address serves whichever project is open, and tiles change during a run.

### PostalMap tiles

The postal code map tiles (postal-code-map-tile) share the map frame but not the scale.

- Their z6 tile is 375 m (24 x 36 tiles over the map). Ours is 281.25 m.
- So each of their tiles is 4/3 of 256 px on the map.
  In lb-phone their `resolution` of 6144 x 9216 says so.
- Pictures of that size do not sit on whole pixels.
  Laid side by side, the browser leaves lines between them.
- The layer's tiles are therefore ours (256 px canvases).
  Each draws the parts of their tiles under it (up to 2 x 2)
  on whole pixels (`postalLayer` in `mapFrame.ts`).

### Map editing

`src/edit/`. A switch of roads, POI and styles.

**POI** (`src/edit/poi/`)

- On the left: the folder tree, or the list of POI styles.
  - The folder tree shows groups, points, counts, shown / locked.
  - It can: drag to reorder and move, search, add / rename / delete folders and groups,
    read a CSV or JSON file.
- In the middle: the map.
  The points are drawn in the browser as the maps draw them (`poiLayer.ts`).
  The tools are place, move, select and select area (keys 1 to 4).
  Select area is shared with the roads (`areaSelect`).
- On the right: save, the tools, what is shown,
  and the values of what is chosen or of a POI style.
  - The values of what is chosen: inherited values say "from {name}". "Inherit".
  - The values of a POI style: look, the icon window, PNG, color, size, outline, circle,
    the label beside and its size, samples drawn by the app at z6 and z8.

**Roads**

- The tools: draw, move, select, select area (Ctrl: a traced outline)
  and whole road (keys 1 to 5).
- Remove (added items) / hide (the game's) / show again, undo / redo, save.
- The values of the selection: street name, flags, height. Lanes each way, narrow, width.
  One's own street name is added and renamed in a small window.
- All the values of the game's path data.
- "Import bundled edits…": a window with a check box a group.
- The list of edits: a group of edits, or connected items, in one row.
  Jump, select, revert.
- The edits that do not apply, with the reason.
- The left map's legend, with a check box a kind (what it shows).
- The left and right maps move together.
  The whole map and the view's box are in the right map's corner ("Road editor" above).
- With edits not saved, these ask first:
  leaving the tab, closing the project, closing the browser tab.

**Styles** (`src/edit/styles/`)

On top:

- The style shown. Under the headings bundled styles and project styles.
- The style operations.
  - Duplicate…: a window with the style to copy (bundled, the project's or shared),
    its name and the file name.
  - Rename…
  - Delete: not while a map uses it.
  - Export…: saves the file.
  - Import…: a file chosen. A window asks for another file name when its own is taken.
  - Save as shared style.
- A line under it: that a bundled style cannot be changed.
  Or the style it was made from and its file, and the maps that use it.

On the left, the list of values:

- A search, the groups opened and closed, part headings.
- A row a value: its name, its value, "Changed" when it differs from the base style,
  and tags of what a change rebuilds (preprocessing, region colors, labels).
  The name's help is the table's description.
- The splitter sizes the list.

On the right, the preview ("The style editor's preview" below):

- On top.
  - What the left map is drawn with: the saved values or the base style.
    A bundled style: its saved values only.
  - The labels' language: only when the project makes Japanese maps.
  - The window's size: a slider of four. 500 m, 1 km, 2 km, 4 km.
    Moving it zooms out to fit the window.
- Under it, two maps. Left: the saved values or the base style.
  Right: the values being edited.
  - Leaflet gets the z8 tiles, and the browser makes them smaller further out.
  - They move and zoom together.
  - The zoom goes z2 to z9 in whole steps.
    Between them the browser would leave seams between the tiles.
  - A zoom draws nothing again.
  - Each shows how long its drawing took.
- In the corner of the maps: the whole land.
  A press takes the window around it. The yellow box.
- What is drawn: "Sample land" or "This project".
  "This project" is the project's own data.
  It is offered once the project has the data the preview reads:
  its blocks' scans and landcover, the road shapes and the zones.
  When it has, it is shown at first.
- On the project's data there are also these.
  - A list of places: six recommended ones and the project's own (`previewPlaces`).
    A place without data cannot be chosen.
  - "Add place": the window's middle under a name. At first the zone's name there.
  - "Delete place": one of the project's.
  - "Map below": none, PostalMap, or the project's satellite map when it has tiles.
    The same place under the two maps, moving with them.
  - The small map of the whole is PostalMap with the blocks without data grey.
    A press there is refused.
- A change draws the right map again after 250 ms.
  The new tiles take the old ones' place once they are in.
- A press on a map opens a box of where the point's color comes from.
  - What is drawn there from the top, its color and how much of the pixel it covers.
  - What the ground picture mixes there with the shares,
    and the weights of the colors laid over it.
  - The point's zone and kind of ground.
  - A row's name shows that row of the list (a table's row and cell),
    opened and marked for a moment.
  - It is asked again when the values or the window change. Esc closes it.
- Without the sample land it is made first, its progress shown.
  Or why it failed, with "Try again".
- The place, the window's size, the left map's values, the language and the view
  stay while the app is open.

The controls of the values:

- A color: its box opens the picker. A change is a value at once.
  - A saturation and brightness square, a hue bar, #, R, G, B.
  - The eyedropper, where the browser has EyeDropper.
  - The colors the style uses.
  - The colors used last in this browser (`localStorage`).
- A number: slider, field, unit. Put on its step.
  A `percent` value shows as 0 to 100 % and is stored as 0 to 1.
- A choice: buttons up to three, else a list.
- A check, checks.
- A font: this PC's fonts, with a sample in the row's language.
  The Japanese font's row only when the project makes Japanese maps.
  A font the PC does not have stays, marked so.
- A text: empty takes the value away.

The four tables come under their rows.

- Per zone: the zones' names from the project's game files, else their codes.
  A zone search.
  Region and buildings' color from a list, empty = the general rule.
  The postal codes' size, empty = the default.
- Depth bands: the depth, the sand and rock bed colors, the opacity.
  A band split in two or merged into the next.
  A depth only between its neighbours'.
- Lights: direction, weight, add, remove (one stays), a small drawing of them.
- Short forms: a short form is taken when its field is left, never empty or twice.
  The word written out. Add, remove.

Changes and saving:

- A cell that differs from the base is tinted. A row has "Revert".
- The bar has undo and redo (Ctrl+Z, Ctrl+Y).
  Changes of one value within a second are one step.
- The bar has save, with the unsaved mark and one line of what saving rebuilds.
  That line is the marks of the rows changed. Nothing when no map uses the style.
- A bundled style's controls cannot be used (the reason says why).
- With changes not saved, these ask first:
  choosing another style, leaving the tab, closing the project, closing the browser tab.
  Meanwhile the style operations
  (duplicate, rename, delete, export, import, save as shared) cannot be used.

### Project settings

`src/settings/`.

- The name, the project file.
- The work folder: shown, with "Open the folder". It is changed in the project file.
- The areas outside the standard map.
  - A check box "Include areas outside the standard map" = `range.extraCells` not null.
  - While on: the top, bottom, left and right lists, saved as chosen.
    - A number over the limits cannot be chosen.
      The limits are 2 top and bottom together, 4 left and right.
    - A number that leaves blocks of the range outside the frame cannot be chosen either.
      A line says why (the project's `frameNeeded`),
      and the check box cannot be turned off (`rangeOutside`).
  - A line on the limits and the map's size in blocks.
  - A check box "Read the roads of Cayo Perico and replace the game's island map"
    = `cayoPerico`.
- The server.
  - The preset.
  - The resources stopped during capture and scan:
    the list, remove, add by name, back to the preset's list.
  - The game's console.
  - The server's resource folders: the list with ✓ / ✗, remove, browse, add.
- The game files (for the atlas or road map):
  what the app settings give, and "Open the app settings".
- The labels (for the atlas).
  - The map languages: English always, Japanese a check box (`maps.atlas.languages`).
  - Where the postal codes come from, and their state.
  - The window to change it: default, another address, a file.
  - "Fetch now" while not fetched.
  - Each font, with whether it is installed and the maps using it.
- The free disk space.

### FiveM setup & check

`src/precheck/`. The job list's rows of work by hand open it too.

**The section "Before starting FiveM"**

- The resource for the server: name and version, "Save to a folder…", "Download as zip",
  the note to keep the folder name,
  the check "Put on the server" and how the mark came about.
- The graphics settings: the table of differences to the shots' values,
  put them in after a backup (after asking), put a backup back, read again.

**The section "Once FiveM has joined the server"**

- Starting the resource: the result, a note for another name or version,
  and, when it did not start, the likely reasons and the output of the game's console.
- The check with the game.
  - A button that runs the pre-check. Not while a run uses the game.
  - Each check with ✓ / ✗ / —, the values found and what to do.
    What to do is the result's `codes` in the screen's words, filled from `values`.
  - The test shot.
    The part the map uses is outlined in white, what stood out in it is boxed in red,
    and what stood out outside it in amber.

### The notice

- When the run has a `gameDone` announcement, this shows over the run status.
  - One line: "The work in the game is done. FiveM and the server can be closed."
    While the run goes on: that the other steps go on without the game.
    When the resource could not be stopped: that too.
  - "Details": what was stopped, how to stop it when the app could not,
    the resources started again, the folder left on the server.
  - "Close".
- It asks `GET /api/game/fivem` every 5 s and goes away once FiveM is closed.

### Map view

`src/view/`.

- A base and a map laid over it (maps with tiles, PostalMap, none),
  with the overlay's opacity.
- The block, cell and minimap-sheet grids.
- Jumps to known places.
- What lies at a clicked point.
  - Coordinates, block with its class and data, satellite tiles up to date, cell,
    minimap sheet, z8 tile.
  - From the block's scans: the material with its class, the height and the water,
    the zone, the nearest street, and whether the point is on a road.
  - From its landcover: the ground class and a building.
- The choices stay when switching screens.
- A map chosen that has no tiles in the project opened shows,
  once the list is in, as the lists do: PostalMap.
  The map laid over: none. The choice stays for the projects that have the map.

**Compare with before**

- It covers the maps whose tiles were written over since the last export
  (`GET /api/project/before`).
  Per map: the tiles kept, the z8 tiles, the places they make (the largest first).
- With it on, the map now is the base,
  and the map before is laid over it (`/api/project/before-tiles/…`).
  The map before is the kept tile, else the tile as it is.
- Buttons before / now (the X key swaps them), and the opacity of the map before.
- The changed z8 tiles are outlined in red.
  Grey where the change does not show (`BeforeTiles.Faint`).
  That is: every pixel within 2 of 256 steps of before.
- Jumps to the places.
  Those where no change shows are listed apart, in a last section that opens and closes:
  "Places whose change does not show".
- The base and the overlay cannot be changed meanwhile.

### The export screen

`src/export/`.

- The output folder: browse, open in Explorer.
- The web tiles per map with tiles: count, size, up to date.
- The maximum zoom: z8, z7, z6, with their counts.
- The format: folder or zip.
- The address they will be served from.
- The minimap resource and its name.
- Why the choices cannot be exported, and the size to write.
- Starting it shows the progress, the stop buttons and the log.
  Afterwards: the folder's record (the web part and the resources).
- Every change of the choices is checked through the API.
- Convert an edited picture (`ConvertPicture.tsx`): the picture file, the original map, the outputs,
  the Convert button.
  - It stands under the Export button. It is a job of its own, apart from an export.
  - A chosen picture shows its size and zoom level, or why it cannot be converted.
  - When the picture's file name tells the original map, that map is chosen.
  - The progress and the record are shown where the export's are.

## Hover help

Every screen item has a short help text (Japanese and English).

- Screens use the controls in `src/FxMapGenerator.Web/src/shared/controls/`
  (`Button`, `Select`, `Info`, ...).
  The controls require a `help` key.
- The texts live in `src/shared/locales/ja.ts` (the canonical key set) and `en.ts`.
- `npm run build` first runs `scripts/check-ui.mjs`. It fails on either of these.
  - A screen uses a raw `<button>`, `<input>`, `<select>`, `<textarea>`, `<a>` or `<th>`
    outside the controls.
  - A help text is not used by any item.
- The type checker fails when `en.ts` misses a key.

## Guides

Each screen has a short guide (driver.js).
Its main parts are lit one by one, each with a box.

- The box holds a title, a text, Back, Next and Done, and n / m.
- The arrow keys and Esc work. The dark around it closes it.
- The box is in the app's colors, light and dark.

### Asking whether to show it

- The first time a screen with a guide is opened on this PC,
  a small box at the bottom right asks whether to show it.
  Show, Later, Don't ask again.
- It asks once what the guide needs is on the screen,
  and has been for 0.8 s with no window open.
  What it needs is the first step's item and the guide's own condition.
  - Plan & progress: the job list.
  - FiveM setup & check: the resource panel.
  - The style editor: both preview maps drawn.
- The answer goes into `settings.json` (`guides`).
  Guide id -> `seen` (shown to its end, or closed on the way) or `never`.
- Later, or moving on without an answer, keeps nothing.
  The page asks again once it is opened again.
- The header's "? Guide" (start screen and project screens)
  shows the guide of the screen shown, at any time.

### What works during a guide

- A guide only explains. The item lit and the rest of the page cannot be pressed
  (`disableActiveInteraction` and driver.js's CSS).
- While the page carries driver.js's `driver-active` mark,
  the screens' keys and the hover help wait.
  The screens' keys are the editors' tool keys, Delete, Esc, Ctrl+Z and Ctrl+Y,
  and the map view's X.
- A screen's key handler that must not act under a guide looks for `.driver-active`,
  as it looks for `.modal-backdrop`.

### Writing a guide

- The guides are `src/FxMapGenerator.Web/src/guide/guides.ts`.
  The ids are the screens: `start`, the project's tabs,
  the editing kinds `roads`, `poi`, `styles`.
- Each step names its item, and may give its box's side.
  The item is a hover help key (`[data-help="..."]`),
  or `AREA` for the element marked `data-guide="<step name>"`
  (a panel, a map, a group of items).
- The texts are `guide.<step name>.title` and `.text` in the dictionaries.
- A step whose item is not on the screen is left out before the guide starts
  (so the count stays right). One that goes meanwhile is skipped.
- `GuideLayer.tsx` asks and shows the guides.
  `guide.css` puts driver.js's box into the app's colors.
- `scripts/check-ui.mjs` checks the guides too.
  - Every step's help key is used by a screen (the table itself does not count).
  - Every `AREA` step has exactly one `data-guide` element.
  - No step is there twice.
  - Every guide text belongs to a step.
- To add a step: put it in the table with its item's help key
  (or mark the element with `data-guide`).
  Put its title and text in both dictionaries.

## Single-file executables

```
dotnet publish src/FxMapGenerator.App -c Release -r win-x64 --self-contained true  -p:PublishSingleFile=true -o out/self-contained
dotnet publish src/FxMapGenerator.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out/slim
```

`FxMapGenerator --version` prints the bundled library versions.
It loads the native Skia library, so it also shows that the single-file bundle is complete.

### The executable's icon

The executable's icon is the picture of the web page's favicon.

- The executable's icon: `src/FxMapGenerator.App/app.ico`, the App's `ApplicationIcon`.
- The web page's favicon: `src/FxMapGenerator.Web/public/favicon.svg`.

Make it again after changing the favicon.

```
dotnet run --project tools/FxMapGenerator.DevTools -c Release -- make-icon src/FxMapGenerator.Web/public/favicon.svg src/FxMapGenerator.App/app.ico
```

- It draws the SVG at the sizes Windows uses (16, 20, 24, 32, 40, 48, 64, 256 px)
  and packs the PNGs into one .ico.
- It draws only what the favicon uses:
  a square viewBox, `rect` and `path` with their fill and stroke.
  It stops at any other element or attribute.

## Style editor

- The styles are the three bundled ones (`MapStyle.Builtin`) and a project's own.
  A project's own are the user styles of `style-format.ja.md`.
- A user style is a bundled atlas style
  with the values it changes put over it (`StyleChanges`).
  `StyleChanges` puts changes over a style, gives the changes between two styles,
  and reads and sets a place's value.
- Shared styles are user style files in `styles\` of the app's settings folder.
  `%LOCALAPPDATA%\FxMapGenerator\styles\` by default. "Duplicate…" can start from them.

| What | Code |
|---|---|
| Reading, checking and writing the file | `UserStyleFile` |
| Listing a folder of them, writing into a project's | `ProjectStyles` |
| The screen's operations | `ProjectSession.Styles.cs` |
| The API | `StyleEndpoints` |

- The screen's operations read the project file again before a change,
  as for the road edits.
- The table the screen lists the values from is `data/style-schema.json` (`StyleSchema`).
  Its form is in `style-format.ja.md`.
- When a row is added, `StyleSchemaTests` checks it against the styles' values
  and the steps' rules of what to make again.
- The rows' hover help is the table's description as it is
  (a control's `help` as `{ text }`, `data-help-text`).

## The style editor's sample land

The style editor's preview shows its changes on a made-up stretch of coast
(the sample land).

- The land is generated by the developer tools and bundled as data (`data/sample-island/`).
- The app only turns it into a project of its own (`SampleIsland.Create`).
  The scans of the cell's 64 blocks, `game/paths.json` and `names.json`,
  the postal codes and the POI, the state.
- So the steps without the game draw it the way they draw any project.
- Nothing of the game is in it.
  The zone codes are the game's, so the styles' zone tables apply to them.
  Every name is made up.

### How the land is made

Code: `tools/FxMapGenerator.DevTools/Island/`. The same seed gives the same result.

- `Sketch.cs`: a rough layout. Only the large shapes come from it.
  The coast, the mountains' edge and summit, the rivers and the lake, the town,
  the highway and its branch, the railway, the avenue.
- `Layout.cs`:
  - The sketch's shapes as fields over the cell.
    How far inland a point is (the coast wandering with noise),
    how deep into the mountains, in the town or on the headland, how far from a river.
  - The rivers' meandering middle lines
    (sine-generated curves, after Langbein and Leopold 1966), and from them the uplift.
- `Terrain.cs`, `SampleLand.cs`:
  - Tectonic uplift against fluvial erosion.
    The stream power law, solved implicitly after Braun and Willett 2013, on a 12 m grid.
  - Then 2 m detail and thermal erosion.
  - The sea bed gets deeper with the distance from the coast, stretched by noise.
  - The lake is where the ground sank.
    The rivers are cut along the layout's middle lines.
  - The ground's material comes from height, slope, wetness and the sea.
- `Roads.cs`:
  - Each way is the cheapest path over the ground, smoothed.
    The path is found by A* with grade, water and layout costs.
  - A height profile by dynamic programming.
    Embankments and cuttings, decks where high over the ground or water,
    tunnels where deep under it.
  - A Y junction, a diamond interchange.
  - The ground is graded under the roads.
- `Town.cs`:
  - Streets are traced along a field of directions
    (the coast's, the avenue's and the highway's; after Chen et al. 2008).
  - Lots along them by district (tall blocks, lower blocks, houses with yards and pools),
    parks, car parks.
  - Fields on the flat low land. Postal codes on buildings.

### Making the bundled data again

Make the bundled data again after changing the tools, then check the project it makes.

```
dotnet run --project tools/FxMapGenerator.DevTools -c Release -- sample-land-bundle data/sample-island c
dotnet run --project tools/FxMapGenerator.DevTools -c Release -- sample-island <empty folder>
FxMapGenerator build <empty folder>/sample-island.fxmapgen.json --no-game --workers 2
```

- `sample-land <folder> <a|b|c>` makes the project straight from the tools,
  without the bundle.
  `--no-roads` for the land alone.
- `a`, `b` and `c` are seeds. The bundle is `c`.

## The style editor's preview

The preview draws a window of the sample land or of the open project's own data,
without writing files (`StylePreview`).

- The window's sizes are 500 m, 1 km, 2 km, 4 km (`StylePreview.Sizes`).
- It is drawn the way the cells are drawn (`CellPainter`, the z8 scale).
- A window is the z8 tiles its square touches (numbered as the map's tiles).
  Only those with a block of the range are drawn.
- Per block, its tiles in the window are drawn as one piece on the workers
  (`CellPainter.DrawArea` may be called at once from many threads).
  They are cut into 256 px tiles and kept as PNG (a quick level) in memory.
- The browser gets the z8 tiles alone and makes them smaller further out.
  That is for checking the style; it is not the exported maps' low-zoom tiles.

### The sample land

App `SampleLand`.

- Made in `sample-island\` of the app's settings folder,
  the first time the preview is opened.
- The bundled data is made a project (`SampleIsland.Create`),
  and its steps without the game run up to the cells' data.
  No cell drawing, low-zoom tiles or minimap. About 42 MB.
- With the open project's workers, else half the CPUs.
- Once made, `sample.json` holds the app's version and the SHA-256 of the bundled data.
  When either differs it is made again the next time.

### The project's data

App `ProjectSession.Preview`. A `StylePreview` of the open project.

- Made again when the project file or its work folder's `state/stages.json` changed.
- It draws the blocks of the range with their scans and landcover only
  (`StylePreview.ReadyBlocks`).
- It waits for the road shapes and the zones (`StylePreview.Waiting`).
- The recommended places are `StylePreview.Recommended`.
- The windows' data kept holds at most 300 blocks.
  The oldest go first, with the values and painters made for them.
  A 4 km window of a town takes some 250 blocks and 5 to 7 GB.

### Workers

- Each drawing (and each point asked about) takes the open project's parallel setting
  (`parallel`) at that moment (`FixedParallel`).
  There is no memory watch as the jobs have.
- The labels' placing and the inside of a block's drawing run on one thread.

### What is made and kept

- A window's data: the range's blocks within 160 m of it are made grids and heights,
  as the cells' data is.
  160 m is for the blurs of the ground picture and the shading.
  - The blocks are read on the workers (`CellGrids.Build`, `CellPrepStage.ShadeHeights`).
  - It is the same whatever the style.
  - Four sets of blocks are kept.
    A larger window over the same blocks makes nothing again.
- Made and kept per set of the style's values. Four of each kind.
  - The ground picture: per `CellPrepStage.GroundValues`.
  - The shading: per `CellPrepStage.ShadeValues`. The strength is the drawing's.
  - The layers: per `CellLayers.Sets`.
  - The region colors: per `regions` section.
    Computed three blurs around the place,
    in a window of `RegionField.Land4` and `ZonesOnFrame`.
  - The labels: per placement key and language.
    The project's labels file when there is one,
    else placed over the whole land as the labels step places them.
- A color or a width only draws again.
- The painters (`CellPainter`: paths and paints) are kept for the last two sets of values too.
  So a point of a window drawn is told at once.
- For each side of the screen,
  a newer request stops an older one still drawing (`SUPERSEDED`).

### Where a point's color comes from

`StylePreview.Pick`.

- Each part of the `CellPainter`'s drawing order carries what it paints (`StepTag`).
  - The parts: the background, the ground picture, the ground layers, the shading,
    the canopy, water, sea bands, buildings, contours, railway,
    the roads (`RoadDrawing`'s parts), the labels and POI.
  - `StepTag`: kind, color, road class, paint, band and bed, text.
- Each is drawn alone on a surface of that one pixel.
  The ones that paint it are taken with the part they cover (`CellPainter.Pick`).
  The shading is taken with its factor.
- They are given from the top down to the first that covers the pixel whole.
- A building adds its color's rule by the point's zone:
  the zone table, the region, the rule for none.
- The ground picture adds what it mixes there (`GroundRaster.Explain`).
  - Blended: the kinds of ground by their Gaussian weights within the blend's reach.
  - Regional: the regions of the land around by the blur's weights
    (the sea region with no land near),
    and the weights of the tones and the trees' darkening.
- The screen finds the table's rows (a table's row and column) for each,
  and shows the row when pressed.

### Measuring the time to draw again

How long each kind of change takes to draw again is measured with the command below.

```
dotnet run --project tools/FxMapGenerator.DevTools -c Release -- preview-timings <project file> <x> <y> <out folder> [workers]
```

- On a 500 m window: the first drawing, the same values, a color, the ground's values,
  the shading's, the layers, the labels, the language, the region colors, another place.
- Then the 1, 2 and 4 km windows, a color change on 4 km and telling a point's color.
- It prints the seconds of what each made, the tiles' count and the process's memory.
- The first drawing's tiles go to `<out>/tiles/8/<x>/<y>.png`.
- The project can be the sample land the app made or a project with its data.
