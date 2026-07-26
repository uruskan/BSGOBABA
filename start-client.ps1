# BSGO client -> local server
#
# Usage:  .\start-client.ps1 -ClientPath "C:\Path\To\BSGO\client\live"
#
# Session token + playerId are the ones the server auto-seeds on first run
# (see server\BSGO Server\BSGO Server\Database\Database.cs:40-46).
# Seeded accounts: 5085935 / 5085936 / 5085937 (token suffix fb7 / fb8 / fb9).
#
# +cdn must point at the client folder itself - the client loads its asset
# bundles from there instead of BigPoint's dead CDN.
# +version must match what the server replies in LoginProtocol.SendInit (rev 4578).

param(
    [Parameter(Mandatory = $true)][string]$ClientPath,
    [string]$GameServer = '127.0.0.1',
    [string]$PlayerId   = '5085935',
    # Pass your own. A session belongs to one account on one server, so there is no useful
    # default to hardcode -- and one baked in here would just be a credential in the source.
    [string]$Session    = '',
    [string]$Language   = 'en'
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $ClientPath 'bsgo.exe'
if (-not (Test-Path $exe)) { throw "bsgo.exe not found in $ClientPath" }

$cdn = $ClientPath.TrimEnd('\') + '/'

$clientArgs = @(
    '+projectID',  '547'
    '+userID',     $PlayerId
    '+sessionID',  'c7faac2379e35f6404eced5f484210ba'
    '+trackingID', '6cc3a6e78a753f29ccabaa0f79b7041b'
    '+gameServer', $GameServer
    '+cdn',        $cdn
    '+language',   $Language
    '+session',    $Session
    '+version',    '3b27980a3b7dd77e597872106ca98000'
)

Write-Host "Launching $exe -> ${GameServer}:27050 as player $PlayerId"
Start-Process -FilePath $exe -ArgumentList $clientArgs -WorkingDirectory $ClientPath
