-- fxmapgen-capture, client: the capture environment (env on / off) and setting the player back down on the ground (safe).
--
-- env on: no NPCs or traffic, no HUD, noon, clear weather, no clouds, no fog or haze, a fixed exposure, dry ground, the
-- player's ped hidden, frozen and kept alive; the beacon in the top-left corner of the frame. Resources that draw on the screen or own the
-- weather (HUD, chat, weather sync, density) are stopped by FxMapGenerator before env on and started again after env off.
-- env off undoes it and sets the ped down where it stood before, frozen until the collision there has loaded: a ped
-- unfrozen high above the ground, or over collision that has not loaded, falls to its death.
local S = FxMapGen.state
local log = FxMapGen.log
local flag = FxMapGen.flag

local WEATHER, HOUR = 'EXTRASUNNY', 12
local MODIFIER = 'fxmapgen'
-- timecycle variables for the capture: no fog or haze (aerial haze grows with the height of the camera), a fixed
-- exposure (EV -3.0; with the automatic exposure, frames of water only came out brighter than frames with land) and
-- ground that dries at once (wet from rain it is about a third darker, and at the clear noon's own speed, 0.6, it took
-- some 100 s to dry)
local TC_VARS = {
    { 'fog_density', 0.0 }, { 'fog_alpha', 0.0 }, { 'fog_haze_density', 0.0 }, { 'fog_haze_alpha', 0.0 },
    { 'fog_start', 20000.0 }, { 'far_clip', 20000.0 },
    { 'postfx_exposure_min', -3.0 }, { 'postfx_exposure_max', -3.0 },
    { 'water_drying_speed_mult', 1000.0 },
}
-- the beacon: 4 x 4 px squares along the top edge of the 1920 x 1080 frame: [ready: green, else red][seq bit 0][1][2]
local BW, BH = 4.0 / 1920.0, 4.0 / 1080.0

local function beaconSquare(i, r, g, b)
    DrawRect(BW * (i + 0.5), BH * 0.5, BW, BH, r, g, b, 255)
end

local function eachFrame()
    DisplayHud(false)
    DisplayRadar(false)
    HideHudAndRadarThisFrame()
    -- frameworks take health off when hunger or thirst reach 0 (past invincibility); hold it at the maximum
    local ped = PlayerPedId()
    if GetEntityHealth(ped) < 200 then
        SetEntityMaxHealth(ped, 200)
        SetEntityHealth(ped, 200)
    end
    SetVehicleDensityMultiplierThisFrame(0.0)
    SetRandomVehicleDensityMultiplierThisFrame(0.0)
    SetParkedVehicleDensityMultiplierThisFrame(0.0)
    SetPedDensityMultiplierThisFrame(0.0)
    SetScenarioPedDensityMultiplierThisFrame(0.0, 0.0)
    SetAmbientVehicleRangeMultiplierThisFrame(0.0)
    SetWindSpeed(0.0)
    -- the cloud layers are unloaded, not only made invisible: seen from straight above, water mirrors the layer overhead
    -- even when it is invisible (the sea came out greyer or darker by the layer), and the game picks another layer
    -- whenever the weather changes
    ClearCloudHat()
    SetCloudHatOpacity(0.0)
    -- setting the weather holds it against the game's own weather cycle (which runs on behind), but not against a change
    -- over time another script has started (a weather sync resource's SetWeatherTypeOvertimePersist: the frames went on
    -- changing until it ended, some 20 s). A change that ends at once takes its place. (Letting the held weather go,
    -- ClearWeatherTypePersist, stops such a change too, but keeps wet ground from drying.)
    SetWeatherTypeOvertimePersist(WEATHER, 0.1)
    SetWeatherTypeNowPersist(WEATHER)
    SetOverrideWeather(WEATHER)
    NetworkOverrideClockTime(HOUR, 0, 0)
    SetRainLevel(0.0)
    if S.ready then beaconSquare(0, 0, 255, 0) else beaconSquare(0, 255, 0, 0) end
    for bit = 0, 2 do
        if (S.seq >> bit) & 1 == 1 then beaconSquare(1 + bit, 255, 255, 255) else beaconSquare(1 + bit, 0, 0, 0) end
    end
end

