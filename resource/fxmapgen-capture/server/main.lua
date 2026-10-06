-- fxmapgen-capture, server: clears the world before each capture, answers hello, and fills up hunger and thirst after
-- a capture. Every request is answered with the event fxmapgen:reply (token, table).
--
-- clearworld deletes EVERY vehicle and NPC on the server, the ones other players left out too. It is refused unless the
-- player has the permission (ace "command.fxmapgen": admins with "command" allowed have it already; others need
-- add_ace <principal> command.fxmapgen allow) and nobody else is on the server.
local ACE = 'command.fxmapgen'
local VERSION = GetResourceMetadata(GetCurrentResourceName(), 'version', 0) or '?'

local function reply(src, token, answer)
    TriggerClientEvent('fxmapgen:reply', src, token, answer)
end

RegisterNetEvent('fxmapgen:hello', function(token)
    local src = source
    reply(src, token, { version = VERSION, ace = IsPlayerAceAllowed(src, ACE), players = #GetPlayers() })
end)

RegisterNetEvent('fxmapgen:clearworld', function(token)
    local src = source
    local players = #GetPlayers()
    local why = nil
    if not IsPlayerAceAllowed(src, ACE) then
        why = 'permission'
    elseif players > 1 then
        why = 'players'
    end
    if why then
        print(('[fxmapgen] clearworld for player %s refused: %s (players on: %d)'):format(src, why, players))
        reply(src, token, { ok = false, why = why, players = players })
        return
    end
    local vehicles, peds = 0, 0
    for _, veh in ipairs(GetAllVehicles()) do
        if DoesEntityExist(veh) then
            DeleteEntity(veh)
            vehicles = vehicles + 1
        end
    end
    for _, ped in ipairs(GetAllPeds()) do
        if DoesEntityExist(ped) and not IsPedAPlayer(ped) then
            DeleteEntity(ped)
            peds = peds + 1
        end
    end
    reply(src, token, { ok = true, players = players, vehicles = vehicles, peds = peds })
end)

-- The client holds the health up during a capture while hunger and thirst go on falling: fill them up afterwards.
local function refillWith(src)
    if GetResourceState('qbx_core') == 'started' then
        return 'qbx_core', function()
            local player = exports.qbx_core:GetPlayer(src)
            player.Functions.SetMetaData('hunger', 100.0)
            player.Functions.SetMetaData('thirst', 100.0)
        end
    elseif GetResourceState('qb-core') == 'started' then
        return 'qb-core', function()
            local player = exports['qb-core']:GetCoreObject().Functions.GetPlayer(src)
            player.Functions.SetMetaData('hunger', 100)
            player.Functions.SetMetaData('thirst', 100)
        end
    end
    return 'none', nil
end

RegisterNetEvent('fxmapgen:refill', function(token)
    local src = source
    if not IsPlayerAceAllowed(src, ACE) then
        reply(src, token, { ok = false, via = 'none', err = 'permission' })
        return
    end
    local via, fill = refillWith(src)
    local ok, err = true, nil
    if fill then ok, err = pcall(fill) end
    reply(src, token, { ok = ok, via = via, err = err ~= nil and tostring(err) or nil })
end)
