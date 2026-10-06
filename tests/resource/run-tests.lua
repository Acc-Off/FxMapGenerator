-- Tests of the fxmapgen-capture resource without the game: its scripts run in the fake game (fakegame.lua), and the
-- tests check the lines it prints for FxMapGenerator (their form and their values) and what it does to the game.
--
--   lua tests/resource/run-tests.lua [--emit <folder>] [name filter ...]
--
-- --emit writes the capture of block z8_60_132 the way FxMapGenerator saves it (<block>.cam.txt = the READY line,
-- <block>.hmap = the HMAP lines without the prefix, <block>.scan.txt = the MSCAN lines of its ground and road scans), for
-- the C# tests that read them with the capture and scan readers.
-- Exit code 0 when every test passes.

local here = (arg and arg[0] or ''):match('^(.*[/\\])') or './'
package.path = here .. '?.lua;' .. package.path
local FakeGame = require('fakegame')
local RESOURCE = here .. '../../resource/fxmapgen-capture'

local emitDir, filters = nil, {}
do
    local i = 1
    while arg and arg[i] do
        if arg[i] == '--emit' then emitDir = arg[i + 1]; i = i + 2 else filters[#filters + 1] = arg[i]; i = i + 1 end
    end
end

-- ---------------------------------------------------------------- helpers
local PREFIX = '[fxmapgen] '

local function newGame(opts) return FakeGame.new(RESOURCE, opts) end

local function strip(line) return line and line:sub(#PREFIX + 1) end

-- key=value pairs of a line (after the prefix and the leading words)
local function kv(line)
    local t = {}
    for k, v in strip(line):gmatch('([%a_][%w_]*)=(%S+)') do t[k] = v end
    return t
end

local function eq(actual, expected, what)
    if actual ~= expected then
        error(('%s: expected %s, got %s'):format(what, tostring(expected), tostring(actual)), 2)
    end
end

local function check(cond, what)
    if not cond then error(what, 2) end
end

local function num(v) return tonumber(v) end

-- the player's ped was never unfrozen where it would have fallen (high up, or before the collision had loaded)
local function noFall(game)
    eq(#game.falls, 0, 'falls (' .. table.concat(game.falls, '; ') .. ')')
end

-- sends a command and waits for the first line after it that matches pattern (a Lua pattern on the line without the prefix)
local function ask(game, command, pattern, maxMs)
    local mark = game:mark()
    game:command(command)
    local line = game:waitLine('^%[fxmapgen%] ' .. pattern, maxMs or 30000, mark)
    check(line, ('no line /%s/ after "%s" (got: %s)'):format(pattern, command, table.concat(game:linesSince(mark), ' | ')))
    return line, mark
end

-- the lines between mark and the first one matching pattern (inclusive)
local function linesUntil(game, mark, pattern, maxMs)
    local line = game:waitLine('^%[fxmapgen%] ' .. pattern, maxMs or 60000, mark)
    check(line, 'no line /' .. pattern .. '/')
    local out = {}
    for _, l in ipairs(game:linesSince(mark)) do
        out[#out + 1] = l
        if l == line then break end
    end
    return out
end

local function block(z, tx, ty)
    local size = ({ [8] = 70.3125 })[z] * 4
    local bx, by = tx // 4, ty // 4
    local x0, y0 = -4140.0 + bx * size, 8400.0 - by * size
    return { x0 = x0, y0 = y0, size = size, cx = x0 + size / 2, cy = y0 - size / 2 }
end

local function waterPoints(world, b)
    local n = 0
    for i = -2, 2 do
        for j = -2, 2 do
            if world:water(b.cx + i * 0.2 * b.size, b.cy - j * 0.2 * b.size) then n = n + 1 end
        end
    end
    return n
end

local function f(fmt, v) return (fmt):format(v) end

local EMIT = {}

-- ---------------------------------------------------------------- tests
local tests = {}
local function test(name, fn) tests[#tests + 1] = { name = name, fn = fn } end

test('manifest lists the scripts in load order and every one compiles', function()
    local m = FakeGame.readManifest(RESOURCE)
    eq(table.concat(m.client, ','), 'client/link.lua,client/env.lua,client/camera.lua,client/sample.lua,client/scan.lua', 'client scripts')
    eq(table.concat(m.server, ','), 'server/main.lua', 'server scripts')
    eq(m.name, 'fxmapgen-capture', 'name')
    check(m.version and m.version:match('^%d+%.%d+%.%d+$'), 'version x.y.z: ' .. tostring(m.version))
    eq(m.lua54, 'yes', 'lua54')
    for _, list in ipairs({ m.client, m.server }) do
        for _, file in ipairs(list) do assert(loadfile(RESOURCE .. '/' .. file)) end
    end
end)

test('hello reports the protocol, the version and what the server says', function()
    local game = newGame()
    local line = ask(game, 'fxmapgen hello', 'HELLO ')
    local v = kv(line)
    eq(v.proto, '1', 'proto')
    eq(v.ver, game.manifest.version, 'ver')
    eq(v.res, 'fxmapgen-capture', 'res')
    eq(v.server, game.manifest.version, 'server')
    eq(v.ace, '1', 'ace')
    eq(v.players, '1', 'players')
    eq(v.build, '3258', 'build')
    eq(v.capture, '1', 'capture')
    check(game.now < 200, 'answered within a few frames, took ' .. game.now .. ' ms')
end)

test('hello without an answer from the server side says server=none after 3 s', function()
    local game = newGame({ noServer = true })
    local line = ask(game, 'fxmapgen hello', 'HELLO ')
    local v = kv(line)
    eq(v.server, 'none', 'server')
    eq(v.ace, '0', 'ace')
    eq(v.players, '-1', 'players')
    check(game.now >= 3000, 'waited 3 s, took ' .. game.now .. ' ms')
end)

test('status before env on', function()
    local game = newGame()
    local v = kv(ask(game, 'fxmapgen status', 'STATUS '))
    eq(v.env, '0', 'env'); eq(v.ready, '0', 'ready'); eq(v.busy, '0', 'busy'); eq(v.hmap, '0', 'hmap'); eq(v.seq, '0', 'seq')
    eq(v.weather, 'CLOUDS', 'weather'); eq(v.hour, '8', 'hour'); eq(v.minute, '30', 'minute')
    eq(v.peds, '10', 'peds (the player not counted)'); eq(v.vehicles, '8', 'vehicles')
    eq(v.near_peds, '10', 'near_peds'); eq(v.near_vehicles, '8', 'near_vehicles')
    eq(v.health, '200', 'health'); eq(v.dead, '0', 'dead'); eq(v.frozen, '0', 'frozen')
    eq(v.px, '200.0', 'px'); eq(v.py, '-900.0', 'py'); eq(v.block, 'none', 'block')
end)

test('env on sets up the capture environment every frame', function()
    local game = newGame()
    ask(game, 'fxmapgen env on', 'ENV on$')
    game:run(100)
    local mod = game.tc.mods['fxmapgen']
    check(mod, 'timecycle modifier fxmapgen created')
    eq(game.tc.active, 'fxmapgen', 'active modifier'); eq(game.tc.strength, 1.0, 'strength')
    eq(mod.fog_density[1], 0.0, 'fog_density'); eq(mod.fog_haze_alpha[1], 0.0, 'fog_haze_alpha')
    eq(mod.far_clip[1], 20000.0, 'far_clip')
    eq(mod.postfx_exposure_min[1], -3.0, 'exposure min'); eq(mod.postfx_exposure_max[1], -3.0, 'exposure max')
    eq(mod.water_drying_speed_mult[1], 1000.0, 'drying speed of wet ground')
    eq(game.weather, 'EXTRASUNNY', 'weather'); eq(game.weatherOverride, 'EXTRASUNNY', 'weather override')
    eq(game.clockOverride[1], 12, 'hour'); eq(game.rain, 0.0, 'rain')
    eq(game.clouds.layer, nil, 'cloud layer unloaded'); eq(game.clouds.opacity, 0.0, 'clouds invisible')
    game.clouds.layer = 'Stormy 01' -- the game loads another layer during the capture
    game:run(32)
    eq(game.clouds.layer, nil, 'a cloud layer loaded meanwhile is unloaded')
    check(game.calls.HideHudAndRadarThisFrame >= 5, 'HUD hidden every frame')
    check(game.calls.SetPedDensityMultiplierThisFrame >= 5, 'no peds every frame')
    eq(game.last.SetPedPopulationBudget[1], 0, 'ped budget'); eq(game.maxWanted, 0, 'max wanted level')
    eq(game.ped.frozen, true, 'frozen'); eq(game.ped.visible, false, 'visible'); eq(game.ped.invincible, true, 'invincible')
    game.ped.health = 150
    game:run(32)
    eq(game.ped.health, 200, 'health held at 200')
    local ready, seq = game:beacon()
    eq(ready, false, 'beacon red'); eq(seq, 0, 'beacon seq')
    local v = kv(ask(game, 'fxmapgen status', 'STATUS '))
    eq(v.env, '1', 'status env'); eq(v.weather, 'EXTRASUNNY', 'status weather'); eq(v.hour, '12', 'status hour')
    ask(game, 'fxmapgen env on', 'ENV on$') -- a second env on is answered too
end)

test('env on in a vehicle gets the ped out first', function()
    local game = newGame({ inVehicle = true })
    ask(game, 'fxmapgen env on', 'ENV on$')
    eq(game.inVehicle, false, 'out of the vehicle')
end)

test('tile over land: READY, the camera and the beacon; hmap: the height grid', function()
    local game = newGame()
    local w = game.world
    local b = block(8, 60, 132)
    local ready = ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'READY ')
    local v = kv(ready)
    local h = 1.06 * (b.size / 2) / math.tan(math.rad(2) / 2)
    local gz = w:surface(b.cx, b.cy)
    eq(v.seq, '1', 'seq'); eq(v.x, f('%.4f', b.cx), 'x'); eq(v.y, f('%.4f', b.cy), 'y')
    eq(v.gz, f('%.3f', gz), 'gz'); eq(v.h, f('%.3f', h), 'h'); eq(v.fov, '2.000', 'fov'); eq(v.margin, '1.060', 'margin')
    check(num(v.scene) >= 400 and num(v.scene) < 2500, 'scene loads in 400 ms: ' .. v.scene)
    eq(v.quiet, '1500', 'the default quiet time'); eq(v.fps, '62.5', 'fps (16 ms frames)')
    check(num(v.settle) >= 1500, 'settle at least the quiet time: ' .. v.settle)
    check(num(v.coll) >= 0 and num(v.maxreq) >= 0, 'coll and maxreq')
    eq(v.water, tostring(waterPoints(w, b)), 'water points'); eq(v.water, '1', 'water points (the pond)')
    eq(v.veh, '8', 'vehicles deleted'); eq(v.peds, '10', 'peds deleted (not the player)')
    eq(v.block, 'z8_60_132', 'block')
    local cam = game.cams[next(game.cams)]
    eq(game.rendering, true, 'script camera rendering')
    eq(f('%.3f', cam.coord.z), f('%.3f', gz + h), 'camera height'); eq(cam.rot[1], -90.0, 'looking down')
    eq(cam.fov, 2.0, 'camera fov'); eq(cam.shallowDof, false, 'no shallow depth of field')
    game:run(32)
    local on, seq = game:beacon()
    eq(on, true, 'beacon green'); eq(seq, 1, 'beacon seq bits')
    check(game.entities[101] == nil and game.entities[201] == nil and game.entities[1] ~= nil, 'world cleared, player kept')
    EMIT.ready = ready

    local mark = game:mark()
    game:command('fxmapgen hmap 1')
    game:run(16)
    eq(strip(ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'ERROR ')), 'ERROR busy: a height grid is being sent', 'tile during hmap')
    eq(strip(ask(game, 'fxmapgen hmap 1', 'ERROR ')), 'ERROR busy: a height grid is being sent', 'hmap during hmap')
    local lines = linesUntil(game, mark, 'HMAP END ')
    local hmap = {}
    for _, l in ipairs(lines) do if l:find('^%[fxmapgen%] HMAP ') then hmap[#hmap + 1] = strip(l) end end
    local begin = kv(PREFIX .. hmap[1])
    check(hmap[1]:find('^HMAP BEGIN '), 'first line BEGIN: ' .. hmap[1])
    eq(begin.seq, '1', 'BEGIN seq'); eq(begin.z, '8', 'z'); eq(begin.tx, '60', 'tx'); eq(begin.ty, '132', 'ty')
    eq(begin.x0, f('%.4f', b.x0), 'x0'); eq(begin.y0, f('%.4f', b.y0), 'y0'); eq(begin.size, '281.2500', 'size')
    eq(begin.step, '1.000', 'step'); eq(begin.n, '282', 'n'); eq(begin.block, 'z8_60_132', 'block')
    local n, nohit, rows = 282, 0, 0
    local idx = 2
    for j = 0, n - 1 do
        local values = {}
        for k = 0, 2 do
            local l = hmap[idx]; idx = idx + 1
            local jj, kk, rest = l:match('^HMAP j=(%d+) k=(%d+) (.*)$')
            eq(num(jj), j, 'row number'); eq(num(kk), k, 'part number')
            local count = 0
            for tok in rest:gmatch('%S+') do values[#values + 1] = tok; count = count + 1 end
            eq(count, k < 2 and 100 or 82, 'values in part ' .. k)
        end
        for i = 0, n - 1 do
            local s = w:surface(b.x0 + i, b.y0 - j)
            local want = s and f('%.1f', s) or 'x'
            if not s then nohit = nohit + 1 end
            if values[i + 1] ~= want then error(('row %d column %d: expected %s, got %s'):format(j, i, want, values[i + 1])) end
        end
        rows = rows + 1
    end
    eq(rows, n, 'rows')
    local fin = kv(PREFIX .. hmap[idx])
    check(hmap[idx]:find('^HMAP END '), 'last line END: ' .. hmap[idx])
    eq(num(fin.nohit), nohit, 'nohit')
    check(nohit > 0, 'the fake world has probe misses in this block')
    eq(idx, #hmap, 'no lines after END')
    EMIT.hmap = hmap
end)

-- The settling waits until the streaming requests have stayed at 0 for quietMs; the fake game has 20 requests right
-- after the ped moved, one less each frame (so 0 some 20 frames after the last move).
test('tile with quietMs 0: READY as soon as the requests are 0 and 10 frames are drawn', function()
    local game = newGame()
    local mark = game:mark()
    game:command('fxmapgen tile 8 60 132 2 1.06 0')
    local ready = game:waitLine('^%[fxmapgen%] READY ', 30000, mark)
    local v = kv(ready)
    eq(v.quiet, '0', 'quiet'); eq(v.fps, '62.5', 'fps')
    -- the collision wait (200 ms after the last move) leaves some 7 frames of requests
    check(num(v.settle) >= 10 * 16 and num(v.settle) <= 20 * 16, 'settle between 10 frames and the last request: ' .. v.settle)
    local last = game.ped.movedAt + 20 * 16
    local at = game.now
    check(at >= last, ('READY after the requests reached 0 (%d ms, 0 from %d ms)'):format(at, last))
    -- over the sea there is no collision wait: the requests take their 20 frames
    local sea = kv(ask(game, 'fxmapgen tile 8 0 0 2 1.06 0', 'READY '))
    check(num(sea.settle) >= 19 * 16 and num(sea.settle) <= 21 * 16, 'sea: settle = the 20 frames of requests: ' .. sea.settle)
end)

test('tile with quietMs 500: READY once the requests have stayed at 0 for 500 ms', function()
    local game = newGame()
    local v = kv(ask(game, 'fxmapgen tile 8 0 0 2 1.06 500', 'READY '))
    eq(v.quiet, '500', 'quiet')
    local s = num(v.settle)
    check(s >= 20 * 16 + 500 and s <= 21 * 16 + 500 + 16, '20 frames of requests + 500 ms: ' .. v.settle)
end)

test('the quiet time starts over when requests come back', function()
    local game = newGame()
    local mark = game:mark()
    game:command('fxmapgen tile 8 0 0 2 1.06 500')
    -- the requests come back for 100 ms, 200 ms into the quiet time
    game:runUntil(function() return game.scene ~= nil and game.rendering end, 30000)
    local quietFrom = game.ped.movedAt + 20 * 16
    game.requestBursts = { { quietFrom + 200, quietFrom + 300 } }
    local v = kv(game:waitLine('^%[fxmapgen%] READY ', 30000, mark))
    check(num(v.maxreq) >= 3, 'the burst was seen: maxreq=' .. v.maxreq)
    check(game.now >= quietFrom + 300 + 500, ('READY 500 ms after the burst: at %d, burst ended %d'):format(game.now, quietFrom + 300))
end)

test('tile over open water: 25 water points cut the scene wait at 2.5 s', function()
    local game = newGame()
    local v = kv(ask(game, 'fxmapgen tile 8 0 0 2 1.06', 'READY '))
    eq(v.water, '25', 'water'); eq(v.gz, '0.000', 'gz = the water surface')
    check(num(v.scene) >= 2500 and num(v.scene) < 3000, 'scene cut at 2.5 s: ' .. v.scene)
end)

test('tile over a coast: some water points, the scene waits normally', function()
    local game = newGame()
    local v = kv(ask(game, 'fxmapgen tile 8 60 136 2 1.06', 'READY '))
    eq(v.water, tostring(waterPoints(game.world, block(8, 60, 136))), 'water'); eq(v.water, '15', 'water (3 rows of sea)')
    eq(v.gz, '0.000', 'gz = the sea at the centre')
    check(num(v.scene) < 2500, 'not cut: ' .. v.scene)
end)

test('tile does not ask the game whether the collision around the ped has loaded', function()
    -- the real game kept answering no over the sea off a coast, with everything loaded
    local game = newGame()
    game.collisionQuestion = false
    local land = kv(ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'READY '))
    check(num(land.coll) < 1000, 'land: the ray under the centre meets the ground at once: coll=' .. land.coll)
    local coast = kv(ask(game, 'fxmapgen tile 8 60 140 2 1.06', 'READY '))    -- sea 150-430 m off the land
    eq(coast.water, '25', 'all sea'); eq(coast.coll, '0', 'no wait over water')
    local open = kv(ask(game, 'fxmapgen tile 8 0 0 2 1.06', 'READY '))       -- the sea bottom far below the ray
    eq(open.coll, '0', 'no wait over the open sea')
    local v = kv(ask(game, 'fxmapgen env off', 'ENV off '))
    eq(v.safe, '1', 'set down at home without the question')
    noFall(game)
end)

test('tile over a mountain: the ped hovers higher until the ground probe hits', function()
    local game = newGame()
    local line, mark = ask(game, 'fxmapgen tile 8 80 40 2 1.06', 'READY ')
    eq(kv(line).gz, '800.000', 'gz')
    check(game:find('ground probe failed with the ped at z=300', mark), 'note: failed at 300 m')
    check(game:find('ground found with the ped hovering at z=600: gz=800.0', mark), 'note: found at 600 m')
end)

test('a second tile supersedes the first', function()
    local game = newGame()
    local mark = game:mark()
    game:command('fxmapgen tile 8 60 132 2 1.06')
    game:run(100)
    game:command('fxmapgen tile 8 56 128 2 1.06')
    local lines = linesUntil(game, mark, 'READY ')
    eq(strip(lines[#lines - 1]), 'FAIL seq=1 reason=superseded', 'first request')
    local v = kv(lines[#lines])
    eq(v.seq, '2', 'second request'); eq(v.block, 'z8_56_128', 'second block')
    game:run(1000)
    check(not game:find('READY seq=1', mark), 'no READY for the first request')
end)

test('a scene that never settles fails with reason=timeout', function()
    local game = newGame()
    local v = kv(ask(game, 'fxmapgen tile 8 64 132 2 1.06', 'FAIL ', 60000))
    eq(v.seq, '1', 'seq'); eq(v.reason, 'timeout', 'reason')
    check(num(v.settle) >= 15000, 'settle gave up after 15 s: ' .. v.settle)
    eq(game:find('READY'), nil, 'no READY')
end)

test('clearworld is refused with another player on', function()
    local game = newGame({ players = { '1', '2' } })
    local line = ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'FAIL ')
    eq(strip(line), 'FAIL seq=1 reason=refused why=players players=2', 'line')
    check(game.entities[101] and game.entities[201], 'nothing deleted')
    check(game.serverConsole[1]:find('refused: players'), 'server log: ' .. tostring(game.serverConsole[1]))
end)

test('clearworld is refused without the permission', function()
    local game = newGame({ aces = {} })
    eq(strip(ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'FAIL ')), 'FAIL seq=1 reason=refused why=permission players=1', 'line')
    check(game.entities[101], 'nothing deleted')
end)

test('no answer from the server side fails with reason=noserver', function()
    local game = newGame({ noServer = true })
    eq(strip(ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'FAIL ')), 'FAIL seq=1 reason=noserver', 'line')
end)

test('hmap needs a block in place', function()
    local game = newGame()
    eq(strip(ask(game, 'fxmapgen hmap', 'ERROR ')), 'ERROR hmap: no block in place (fxmapgen tile first)', 'before any tile')
    ask(game, 'fxmapgen cam 219.375 -1021.875 8539.785 2', 'READY ')
    eq(strip(ask(game, 'fxmapgen hmap', 'ERROR ')), 'ERROR hmap: no block in place (fxmapgen tile first)', 'after cam')
    eq(strip(ask(game, 'fxmapgen hmap 0', 'ERROR ')), 'ERROR usage: fxmapgen hmap [step]', 'step 0')
end)

test('cam: READY without a block', function()
    local game = newGame()
    local v = kv(ask(game, 'fxmapgen cam 219.375 -1021.875 8539.785 2', 'READY '))
    eq(v.block, 'none', 'block'); eq(v.margin, '1.000', 'margin'); eq(v.x, '219.3750', 'x')
end)

test('env off sets the ped down where it stood, undoes the environment and refills (Qbox)', function()
    local game = newGame()
    local start = game.startPos
    ask(game, 'fxmapgen env on', 'ENV on$')
    ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'READY ')
    local line, mark = ask(game, 'fxmapgen env off', 'ENV off ')
    local v = kv(line)
    eq(v.safe, '1', 'safe'); eq(v.x, f('%.1f', start.x), 'x'); eq(v.y, f('%.1f', start.y), 'y'); eq(v.z, f('%.1f', start.z), 'z')
    eq(game.ped.frozen, false, 'unfrozen'); eq(game.ped.visible, true, 'visible'); eq(game.ped.invincible, false, 'invincible')
    noFall(game)
    eq(game.tc.active, nil, 'timecycle modifier cleared')
    eq(game.weatherOverride, nil, 'weather override cleared'); eq(game.weatherPersist, false, 'weather persist cleared')
    eq(game.clockOverride, nil, 'clock override cleared'); eq(game.rain, -1.0, 'rain back to the weather')
    eq(game.clouds.opacity, 1.0, 'clouds visible again')
    eq(game.maxWanted, 2, 'max wanted level as before'); eq(game.rendering, false, 'script camera off')
    eq(next(game.cams), nil, 'camera destroyed'); eq(game.focus, nil, 'focus cleared')
    eq(game.last.SetPedPopulationBudget[1], 3, 'ped budget back')
    eq(strip(game:waitLine('REFILL ', 5000, mark)), 'REFILL ok=1 via=qbx_core', 'refill')
    eq(game.metadata.hunger, 100.0, 'hunger'); eq(game.metadata.thirst, 100.0, 'thirst')
    local s = kv(ask(game, 'fxmapgen status', 'STATUS '))
    eq(s.env, '0', 'status env'); eq(s.frozen, '0', 'status frozen')
    eq(strip(ask(game, 'fxmapgen env off', 'ENV off ')), 'ENV off already=1', 'env off again')
end)

test('env off refills through QBCore, or nothing without a framework', function()
    local qbcore = newGame({ resources = { ['fxmapgen-capture'] = 'started', ['qb-core'] = 'started' } })
    ask(qbcore, 'fxmapgen env on', 'ENV on$')
    local _, mark = ask(qbcore, 'fxmapgen env off', 'ENV off ')
    eq(strip(qbcore:waitLine('REFILL ', 5000, mark)), 'REFILL ok=1 via=qb-core', 'QBCore')
    eq(qbcore.metadata.hunger, 100, 'hunger')
    local none = newGame({ resources = { ['fxmapgen-capture'] = 'started' } })
    ask(none, 'fxmapgen env on', 'ENV on$')
    _, mark = ask(none, 'fxmapgen env off', 'ENV off ')
    eq(strip(none:waitLine('REFILL ', 5000, mark)), 'REFILL ok=1 via=none', 'no framework')
    local denied = newGame({ aces = {} })
    ask(denied, 'fxmapgen env on', 'ENV on$')
    _, mark = ask(denied, 'fxmapgen env off', 'ENV off ')
    eq(strip(denied:waitLine('REFILL ', 5000, mark)), 'REFILL ok=0 via=none error=permission', 'no permission')
    eq(denied.metadata.hunger, nil, 'not refilled')
    noFall(qbcore); noFall(none); noFall(denied)
end)

test('env off during a tile: the request gives up, the ped is set down', function()
    local game = newGame()
    local mark = game:mark()
    game:command('fxmapgen tile 8 60 132 2 1.06')
    game:run(200)
    game:command('fxmapgen env off')
    local lines = linesUntil(game, mark, 'ENV off ')
    check(game:find('^%[fxmapgen%] FAIL seq=1 reason=superseded$', mark), 'FAIL superseded: ' .. table.concat(lines, ' | '))
    eq(kv(lines[#lines]).safe, '1', 'safe')
    game:run(5000)
    eq(game:find('READY', mark), nil, 'no READY')
    eq(game.ped.frozen, false, 'unfrozen'); eq(game.rendering, false, 'no script camera')
    noFall(game)
end)

test('env off after the test shot far out at sea: the ped waits frozen at home until the collision there has loaded', function()
    -- the pre-check's order: tile z8_0_0, about 10 km from where the ped stood, then env
    -- off. The collision at home takes 1.5 s after such a jump; unfreezing sooner would drop the ped through the ground.
    local game = newGame()
    local start = game.startPos
    ask(game, 'fxmapgen tile 8 0 0 2 1.06', 'READY ')
    local v = kv(ask(game, 'fxmapgen env off', 'ENV off '))
    eq(v.safe, '1', 'safe'); eq(v.x, f('%.1f', start.x), 'x'); eq(v.y, f('%.1f', start.y), 'y')
    eq(game.ped.frozen, false, 'unfrozen')
    noFall(game)
end)

test('env off where the floor comes after the ground under it: the ped waits for the floor it stood on', function()
    -- as can happen in the game: a ped on an interior's floor over the sea, set down on the sea bottom 43 m below
    -- because the floor's collision is not there yet after shots far away. Here a roof whose collision
    -- comes 3 s after the ped's move, 1.5 s after the ground's.
    local game = newGame()
    local x, y = 200.0, -1020.0
    check(game.world:building(x, y), 'a building where the ped stands')
    local roof = game.world:ground(x, y)
    game:move(game.ped.handle, x, y, roof + 1.0)
    game:run(3000)
    game.buildingsLoadMs = 3000
    ask(game, 'fxmapgen env on', 'ENV on$')
    ask(game, 'fxmapgen tile 8 0 0 2 1.06', 'READY ')
    local v = kv(ask(game, 'fxmapgen env off', 'ENV off '))
    eq(v.safe, '1', 'safe'); eq(v.z, f('%.1f', roof + 1.0), 'set down on the roof')
    eq(f('%.1f', game.ped.pos.z), f('%.1f', roof + 1.0), 'the ped stays on the roof')
    eq(game.ped.frozen, false, 'unfrozen')
    noFall(game)
end)

test('env off with nothing to stand on near where the ped was: after the wait, on what is below', function()
    -- a ped held up in the air when the capture began: nothing near under it ever comes, the ground 30 m down does
    local game = newGame()
    local x, y = 200.0, -900.0
    local ground = game.world:ground(x, y)
    game:move(game.ped.handle, x, y, ground + 30.0)
    game:run(1000)
    ask(game, 'fxmapgen env on', 'ENV on$')
    local t = game.now
    local v = kv(ask(game, 'fxmapgen env off', 'ENV off '))
    eq(v.safe, '1', 'safe'); eq(v.z, f('%.1f', ground + 1.0), 'on the ground below')
    check(game.now - t >= 10000, 'the whole wait for something near first: ' .. (game.now - t) .. ' ms')
    noFall(game)
end)

test('a tile right after env off: the landing gives way to the capture', function()
    local game = newGame()
    local start = game.startPos
    ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'READY ')
    local mark = game:mark()
    game:command('fxmapgen env off')
    game:run(50)
    game:command('fxmapgen tile 8 56 128 2 1.06')
    local tileAt = game.now
    local lines = linesUntil(game, mark, 'READY ')
    check(game:find('^%[fxmapgen%] ENV off safe=0 ', mark), 'the landing gave up: ' .. table.concat(lines, ' | '))
    local b = block(8, 56, 128)
    for _, m in ipairs(game.moves) do
        if m.t > tileAt and (m.x ~= b.cx or m.y ~= b.cy) then
            error(('the ped was moved away from the new block at %d ms: (%.1f, %.1f)'):format(m.t, m.x, m.y))
        end
    end
    eq(f('%.1f', game.ped.pos.x), f('%.1f', b.cx), 'the ped stays over the new block')
    eq(game.ped.frozen, true, 'still frozen for the capture')
    local v = kv(ask(game, 'fxmapgen env off', 'ENV off '))
    eq(v.safe, '1', 'set down later'); eq(v.x, f('%.1f', start.x), 'where it first stood'); eq(v.y, f('%.1f', start.y), 'y')
    noFall(game)
end)

test('env off during hmap: HMAP ABORT and no END', function()
    local game = newGame()
    ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'READY ')
    local _, mark = ask(game, 'fxmapgen hmap', 'HMAP j=20 ')
    game:command('fxmapgen env off')
    game:waitLine('^%[fxmapgen%] ENV off ', 15000, mark)
    check(game:find('^%[fxmapgen%] HMAP ABORT$', mark), 'HMAP ABORT')
    eq(game:find('HMAP END', mark), nil, 'no HMAP END')
    eq(kv(ask(game, 'fxmapgen status', 'STATUS ')).hmap, '0', 'hmap no longer busy')
end)

test('safe sets the ped down on the surface at a point', function()
    local game = newGame()
    ask(game, 'fxmapgen env on', 'ENV on$')
    eq(strip(ask(game, 'fxmapgen safe', 'ERROR ')), 'ERROR safe: the capture environment is on (fxmapgen env off sets the character down)', 'with env on')
    ask(game, 'fxmapgen env off', 'ENV off ')
    eq(strip(ask(game, 'fxmapgen safe 219.375', 'ERROR ')), 'ERROR usage: fxmapgen safe [x y [z]]', 'x without y')
    game.ped.frozen = true
    local v = kv(ask(game, 'fxmapgen safe 219.375 -1021.875', 'SAFE '))
    local gz = game.world:surface(219.375, -1021.875)
    eq(v.ok, '1', 'ok'); eq(v.z, f('%.1f', gz + 1.0), 'z = surface + 1')
    eq(game.ped.frozen, false, 'unfrozen'); eq(game.focus, nil, 'focus cleared')
    noFall(game)
end)

test('the resource stopping during a capture leaves the ped frozen where it stood', function()
    local game = newGame()
    local start = game.startPos
    ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'READY ')
    local mark = game:mark()
    game:stopResource()
    local p = game.ped.pos
    eq(f('%.1f', p.x), f('%.1f', start.x), 'x'); eq(f('%.1f', p.y), f('%.1f', start.y), 'y'); eq(f('%.1f', p.z), f('%.1f', start.z), 'z')
    eq(game.ped.frozen, true, 'still frozen'); eq(game.ped.visible, true, 'visible')
    eq(game.tc.active, nil, 'timecycle modifier cleared'); eq(game.rendering, false, 'script camera off')
    check(game:find('stopped during a capture', mark), 'note in the log')
end)

test('res: the state of the resources named, or of all', function()
    local game = newGame()
    local _, mark = ask(game, 'fxmapgen res qbx_hud chat nothing_here', 'RES END$')
    local lines = game:linesSince(mark)
    eq(table.concat(lines, '|'), '[fxmapgen] RES BEGIN n=3|[fxmapgen] RES qbx_hud started|[fxmapgen] RES chat started|'
        .. '[fxmapgen] RES nothing_here missing|[fxmapgen] RES END', 'named')
    _, mark = ask(game, 'fxmapgen res', 'RES END$')
    lines = game:linesSince(mark)
    eq(lines[1], '[fxmapgen] RES BEGIN n=7', 'all: count')
    eq(lines[2], '[fxmapgen] RES Renewed-Weathersync started', 'all: sorted')
end)

test('ping says nothing; unknown or malformed commands say ERROR', function()
    local game = newGame()
    local mark = game:mark()
    game:command('fxmapgen ping')
    game:run(100)
    eq(#game:linesSince(mark), 0, 'ping is silent')
    check(strip(ask(game, 'fxmapgen', 'ERROR ')):find('^ERROR usage: fxmapgen hello | status'), 'no sub-command')
    check(ask(game, 'fxmapgen dance', 'ERROR usage: '), 'unknown sub-command')
    local usage = 'ERROR usage: fxmapgen tile <z 6..11> <tx> <ty> <fov> [margin] [quietMs 0..10000]'
    eq(strip(ask(game, 'fxmapgen tile 8 60', 'ERROR ')), usage, 'tile')
    eq(strip(ask(game, 'fxmapgen tile 5 60 132 2', 'ERROR ')), usage, 'zoom')
    eq(strip(ask(game, 'fxmapgen tile 8 60.5 132 2', 'ERROR ')), usage, 'tx')
    eq(strip(ask(game, 'fxmapgen tile 8 60 132 2 1.06 -1', 'ERROR ')), usage, 'quietMs below 0')
    eq(strip(ask(game, 'fxmapgen tile 8 60 132 2 1.06 10001', 'ERROR ')), usage, 'quietMs above 10000')
    eq(strip(ask(game, 'fxmapgen tile 8 60 132 2 1.06 1.5', 'ERROR ')), usage, 'quietMs not whole')
    eq(strip(ask(game, 'fxmapgen cam 1 2 3', 'ERROR ')), 'ERROR usage: fxmapgen cam <x> <y> <h> <fov>', 'cam')
    eq(strip(ask(game, 'fxmapgen env', 'ERROR ')), 'ERROR usage: fxmapgen env on|off', 'env')
end)

-- The form of every line of a whole visit: the prefix; key=value pairs without spaces on the machine lines; HMAP rows of
-- numbers and x. FxMapGenerator's readers take the values by key.
test('every line of a visit has the protocol form', function()
    local game = newGame()
    local mark = game:mark()
    for _, c in ipairs({ { 'fxmapgen hello', 'HELLO ' }, { 'fxmapgen status', 'STATUS ' }, { 'fxmapgen res chat', 'RES END$' },
        { 'fxmapgen env on', 'ENV on$' }, { 'fxmapgen tile 8 60 132 2 1.06', 'READY ' }, { 'fxmapgen hmap 4', 'HMAP END ' },
        { 'fxmapgen tile 8 64 132 2 1.06', 'FAIL ' }, { 'fxmapgen scan ground 8 60 132 canopy', 'MSCAN DONE ' },
        { 'fxmapgen scan roads 8 60 132', 'MSCAN DONE ' }, { 'fxmapgen env off', 'ENV off ' } }) do
        ask(game, c[1], c[2], 60000)
    end
    game:waitLine('^%[fxmapgen%] REFILL ', 5000, mark)
    local keys = {
        HELLO = 'proto ver res server ace players build capture',
        STATUS = 'env ready busy hmap scan seq weather hour minute peds vehicles near_peds near_vehicles health dead frozen px py pz block',
        READY = 'seq x y gz h fov margin scene coll settle quiet fps maxreq water veh peds block',
    }
    local seen = {}
    for _, line in ipairs(game:linesSince(mark)) do
        check(line:sub(1, #PREFIX) == PREFIX, 'prefix: ' .. line)
        local body = strip(line)
        local word = body:match('^(%u+)')
        if word then
            seen[word] = true
            if word == 'HMAP' and body:find('^HMAP j=') then
                check(body:find('^HMAP j=%d+ k=%d+ [%-%d%.x ]+$'), 'HMAP row: ' .. body)
            elseif word == 'MSCAN' and body:find('^MSCAN %l') then
                check(body:find('^MSCAN %l+%d? j=%d+ k=%d+ [%-%d%.x%* ]+$') or body:find('^MSCAN dict mat %d+ %-?%d+$')
                    or body:find('^MSCAN dict street %d+ %-?%d+ ') or body:find('^MSCAN dict zone %d+ %u+ ')
                    or body:find('^MSCAN chunk %d+,%d+ surf=[%-%d%.x]+ wait=%d+ coll=[01%-] quiet=[01%-] req=%d+$'), 'MSCAN line: ' .. body)
            elseif word == 'RES' and not body:find('^RES BEGIN') and body ~= 'RES END' then
                check(body:find('^RES %S+ %a+$'), 'RES line: ' .. body)
            elseif word ~= 'ERROR' then
                local rest = body:gsub('^HMAP %u+', ''):gsub('^MSCAN %u+', ''):gsub('^RES %u+', ''):gsub('^ENV %a+', ''):gsub('^%u+', '')
                local names = {}
                for tok in rest:gmatch('%S+') do
                    check(tok:find('^[%a_][%w_]*=[^%s=]+$'), 'key=value token "' .. tok .. '" in: ' .. body)
                    names[#names + 1] = tok:match('^([%w_]+)=')
                end
                if keys[word] then eq(table.concat(names, ' '), keys[word], word .. ' keys') end
            end
        end
    end
    for _, word in ipairs({ 'HELLO', 'STATUS', 'RES', 'ENV', 'READY', 'HMAP', 'FAIL', 'MSCAN', 'REFILL' }) do
        check(seen[word], 'a ' .. word .. ' line was checked')
    end
    noFall(game)
end)

-- ---------------------------------------------------------------- scans (scan.lua)
-- The MSCAN lines of one scan: BEGIN, dict and rows by kind (run-length decoded, parts joined), END, DONE / ABORT.
local function readScan(lines)
    local s = { rows = {}, dict = { mat = {}, street = {}, zone = {} }, chunks = {} }
    for _, l in ipairs(lines) do
        local body = strip(l)
        if body:find('^MSCAN BEGIN ') then s.begin = kv(l)
        elseif body:find('^MSCAN END ') then s['end'] = kv(l)
        elseif body:find('^MSCAN DONE ') then s.done = kv(l)
        elseif body:find('^MSCAN ABORT ') then s.abort = kv(l)
        elseif body:find('^MSCAN chunk ') then s.chunks[#s.chunks + 1] = body
        elseif body:find('^MSCAN dict ') then
            local what, idx, a, b = body:match('^MSCAN dict (%a+) (%d+) (%S+) ?(.*)$')
            s.dict[what][num(idx)] = what == 'mat' and num(a) or { a, b }
        else
            local kind, j, k, rest = body:match('^MSCAN (%a+%d?) j=(%d+) k=(%d+) ?(.*)$')
            if kind then
                s.rows[kind] = s.rows[kind] or {}
                local row = s.rows[kind][num(j)] or {}
                check(num(k) == #(row.parts or {}), kind .. ' parts in order')
                row.parts = row.parts or {}
                row.parts[#row.parts + 1] = rest
                s.rows[kind][num(j)] = row
            end
        end
    end
    -- decode: "v*count" -> count times v
    for _, rows in pairs(s.rows) do
        for j, row in pairs(rows) do
            local vals = {}
            for _, part in ipairs(row.parts) do
                for tok in part:gmatch('%S+') do
                    local v, c = tok:match('^(.-)%*(%d+)$')
                    if v then for _ = 1, num(c) do vals[#vals + 1] = v end else vals[#vals + 1] = tok end
                end
            end
            rows[j] = vals
        end
    end
    return s
end

local function scanLines(game, command, maxMs)
    local mark = game:mark()
    game:command(command)
    return linesUntil(game, mark, 'MSCAN [DA][OB][NO]', maxMs or 120000), mark
end

test('scan ground: four sections streamed in turn; materials, heights, water, water collision', function()
    local game = newGame()
    local w = game.world
    local b = block(8, 60, 132)
    local lines = scanLines(game, 'fxmapgen scan ground 8 60 132')
    local s = readScan(lines)
    local v = s.begin
    eq(v.v, '1', 'format'); eq(v.kind, 'mat', 'kind'); eq(v.seq, '1', 'seq'); eq(v.block, 'z8_60_132', 'block')
    eq(v.z, '8', 'z'); eq(v.tx, '60', 'tx'); eq(v.ty, '132', 'ty')
    eq(v.x0, f('%.4f', b.x0), 'x0'); eq(v.y0, f('%.4f', b.y0), 'y0'); eq(v.size, '281.2500', 'size'); eq(v.step, '1.000', 'step')
    eq(v.n, '282', 'n'); eq(v.flags, '1', 'flags'); eq(v.fol, '1', 'fol'); eq(v.chunks, '2', 'chunks'); eq(v.pflags, '128', 'pflags')
    eq(v.pflags2, nil, 'no canopy probe unless asked')
    -- the ped went to the centre of each quarter of the block, north-west first, and stayed there while it was scanned
    local centres = {}
    for _, m in ipairs(game.moves) do
        for cj = 0, 1 do
            for ci = 0, 1 do
                local cx, cy = b.x0 + (ci + 0.5) * b.size / 2, b.y0 - (cj + 0.5) * b.size / 2
                if math.abs(m.x - cx) < 1e-6 and math.abs(m.y - cy) < 1e-6 then centres[cj * 2 + ci + 1] = centres[cj * 2 + ci + 1] or m.t end
            end
        end
    end
    check(centres[1] and centres[2] and centres[3] and centres[4], 'the ped visited the four section centres')
    check(centres[1] < centres[2] and centres[2] < centres[3] and centres[3] < centres[4], 'in row order')
    eq(#s.chunks, 4, 'a chunk line per section')
    -- every point against the made-up world
    local n = 282
    local nWater, mats = 0, {}
    for j = 0, n - 1 do
        local mat, hz, water, fol = s.rows.mat[j], s.rows.hz[j], s.rows.water[j], s.rows.fol[j]
        eq(#mat, n, 'mat row ' .. j); eq(#hz, n, 'hz row ' .. j); eq(#water, n, 'water row ' .. j); eq(#fol, n, 'fol row ' .. j)
        for i = 0, n - 1 do
            local x, y = b.x0 + i, b.y0 - j
            local want = w:material(x, y)
            mats[want] = true
            eq(s.dict.mat[num(mat[i + 1])], want, ('material at %d,%d'):format(i, j))
            eq(hz[i + 1], f('%.1f', w:ground(x, y)), ('height at %d,%d'):format(i, j))
            local wl = w:water(x, y)
            eq(water[i + 1], wl and f('%.1f', wl) or '.', ('water at %d,%d'):format(i, j))
            eq(fol[i + 1], w:kind(x, y) == 'pond' and f('%.1f', w.pond.level) or 'x', ('water collision at %d,%d'):format(i, j))
            if wl then nWater = nWater + 1 end
        end
    end
    eq(s.rows.fol2, nil, 'no canopy rows')
    local e = s['end']
    eq(e.kind, 'mat', 'END kind'); eq(e.seq, '1', 'END seq'); eq(e.n, '282', 'END n'); eq(e.cells, tostring(n * n), 'cells')
    eq(e.nohit, '0', 'every ray hit (the collision had loaded)'); eq(num(e.water), nWater, 'water points'); eq(e.fol, e.water, 'the pond')
    local nm = 0
    for _ in pairs(mats) do nm = nm + 1 end
    eq(num(e.mats), nm, 'materials'); eq(nm, 4, 'concrete, tarmac, grass and the pond bottom')
    eq(s.done.seq, '1', 'DONE seq'); eq(s.done.kind, 'ground', 'DONE kind'); eq(s.done.block, 'z8_60_132', 'DONE block')
    eq(kv(ask(game, 'fxmapgen status', 'STATUS ')).scan, '0', 'no longer busy')
    EMIT.scan = EMIT.scan or {}
    for _, l in ipairs(lines) do if strip(l):find('^MSCAN ') then EMIT.scan[#EMIT.scan + 1] = l end end
end)

test('scan ground: the buildings\' collision loads after the ground\'s; each section waits for it', function()
    local game = newGame()
    game.buildingsLoadMs = 1500          -- the ground after 200 ms, the roofs around 1.5 s after the ped last moved
    local w = game.world
    local b = block(8, 60, 132)
    local s = readScan(scanLines(game, 'fxmapgen scan ground 8 60 132'))
    eq(#s.chunks, 4, 'a chunk line per section')
    for _, c in ipairs(s.chunks) do
        local v = kv(c)
        eq(v.coll, '1', 'the collision around the ped reported loaded: ' .. c)
        check(num(v.wait) >= 1500, 'the section waited for the buildings: ' .. c)
    end
    local roofs, wrong = 0, 0
    for j = 0, 281 do
        for i = 0, 281 do
            local x, y = b.x0 + i, b.y0 - j
            if w:kind(x, y) == 'land' and w:building(x, y) then
                roofs = roofs + 1
                if s.rows.hz[j][i + 1] ~= f('%.1f', w:ground(x, y)) then wrong = wrong + 1 end
            end
        end
    end
    check(roofs > 1000, 'the block has buildings')
    eq(wrong, 0, 'every roof hit (none of the ground under them)')
end)

test('scan ground: the game says the collision around is loaded before the buildings come; the quiet wait covers it', function()
    local game = newGame()
    game.buildingsLoadMs = 2500          -- the roofs 2.5 s after the ped last moved, the requests open until then
    game.lateBuildings = true            -- HasCollisionLoadedAroundEntity says yes with the ground alone
    local w = game.world
    local b = block(8, 60, 132)
    local s = readScan(scanLines(game, 'fxmapgen scan ground 8 60 132'))
    eq(#s.chunks, 4, 'a chunk line per section')
    for _, c in ipairs(s.chunks) do
        local v = kv(c)
        eq(v.coll, '1', 'said loaded: ' .. c); eq(v.quiet, '1', 'the requests went quiet: ' .. c); eq(v.req, '0', 'none open as the rays start: ' .. c)
        check(num(v.wait) >= 2500, 'the section waited for the buildings: ' .. c)
    end
    local wrong = 0
    for j = 0, 281 do
        for i = 0, 281 do
            local x, y = b.x0 + i, b.y0 - j
            if w:kind(x, y) == 'land' and w:building(x, y) and s.rows.hz[j][i + 1] ~= f('%.1f', w:ground(x, y)) then wrong = wrong + 1 end
        end
    end
    eq(wrong, 0, 'every roof hit')
end)

test('scan ground: collision around that never reports loaded: coll=0 after 3 s a section, the scan goes on', function()
    local game = newGame()
    game.buildingsLoadMs = 1e9
    local s = readScan(scanLines(game, 'fxmapgen scan ground 8 60 132'))
    eq(#s.chunks, 4, 'a chunk line per section')
    for _, c in ipairs(s.chunks) do
        local v = kv(c)
        eq(v.coll, '0', 'not reported loaded: ' .. c)
        eq(v.quiet, '0', 'the requests never went quiet either (the buildings never came): ' .. c)
        check(num(v.req) >= 1, 'a request still open: ' .. c)
        check(num(v.wait) >= 8000, 'waited the 3 s and the 5 s: ' .. c)
    end
    eq(s.done.kind, 'ground', 'the scan finished')
end)

test('scan ground canopy: the third ray finds the tree crowns', function()
    local game = newGame()
    local w = game.world
    local b = block(8, 60, 132)
    local s = readScan(scanLines(game, 'fxmapgen scan ground 8 60 132 canopy'))
    eq(s.begin.pflags2, '256', 'pflags2')
    local trees = 0
    for j = 0, 281 do
        for i = 0, 281 do
            local x, y = b.x0 + i, b.y0 - j
            local want = w:tree(x, y) and f('%.1f', w:ground(x, y) + 8.0) or 'x'
            if w:tree(x, y) then trees = trees + 1 end
            eq(s.rows.fol2[j][i + 1], want, ('canopy at %d,%d'):format(i, j))
        end
    end
    check(trees > 1000, 'the block has trees: ' .. trees)
    eq(num(s['end'].fol2), trees, 'END fol2')
end)

test('scan roads: on the road every metre, streets and zones every 4 m', function()
    local game = newGame()
    local w = game.world
    local b = block(8, 60, 132)
    local lines = scanLines(game, 'fxmapgen scan roads 8 60 132')
    local s = readScan(lines)
    local v = s.begin
    eq(v.kind, 'road', 'kind'); eq(v.seq, '1', 'seq'); eq(v.block, 'z8_60_132', 'block'); eq(v.step, '4.000', 'step')
    eq(v.n, '71', 'n (as the scans of the world)'); eq(v.pstep, '1.000', 'pstep'); eq(v.pn, '282', 'pn')
    -- the ped went to the block's centre once
    local last = game.moves[#game.moves]
    check(math.abs(last.x - b.cx) < 1e-6 and math.abs(last.y - b.cy) < 1e-6, 'the ped stands over the centre')
    local nOn = 0
    for j = 0, 70 do
        eq(#s.rows.street[j], 71, 'street row ' .. j); eq(#s.rows.zone[j], 71, 'zone row ' .. j)
        for i = 0, 70 do
            local x, y = b.x0 + 4 * i, b.y0 - 4 * j
            local st = s.dict.street[num(s.rows.street[j][i + 1])]
            eq(st[2], w:street(x, y) or '', ('street at %d,%d'):format(i, j))   -- none over the pond: hash 0, no name
            local zn = s.dict.zone[num(s.rows.zone[j][i + 1])]
            eq(zn[1], w:zone(x, y), ('zone at %d,%d'):format(i, j)); eq(zn[2], FakeGame.ZONES[zn[1]], 'zone label')
        end
    end
    for j = 0, 281 do
        for i = 0, 281 do
            local on = w:onRoad(b.x0 + i, b.y0 - j)
            if on then nOn = nOn + 1 end
            eq(s.rows.onroad[j][i + 1], on and '1' or '0', ('on the road at %d,%d'):format(i, j))
        end
    end
    local e = s['end']
    eq(num(e.onroad), nOn, 'END onroad'); eq(e.streets, '3', 'streets (and none over the pond)'); eq(e.zones, '2', 'zones (the block crosses x = 250)')
    eq(s.done.kind, 'roads', 'DONE kind')
    EMIT.scan = EMIT.scan or {}
    for _, l in ipairs(lines) do if strip(l):find('^MSCAN ') then EMIT.scan[#EMIT.scan + 1] = l end end
end)

test('scan ground over the open sea: the scene wait is cut short, nothing is hit above the probe depth', function()
    local game = newGame()
    local lines = scanLines(game, 'fxmapgen scan ground 8 28 140')
    local s = readScan(lines)
    local e = s['end']
    eq(e.nohit, e.cells, 'the sea bottom (600 m down) is below the rays'); eq(e.water, e.cells, 'water everywhere')
    eq(e.mats, '0', 'no material')
    for _, c in ipairs(s.chunks) do
        local wait = num(c:match('wait=(%d+)'))
        check(wait < 2500 + 300 + 200, 'a section over open water waits at most 2.5 s for the scene: ' .. c)
    end
end)

test('scan ground over deep water: the rays go down to -500 m and hit a sea bottom 300 m down', function()
    local game = newGame()
    local s = readScan(scanLines(game, 'fxmapgen scan ground 8 40 140'))
    eq(s.begin.zmin, '-500', 'BEGIN zmin')
    eq(s['end'].nohit, '0', 'every ray hits the sea bottom'); eq(s['end'].water, s['end'].cells, 'water everywhere')
end)

test('scan ground over a sloping deep bottom: the rows come in parts short enough for the game, every point arrives', function()
    local game = newGame()
    local lines = scanLines(game, 'fxmapgen scan ground 8 20 140')
    local s = readScan(lines)
    local n = num(s.begin.n)
    eq(s['end'].nohit, '0', 'every ray hits the bottom')
    local parts = 0
    for _, l in ipairs(lines) do
        check(#l <= 1000, 'a line the game keeps whole (' .. #l .. ' characters): ' .. l:sub(1, 60))
        local k = strip(l):match('^MSCAN hz j=%d+ k=(%d+)')
        if k then parts = math.max(parts, num(k) + 1) end
    end
    check(parts >= 2, 'a row of the slope needs more than one part (' .. parts .. ')')
    for _, kind in ipairs({ 'mat', 'hz', 'water', 'fol' }) do
        for j = 0, n - 1 do
            local row = s.rows[kind] and s.rows[kind][j]
            eq(row and #row or 0, n, kind .. ' row ' .. j .. ': every point')
        end
    end
    eq(s.rows.hz[0][1], '-250.0', 'the west edge 250 m down'); eq(s.rows.hz[0][n], '-261.2', 'the east edge 11 m deeper')
end)

test('scan outside the standard frame: west and north of it the numbers are negative, east and south past 127 and 191', function()
    local game = newGame()
    local s = readScan(scanLines(game, 'fxmapgen scan ground 8 -128 -64'))
    eq(s.begin.block, 'z8_-128_-64', 'BEGIN block of the north-west corner of the frame with 4 cells to the left and 2 above')
    eq(num(s.begin.tx), -128, 'BEGIN tx'); eq(num(s.begin.ty), -64, 'BEGIN ty')
    eq(num(s.begin.x0), -4140 - 32 * 281.25, 'BEGIN x0'); eq(num(s.begin.y0), 8400 + 16 * 281.25, 'BEGIN y0')
    check(s.done ~= nil, 'the scan ran to its end')
    s = readScan(scanLines(game, 'fxmapgen scan roads 8 252 252'))
    eq(s.begin.block, 'z8_252_252', 'the south-east corner block of the frame with 4 cells to the right and 2 below')
    eq(num(s.begin.x0), -4140 + 63 * 281.25, 'BEGIN x0 east'); eq(num(s.begin.y0), 8400 - 63 * 281.25, 'BEGIN y0 south')
    check(s.done ~= nil, 'the roads scan ran to its end')
end)

test('scan stop and env off: MSCAN ABORT and no END or DONE; the other requests wait while a scan runs', function()
    local game = newGame()
    ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'READY ')
    local _, mark = ask(game, 'fxmapgen scan ground 8 60 132', 'MSCAN chunk 0,0 ')
    eq(strip(ask(game, 'fxmapgen tile 8 60 132 2 1.06', 'ERROR ')), 'ERROR busy: a scan is running', 'tile during a scan')
    eq(strip(ask(game, 'fxmapgen cam 0 0 100 2', 'ERROR ')), 'ERROR busy: a scan is running', 'cam during a scan')
    eq(strip(ask(game, 'fxmapgen hmap', 'ERROR ')), 'ERROR busy: a scan is running', 'hmap during a scan')
    eq(strip(ask(game, 'fxmapgen scan roads 8 60 132', 'ERROR ')), 'ERROR busy: a scan is running', 'scan during a scan')
    eq(kv(ask(game, 'fxmapgen status', 'STATUS ')).scan, '1', 'STATUS scan=1')
    game:command('fxmapgen scan stop')
    local abort = game:waitLine('^%[fxmapgen%] MSCAN ABORT ', 10000, mark)
    check(abort, 'MSCAN ABORT after scan stop')
    eq(kv(abort).seq, '1', 'ABORT seq'); eq(kv(abort).kind, 'ground', 'ABORT kind'); eq(kv(abort).block, 'z8_60_132', 'ABORT block')
    game:run(500)
    eq(game:find('MSCAN END', mark), nil, 'no END'); eq(game:find('MSCAN DONE', mark), nil, 'no DONE')

    -- a scan right after: seq 2; env off stops it
    local _, mark2 = ask(game, 'fxmapgen scan roads 8 60 132', 'MSCAN BEGIN ')
    game:run(100)
    game:command('fxmapgen env off')
    game:waitLine('^%[fxmapgen%] ENV off ', 15000, mark2)
    local abort2 = game:waitLine('^%[fxmapgen%] MSCAN ABORT ', 5000, mark2)
    check(abort2, 'MSCAN ABORT after env off')
    eq(kv(abort2).seq, '2', 'second scan seq'); eq(kv(abort2).kind, 'roads', 'kind')
    eq(game:find('MSCAN DONE', mark2), nil, 'no DONE')
    game:run(1000)
    noFall(game)

    -- while a tile request is on its way, a scan waits its turn
    game:command('fxmapgen tile 8 60 132 2 1.06')
    game:run(32)
    local m3 = game:mark()
    eq(strip(ask(game, 'fxmapgen scan ground 8 60 132', 'ERROR ')), 'ERROR busy: a tile request is on its way', 'scan during a tile')
    game:run(100)
    eq(game:find('MSCAN BEGIN', m3), nil, 'nothing started')
end)

test('scan usage errors', function()
    local game = newGame()
    local usage = 'ERROR usage: fxmapgen scan ground|roads 8 <tx> <ty> [canopy] | scan stop'
    for _, c in ipairs({ 'fxmapgen scan', 'fxmapgen scan trees 8 60 132', 'fxmapgen scan ground 7 60 132', 'fxmapgen scan ground 8 60',
        'fxmapgen scan ground 8 60.5 132', 'fxmapgen scan ground 8 -132 132', 'fxmapgen scan ground 8 256 132',
        'fxmapgen scan ground 8 60 -68', 'fxmapgen scan ground 8 60 256',
        'fxmapgen scan roads 8 60 132 canopy', 'fxmapgen scan ground 8 60 132 trees', 'fxmapgen scan ground 8 60 132 canopy 1' }) do
        eq(strip(ask(game, c, 'ERROR ')), usage, c)
    end
    local mark = game:mark()
    game:command('fxmapgen scan stop')
    game:run(100)
    eq(#game:linesSince(mark), 0, 'scan stop without a scan says nothing')
end)

-- ---------------------------------------------------------------- run
local passed, failed = 0, 0
for _, t in ipairs(tests) do
    local wanted = #filters == 0
    for _, flt in ipairs(filters) do if t.name:find(flt, 1, true) then wanted = true end end
    if wanted then
        local ok, err = xpcall(t.fn, debug.traceback)
        if ok then
            passed = passed + 1
            print('ok    ' .. t.name)
        else
            failed = failed + 1
            print('FAIL  ' .. t.name .. '\n      ' .. tostring(err):gsub('\n', '\n      '))
        end
    end
end

if emitDir then
    assert(EMIT.ready and EMIT.hmap, '--emit needs the "tile over land" test')
    local cam = assert(io.open(emitDir .. '/z8_60_132.cam.txt', 'w'))
    cam:write(EMIT.ready, '\n')
    cam:close()
    local hm = assert(io.open(emitDir .. '/z8_60_132.hmap', 'w'))
    hm:write(table.concat(EMIT.hmap, '\n'), '\n')
    hm:close()
    print('wrote ' .. emitDir .. '/z8_60_132.cam.txt and .hmap')
    if EMIT.scan then
        local sc = assert(io.open(emitDir .. '/z8_60_132.scan.txt', 'w'))
        sc:write(table.concat(EMIT.scan, '\n'), '\n')
        sc:close()
        print('wrote ' .. emitDir .. '/z8_60_132.scan.txt')
    end
end

print(('%d passed, %d failed'):format(passed, failed))
os.exit(failed == 0 and 0 or 1)
