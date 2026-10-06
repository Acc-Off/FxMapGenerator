-- fxmapgen-capture, client: what is sampled over the block in place. hmap = the height grid of the visible surface.
--
--   fxmapgen hmap [step]     after READY of a tile; step in metres (default 1)
--   HMAP BEGIN seq=<n> z= tx= ty= x0= y0= size= step= n=<samples per side> block=<name>
--   HMAP j=<row> k=<part> v v v ...     rows j = 0..n-1 from the north edge y0 southwards, samples i = 0..n-1 from the west
--                                       edge x0 eastwards; each row in parts of 100; v = height (0.1 m), x = no hit
--   HMAP END nohit=<n> ms=<n>           | HMAP ABORT when env off came in between (the grid is incomplete)
local S = FxMapGen.state
local log = FxMapGen.log

local PER_LINE = 100

FxMapGen.commands.hmap = function(args)
    local step = tonumber(args[2] or '1')
    if not step or step <= 0 then log('ERROR usage: fxmapgen hmap [step]') return end
    local b = S.block
    if not (b and S.ready) then log('ERROR hmap: no block in place (fxmapgen tile first)') return end
    if S.hmapBusy then log('ERROR busy: a height grid is being sent') return end
    if S.scanBusy then log('ERROR busy: a scan is running') return end
    S.hmapBusy, S.hmapAbort = true, false
    local seq = S.seq
    CreateThread(function()
        local t0 = GetGameTimer()
        local n = math.floor(b.size / step + 0.5) -- cells per side; samples per side = n + 1
        -- the collision is streamed around the ped: ask for it over the whole block first
        for i = 0, 2 do
            for j = 0, 2 do
                RequestCollisionAtCoord(b.x0 + (i + 0.5) * b.size / 3, b.y0 - (j + 0.5) * b.size / 3, S.last.gz)
            end
        end
        Wait(300)
        log('HMAP BEGIN seq=%d z=%d tx=%d ty=%d x0=%.4f y0=%.4f size=%.4f step=%.3f n=%d block=%s',
            seq, b.z, b.tx, b.ty, b.x0, b.y0, b.size, step, n + 1, b.name)
        local nohit = 0
        for j = 0, n do
            if S.hmapAbort or S.seq ~= seq then
                S.hmapBusy = false
                log('HMAP ABORT')
                return
            end
            local y = b.y0 - j * step
            local vals, part = {}, 0
            for i = 0, n do
                local x = b.x0 + i * step
                local ok, z = GetGroundZFor_3dCoord(x, y, 1200.0, false)
                -- over water the probe gives the bottom; the visible surface is the water
                local wok, wz = GetWaterHeightNoWaves(x, y, 0.0)
                if wok and (not ok or wz > z) then ok, z = true, wz end
                if ok then
                    vals[#vals + 1] = ('%.1f'):format(z)
                else
                    vals[#vals + 1] = 'x'
                    nohit = nohit + 1
                end
                if #vals == PER_LINE or i == n then
                    log('HMAP j=%d k=%d %s', j, part, table.concat(vals, ' '))
                    part = part + 1
                    vals = {}
                end
            end
            if j % 8 == 7 then Wait(0) end
        end
        S.hmapBusy = false
        log('HMAP END nohit=%d ms=%d', nohit, GetGameTimer() - t0)
    end)
end
