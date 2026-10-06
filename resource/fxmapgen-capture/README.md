# fxmapgen-capture

[日本語](README.ja.md)

The in-game side of [FxMapGenerator](https://github.com/Acc-Off/FxMapGenerator): it takes the straight-down shots and
the height grids that FxMapGenerator turns into a satellite map, and scans the ground and the roads (materials, water,
street and zone names) for the atlas and the road map. FxMapGenerator drives it through the game's console;
it also starts and stops it.

## Install

1. FxMapGenerator comes with this resource. On its FiveM setup & check screen, "Resource for the server" writes the
   `fxmapgen-capture` folder into a folder you choose: the server's `resources` folder when the server is on the same
   PC or a shared folder. For another server, download the zip there and unpack it into the server's `resources`
   folder. A category folder such as `resources/[fxmapgen]/` is fine. Keep the folder name `fxmapgen-capture`:
   FxMapGenerator starts and stops the resource by it.
2. Once FiveM has joined the server, "Start the resource" on the same screen sends `refresh` and
   `ensure fxmapgen-capture` from the game console. It does not need a line in `server.cfg`. Without the permission to
   send server commands, write `ensure fxmapgen-capture` into `server.cfg` and restart the server instead.
3. The player who captures needs the permission `command.fxmapgen`. Admins allowed `command` (the usual
   `add_ace group.admin command allow`) have it already. For anyone else:
   `add_ace identifier.fivem:<id> command.fxmapgen allow`.

When the capture and scan are done to the end, FxMapGenerator stops the resource (`stop fxmapgen-capture`); after a run
stopped half-way it stays running, so the rest can follow at once. The folder stays on the server: remove it when you
no longer need it.

## What it does to the server

- Each time the camera is placed over a block (for a shot or the height data) it **deletes every vehicle and NPC on the
  server**, the ones other players left out too. It refuses while anyone else is on the server. Capture on a copy of
  the server, or at a time nobody else plays.
- During the capture the capturing player's character is hidden, frozen and kept alive; afterwards it is set down where
  it stood, and its hunger and thirst are filled up (Qbox and QBCore).

## If a capture was cut off

If FxMapGenerator stopped in the middle and the character stays hidden or frozen, type into the game console (F8):

```
fxmapgen env off
```

or, when that answers `already=1`:

```
fxmapgen safe
```

The character waits frozen (10 s at most) until the floor or ground right under where it stood has loaded, then stands
on it; otherwise it is set down on what was found lower, and if nothing was found it stays frozen (`safe=0`: run
`fxmapgen safe` again).
