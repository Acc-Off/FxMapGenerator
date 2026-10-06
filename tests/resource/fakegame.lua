-- A fake FiveM for testing the fxmapgen-capture resource under plain Lua 5.4. The resource's client and server scripts run
-- with the natives they call replaced by a small model of the game, so its commands and the lines it prints can be checked
-- without GTA V. It is not a model of the real game: a virtual clock of 16 ms frames, threads as coroutines, net events
-- delivered after 30 ms, a made-up world (land with buildings, sea, a pond, a mountain, a patch where streaming never goes
-- quiet), and collision that loads 200 ms after the ped has come within reach. The land has materials (buildings of
-- concrete, a grid of tarmac roads, grass with trees between), streets and zones for the scans.
--
--   local FakeGame = require('fakegame')
--   local game = FakeGame.new('resource/fxmapgen-capture', { players = { '1', '2' } })
--   local mark = game:mark()
--   game:command('fxmapgen tile 8 60 132 2 1.06')
--   local line = game:waitLine('^%[fxmapgen%] READY ', 30000, mark)
--
-- A native the resource calls that is not modelled here is nil, so calling it fails the test like a misspelt native
-- would fail in the game.

local FakeGame = {}
FakeGame.__index = FakeGame

local FRAME_MS, LATENCY_MS = 16, 30

-- ---------------------------------------------------------------- vectors, hashes, copies
local Vec = {}
Vec.__index = Vec
local function vec3(x, y, z) return setmetatable({ x = x, y = y, z = z }, Vec) end
Vec.__sub = function(a, b) return vec3(a.x - b.x, a.y - b.y, a.z - b.z) end
Vec.__len = function(a) return math.sqrt(a.x * a.x + a.y * a.y + a.z * a.z) end
FakeGame.vec3 = vec3

-- GetHashKey: Jenkins one-at-a-time of the lower-case name, as a signed 32-bit number like the game gives
local function joaat(s)
    local h = 0
    for i = 1, #s do
        h = (h + s:lower():byte(i)) & 0xFFFFFFFF
        h = (h + (h << 10)) & 0xFFFFFFFF
        h = h ~ (h >> 6)
    end
    h = (h + (h << 3)) & 0xFFFFFFFF
    h = h ~ (h >> 11)
    h = (h + (h << 15)) & 0xFFFFFFFF
    if h >= 0x80000000 then h = h - 0x100000000 end
    return h
end
FakeGame.joaat = joaat

-- net events carry copies, like the game's serialisation
local function copy(v)
    if type(v) ~= 'table' then return v end
    local t = {}
    for k, x in pairs(v) do t[k] = copy(x) end
    return t
end

-- ---------------------------------------------------------------- the made-up world
-- rectangles are { west, north, east, south } in game metres (y grows northwards)
local function inRect(r, x, y) return x >= r[1] and x <= r[3] and y <= r[2] and y >= r[4] end

-- the longest line the game keeps of what the client prints (FiveM, seen on the scan rows of a deep sea bottom)
local CLIENT_LINE_MAX = 1023

local World = {}
World.__index = World

function FakeGame.defaultWorld()
    return setmetatable({
        land = { -500.0, 0.0, 1000.0, -1300.0 },        -- its south edge crosses block z8_60_136 (a coast block)
        mountain = { 1185.0, 5888.0, 2066.0, 5006.0 },  -- around block z8_80_40, ground at 800 m
        pond = { 300.0, -960.0, 340.0, -1000.0, level = 45.0, bottom = 35.0 }, -- in block z8_60_132
        noisy = { 360.0, -881.25, 641.25, -1162.5 },    -- block z8_64_132: streaming never goes quiet
        slope = { -2733.75, -1443.75, -2452.5, -1725.0 }, -- block z8_20_140: a sea bottom from 250 m down, deeper eastwards
    }, World)
end

function World:kind(x, y)
    if inRect(self.pond, x, y) then return 'pond' end
    if inRect(self.land, x, y) or inRect(self.mountain, x, y) then return 'land' end
    return 'sea'
end

-- metres from (x, y) to the nearest land (the land and the mountain rectangles)
function World:toLand(x, y)
    local best = math.huge
    for _, r in ipairs({ self.land, self.mountain }) do
        local dx = math.max(r[1] - x, 0.0, x - r[3])
        local dy = math.max(r[4] - y, 0.0, y - r[2])
        best = math.min(best, math.sqrt(dx * dx + dy * dy))
    end
    return best
end

