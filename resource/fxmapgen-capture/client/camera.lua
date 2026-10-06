-- fxmapgen-capture, client: the camera straight down over a block (tile) or a point (cam), and the READY / FAIL lines.
--
--   fxmapgen tile <z> <tx> <ty> <fov> [margin] [quietMs]
--     block = the 4 x 4 tiles of zoom z from tile (tx, ty) (z8: 281.25 m); the camera looks straight down on its centre from
--     gz + h, h = margin * (size / 2) / tan(fov / 2) (fov is vertical, in degrees). margin > 1 widens the frame around the
--     block (the orthorectification needs it where roofs and slopes lean out of the block). quietMs (0..10000, default
--     1500): how long the game's streaming requests have to stay at 0 before READY. FxMapGenerator measures what a PC
--     needs in its pre-check (0 = READY as soon as they reach 0, then it watches the frames) and sends that.
--   fxmapgen cam <x> <y> <h> <fov>
--
--   READY seq=<n> x= y= gz= h= fov= margin= scene= coll= settle= quiet= fps= maxreq= water= veh= peds= block=<name | none>
--     gz = ground (or water surface, 0 at sea) under the centre; the camera is at gz + h. scene / coll / settle = ms spent
--     loading the scene, waiting for the collision under the centre (none over water) and settling; quiet = the quietMs
--     the settling waited for; fps = frames drawn per second while settling; maxreq = most streaming requests seen while
--     settling; water = points of a 5 x 5 grid over the block that have water (25 = open water); veh / peds = vehicles
--     and NPCs the server deleted.
--   FAIL seq=<n> reason=superseded                 a newer request or env off took over
--   FAIL seq=<n> reason=timeout scene= coll= settle=
--   FAIL seq=<n> reason=refused why=permission|players players=<n>    the server would not clear the world
--   FAIL seq=<n> reason=noserver                   the server side did not answer
local S = FxMapGen.state
local log = FxMapGen.log

local WORLD_LEFT, WORLD_TOP = -4140.0, 8400.0
-- metres per tile by zoom; a block is 4 x 4 tiles
local TILE_M = { [6] = 281.25, [7] = 140.625, [8] = 70.3125, [9] = 35.15625, [10] = 17.578125, [11] = 8.7890625 }
-- static collision only streams within a few hundred metres (3D) of the ped, so the ground probe fails when the ped hovers
-- too far above or below the surface: the ped hovers at these heights in turn until the probe hits
local HOVER = { 300.0, 600.0, 900.0 }
local SCENE_MAX = 20000
-- open water never reports the scene loaded: the wait over a block of water only is cut short
local SCENE_MAX_WATER = 2500
local COLL_MAX, SETTLE_MAX, CLEAR_MAX = 10000, 15000, 3000
-- settling: the streaming requests stay at 0 for quietMs, and at least MIN_FRAMES frames are drawn
local QUIET_DEFAULT, QUIET_MAX, MIN_FRAMES = 1500, 10000, 10

-- Visible surface at (x, y): the water surface first (over deep sea the ground probe gives the bottom or nothing), then
-- the ground. nil while the collision there has not loaded.
function FxMapGen.groundZ(x, y)
    local wok, wz = GetWaterHeightNoWaves(x, y, 0.0)
    if wok then return wz end
    local ok, z = GetGroundZFor_3dCoord(x, y, 1200.0, false)
    if ok and z > -100.0 then return z end
    return nil
end

-- Moves the frozen ped over (x, y) at the hover heights in turn and returns the surface there and the height that found
-- it (nil when none did, or when alive() turned false).
function FxMapGen.surfaceAt(ped, x, y, alive)
    for _, hz in ipairs(HOVER) do
        SetEntityCoordsNoOffset(ped, x, y, hz, false, false, false)
        SetFocusPosAndVel(x, y, hz, 0.0, 0.0, 0.0)
        RequestCollisionAtCoord(x, y, hz)
        local t0 = GetGameTimer()
        while GetGameTimer() - t0 < 2500 do
            local gz = FxMapGen.groundZ(x, y)
            if gz then return gz, hz end
            Wait(100)
            if alive and not alive() then return nil end
        end
        log('ground probe failed with the ped at z=%.0f', hz)
    end
    return nil
end

function FxMapGen.destroyCam()
    if S.cam then
        RenderScriptCams(false, false, 0, true, true)
        DestroyCam(S.cam, false)
        S.cam = nil
    end