function FxMapGen.envOn()
    if S.env then return end
    local ped = PlayerPedId()
    if IsPedInAnyVehicle(ped, false) then ClearPedTasksImmediately(ped) end -- out of it (the vehicle is cleared too)
    if not S.savedPos then S.savedPos = GetEntityCoords(ped) end
    if not S.savedWanted then S.savedWanted = GetMaxWantedLevel() end
    S.env = true
    S.envEpoch = S.envEpoch + 1
    local epoch = S.envEpoch

    SetPedPopulationBudget(0)
    SetVehiclePopulationBudget(0)
    SetCreateRandomCops(false)
    SetCreateRandomCopsNotOnScenarios(false)
    SetCreateRandomCopsOnScenarios(false)
    SetDispatchCopsForPlayer(PlayerId(), false)
    SetMaxWantedLevel(0)
    SetRandomBoats(false)
    SetGarbageTrucks(false)
    SetRandomTrains(false)
    DeleteAllTrains()
    SetAllVehicleGeneratorsActiveInArea(-10000.0, -10000.0, -1000.0, 10000.0, 10000.0, 2000.0, false, false)
    SetRandomEventFlag(false)

    SetEntityVisible(ped, false, false)
    FreezeEntityPosition(ped, true)
    SetEntityInvincible(ped, true)

    if GetTimecycleModifierIndexByName(MODIFIER) == -1 then CreateTimecycleModifier(MODIFIER) end
    for _, v in ipairs(TC_VARS) do SetTimecycleModifierVar(MODIFIER, v[1], v[2], 0.0) end
    SetTimecycleModifier(MODIFIER)
    SetTimecycleModifierStrength(1.0)

    CreateThread(function()
        while S.env and S.envEpoch == epoch do
            eachFrame()
            Wait(0)
        end
    end)
end

-- Everything env on changed except the ped. Toggles the game cannot read back go to the game's defaults.
local function restoreWorld()
    ClearTimecycleModifier()
    FxMapGen.destroyCam()
    ClearFocus()
    NewLoadSceneStop()
    ClearOverrideWeather()
    ClearWeatherTypePersist()
    NetworkClearClockTimeOverride()
    SetRainLevel(-1.0)
    SetCloudHatOpacity(1.0) -- the layers themselves come back with the next change of the weather
    SetPedPopulationBudget(3)
    SetVehiclePopulationBudget(3)
    SetCreateRandomCops(true)
    SetCreateRandomCopsNotOnScenarios(true)
    SetCreateRandomCopsOnScenarios(true)
    SetDispatchCopsForPlayer(PlayerId(), true)
    SetMaxWantedLevel(S.savedWanted or 5)
    S.savedWanted = nil
    SetRandomBoats(true)
    SetGarbageTrucks(true)
    SetRandomTrains(true)
    SetAllVehicleGeneratorsActive()
    SetRandomEventFlag(true)
    DisplayHud(true)
    DisplayRadar(true)
end

-- The height where a ray straight down from fromZ (50 m at most) meets the map's collision; nil while the collision there
-- has not loaded. A ped set down on it does not fall through. (The game's "collision loaded around the entity" is no help
-- here: once the script focus is cleared it stays false for the player's ped, even on loaded ground, and over the sea
-- off a coast it can stay false with the focus set too.) Used by the landing and by tile.
function FxMapGen.solidBelow(x, y, fromZ, ped)
    local ray = StartExpensiveSynchronousShapeTestLosProbe(x, y, fromZ, x, y, fromZ - 50.0, 1, ped, 4)
    local _, hit, at = GetShapeTestResult(ray)
    if hit == 1 then return at.z end
    return nil
end

