-- Runs an exported minimap resource's client scripts in a small fake game (Lua 5.4) and prints what they did to it, one
-- line each, for the C# test that wrote the resource (tests/FxMapGenerator.Core.Tests/Export/MinimapResourceTests.cs):
--
--   datafile <type> <path>          a data_file line of the manifest
--   zoomlevel <i> <zoom> <a> <b> <c> <d>   SetMapZoomDataLevel
--   tile <scaleform> <name> <txd> <txn> <x> <y> <width> <height> <centered> <alpha> <rotation>
--                                   Extra Map Tiles' DRAW_TEXTURE on a Scaleform (its units: 1728 x 2880 for the world)
--   blip <x> <y>                    the invisible blips that stretch the pause map to the tiles
--   replace <texture> <txd> <txn>   AddReplaceTexture (the radar masks)
--   scene <name> zoom=<calls>:<zoom> hide=<calls> outside=<calls> picture=<calls>:<hash>:<x>:<y>:<a>:<b> asked=<calls>
--                                   what the zoom script did in the 10 frames of a scene (the player's place, below):
--                                   SetRadarZoom (its last value), HideMinimapExteriorMapThisFrame,
--                                   SetRadarAsExteriorThisFrame, SetRadarAsInteriorThisFrame (its last arguments, the
--                                   hash without its sign; "picture=0" when not called) and how often it asked the
--                                   game for an interior's name hash
--   stopzoom <calls> <zoom>         SetRadarZoom when the resource stops
--   reset <i>                       ResetMapZoomDataLevel once the resource stopped
--   alive <n>                       the threads still running after the stop
--
--   lua tests/minimap/run-resource.lua <resource folder> [fixed-zoom]
--
-- The scripts are loaded as FiveM does: the manifest's shared scripts, then its client scripts, in their order. With
-- "fixed-zoom" the server's setting fxmapgen_minimap_fixed_zoom is "true". The scenes walk the player through an
-- interior with a radar picture of its own (fxtest_home), one a picture of underground passages stands for
-- (fxtest_drain) and one without any (fxtest_mlo): the resource's interiors.lua has to name the first two.

local dir = assert(arg and arg[1], 'usage: lua run-resource.lua <resource folder> [fixed-zoom]')
local fixedZoom = arg[2] == 'fixed-zoom'

local out = {}
local function emit(...) out[#out + 1] = table.concat({ ... }, ' ') end
local function num(v) return string.format('%.4f', v) end

-- ---------------------------------------------------------------- threads: coroutines, one step per frame
local threads = {}
function CreateThread(f) threads[#threads + 1] = coroutine.create(f) end
function Wait() coroutine.yield() end
Citizen = { CreateThread = CreateThread, Wait = Wait }

local function frames(n)
    for _ = 1, n do
        for _, co in ipairs(threads) do
            if coroutine.status(co) == 'suspended' then
                local ok, err = coroutine.resume(co)
                if not ok then error(err, 0) end
            end
        end
    end
end

-- ---------------------------------------------------------------- the game
local handlers = {}
function AddEventHandler(name, f)
    handlers[name] = handlers[name] or {}
    table.insert(handlers[name], f)
end
function GetCurrentResourceName() return 'fxmapgen-minimap-test' end
function PlayerPedId() return 1 end
function GetPlayerPed() return 1 end
function GetConvar(name, default)
    if name == 'fxmapgen_minimap_fixed_zoom' and fixedZoom then return 'true' end
    return default
end

-- the game's name hash (joaat of the name in lower case), as FiveM's Lua gets hashes: a signed 32-bit number
local function joaat(s)
    local h = 0
    s = s:lower()
    for i = 1, #s do
        h = (h + s:byte(i)) & 0xFFFFFFFF
        h = (h + (h << 10)) & 0xFFFFFFFF
        h = h ~ (h >> 6)
    end
    h = (h + (h << 3)) & 0xFFFFFFFF
    h = h ~ (h >> 11)
    return (h + (h << 15)) & 0xFFFFFFFF
end
local function signed(h) return h >= 0x80000000 and h - 0x100000000 or h end
function GetHashKey(s) return signed(joaat(s)) end

-- where the player is: an interior by its name (nil: outside), on foot, in a vehicle or neither (swimming), the pause
-- menu and the view of its map
local place = { interior = nil, vehicle = false, swimming = false, pause = false, pauseInterior = false }
local handles, names = {}, {}
local function interiorHandle(name)
    if not name then return 0 end
    if not handles[name] then
        names[#names + 1] = name
        handles[name] = 100 + #names
    end
    return handles[name]
end
local count = {}
local function reset() count = { zoom = 0, zoomValue = nil, hide = 0, outside = 0, picture = 0, pictureArgs = nil, asked = 0 } end
reset()

function IsPedOnFoot() return not place.vehicle and not place.swimming end
function IsPedInAnyVehicle() return place.vehicle end
function GetInteriorFromEntity() return interiorHandle(place.interior) end
function GetInteriorLocationAndNamehash(interior)
    count.asked = count.asked + 1
    return { x = 0.0, y = 0.0, z = 0.0 }, signed(joaat(names[interior - 100]))
end
function IsPauseMenuActive() return place.pause end
function IsPausemapInInteriorMode() return place.pause and place.pauseInterior end
function HideMinimapExteriorMapThisFrame() count.hide = count.hide + 1 end
function SetRadarAsExteriorThisFrame() count.outside = count.outside + 1 end
function SetRadarAsInteriorThisFrame(hash, x, y, a, b)
    count.picture = count.picture + 1
    count.pictureArgs = table.concat({ hash & 0xFFFFFFFF, num(x), num(y), a, b }, ':')
end
function SetRadarZoom(z) count.zoom, count.zoomValue = count.zoom + 1, z end
function SetMapZoomDataLevel(i, zoom, a, b, c, d) emit('zoomlevel', i, num(zoom), num(a), num(b), num(c), num(d)) end
function ResetMapZoomDataLevel(i) emit('reset', i) end

function RequestStreamedTextureDict() end
function HasStreamedTextureDictLoaded() return true end
function SetStreamedTextureDictAsNoLongerNeeded() end
function IsMinimapRendering() return true end
function RequestScaleformMovie(name) return name end
function HasScaleformMovieLoaded() return true end
function SetScaleformMovieAsNoLongerNeeded() return nil end

local call
function BeginScaleformMovieMethod(handle, method) call = { handle = handle, method = method, args = {} } end
local function push(v) table.insert(call.args, v) end
PushScaleformMovieFunctionParameterString = push
PushScaleformMovieFunctionParameterFloat = push
PushScaleformMovieFunctionParameterBool = push
PushScaleformMovieFunctionParameterInt = push
function EndScaleformMovieMethod()
    if call.method == 'DRAW_TEXTURE' then
        local a = call.args
        emit('tile', call.handle, a[1], a[2], a[3], num(a[4]), num(a[5]), num(a[6]), num(a[7]), tostring(a[8]), a[9], num(a[10]))
    end
end

function SetBigmapActive() end
function DisplayRadar() end
function AddReplaceTexture(_, name, txd, txn) emit('replace', name, txd, txn) end
function AddBlipForCoord(x, y) emit('blip', num(x), num(y)); return #out end
function SetBlipDisplay() end
function SetBlipAlpha() end
function RemoveBlip() end
function exports() end
print = function() end   -- Extra Map Tiles' own messages

-- ---------------------------------------------------------------- the manifest, then the scripts
local manifest = { client = {}, shared = {} }
local manifestEnv = setmetatable({}, { __index = function(_, key)
    return function(value)
        if key == 'client_scripts' then manifest.client = value
        elseif key == 'shared_scripts' then manifest.shared = value
        elseif key == 'data_file' then return function(path) emit('datafile', value, path) end
        end
    end
end })
assert(loadfile(dir .. '/fxmanifest.lua', 't', manifestEnv))()
for _, list in ipairs({ manifest.shared, manifest.client }) do
    for _, script in ipairs(list) do assert(loadfile(dir .. '/' .. script, 't'))() end
end

-- ---------------------------------------------------------------- the scenes, then the stop
local scenes = {
    { 'outside' },
    { 'no-picture', interior = 'fxtest_mlo' },
    { 'home', interior = 'fxtest_home' },
    { 'home-pause-interior', interior = 'fxtest_home', pause = true, pauseInterior = true },
    { 'home-pause-outside', interior = 'fxtest_home', pause = true },
    { 'home-vehicle', interior = 'fxtest_home', vehicle = true },
    { 'drain', interior = 'fxtest_drain' },
    { 'drain-swimming', interior = 'fxtest_drain', swimming = true },
    { 'outside-again' },
}
for _, scene in ipairs(scenes) do
    place = { interior = scene.interior, vehicle = scene.vehicle or false, swimming = scene.swimming or false,
        pause = scene.pause or false, pauseInterior = scene.pauseInterior or false }
    reset()
    frames(10)
    emit('scene', scene[1], 'zoom=' .. count.zoom .. ':' .. tostring(count.zoomValue), 'hide=' .. count.hide, 'outside=' .. count.outside,
        'picture=' .. count.picture .. (count.pictureArgs and ':' .. count.pictureArgs or ''), 'asked=' .. count.asked)
end

reset()
for _, f in ipairs(handlers.onClientResourceStop or {}) do f('another-resource') end
for _, f in ipairs(handlers.onClientResourceStop or {}) do f(GetCurrentResourceName()) end
emit('stopzoom', count.zoom, tostring(count.zoomValue))
frames(2)
local alive = 0
for _, co in ipairs(threads) do if coroutine.status(co) ~= 'dead' then alive = alive + 1 end end
emit('alive', alive)

for _, line in ipairs(out) do io.write(line, '\n') end
