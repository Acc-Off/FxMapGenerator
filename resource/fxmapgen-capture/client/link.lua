-- fxmapgen-capture, client: the link with FxMapGenerator. Loaded first: the shared state, the output lines, the console
-- command and the commands that only report (hello, status, res, ping).
--
-- FxMapGenerator drives the resource through the game's console socket with "fxmapgen <sub> ..." and reads the lines
-- printed back. Every line starts with "[fxmapgen] ". Lines for the program begin with an upper-case word and carry
-- key=value pairs without spaces; other lines are notes for the log. Protocol 1:
--
--   fxmapgen hello              HELLO proto=1 ver=<version> res=<resource name> server=<server-side version | none>
--                                     ace=0|1 players=<n> build=<game build> capture=<n>   (once the server answers, 3 s at most)
--   fxmapgen status             STATUS env= ready= busy= hmap= scan= seq= weather= hour= minute= peds= vehicles= near_peds=
--                                     near_vehicles= health= dead= frozen= px= py= pz= block=
--   fxmapgen res [name ...]     RES BEGIN n=<count>, one RES <name> <state> per resource (all of them without names),
--                                     RES END
--   fxmapgen ping               nothing: keeps the console connection open (the game drops it after about 5 s idle)
--   fxmapgen env on|off         ENV on | ENV off safe=0|1 x= y= z= | ENV off already=1               (env.lua)
--   fxmapgen safe [x y [z]]     SAFE ok=0|1 x= y= z=                                                  (env.lua)
--   fxmapgen tile <z> <tx> <ty> <fov> [margin] [quietMs]    READY seq= ... | FAIL seq= reason= ...   (camera.lua)
--   fxmapgen cam <x> <y> <h> <fov>                the same for a camera that is not over a block     (camera.lua)
--   fxmapgen hmap [step]        HMAP BEGIN ..., HMAP j= k= ... per part of a row, HMAP END | HMAP ABORT  (sample.lua)
--   fxmapgen scan ground|roads 8 <tx> <ty> [canopy]   MSCAN BEGIN ..., rows, MSCAN END, MSCAN DONE | MSCAN ABORT
--   fxmapgen scan stop                                                                                (scan.lua)
--   after env off               REFILL ok=0|1 via=<qbx_core | qb-core | none> [error=<text>]           (env.lua)
--   anything that cannot run    ERROR <reason>