-- How far below z (the ped's height, about a metre over its feet) the landing waits for something to stand on: the floor
-- it stood on. What a ray meets farther down (the ground under a roof or an interior's floor whose collision has not
-- loaded yet, after the shots far away) is taken only when nothing nearer has come by the end of the wait.
local NEAR = 2.0

-- Sets the frozen ped down on the ground at (x, y) and unfreezes it. With z (where the ped stood before) the ground is
-- looked for below z; without, the surface is found the way tile does. The ped waits frozen until a ray down meets the
-- collision within NEAR below z, or the water there (10 s at most); after that it takes whatever was met farther down.
-- Returns true when it stands on the ground (or swims); false leaves it frozen and invincible where it is
-- ("fxmapgen safe" tries again).
function FxMapGen.land(x, y, z)
    local ped = PlayerPedId()
    -- env on again meanwhile: the ped belongs to the capture, leave it alone
    local function wanted() return not S.env end
    ClearPedTasksImmediately(ped)
    FreezeEntityPosition(ped, true)
    SetEntityInvincible(ped, true)
    if not z then
        local surface = FxMapGen.surfaceAt(ped, x, y, wanted)
        ClearFocus()
        if not surface or not wanted() then return false end
        z = surface + 1.0
    end
    SetEntityCoordsNoOffset(ped, x, y, z, false, false, false)
    local gz, lower = nil, nil
    local t0 = GetGameTimer()
    while GetGameTimer() - t0 < 10000 do
        RequestCollisionAtCoord(x, y, z)
        local g = FxMapGen.solidBelow(x, y, z + 1.0, ped)
        -- under water the ray finds the bottom (or nothing); the ped swims at the surface, no ground needed
        local wok, wz = GetWaterHeightNoWaves(x, y, 0.0)
        local top = g
        if wok and wz <= z + 1.0 and (not g or wz > g) then top = wz end
        if top and top >= z - NEAR then gz = top break end
        lower = top or lower
        Wait(100)
        if not wanted() then return false end
    end
    gz = gz or lower
    if not gz then return false end
    SetEntityCoordsNoOffset(ped, x, y, gz + 1.0, false, false, false)
    Wait(200)
    if not wanted() then return false end
    FreezeEntityPosition(ped, false)
    SetEntityInvincible(ped, false)
    SetEntityVisible(ped, true, false)
    return true
end

-- Lands the ped at (x, y[, z]) in a thread, then reports with the line made by done(ok, coords).
local function landThen(x, y, z, done)
    S.landing = true
    CreateThread(function()
        local ok = FxMapGen.land(x + 0.0, y + 0.0, z and z + 0.0)
        S.landing = false
        if ok then S.savedPos = nil end
        done(ok, GetEntityCoords(PlayerPedId()))
    end)
end

function FxMapGen.envOff()
    if not S.env then log('ENV off already=1') return end
    S.env = false -- a tile or cam request on its way gives up
    S.ready = false
    S.busy = false
    S.hmapAbort = true
    S.scanAbort = true
    restoreWorld()
    local ped = PlayerPedId()
    SetEntityVisible(ped, true, false)
    local p = S.savedPos or GetEntityCoords(ped)
    landThen(p.x, p.y, p.z, function(ok, c)
        log('ENV off safe=%d x=%.1f y=%.1f z=%.1f', flag(ok), c.x, c.y, c.z)
        -- hunger and thirst went on falling while the health was held up: the server fills them (Qbox, QBCore)
        local r = FxMapGen.answer(FxMapGen.request('fxmapgen:refill'), 3000)
        if not r then
            log('REFILL ok=0 via=none error=noserver')
        elseif r.ok then
            log('REFILL ok=1 via=%s', r.via)
        else
            log('REFILL ok=0 via=%s error=%s', r.via, tostring(r.err))
        end
    end)
end

FxMapGen.commands.env = function(args)
    if args[2] == 'on' then
        FxMapGen.envOn()
        log('ENV on')
    elseif args[2] == 'off' then
        FxMapGen.envOff()
    else
        log('ERROR usage: fxmapgen env on|off')
    end
end

-- fxmapgen safe [x y [z]]: sets the ped down on the ground, frozen until the collision has loaded. Without a point: where
-- the ped stood before env on, or right under it. For a character left frozen by a stop or a failed landing.
FxMapGen.commands.safe = function(args)
    local x, y, z = tonumber(args[2]), tonumber(args[3]), tonumber(args[4])
    if (args[2] and not (x and y)) or (args[4] and not z) then log('ERROR usage: fxmapgen safe [x y [z]]') return end
    if S.env then log('ERROR safe: the capture environment is on (fxmapgen env off sets the character down)') return end
    if S.landing then log('ERROR busy: the character is being set down') return end
    if not x then
        local p = S.savedPos or GetEntityCoords(PlayerPedId())
        x, y, z = p.x, p.y, p.z
    end
    landThen(x, y, z, function(ok, c)
        log('SAFE ok=%d x=%.1f y=%.1f z=%.1f', flag(ok), c.x, c.y, c.z)
    end)
end

-- Stopped while capturing: no thread runs any more, so the ped goes back to where it stood, still frozen (the collision
-- there may not have loaded). Starting the resource again and "fxmapgen safe" sets it down.
AddEventHandler('onResourceStop', function(res)
    if res ~= GetCurrentResourceName() or not S.env then return end
    S.env = false
    restoreWorld()
    local ped = PlayerPedId()
    if S.savedPos then SetEntityCoordsNoOffset(ped, S.savedPos.x, S.savedPos.y, S.savedPos.z, false, false, false) end
    SetEntityVisible(ped, true, false)
    log('stopped during a capture: the character waits frozen where it stood; start the resource and run "fxmapgen safe"')
end)
