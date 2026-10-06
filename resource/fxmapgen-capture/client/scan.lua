-- fxmapgen-capture, client: the scans of a block (what the atlas and the road map are made from). The ped moves itself;
-- no camera and no tile request are needed first.
--
--   fxmapgen scan ground <z> <tx> <ty> [canopy]
--     the block's 2 x 2 sections in turn: the frozen ped streams the scene and the collision at the section's centre
--     (a ray down has to meet the collision, then the game has to report the collision around the ped loaded, then the
--     streaming requests have to stay at 0 for settle ms: the buildings' collision can come later than the ground's, and
--     right after a jump across the map even after the game has said the collision around was loaded), then rays
--     straight down every metre give the material and height of the top collision (flags 1), the height of the
--     water collision of rivers, pools and fountains (flags 128) and, with canopy, of the tree canopy (flags 256); the
--     game's water surface (no waves) is read at the same points
--   fxmapgen scan roads <z> <tx> <ty>
--     from the block's centre: whether each point is on a road (every metre), the street name and the zone (every 4 m)
--   fxmapgen scan stop          the scan on its way ends with MSCAN ABORT (env off too)
--
-- Lines (format v=1; the program reads rows by kind):
--   MSCAN BEGIN v=1 kind=mat seq= block= z= tx= ty= x0= y0= size= step= n= flags= fol= chunks= pflags= [pflags2=] settle= zmin=
--   MSCAN dict mat <index> <hash>                 each material the first time it is hit (index from 1; 0 = no hit)
--   MSCAN <row kind> j=<row> k=<part> v v ...     rows j = 0..n-1 from the north edge y0, points i = 0..n-1 from the west
--                                                 edge x0, every step m; run-length coded ("v*count"), parts of at most
--                                                 900 characters of tokens (the game cuts a printed line at 1,023):
--     mat (index), hz (hit height, 0.1 m; x = no hit), fol (flags 128), fol2 (flags 256, with canopy), water (. = none)
--   MSCAN chunk <ci>,<cj> surf=<surface | x> wait=<ms> coll=<1 | 0 | -> quiet=<1 | 0 | -> req=<n>   a section's streaming
--                                                 stop (coll: the collision around the ped reported loaded / not within
--                                                 3 s / not asked: open water; quiet: no streaming requests for settle ms /
--                                                 not within 5 s / not waited: open water; req: the requests as the rays start)
--   MSCAN END kind=mat seq= n= cells= nohit= water= fol= fol2= mats= scan_ms= emit_ms= ms=
--   MSCAN BEGIN v=1 kind=road seq= block= z= tx= ty= x0= y0= size= step=4.000 n= pstep=1.000 pn=
--   MSCAN dict street <index> <hash> <name> | MSCAN dict zone <index> <code> <label>
--   MSCAN street / zone j= k= ...                 the step grid (index; street 1.. in dict order)
--   MSCAN onroad j= k= ...                        the pstep grid, 0 / 1
--   MSCAN END kind=road seq= n= streets= zones= onroad= pn= wait= scan_ms= ms=
--   MSCAN DONE seq= kind=ground|roads block= ms=  | MSCAN ABORT seq= kind= block=   (then no END or DONE follows)
local S = FxMapGen.state
local log = FxMapGen.log

local WORLD_LEFT, WORLD_TOP, Z8_BLOCK = -4140.0, 8400.0, 281.25
-- the z8 tiles a project's map frame can reach: the standard frame's 0..127 x 0..191 with up to 4 cells of 32 tiles to the
-- left and right and 2 above and below (the numbers go negative west and north of the standard frame)
local TX_MIN, TX_MAX, TY_MIN, TY_MAX = -128, 255, -64, 255
local STEP, CHUNKS, YIELD_ROWS, SETTLE_MS, ZMIN = 1.0, 2, 32, 300, -500.0
local ROAD_STEP, ONROAD_STEP = 4.0, 1.0
local FLAGS, WATER_FLAGS, CANOPY_FLAGS = 1, 128, 256
-- the tokens of a row line: the game cuts a printed line at 1,023 characters (with "[fxmapgen] MSCAN hz j=281 k=1 " in
-- front); 120 tokens of a sloping sea bottom ("-257.2*2 ") came to 1,080
local PER_LINE_CHARS = 900
local SCENE_MAX, SCENE_MAX_WATER, COLL_MAX, QUIET_MAX = 8000, 2500, 3000, 5000