FxMapGen = {
    PROTO = 1,
    -- what a capture takes: raised with every change of the captured content (the ray range, the waits, the values), so
    -- that the program can tell which blocks a later change affects (1: the ground scan's ray runs from 1200 m to -500 m)
    CAPTURE = 1,
    commands = {},
    state = {
        env = false,       -- the capture environment is on (env.lua)
        envEpoch = 0,      -- counts env on; the per-frame loop of an earlier env on stops when it changes
        ready = false,     -- the camera of request seq is in place and the scene has settled
        busy = false,      -- a tile / cam request is on its way
        seq = 0,           -- request number, counted up by tile / cam; a request on its way gives up at a newer one or at env off
        block = nil,       -- block of the last tile request
        last = nil,        -- camera of the last READY
        cam = nil,
        hmapBusy = false,
        hmapAbort = false,
        scanBusy = false,  -- a scan is on its way (scan.lua)
        scanAbort = false,
        scanSeq = 0,       -- scan request number (apart from seq)
        landing = false,
        savedPos = nil,    -- where the player stood before env on; kept until the player is back on the ground
        savedWanted = nil, -- the max wanted level before env on
        token = 0,         -- server requests
        waiting = {},
        replies = {},
    },
}

local S = FxMapGen.state

function FxMapGen.log(fmt, ...)
    print(('[fxmapgen] ' .. fmt):format(...))
end
local log = FxMapGen.log

function FxMapGen.flag(v)
    return v and 1 or 0
end
local flag = FxMapGen.flag

-- Server requests: request() sends one and returns its token, answer() waits (in a thread) for the reply table.
-- answer() gives nil when no reply came within timeoutMs, or as soon as keepWaiting() turns false.
function FxMapGen.request(event, ...)
    S.token = S.token + 1
    local token = S.token
    S.waiting[token] = true
    TriggerServerEvent(event, token, ...)
    return token
end

function FxMapGen.answer(token, timeoutMs, keepWaiting)
    local t0 = GetGameTimer()
    while S.replies[token] == nil and GetGameTimer() - t0 < timeoutMs and (keepWaiting == nil or keepWaiting()) do
        Wait(20)
    end
    local r = S.replies[token]
    FxMapGen.forget(token)
    return r
end

-- A request whose answer is no longer wanted (a late reply is then dropped).
function FxMapGen.forget(token)
    S.replies[token], S.waiting[token] = nil, nil
end

RegisterNetEvent('fxmapgen:reply', function(token, answer)
    if S.waiting[token] then S.replies[token] = answer end
end)

-- ---------------------------------------------------------------- hello, status, res, ping
FxMapGen.commands.hello = function()
    CreateThread(function()
        local r = FxMapGen.answer(FxMapGen.request('fxmapgen:hello'), 3000)
        local res = GetCurrentResourceName()
        log('HELLO proto=%d ver=%s res=%s server=%s ace=%d players=%d build=%d capture=%d', FxMapGen.PROTO,
            GetResourceMetadata(res, 'version', 0) or '?', res, r and r.version or 'none', flag(r and r.ace),
            r and r.players or -1, GetGameBuildNumber(), FxMapGen.CAPTURE)
    end)
end

local WEATHERS = { 'EXTRASUNNY', 'CLEAR', 'NEUTRAL', 'SMOG', 'FOGGY', 'OVERCAST', 'CLOUDS', 'CLEARING', 'RAIN', 'THUNDER',
    'SNOW', 'BLIZZARD', 'SNOWLIGHT', 'XMAS', 'HALLOWEEN', 'RAIN_HALLOWEEN', 'SNOW_HALLOWEEN' }
local NEAR = 600.0 -- metres around the player that count as near

local function weatherName()
    local hash = GetPrevWeatherTypeHashName()
    for _, name in ipairs(WEATHERS) do
        if GetHashKey(name) == hash then return name end
    end
    return ('%08x'):format(hash & 0xFFFFFFFF)
end

FxMapGen.commands.status = function()
    local ped = PlayerPedId()
    local c = GetEntityCoords(ped)
    local peds, vehicles, nearPeds, nearVehicles = 0, 0, 0, 0
    for _, p in ipairs(GetGamePool('CPed')) do
        if p ~= ped then
            peds = peds + 1
            if #(GetEntityCoords(p) - c) < NEAR then nearPeds = nearPeds + 1 end
        end
    end
    for _, v in ipairs(GetGamePool('CVehicle')) do
        vehicles = vehicles + 1
        if #(GetEntityCoords(v) - c) < NEAR then nearVehicles = nearVehicles + 1 end
    end
    log('STATUS env=%d ready=%d busy=%d hmap=%d scan=%d seq=%d weather=%s hour=%d minute=%d peds=%d vehicles=%d near_peds=%d near_vehicles=%d health=%d dead=%d frozen=%d px=%.1f py=%.1f pz=%.1f block=%s',
        flag(S.env), flag(S.ready), flag(S.busy), flag(S.hmapBusy), flag(S.scanBusy), S.seq, weatherName(), GetClockHours(), GetClockMinutes(),
        peds, vehicles, nearPeds, nearVehicles, GetEntityHealth(ped), flag(IsEntityDead(ped)),
        flag(IsEntityPositionFrozen(ped)), c.x, c.y, c.z, S.block and S.block.name or 'none')
end

-- The state of resources as this client sees them (started, stopped, missing, ...): the ones named, or all of them.
FxMapGen.commands.res = function(args)
    local names = {}
    for i = 2, #args do names[#names + 1] = args[i] end
    if #names == 0 then
        for i = 0, GetNumResources() - 1 do
            local name = GetResourceByFindIndex(i)
            if name then names[#names + 1] = name end
        end
        table.sort(names)
    end
    log('RES BEGIN n=%d', #names)
    for _, name in ipairs(names) do log('RES %s %s', name, GetResourceState(name)) end
    log('RES END')
end

FxMapGen.commands.ping = function() end

-- ---------------------------------------------------------------- the console command
local USAGE = 'usage: fxmapgen hello | status | res [name ...] | ping | env on|off | safe [x y [z]]'
    .. ' | tile <z> <tx> <ty> <fov> [margin] [quietMs] | cam <x> <y> <h> <fov> | hmap [step]'
    .. ' | scan ground|roads 8 <tx> <ty> [canopy] | scan stop'

RegisterCommand('fxmapgen', function(_, args)
    local handler = args[1] and FxMapGen.commands[args[1]]
    if handler then handler(args) else log('ERROR %s', USAGE) end
end, false)