end

-- Points of a 5 x 5 grid over the square (side metres around cx, cy) that have water. The game answers from its water
-- data, which is always loaded, so this needs no streaming.
local function waterPoints(cx, cy, side)
    local n = 0
    for i = -2, 2 do
        for j = -2, 2 do
            if GetWaterHeightNoWaves(cx + i * 0.2 * side, cy - j * 0.2 * side, 0.0) then n = n + 1 end
        end
    end
    return n
end

-- r = { x, y, h, fov, margin, quiet, water, block }
local function goTo(r)
    if not S.env then FxMapGen.envOn() end
    S.seq = S.seq + 1
    local mySeq = S.seq
    S.ready = false
    S.busy = true
    local function alive() return S.seq == mySeq and S.env end
    local ped = PlayerPedId()
    local x, y, h, fov = r.x, r.y, r.h, r.fov

    -- the server deletes every vehicle and NPC; its answer is awaited before the scene settles (below)
    local clearToken = FxMapGen.request('fxmapgen:clearworld')
    local function giveUp()
        FxMapGen.forget(clearToken)
        log('FAIL seq=%d reason=superseded', mySeq)
    end
    ClearAreaOfVehicles(x, y, 100.0, 600.0, false, false, false, false, false)
    ClearAreaOfPeds(x, y, 100.0, 600.0, 1)

    -- stream around the target point while the ped hovers frozen at a safe height
    SetEntityCoordsNoOffset(ped, x, y, HOVER[1], false, false, false)
    SetFocusPosAndVel(x, y, 100.0, 0.0, 0.0, 0.0)
    NewLoadSceneStop()
    NewLoadSceneStartSphere(x, y, 100.0, 400.0, 0)
    local sceneMax = (r.water == 25) and SCENE_MAX_WATER or SCENE_MAX
    local t0 = GetGameTimer()
    while not IsNewLoadSceneLoaded() and GetGameTimer() - t0 < sceneMax do
        Wait(50)
        if not alive() then return giveUp() end
    end
    local gz, hz = FxMapGen.surfaceAt(ped, x, y, alive)
    if not alive() then return giveUp() end
    if hz and hz ~= HOVER[1] then log('ground found with the ped hovering at z=%.0f: gz=%.1f', hz, gz) end
    local sceneMs = GetGameTimer() - t0
    gz = gz or 0.0
    if gz < 0.0 then gz = 0.0 end -- sea: the water surface is the reference
    SetEntityCoordsNoOffset(ped, x, y, gz + 1.0, false, false, false)
    SetFocusPosAndVel(x, y, gz, 0.0, 0.0, 0.0)

    if not S.cam then
        S.cam = CreateCam('DEFAULT_SCRIPTED_CAMERA', true)
        SetCamUseShallowDofMode(S.cam, false)
    end
    SetCamCoord(S.cam, x, y, gz + h)
    SetCamRot(S.cam, -90.0, 0.0, 0.0, 2)
    SetCamFov(S.cam, fov)
    SetCamFarClip(S.cam, 10000.0)
    SetCamNearClip(S.cam, 1.0)
    RenderScriptCams(true, false, 0, true, true)

    -- the collision under the centre: on the ground a ray down has to meet it; over water none is needed (the frozen ped
    -- floats at the surface). The game's "collision loaded around the entity" is not asked: over the sea off a coast it
    -- stayed false with everything loaded (a block of sea a few hundred metres off Vespucci Beach).
    local t1 = GetGameTimer()
    if not GetWaterHeightNoWaves(x, y, 0.0) then
        while not FxMapGen.solidBelow(x, y, gz + 2.0, ped) and GetGameTimer() - t1 < COLL_MAX do
            RequestCollisionAtCoord(x, y, gz)
            Wait(50)
            if not alive() then return giveUp() end
        end
    end
    local collMs = GetGameTimer() - t1

    local clear = FxMapGen.answer(clearToken, CLEAR_MAX, alive)
    if not alive() then return giveUp() end
    if not clear then
        S.busy = false
        log('FAIL seq=%d reason=noserver', mySeq)
        return
    end
    if not clear.ok then
        S.busy = false
        log('FAIL seq=%d reason=refused why=%s players=%d', mySeq, clear.why, clear.players)
        return
    end

    -- settle: no streaming requests for r.quiet ms in a row, and at least MIN_FRAMES frames (15 s at most)
    local t2 = GetGameTimer()
    local frames, maxReq, quietFrom, settled = 0, 0, nil, false
    while true do
        local now = GetGameTimer()
        local n = GetNumberOfStreamingRequests()
        if n > maxReq then maxReq = n end
        if n > 0 then quietFrom = nil elseif not quietFrom then quietFrom = now end
        if frames >= MIN_FRAMES and quietFrom and now - quietFrom >= r.quiet then settled = true break end
        if now - t2 >= SETTLE_MAX then break end
        frames = frames + 1
        Wait(0)
        if not alive() then return giveUp() end
    end
    local settleMs = GetGameTimer() - t2
    local fps = settleMs > 0 and frames * 1000.0 / settleMs or 0.0

    S.busy = false
    if sceneMs >= SCENE_MAX or collMs >= COLL_MAX or not settled then
        log('FAIL seq=%d reason=timeout scene=%d coll=%d settle=%d', mySeq, sceneMs, collMs, settleMs)
        return
    end
    S.last = { x = x, y = y, gz = gz, h = h, fov = fov, margin = r.margin }
    S.ready = true
    log('READY seq=%d x=%.4f y=%.4f gz=%.3f h=%.3f fov=%.3f margin=%.3f scene=%d coll=%d settle=%d quiet=%d fps=%.1f maxreq=%d water=%d veh=%d peds=%d block=%s',
        mySeq, x, y, gz, h, fov, r.margin, sceneMs, collMs, settleMs, r.quiet, fps, maxReq, r.water, clear.vehicles,
        clear.peds, r.block and r.block.name or 'none')
