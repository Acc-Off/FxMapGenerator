fx_version 'cerulean'
game 'gta5'
lua54 'yes'

name 'fxmapgen-capture'
author 'Acc-Off'
description 'FxMapGenerator: the in-game side of the map capture (top-down shots, height grids and scans), driven by the FxMapGenerator executable'
version '0.1.0'
repository 'https://github.com/Acc-Off/FxMapGenerator'

client_scripts {
    'client/link.lua',
    'client/env.lua',
    'client/camera.lua',
    'client/sample.lua',
    'client/scan.lua',
}

server_script 'server/main.lua'
