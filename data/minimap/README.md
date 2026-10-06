# Minimap resource parts

The files the minimap export puts next to the texture dictionaries it makes (`Core/Export/MinimapResource.cs`).

| File | What | Source |
|---|---|---|
| `ydd/minimap_<r>_<c>.ydd` (65) | Empty drawables with the names of the game's minimap drawables, so the game's own lines are not drawn over the new pictures | The postal code map minimap by Virus_City (first release, [cfx.re forum](https://forum.cfx.re/t/release-postal-code-map-minimap-new-improved-v1-3/147458)); the author allows releasing versions with credit |
| `extra-map-tiles/` | Extra Map Tiles 3.0.1: draws texture dictionaries beyond the game's 2 x 3 minimap sheets on the pause map and the radar (its own `minimap_main_map.gfx`), puts invisible blips at their corners so the pause map reaches them, and can replace the radar masks (`radar_masks.ytd`). The export writes its `config.lua` (one tile per cell outside the standard frame) and `fxmanifest.lua` | [alexlicuriceanu/extra-map-tiles](https://github.com/alexlicuriceanu/extra-map-tiles) by Alex Licuriceanu (L1CKS), the release zip of tag [v3.0.1](https://github.com/alexlicuriceanu/extra-map-tiles/releases/tag/v3.0.1) (`extra-map-tiles-3.0.1.zip`, md5 `7db14c15f8144c9e14bb37474dc4c23f`), MIT License (`extra-map-tiles/LICENSE`). Kept byte for byte (`.gitattributes`): `client.lua`, `exports.lua`, `scaleforms.lua`, `utils.lua`, `LICENSE`, `stream/minimap_main_map.gfx`, `stream/radar_masks.ytd`. Left out: its sample tiles, `images/`, README, `config.lua` and `fxmanifest.lua`. Later versions of the repository are under another license; this one is the MIT release |

The exported resource's `CREDITS.txt` names both, and its `extra-map-tiles-LICENSE.txt` is Extra Map Tiles' license.