local function mscan(fmt, ...) log('MSCAN ' .. fmt, ...) end

local function aborted(seq) return S.scanAbort or S.scanSeq ~= seq end

-- a row, run-length coded ("v*count" or "v"), in parts of at most PER_LINE_CHARS characters of tokens
local function emitRle(kind, j, vals)
    local out, len, cur, cnt, part = {}, 0, nil, 0, 0
    local function flush()
        mscan('%s j=%d k=%d %s', kind, j, part, table.concat(out, ' '))
        part, out, len = part + 1, {}, 0
    end
    local function push(tok)
        if #out > 0 and len + 1 + #tok > PER_LINE_CHARS then flush() end
        len = len + (#out > 0 and 1 or 0) + #tok
        out[#out + 1] = tok
    end
    for i = 1, #vals do
        local v = vals[i]
        if v == cur then cnt = cnt + 1
        else
            if cur ~= nil then push(cnt > 1 and (cur .. '*' .. cnt) or cur) end
            cur, cnt = v, 1
        end
    end
    if cur ~= nil then push(cnt > 1 and (cur .. '*' .. cnt) or cur) end
    if #out > 0 or part == 0 then flush() end
end

-- points of a 5 x 5 grid over the block that have water (25 = open water: the scene never reports loaded there)
local function waterPoints(b)
    local n = 0
    for i = -2, 2 do
        for j = -2, 2 do
            if GetWaterHeightNoWaves(b.cx + i * 0.2 * b.size, b.cy - j * 0.2 * b.size, 0.0) then n = n + 1 end
        end
    end
    return n
end

-- Parks the frozen ped over (x, y) and streams the scene and the collision there: the scene around the point, the
-- surface found with the ped hovering (water first), then a ray down has to meet the collision (not over water), then
-- the game has to report the collision around the ped loaded (the buildings' collision can come after the ground's,
-- so the ray alone can miss roofs), then the streaming requests have to stay at 0 for SETTLE_MS in a row (QUIET_MAX at
-- most; over open water a plain SETTLE_MS): right after a jump across the map, whole houses (at Paleto Bay, for one)
-- can come after the game has said the collision around is loaded.
-- Returns the surface (nil when none was found), the ms it took, whether the collision around was reported loaded
-- (true / false after COLL_MAX / nil when not asked) and whether the requests went quiet (true / false after QUIET_MAX /
-- nil when not waited); nil, nil when the scan was stopped.
local function streamAt(ped, x, y, openWater, seq)
    local t0 = GetGameTimer()
    local coll, quiet = nil, nil
    SetEntityCoordsNoOffset(ped, x, y, 300.0, false, false, false)
    SetFocusPosAndVel(x, y, 100.0, 0.0, 0.0, 0.0)
    NewLoadSceneStop()
    NewLoadSceneStartSphere(x, y, 100.0, 400.0, 0)
    local sceneMax = openWater and SCENE_MAX_WATER or SCENE_MAX
    while not IsNewLoadSceneLoaded() and GetGameTimer() - t0 < sceneMax do
        Wait(50)
        if aborted(seq) then NewLoadSceneStop() return nil, nil end
    end
    local gz = FxMapGen.surfaceAt(ped, x, y, function() return not aborted(seq) end)
    if aborted(seq) then NewLoadSceneStop() return nil, nil end
    if gz then
        SetEntityCoordsNoOffset(ped, x, y, gz + 1.0, false, false, false)
        SetFocusPosAndVel(x, y, gz, 0.0, 0.0, 0.0)
        if not GetWaterHeightNoWaves(x, y, 0.0) then
            local t1 = GetGameTimer()
            while not FxMapGen.solidBelow(x, y, gz + 2.0, ped) and GetGameTimer() - t1 < COLL_MAX do
                RequestCollisionAtCoord(x, y, gz)
                Wait(50)
                if aborted(seq) then NewLoadSceneStop() return nil, nil end
            end
        end
        if not openWater then
            RequestCollisionAtCoord(x, y, gz - 10.0)
            local t2 = GetGameTimer()
            coll = HasCollisionLoadedAroundEntity(ped)
            while not coll and GetGameTimer() - t2 < COLL_MAX do
                Wait(50)
                if aborted(seq) then NewLoadSceneStop() return nil, nil end
                coll = HasCollisionLoadedAroundEntity(ped)
            end
            local t3, quietFrom = GetGameTimer(), nil
            quiet = false
            while GetGameTimer() - t3 < QUIET_MAX do
                local now = GetGameTimer()
                if GetNumberOfStreamingRequests() > 0 then quietFrom = nil elseif not quietFrom then quietFrom = now end
                if quietFrom and now - quietFrom >= SETTLE_MS then quiet = true break end
                Wait(0)
                if aborted(seq) then NewLoadSceneStop() return nil, nil end
            end
        else
            Wait(SETTLE_MS)
        end
    end
    NewLoadSceneStop()
    return gz, GetGameTimer() - t0, coll, quiet
end

local function probe(x, y, flags, ped)
    return StartExpensiveSynchronousShapeTestLosProbe(x, y, 1200.0, x, y, ZMIN, flags, ped, 4)
end

-- returns false when the scan was stopped
local function scanGround(b, seq, canopy)
    local ped = PlayerPedId()
    local t0 = GetGameTimer()
    local n = math.floor(b.size / STEP + 0.5)
    local N = n + 1
    mscan('BEGIN v=1 kind=mat seq=%d block=%s z=%d tx=%d ty=%d x0=%.4f y0=%.4f size=%.4f step=%.3f n=%d flags=%d fol=1 chunks=%d pflags=%d%s settle=%d zmin=%.0f',
        seq, b.name, b.z, b.tx, b.ty, b.x0, b.y0, b.size, STEP, N, FLAGS, CHUNKS, WATER_FLAGS,
        canopy and (' pflags2=%d'):format(CANOPY_FLAGS) or '', SETTLE_MS, ZMIN)
    local mat, hz, fz, cz, wz = {}, {}, {}, {}, {}      -- flat arrays, key = j * N + i + 1
    local dict, dictN = {}, 0
    local nohit, nwater, nfol, ncanopy, scanMs = 0, 0, 0, 0, 0
    local openWater = waterPoints(b) == 25
    local CH = b.size / CHUNKS
    for cj = 0, CHUNKS - 1 do
        for ci = 0, CHUNKS - 1 do
            local mx, my = b.x0 + (ci + 0.5) * CH, b.y0 - (cj + 0.5) * CH
            local sz, waitMs, coll, quiet = streamAt(ped, mx, my, openWater, seq)
            if not waitMs then return false end
            local req = GetNumberOfStreamingRequests()
            -- the sample range of this section (the last one takes the trailing sample n)
            local i0 = math.floor(ci * n / CHUNKS + 0.5)
            local i1 = (ci == CHUNKS - 1) and n or (math.floor((ci + 1) * n / CHUNKS + 0.5) - 1)
            local j0 = math.floor(cj * n / CHUNKS + 0.5)
            local j1 = (cj == CHUNKS - 1) and n or (math.floor((cj + 1) * n / CHUNKS + 0.5) - 1)
            local ts = GetGameTimer()
            for j = j0, j1 do
                if aborted(seq) then return false end
                local y = b.y0 - j * STEP
                local base = j * N
                for i = i0, i1 do
                    local x = b.x0 + i * STEP
                    local k = base + i + 1
                    local _, hit, at, _, mh = GetShapeTestResultIncludingMaterial(probe(x, y, FLAGS, ped))
                    if hit == 1 or hit == true then
                        local idx = dict[mh]
                        if not idx then dictN = dictN + 1; idx = dictN; dict[mh] = idx; mscan('dict mat %d %d', idx, mh) end
                        mat[k] = idx
                        hz[k] = ('%.1f'):format(at.z)
                    else
                        mat[k] = 0; hz[k] = 'x'; nohit = nohit + 1
                    end
                    local _, h2, a2 = GetShapeTestResult(probe(x, y, WATER_FLAGS, ped))
                    if h2 == 1 or h2 == true then fz[k] = ('%.1f'):format(a2.z); nfol = nfol + 1 else fz[k] = 'x' end
                    if canopy then
                        local _, h3, a3 = GetShapeTestResult(probe(x, y, CANOPY_FLAGS, ped))
                        if h3 == 1 or h3 == true then cz[k] = ('%.1f'):format(a3.z); ncanopy = ncanopy + 1 else cz[k] = 'x' end
                    end
                    local wok, w = GetWaterHeightNoWaves(x, y, 0.0)
                    if wok then wz[k] = ('%.1f'):format(w); nwater = nwater + 1 else wz[k] = '.' end
                end
                if (j - j0) % YIELD_ROWS == YIELD_ROWS - 1 then Wait(0) end
            end
            Wait(0) -- the game timer only moves per frame: yield, then read the section's scan time
            scanMs = scanMs + (GetGameTimer() - ts)
            local function flag(v) return v == nil and '-' or (v and '1' or '0') end
            mscan('chunk %d,%d surf=%s wait=%d coll=%s quiet=%s req=%d', ci, cj, sz and ('%.1f'):format(sz) or 'x', waitMs,
                flag(coll), flag(quiet), req)
        end
    end
    local te = GetGameTimer()
    for j = 0, n do
        if aborted(seq) then return false end
        local base = j * N
        local rm, rz, rf, rc, rw = {}, {}, {}, {}, {}
        for i = 0, n do
            local k = base + i + 1
            rm[i + 1] = tostring(mat[k] or 0); rz[i + 1] = hz[k] or 'x'; rw[i + 1] = wz[k] or '.'; rf[i + 1] = fz[k] or 'x'
            if canopy then rc[i + 1] = cz[k] or 'x' end
        end
        emitRle('mat', j, rm); emitRle('hz', j, rz); emitRle('water', j, rw); emitRle('fol', j, rf)
        if canopy then emitRle('fol2', j, rc) end
        if j % 16 == 15 then Wait(0) end
    end
    Wait(0)
    mscan('END kind=mat seq=%d n=%d cells=%d nohit=%d water=%d fol=%d fol2=%d mats=%d scan_ms=%d emit_ms=%d ms=%d',
        seq, N, N * N, nohit, nwater, nfol, ncanopy, dictN, scanMs, GetGameTimer() - te, GetGameTimer() - t0)
    return true
end

local function scanRoads(b, seq)
    local ped = PlayerPedId()
    local t0 = GetGameTimer()
    local n, pn = math.floor(b.size / ROAD_STEP + 0.5), math.floor(b.size / ONROAD_STEP + 0.5)
    mscan('BEGIN v=1 kind=road seq=%d block=%s z=%d tx=%d ty=%d x0=%.4f y0=%.4f size=%.4f step=%.3f n=%d pstep=%.3f pn=%d',
        seq, b.name, b.z, b.tx, b.ty, b.x0, b.y0, b.size, ROAD_STEP, n + 1, ONROAD_STEP, pn + 1)
    local sz, waitMs = streamAt(ped, b.cx, b.cy, waterPoints(b) == 25, seq)
    if not waitMs then return false end
    local fallback = sz or 0.0
    local ts = GetGameTimer()
    local function groundOr(x, y)
        local ok, z = GetGroundZFor_3dCoord(x, y, 1200.0, false)
        if ok then return z end
        local wok, w = GetWaterHeightNoWaves(x, y, 0.0)
        if wok then return w end
        return fallback
    end
    local streets, nStreets, zones, nZones = {}, 0, {}, 0
    for j = 0, n do
        if aborted(seq) then return false end
        local y = b.y0 - j * ROAD_STEP
        local rs, rz = {}, {}
        for i = 0, n do
            local x = b.x0 + i * ROAD_STEP
            local z = groundOr(x, y)
            local sh = GetStreetNameAtCoord(x, y, z)
            local si = streets[sh]
            if not si then
                nStreets = nStreets + 1; si = nStreets; streets[sh] = si
                mscan('dict street %d %d %s', si, sh, tostring(GetStreetNameFromHashKey(sh)))
            end
            rs[i + 1] = tostring(si)
            local code = tostring(GetNameOfZone(x, y, z))
            local zi = zones[code]
            if not zi then
                nZones = nZones + 1; zi = nZones; zones[code] = zi
                mscan('dict zone %d %s %s', zi, code, tostring(GetLabelText(code)))
            end
            rz[i + 1] = tostring(zi)
        end
        emitRle('street', j, rs); emitRle('zone', j, rz)
        if j % 4 == 3 then Wait(0) end
    end
    local nOn = 0
    for j = 0, pn do
        if aborted(seq) then return false end
        local y = b.y0 - j * ONROAD_STEP
        local r = {}
        for i = 0, pn do
            local x = b.x0 + i * ONROAD_STEP
            local on = IsPointOnRoad(x, y, groundOr(x, y), 0)
            if on then nOn = nOn + 1 end
            r[i + 1] = on and '1' or '0'
        end
        emitRle('onroad', j, r)
        if j % 8 == 7 then Wait(0) end
    end
    Wait(0)
    mscan('END kind=road seq=%d n=%d streets=%d zones=%d onroad=%d pn=%d wait=%d scan_ms=%d ms=%d',
        seq, n + 1, nStreets, nZones, nOn, pn + 1, waitMs, GetGameTimer() - ts, GetGameTimer() - t0)
    return true
end

local function z8Block(tx, ty)
    local bx, by = tx // 4, ty // 4
    local x0, y0 = WORLD_LEFT + bx * Z8_BLOCK, WORLD_TOP - by * Z8_BLOCK
    return { name = ('z8_%d_%d'):format(bx * 4, by * 4), z = 8, tx = bx * 4, ty = by * 4, x0 = x0, y0 = y0,
             size = Z8_BLOCK, cx = x0 + Z8_BLOCK / 2, cy = y0 - Z8_BLOCK / 2 }
end

local USAGE = 'ERROR usage: fxmapgen scan ground|roads 8 <tx> <ty> [canopy] | scan stop'

FxMapGen.commands.scan = function(args)
    local kind = args[2]
    if kind == 'stop' then
        if S.scanBusy then S.scanAbort = true end
        return
    end
    local z, tx, ty = math.tointeger(tonumber(args[3])), math.tointeger(tonumber(args[4])), math.tointeger(tonumber(args[5]))
    local canopy = args[6] == 'canopy'
    if not (kind == 'ground' or kind == 'roads') or z ~= 8 or not tx or not ty or tx < TX_MIN or ty < TY_MIN or tx > TX_MAX or ty > TY_MAX
        or (args[6] ~= nil and not (kind == 'ground' and canopy)) or args[7] ~= nil then
        log(USAGE)
        return
    end
    if S.scanBusy then log('ERROR busy: a scan is running') return end
    if S.busy then log('ERROR busy: a tile request is on its way') return end
    if S.hmapBusy then log('ERROR busy: a height grid is being sent') return end
    local b = z8Block(tx, ty)
    S.scanSeq = S.scanSeq + 1
    local seq = S.scanSeq
    S.scanBusy, S.scanAbort = true, false
    CreateThread(function()
        if not S.env then FxMapGen.envOn() end
        local t0 = GetGameTimer()
        local done
        if kind == 'ground' then done = scanGround(b, seq, canopy) else done = scanRoads(b, seq) end
        S.scanBusy = false
        if done then
            mscan('DONE seq=%d kind=%s block=%s ms=%d', seq, kind, b.name, GetGameTimer() - t0)
        else
            mscan('ABORT seq=%d kind=%s block=%s', seq, kind, b.name)
        end
    end)
end
