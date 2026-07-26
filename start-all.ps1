# Starts everything in the right order: MongoDB -> game server -> bot GUI.
# Then click "Launch Game" inside the bot window.
#
# Order matters: the client connects the moment it launches, so the proxy must
# already be listening on 27050. The bot auto-starts its proxy on open.
#
# ASCII only. Windows PowerShell 5.1 reads this file as ANSI, so non-ASCII
# characters (em-dashes, arrows) corrupt and break the parser.

$ErrorActionPreference = 'Stop'

$serverDir = "C:\Users\uruskan\Desktop\BSGOBABA\server\BSGO Server\BSGO Server\bin\Debug\netcoreapp3.0"
$serverDll = Join-Path $serverDir "BSGO Server.dll"
$serverSln = "C:\Users\uruskan\Desktop\BSGOBABA\server\BSGO Server\BSGO Server.sln"
$botProj   = "C:\Users\uruskan\Desktop\BSGOBABA\bot\BsgoBot\BsgoBot.csproj"
$botExe    = "C:\Users\uruskan\Desktop\BSGOBABA\bot\BsgoBot\bin\Debug\net9.0-windows\bsgobot.exe"

function Test-Port([int]$Port) {
    [bool](Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
}

# --- 1. MongoDB ---------------------------------------------------------------
$svc = Get-Service -Name MongoDB -ErrorAction SilentlyContinue
if ($null -eq $svc) { throw "MongoDB service not found. Install: winget install --id MongoDB.Server" }
if ($svc.Status -ne 'Running') { Start-Service MongoDB; Write-Host "MongoDB started." }
else { Write-Host "MongoDB already running." }

# --- 2. Game server on 27060 --------------------------------------------------
if (Test-Port 27060) {
    Write-Host "Game server already listening on 27060."
}
else {
    if (-not (Test-Path $serverDll)) {
        Write-Host "Server not built, compiling..."
        dotnet build $serverSln -v q --nologo
    }
    Write-Host "Starting game server on 27060..."
    Start-Process -FilePath "dotnet" `
        -ArgumentList "exec", "--roll-forward", "LatestMajor", "`"$serverDll`"" `
        -WorkingDirectory $serverDir

    for ($i = 0; $i -lt 40; $i++) {
        if (Test-Port 27060) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Test-Port 27060) { Write-Host "Game server up." }
    else { Write-Warning "Server did not open 27060 in 20s. Check its console window." }
}

# --- 3. Port 27050 must be free for the proxy ---------------------------------
if (Test-Port 27050) {
    Write-Warning "Port 27050 already in use. Bot proxy needs it (client hardcodes 27050)."
    Write-Warning "Probably an old bsgobot is still running. Close it and rerun."
}

# --- 4. Bot GUI ---------------------------------------------------------------
if (-not (Test-Path $botExe)) {
    Write-Host "Bot not built, compiling..."
    dotnet build $botProj -v q --nologo
}
Write-Host "Starting bot..."
Start-Process -FilePath $botExe -WorkingDirectory (Split-Path $botExe)

Write-Host ""
Write-Host "Ready. In the bot window:" -ForegroundColor Green
Write-Host "  1. Proxy auto-starts. Check 'upstream ... [UP]' in the stats panel."
Write-Host "  2. Click 'Launch Game'"
Write-Host "  3. Fly into a sector, fire once manually, then click 'Go Farm'"
Write-Host ""
Write-Host "Press any key to close this window..."
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