end

local function blockAt(z, tx, ty)
    local tm = TILE_M[z]
    if not tm then return nil end
    local size = tm * 4
    local bx, by = tx // 4, ty // 4
    local x0, y0 = WORLD_LEFT + bx * size, WORLD_TOP - by * size
    return { name = ('z%d_%d_%d'):format(z, bx * 4, by * 4), z = z, tx = bx * 4, ty = by * 4, x0 = x0, y0 = y0,
             size = size, cx = x0 + size / 2, cy = y0 - size / 2 }
end

local function busyWithHmap()
    if S.hmapBusy then log('ERROR busy: a height grid is being sent') end
    if S.scanBusy and not S.hmapBusy then log('ERROR busy: a scan is running') end
    return S.hmapBusy or S.scanBusy
end

FxMapGen.commands.tile = function(args)
    local z, tx, ty = math.tointeger(tonumber(args[2])), math.tointeger(tonumber(args[3])), math.tointeger(tonumber(args[4]))
    local fov, margin = tonumber(args[5]), tonumber(args[6] or '1')
    local quiet = math.tointeger(tonumber(args[7] or QUIET_DEFAULT))
    local b = z and tx and ty and blockAt(z, tx, ty)
    if not (b and fov and margin and quiet) or fov <= 0 or fov >= 180 or margin <= 0 or quiet < 0 or quiet > QUIET_MAX then
        log('ERROR usage: fxmapgen tile <z 6..11> <tx> <ty> <fov> [margin] [quietMs 0..10000]')
        return
    end
    if busyWithHmap() then return end
    S.block = b
    local h = margin * (b.size / 2) / math.tan(math.rad(fov) / 2)
    CreateThread(function()
        goTo({ x = b.cx, y = b.cy, h = h, fov = fov + 0.0, margin = margin + 0.0, quiet = quiet,
               water = waterPoints(b.cx, b.cy, b.size), block = b })
    end)
end

FxMapGen.commands.cam = function(args)
    local x, y, h, fov = tonumber(args[2]), tonumber(args[3]), tonumber(args[4]), tonumber(args[5])
    if not (x and y and h and fov) or h <= 0 or fov <= 0 or fov >= 180 then
        log('ERROR usage: fxmapgen cam <x> <y> <h> <fov>')
        return
    end
    if busyWithHmap() then return end
    S.block = nil
    -- water is counted over the ground the frame's height covers
    local side = 2 * h * math.tan(math.rad(fov) / 2)
    CreateThread(function()
        goTo({ x = x + 0.0, y = y + 0.0, h = h + 0.0, fov = fov + 0.0, margin = 1.0, quiet = QUIET_DEFAULT,
               water = waterPoints(x, y, side) })
    end)
end
