# gta-toolkit (vendored)

Source: https://github.com/carmineos/gta-toolkit — commit `ff555267fb3231d8c8822449480d3ef7f467d0b2` (2023-11-28), MIT (see LICENSE.md).

Only `RageLib` and `RageLib.GTA5` are included. Sources are unmodified; the only addition is `Directory.Build.props`
(target framework raised from net8.0 to net10.0, warning suppression).

FxMapGenerator uses it to write texture dictionaries (`.ytd`, RSC7 resources) for the minimap, and to read the game's RPF
archives for the road and name data. `GTA5Constants.Generate` is not used because the current GTA5.exe no longer
contains the NG keys in plain form; the keys are loaded from files the user provides.