-- what the ground probe hits: the terrain (buildings 20 m high on a 40 m pattern), the pond's bottom or the sea bottom
-- (40 m down within 500 m of land, 300 m down within 1 km, 600 m down on the open sea: below the ground probe's -500 m;
-- on the slope 250 m down at its west edge and 4 cm deeper every metre eastwards)
function World:ground(x, y)
    local kind = self:kind(x, y)
    if kind == 'pond' then return self.pond.bottom end
    if kind == 'sea' and self.slope and inRect(self.slope, x, y) then return -250.0 - 0.04 * (x - self.slope[1]) end
    if kind == 'sea' then
        local d = self:toLand(x, y)
        return d <= 500.0 and -40.0 or d <= 1000.0 and -300.0 or -600.0
    end
    if inRect(self.mountain, x, y) then return 800.0 end
    local building = ((x // 40) + (y // 40)) % 7 == 0
    return 30.0 + 0.01 * (x + 500.0) + (building and 20.0 or 0.0)
end

-- the ground without the buildings (what a ray meets where a building's collision has not loaded yet) and its material
function World:bare(x, y)
    if self:kind(x, y) ~= 'land' or inRect(self.mountain, x, y) or not self:building(x, y) then return self:ground(x, y) end
    return self:ground(x, y) - 20.0
end

function World:water(x, y)
    local kind = self:kind(x, y)
    if kind == 'pond' then return self.pond.level end
    if kind == 'sea' then return 0.0 end
    return nil
end

-- points of the land where the ground probe finds nothing, like gaps in the collision
function World:probeMiss(x, y)
    return self:kind(x, y) == 'land' and math.floor(x) % 53 == 0 and math.floor(y) % 47 == 0
end

-- the height grid's value at (x, y): the higher of the ground and the water; nil where the probe misses on dry land
function World:surface(x, y)
    local w = self:water(x, y)
    if self:probeMiss(x, y) then return w end
    local g = self:ground(x, y)
    if w and w > g then return w end
    return g
end

-- the scene around a point in open water never reports loaded
function World:openWater(x, y)
    for i = -2, 2 do
        for j = -2, 2 do
            if self:kind(x + i * 100.0, y + j * 100.0) ~= 'sea' then return false end
        end
    end
    return true
end

function World:noisyAt(x, y) return inRect(self.noisy, x, y) end

-- the scans: material hashes as the game gives them (signed 32-bit)
FakeGame.MATERIALS = { TARMAC = 282940568, CONCRETE = 1187676648, GRASS = 1333033863, ROCK = -840216541,
                       SAND_UNDERWATER = -1136057692, MUD_UNDERWATER = -273490167 }
FakeGame.ZONES = { LEGSQU = 'Legion Square', PBOX = 'Pillbox Hill', OCEANA = 'Pacific Ocean', CHIL = 'Mount Chiliad' }

function World:building(x, y) return ((x // 40) + (y // 40)) % 7 == 0 end

-- roads: a grid of 12 m wide streets every 100 m over the low land (not the mountain)
function World:onRoad(x, y)
    return self:kind(x, y) == 'land' and not inRect(self.mountain, x, y)
        and (math.abs(x % 100.0 - 50.0) <= 6.0 or math.abs(y % 100.0 - 50.0) <= 6.0)
end

function World:bareMaterial(x, y)
    if self:kind(x, y) ~= 'land' or inRect(self.mountain, x, y) or not self:building(x, y) then return self:material(x, y) end
    return self:onRoad(x, y) and FakeGame.MATERIALS.TARMAC or FakeGame.MATERIALS.GRASS
end

function World:material(x, y)
    local kind = self:kind(x, y)
    local M = FakeGame.MATERIALS
    if kind == 'pond' then return M.MUD_UNDERWATER end
    if kind == 'sea' then return M.SAND_UNDERWATER end
    if inRect(self.mountain, x, y) then return M.ROCK end
    if self:building(x, y) then return M.CONCRETE end
    if self:onRoad(x, y) then return M.TARMAC end
    return M.GRASS
end

-- tree crowns 8 m over some of the grass
function World:tree(x, y)
    return self:material(x, y) == FakeGame.MATERIALS.GRASS and ((x // 10) + (y // 10)) % 5 == 0
end

-- the street nearest the point: the avenues run north-south (x = 50 m + 100 k), the streets east-west; none at sea
function World:street(x, y)
    if self:kind(x, y) ~= 'land' then return nil end
    return math.abs(x % 100.0 - 50.0) <= math.abs(y % 100.0 - 50.0) and 'Fake Ave' or 'Fake St'
end

function World:zone(x, y)
    if self:kind(x, y) == 'sea' then return 'OCEANA' end
    if inRect(self.mountain, x, y) then return 'CHIL' end
    return x < 250.0 and 'LEGSQU' or 'PBOX'
end

-- ---------------------------------------------------------------- the manifest
local function readManifest(dir)
    local m = { client = {}, server = {} }
    local env = {}
    for _, key in ipairs({ 'fx_version', 'game', 'lua54', 'name', 'author', 'description', 'version', 'repository' }) do
        env[key] = function(v) m[key] = v end
    end
    local function adder(list) return function(v)
        if type(v) == 'table' then for _, s in ipairs(v) do list[#list + 1] = s end else list[#list + 1] = v end
    end end
    env.client_scripts, env.client_script = adder(m.client), adder(m.client)
    env.server_scripts, env.server_script = adder(m.server), adder(m.server)
    assert(loadfile(dir .. '/fxmanifest.lua', 't', env))()
    return m
end
FakeGame.readManifest = readManifest

-- ---------------------------------------------------------------- the game
local STD = { 'assert', 'error', 'ipairs', 'next', 'pairs', 'pcall', 'select', 'tonumber', 'tostring', 'type', 'xpcall',
    'setmetatable', 'getmetatable', 'rawget', 'rawset', 'rawequal', 'rawlen', 'string', 'table', 'math', 'utf8' }

-- opts: players (server ids, default { '1' }), aces (set, default { ['command.fxmapgen'] = true }), resources (name ->
-- state), noServer (the server scripts are not loaded), inVehicle, maxWanted, world
function FakeGame.new(resourceDir, opts)
    opts = opts or {}
    local game = setmetatable({}, FakeGame)
    game.dir = resourceDir
    game.manifest = readManifest(resourceDir)
    game.resourceName = 'fxmapgen-capture'
    game.world = opts.world or FakeGame.defaultWorld()
    game.now = 0
    game.threads = {}
    game.queue = {}
    game.handlers = { client = {}, server = {} }
    game.commands = {}
    game.console, game.serverConsole = {}, {}
    game.calls, game.last = {}, {}
    game.draws, game.lastDraws = {}, {}
    game.players = opts.players or { '1' }
    game.aces = opts.aces or { ['command.fxmapgen'] = true }
    game.resources = opts.resources or {
        ['fxmapgen-capture'] = 'started', chat = 'started', ox_lib = 'started', qbx_core = 'started',
        qbx_hud = 'started', qbx_density = 'started', ['Renewed-Weathersync'] = 'started',
    }
    game.inVehicle = opts.inVehicle or false
    game.maxWanted = opts.maxWanted or 2
    game.weather, game.weatherOverride, game.weatherPersist, game.rain = 'CLOUDS', nil, false, -1.0
    game.clock, game.clockOverride = { 8, 30 }, nil
    game.clouds = { layer = 'Cirrus', opacity = 1.0 } -- the cloud layer the weather has loaded
    game.tc = { names = {}, mods = {}, active = nil, strength = nil }
    game.cams, game.nextCam, game.rendering = {}, 900, false
    game.focus, game.scene = nil, nil
    game.metadata = {}
    game.falls = {} -- times the player's ped was unfrozen where it would fall
    game.moves = {} -- every move of the player's ped: { t, x, y, z }

    -- entities: the player's ped, and NPCs and vehicles around where the player stands
    game.entities = {}
    local start = vec3(200.0, -900.0, game.world:ground(200.0, -900.0) + 1.0)
    game.ped = { handle = 1, kind = 'ped', player = true, pos = start, movedAt = -1000, frozen = false, visible = true,
                 invincible = false, health = 200, maxHealth = 200 }
    game.entities[1] = game.ped
    for i = 1, 10 do
        game.entities[200 + i] = { handle = 200 + i, kind = 'ped', pos = vec3(200.0 + 10 * i, -900.0, 38.0), health = 200 }
    end
    for i = 1, 8 do
        game.entities[100 + i] = { handle = 100 + i, kind = 'vehicle', pos = vec3(200.0, -900.0 - 12 * i, 38.0), health = 1000 }
    end
    game.startPos = start

    game.clientEnv = game:makeEnv(game:clientNatives())
    game.serverEnv = game:makeEnv(game:serverNatives())
    for _, f in ipairs(game.manifest.client) do game:load(game.clientEnv, f) end
    if not opts.noServer then
        for _, f in ipairs(game.manifest.server) do game:load(game.serverEnv, f) end
    end
    return game
end

function FakeGame:makeEnv(natives)
    local env = {}
    for _, k in ipairs(STD) do env[k] = _G[k] end
    for k, v in pairs(natives) do env[k] = v end
    env._G = env
    return env
end

function FakeGame:load(env, file)
    local chunk = assert(loadfile(self.dir .. '/' .. file, 't', env))
    chunk()
end

function FakeGame:record(name, ...)
    self.calls[name] = (self.calls[name] or 0) + 1
    self.last[name] = table.pack(...)
end

function FakeGame:print(side, ...)
    local parts = table.pack(...)
    for i = 1, parts.n do parts[i] = tostring(parts[i]) end
    local line = table.concat(parts, '\t', 1, parts.n)
    local out = side == 'client' and self.console or self.serverConsole
    for l in (line .. '\n'):gmatch('(.-)\n') do
        -- the game cuts a line the client prints at 1,023 characters
        out[#out + 1] = side == 'client' and l:sub(1, CLIENT_LINE_MAX) or l
    end
end

function FakeGame:thread(fn, side)
    self.threads[#self.threads + 1] = { co = coroutine.create(fn), wake = self.now, side = side }
end

function FakeGame:on(side, name, fn)
    local list = self.handlers[side][name]
    if not list then list = {}; self.handlers[side][name] = list end
    list[#list + 1] = fn
end

function FakeGame:send(side, name, src, ...)
    self.queue[#self.queue + 1] = { at = self.now + LATENCY_MS, side = side, name = name, src = src, args = copy(table.pack(...)) }
end

function FakeGame:fire(side, name, src, args)
    local list = self.handlers[side][name]
    if not list then return end
    if side == 'server' then self.serverEnv.source = src end
    for _, fn in ipairs(list) do fn(table.unpack(args, 1, args.n)) end
    if side == 'server' then self.serverEnv.source = nil end
end

-- the player's ped (or another entity) moves; streaming starts over when the ped moves more than a metre
function FakeGame:move(h, x, y, z)
    local e = self.entities[h] or error('no entity ' .. tostring(h))
    if h == self.ped.handle then
        local d2 = (e.pos.x - x) ^ 2 + (e.pos.y - y) ^ 2 + (e.pos.z - z) ^ 2
        if d2 > 1.0 then
            self.ped.movedAt = self.now
            -- a jump across the map takes the collision longer to load than a step nearby
            self.ped.loadMs = d2 > 350.0 ^ 2 and 1500 or 200
        end
        self.moves[#self.moves + 1] = { t = self.now, x = x, y = y, z = z }
    end
    e.pos = vec3(x, y, z)
end

-- collision at (x, y) has loaded once the ped has stayed within reach for 200 ms (1.5 s after a jump of more than
-- 350 m): 350 m across, 400 m up or down
function FakeGame:collisionAt(x, y)
    local p = self.ped.pos
    if self.now - self.ped.movedAt < (self.ped.loadMs or 200) then return false end
    if (p.x - x) ^ 2 + (p.y - y) ^ 2 > 350.0 ^ 2 then return false end
    return math.abs(p.z - self.world:ground(x, y)) <= 400.0
end

-- the buildings' collision: with the ground's unless game.buildingsLoadMs is set, then that long after the ped last
-- moved (as in the real game, where rays can meet the ground before the roofs around have loaded)
function FakeGame:buildingsLoaded()
    return self.buildingsLoadMs == nil or self.now - self.ped.movedAt >= self.buildingsLoadMs
end

-- ---------------------------------------------------------------- running
function FakeGame:frame()
    if self.stopped then error('the resource has stopped') end
    self.now = self.now + FRAME_MS
    self.draws = {}
    local due, later = {}, {}
    for _, ev in ipairs(self.queue) do
        if ev.at <= self.now then due[#due + 1] = ev else later[#later + 1] = ev end
    end
    self.queue = later
    for _, ev in ipairs(due) do self:fire(ev.side, ev.name, ev.src, ev.args) end
    local list = {}
    for i, t in ipairs(self.threads) do list[i] = t end
    for _, t in ipairs(list) do
        if not t.dead and t.wake <= self.now then
            local ok, ms = coroutine.resume(t.co)
            if not ok then error(debug.traceback(t.co, ms), 0) end
            if coroutine.status(t.co) == 'dead' then t.dead = true else t.wake = self.now + (ms or 0) end
        end
    end
    local alive = {}
    for _, t in ipairs(self.threads) do if not t.dead then alive[#alive + 1] = t end end
    self.threads = alive
    self.lastDraws = self.draws
end

function FakeGame:run(ms)
    local stop = self.now + ms
    while self.now < stop do self:frame() end
end

-- runs frames until pred() is true; false after maxMs
function FakeGame:runUntil(pred, maxMs)
    local stop = self.now + maxMs
    while not pred() do
        if self.now >= stop then return false end
        self:frame()
    end
    return true
end

function FakeGame:mark() return #self.console end

-- the first console line after mark that matches the Lua pattern
function FakeGame:find(pattern, mark)
    for i = (mark or 0) + 1, #self.console do
        if self.console[i]:find(pattern) then return self.console[i], i end
    end
    return nil
end

function FakeGame:waitLine(pattern, maxMs, mark)
    local line
    self:runUntil(function()
        line = self:find(pattern, mark)
        return line ~= nil
    end, maxMs)
    return line
end

function FakeGame:linesSince(mark)
    local out = {}
    for i = mark + 1, #self.console do out[#out + 1] = self.console[i] end
    return out
end

-- a command typed into the client console
function FakeGame:command(line)
    local words = {}
    for w in line:gmatch('%S+') do words[#words + 1] = w end
    local fn = self.commands[words[1]] or error('no command ' .. words[1])
    local args = {}
    for i = 2, #words do args[#args + 1] = words[i] end
    fn(0, args, line)
end

-- the resource stops: onResourceStop runs, then no thread or handler of it runs any more
function FakeGame:stopResource()
    self:fire('client', 'onResourceStop', nil, table.pack(self.resourceName))
    self.threads = {}
    self.queue = {}
    self.stopped = true
end

-- the beacon drawn in the last frame: ready (true / false / nil if not drawn) and the seq bits as a number
function FakeGame:beacon()
    local squares = {}
    for _, d in ipairs(self.lastDraws) do
        local i = math.floor(d[1] / (4.0 / 1920.0))
        squares[i] = d
    end
    if not squares[0] then return nil end
    local ready = squares[0][6] == 255 and squares[0][5] == 0
    local seq = 0
    for bit = 0, 2 do
        local s = squares[1 + bit]
        if s and s[5] == 255 then seq = seq | (1 << bit) end
    end
    return ready, seq
end

-- ---------------------------------------------------------------- natives
function FakeGame:clientNatives()
    local game = self
    local n = {}
    local function ent(h) return game.entities[h] or error('no entity ' .. tostring(h)) end
    local function rec(name) return function(...) game:record(name, ...) end end
    local function sortedResources()
        local names = {}
        for k in pairs(game.resources) do names[#names + 1] = k end
        table.sort(names)
        return names
    end

    -- threads, clock, console, events, resources
    n.CreateThread = function(fn) game:thread(fn, 'client') end
    n.Wait = function(ms) coroutine.yield(ms or 0) end
    n.GetGameTimer = function() return game.now end
    n.print = function(...) game:print('client', ...) end
    n.RegisterCommand = function(name, fn) game.commands[name] = fn end
    n.RegisterNetEvent = function(name, fn) if fn then game:on('client', name, fn) end end
    n.AddEventHandler = function(name, fn) game:on('client', name, fn) end
    n.TriggerServerEvent = function(name, ...) game:send('server', name, 1, ...) end
    n.GetCurrentResourceName = function() return game.resourceName end
    n.GetResourceMetadata = function(res, key) return res == game.resourceName and game.manifest[key] or nil end
    n.GetResourceState = function(name) return game.resources[name] or 'missing' end
    n.GetNumResources = function() return #sortedResources() end
    n.GetResourceByFindIndex = function(i) return sortedResources()[i + 1] end
    n.GetGameBuildNumber = function() return 3258 end
    n.GetHashKey = joaat

    -- the player and entities
    n.PlayerPedId = function() return game.ped.handle end
    n.PlayerId = function() return 0 end
    n.GetEntityCoords = function(h) local p = ent(h).pos; return vec3(p.x, p.y, p.z) end
    n.SetEntityCoordsNoOffset = function(h, x, y, z) game:move(h, x, y, z) end
    n.SetEntityCoords = function(h, x, y, z) game:move(h, x, y, z) end
    n.FreezeEntityPosition = function(h, on)
        ent(h).frozen = on
        -- the accident this guards against: a ped unfrozen high up, or over collision that has not loaded, falls
        if h == game.ped.handle and not on then
            local p = game.ped.pos
            local surface = game.world:surface(p.x, p.y) or game.world:ground(p.x, p.y)
            if not game:collisionAt(p.x, p.y) or p.z - surface > 3.0 then
                game.falls[#game.falls + 1] = ('unfrozen at (%.1f, %.1f, %.1f), surface %.1f, collision %s'):format(
                    p.x, p.y, p.z, surface, tostring(game:collisionAt(p.x, p.y)))
            end
        end
    end
    n.IsEntityPositionFrozen = function(h) return ent(h).frozen end
    n.SetEntityVisible = function(h, on) ent(h).visible = on end
    n.SetEntityInvincible = function(h, on) ent(h).invincible = on end
    n.GetEntityHealth = function(h) return ent(h).health end
    n.SetEntityHealth = function(h, v) ent(h).health = v end
    n.SetEntityMaxHealth = function(h, v) ent(h).maxHealth = v end
    n.IsEntityDead = function(h) return ent(h).health <= 100 end
    n.IsPedInAnyVehicle = function() return game.inVehicle end
    n.ClearPedTasksImmediately = function() game.inVehicle = false; game:record('ClearPedTasksImmediately') end
    n.GetGamePool = function(pool)
        local kind = pool == 'CPed' and 'ped' or pool == 'CVehicle' and 'vehicle' or error('pool ' .. pool)
        local out = {}
        for h, e in pairs(game.entities) do if e.kind == kind then out[#out + 1] = h end end
        table.sort(out)
        return out
    end
    n.GetMaxWantedLevel = function() return game.maxWanted end
    n.SetMaxWantedLevel = function(v) game.maxWanted = v end

    -- weather, clock, timecycle
    n.SetWeatherTypeNowPersist = function(name) game.weather, game.weatherPersist = name, true end
    n.SetOverrideWeather = function(name) game.weatherOverride = name end
    n.ClearOverrideWeather = function() game.weatherOverride = nil end
    n.ClearWeatherTypePersist = function() game.weatherPersist = false end
    n.GetPrevWeatherTypeHashName = function() return joaat(game.weatherOverride or game.weather) end
    n.SetRainLevel = function(v) game.rain = v end
    n.ClearCloudHat = function() game.clouds.layer = nil end
    n.SetCloudHatOpacity = function(v) game.clouds.opacity = v end
    n.NetworkOverrideClockTime = function(h, m) game.clockOverride = { h, m } end
    n.NetworkClearClockTimeOverride = function() game.clockOverride = nil end
    n.GetClockHours = function() return (game.clockOverride or game.clock)[1] end
    n.GetClockMinutes = function() return (game.clockOverride or game.clock)[2] end
    n.GetTimecycleModifierIndexByName = function(name)
        for i, nm in ipairs(game.tc.names) do if nm == name then return i - 1 end end
        return -1
    end
    n.CreateTimecycleModifier = function(name)
        game.tc.names[#game.tc.names + 1] = name
        game.tc.mods[name] = {}
    end
    n.SetTimecycleModifierVar = function(name, var, v1, v2)
        local mod = game.tc.mods[name] or error('no timecycle modifier ' .. name)
        mod[var] = { v1, v2 }
    end
    n.SetTimecycleModifier = function(name) game.tc.active = name end
    n.SetTimecycleModifierStrength = function(s) game.tc.strength = s end
    n.ClearTimecycleModifier = function() game.tc.active = nil end

    -- camera
    n.CreateCam = function(name)
        game.nextCam = game.nextCam + 1
        game.cams[game.nextCam] = { name = name }
        return game.nextCam
    end
    local function cam(h) return game.cams[h] or error('no camera ' .. tostring(h)) end
    n.SetCamUseShallowDofMode = function(h, on) cam(h).shallowDof = on end
    n.SetCamCoord = function(h, x, y, z) cam(h).coord = vec3(x, y, z) end
    n.SetCamRot = function(h, x, y, z, order) cam(h).rot = { x, y, z, order } end
    n.SetCamFov = function(h, v) cam(h).fov = v end
    n.SetCamFarClip = function(h, v) cam(h).far = v end
    n.SetCamNearClip = function(h, v) cam(h).near = v end
    n.RenderScriptCams = function(on) game.rendering = on end
    n.DestroyCam = function(h) cam(h); game.cams[h] = nil end

    -- streaming, collision, probes
    n.SetFocusPosAndVel = function(x, y, z) game.focus = vec3(x, y, z) end
    n.ClearFocus = function() game.focus = nil end
    n.NewLoadSceneStartSphere = function(x, y) game.scene = { x = x, y = y, at = game.now } end
    n.NewLoadSceneStop = function() game.scene = nil end
    n.IsNewLoadSceneLoaded = function()
        local s = game.scene
        return s ~= nil and not game.world:openWater(s.x, s.y) and game.now - s.at >= 400
    end
    -- as the real game answered: yes while a script focus near the entity streams its collision (the tile
    -- flow), no for ever once the focus is cleared (env off, safe), even with the ground loaded and people walking about
    n.HasCollisionLoadedAroundEntity = function(h)
        if game.collisionQuestion == false then return false end -- as over the sea off a coast in the real game
        local p, f = ent(h).pos, game.focus
        if not f or (f.x - p.x) ^ 2 + (f.y - p.y) ^ 2 > 350.0 ^ 2 then return false end
        -- game.lateBuildings: the answer does not wait for the buildings (as at Paleto Bay right after a jump across the map)
        return game:collisionAt(p.x, p.y) and (game.lateBuildings or game:buildingsLoaded())
    end
    -- a ray straight down once the collision there has loaded: the map's collision (flags 1) is the ground with its
    -- material, the water collision (flags 128) only the pond's (not the sea), the foliage (flags 256) the tree crowns
    n.StartExpensiveSynchronousShapeTestLosProbe = function(x1, y1, z1, x2, y2, z2, flags)
        game.rays = (game.rays or 0) + 1
        local hit = nil
        if x1 == x2 and y1 == y2 and game:collisionAt(x1, y1) then
            local w, z, mat = game.world, nil, 0
            if flags == 1 and not game:buildingsLoaded() then z, mat = w:bare(x1, y1), w:bareMaterial(x1, y1)
            elseif flags == 1 then z, mat = w:ground(x1, y1), w:material(x1, y1)
            elseif flags == 128 then z = w:kind(x1, y1) == 'pond' and w.pond.level or nil
            elseif flags == 256 then z = w:tree(x1, y1) and w:ground(x1, y1) + 8.0 or nil end
            if z and z <= z1 and z >= z2 then hit = { at = vec3(x1, y1, z), mat = mat } end
        end
        game.shapeTests = game.shapeTests or {}
        game.shapeTests[game.rays] = hit or false
        return game.rays
    end
    n.GetShapeTestResult = function(handle)
        local hit = game.shapeTests[handle]
        game.shapeTests[handle] = nil
        if hit then return 2, 1, hit.at, vec3(0.0, 0.0, 1.0), 0 end
        return 2, 0, vec3(0.0, 0.0, 0.0), vec3(0.0, 0.0, 0.0), 0
    end
    n.GetShapeTestResultIncludingMaterial = function(handle)
        local hit = game.shapeTests[handle]
        game.shapeTests[handle] = nil
        if hit then return 2, true, hit.at, vec3(0.0, 0.0, 1.0), hit.mat, 0 end
        return 2, false, vec3(0.0, 0.0, 0.0), vec3(0.0, 0.0, 0.0), 0, 0
    end
    -- roads, streets and zones answer from the map data, loaded or not
    n.IsPointOnRoad = function(x, y) return game.world:onRoad(x, y) end
    n.GetStreetNameAtCoord = function(x, y)
        local s = game.world:street(x, y)
        return s and joaat(s) or 0, 0
    end
    n.GetStreetNameFromHashKey = function(h)
        for _, s in ipairs({ 'Fake Ave', 'Fake St' }) do if joaat(s) == h then return s end end
        return ''
    end
    n.GetNameOfZone = function(x, y) return game.world:zone(x, y) end
    n.GetLabelText = function(code) return FakeGame.ZONES[code] or 'NULL' end
    -- 20 requests right after the ped moved, one less each frame; always some in the noisy block; game.requestBursts =
    -- { { from, to }, ... } (game times in ms) adds requests coming back for a while
    n.GetNumberOfStreamingRequests = function()
        local p = game.ped.pos
        if game.world:noisyAt(p.x, p.y) then return 2 end
        for _, b in ipairs(game.requestBursts or {}) do
            if game.now >= b[1] and game.now < b[2] then return 3 end
        end
        -- the buildings' collision still on its way keeps a request open
        return math.max(game:buildingsLoaded() and 0 or 1, 20 - (game.now - game.ped.movedAt) // FRAME_MS)
    end
    n.GetGroundZFor_3dCoord = function(x, y, z)
        if not game:collisionAt(x, y) or game.world:probeMiss(x, y) then return false, 0.0 end
        local g = game:buildingsLoaded() and game.world:ground(x, y) or game.world:bare(x, y)
        if g > z then return false, 0.0 end
        return true, g
    end
    n.GetWaterHeightNoWaves = function(x, y)
        local w = game.world:water(x, y)
        if w then return true, w end
        return false, 0.0
    end

    -- the beacon
    n.DrawRect = function(x, y, w, h, r, g, b, a) game.draws[#game.draws + 1] = { x, y, w, h, r, g, b, a } end

    -- switches recorded for the tests (calls counted, last arguments kept)
    for _, name in ipairs({ 'DisplayHud', 'DisplayRadar', 'HideHudAndRadarThisFrame', 'SetVehicleDensityMultiplierThisFrame',
        'SetRandomVehicleDensityMultiplierThisFrame', 'SetParkedVehicleDensityMultiplierThisFrame',
        'SetPedDensityMultiplierThisFrame', 'SetScenarioPedDensityMultiplierThisFrame',
        'SetAmbientVehicleRangeMultiplierThisFrame', 'SetWindSpeed', 'SetWeatherTypeOvertimePersist', 'SetPedPopulationBudget',
        'SetVehiclePopulationBudget', 'SetCreateRandomCops', 'SetCreateRandomCopsNotOnScenarios',
        'SetCreateRandomCopsOnScenarios', 'SetDispatchCopsForPlayer', 'SetRandomBoats', 'SetGarbageTrucks',
        'SetRandomTrains', 'DeleteAllTrains', 'SetAllVehicleGeneratorsActiveInArea', 'SetAllVehicleGeneratorsActive',
        'SetRandomEventFlag', 'RequestCollisionAtCoord', 'ClearAreaOfVehicles', 'ClearAreaOfPeds' }) do
        n[name] = rec(name)
    end
    return n
end

function FakeGame:serverNatives()
    local game = self
    local n = {}
    local function ofKind(kind)
        local out = {}
        for h, e in pairs(game.entities) do if e.kind == kind then out[#out + 1] = h end end
        table.sort(out)
        return out
    end
    local function framework(resource, value)
        return function()
            if game.resources[resource] ~= 'started' then error('No such export in resource ' .. resource) end
            return value
        end
    end
    local player = { Functions = { SetMetaData = function(key, value) game.metadata[key] = value end } }
    local exportsOf = {
        qbx_core = { GetPlayer = function(_, src) return tonumber(src) == 1 and player or nil end },
        ['qb-core'] = { GetCoreObject = function() return { Functions = { GetPlayer = function(src)
            return tonumber(src) == 1 and player or nil end } } end },
    }

    n.CreateThread = function(fn) game:thread(fn, 'server') end
    n.Wait = function(ms) coroutine.yield(ms or 0) end
    n.GetGameTimer = function() return game.now end
    n.print = function(...) game:print('server', ...) end
    n.RegisterNetEvent = function(name, fn) if fn then game:on('server', name, fn) end end
    n.AddEventHandler = function(name, fn) game:on('server', name, fn) end
    n.TriggerClientEvent = function(name, target, ...)
        if target == 1 or target == -1 then game:send('client', name, nil, ...) end
    end
    n.GetCurrentResourceName = function() return game.resourceName end
    n.GetResourceMetadata = function(res, key) return res == game.resourceName and game.manifest[key] or nil end
    n.GetResourceState = function(name) return game.resources[name] or 'missing' end
    n.GetPlayers = function() return copy(game.players) end
    n.IsPlayerAceAllowed = function(_, ace) return game.aces[ace] == true end
    n.GetAllVehicles = function() return ofKind('vehicle') end
    n.GetAllPeds = function() return ofKind('ped') end
    n.DoesEntityExist = function(h) return game.entities[h] ~= nil end
    n.DeleteEntity = function(h) game.entities[h] = nil end
    n.IsPedAPlayer = function(h) return game.entities[h] ~= nil and game.entities[h].player == true end
    n.exports = setmetatable({}, { __index = function(_, res)
        local e = exportsOf[res]
        if not e then error('No such export resource ' .. res) end
        local proxy = {}
        for k, fn in pairs(e) do proxy[k] = function(...) framework(res, true)(); return fn(...) end end
        return proxy
    end })
    return n
end

return FakeGame
