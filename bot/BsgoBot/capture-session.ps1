# capture-session.ps1  -  Option (a) session auto-capture watcher.
#
# Watches for the bsgo.fun launcher's own bsgo.exe, reads its command-line flags
# (+gameServer / +userID / +session / +version), kills that client to free the
# single-use session, and writes a "Live (captured)" server profile into bot.json.
# Then you open the bot and click Launch Game to relaunch THROUGH the proxy with
# the captured session - the test of whether the live server accepts the reuse.
#
# It only captures clients pointed at a real host; it ignores any bsgo.exe already
# pointed at 127.0.0.1 (that is the bot's own relaunch, not the launcher).
#
# ASCII only - Windows PowerShell 5.1 reads this file as ANSI.
#
# Run:  powershell -ExecutionPolicy Bypass -File .\capture-session.ps1
# Stop: Ctrl+C

$ErrorActionPreference = 'Stop'

# bot.json the running exe actually reads is next to the exe (bin dir). Write both
# the source copy and the bin copy so it works however you launch the bot.
$targets = @(
    (Join-Path $PSScriptRoot 'bot.json'),
    (Join-Path $PSScriptRoot 'bin\Debug\net9.0-windows\bot.json')
) | Where-Object { Test-Path (Split-Path $_ -Parent) }

function Get-Flag([string]$cmd, [string]$flag) {
    # \+session must not match \+sessionID: require whitespace right after the flag.
    $m = [regex]::Match($cmd, [regex]::Escape($flag) + '\s+(\S+)')
    if ($m.Success) { return $m.Groups[1].Value } else { return $null }
}

function Update-BotJson($host_, [int]$port, $playerId, $session, $lang, $version) {
    foreach ($path in $targets) {
        if (-not (Test-Path $path)) {
            # Minimal skeleton if this copy does not exist yet.
            $cfg = [pscustomobject]@{
                ListenHost = '127.0.0.1'; ListenPort = 27050; AutoStartProxy = $true
                Servers = @(); Clients = @(); SelectedServer = 0; SelectedClient = 0
            }
        }
        else {
            $cfg = Get-Content $path -Raw | ConvertFrom-Json
        }

        $profile = [ordered]@{
            Name     = 'Live (captured)'
            Host     = $host_
            Port     = $port
            PlayerId = "$playerId"
            Session  = $session
            Language = if ($lang) { $lang } else { 'en' }
        }

        $list = New-Object System.Collections.ArrayList
        $idx = -1
        $i = 0
        foreach ($s in @($cfg.Servers)) {
            if ($s.Name -eq 'Live (captured)') { $idx = $i }
            [void]$list.Add($s)
            $i++
        }
        if ($idx -ge 0) { $list[$idx] = [pscustomobject]$profile }
        else { $idx = $list.Add([pscustomobject]$profile) }

        $cfg.Servers = $list.ToArray()
        $cfg.SelectedServer = $idx

        # Keep the client version in sync with what the launcher used.
        if ($version -and $cfg.Clients) {
            foreach ($c in @($cfg.Clients)) {
                if ($c.PSObject.Properties.Name -contains 'Version') { $c.Version = $version }
            }
        }

        $cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $path -Encoding UTF8
        Write-Host "  wrote $path" -ForegroundColor DarkGray
    }
}

Write-Host "Session watcher running. Log in through the bsgo.fun launcher now." -ForegroundColor Green
Write-Host "Watching for the launcher's bsgo.exe (ignores 127.0.0.1 clients)..." -ForegroundColor Green
Write-Host ""

$seen = @{}

while ($true) {
    $procs = Get-CimInstance Win32_Process -Filter "Name='bsgo.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        if ($seen.ContainsKey($p.ProcessId)) { continue }
        $cmd = $p.CommandLine
        if (-not $cmd) { continue }

        $gameServer = Get-Flag $cmd '+gameServer'
        if (-not $gameServer) { continue }

        # Ignore the bot's own relaunch (points at the local proxy).
        if ($gameServer -eq '127.0.0.1' -or $gameServer -eq 'localhost') {
            $seen[$p.ProcessId] = $true
            continue
        }

        $userId  = Get-Flag $cmd '+userID'
        $session = Get-Flag $cmd '+session'
        $version = Get-Flag $cmd '+version'
        $lang    = Get-Flag $cmd '+language'

        if (-not $session) {
            Write-Host "Found bsgo.exe -> $gameServer but no +session. Skipping." -ForegroundColor Yellow
            $seen[$p.ProcessId] = $true
            continue
        }

        Write-Host "CAPTURED from launcher client (PID $($p.ProcessId)):" -ForegroundColor Cyan
        Write-Host "  gameServer : $gameServer"
        Write-Host "  userID     : $userId"
        Write-Host "  session    : $session"
        Write-Host "  version    : $version"

        # Free the single-use session: kill the launcher's client before/just as it
        # connects, so the proxied relaunch is the session's first-and-only use.
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-Host "  killed launcher client to free the session." -ForegroundColor DarkGray
        }
        catch {
            Write-Host "  could not kill PID $($p.ProcessId): $($_.Exception.Message)" -ForegroundColor Yellow
        }

        Update-BotJson $gameServer 27050 $userId $session $lang $version

        $seen[$p.ProcessId] = $true
        Write-Host ""
        Write-Host "Ready. Now open the bot and click Launch Game." -ForegroundColor Green
        Write-Host "If the live server accepts the connection -> reuse works." -ForegroundColor Green
        Write-Host "If it drops at login -> the session is strictly single-use." -ForegroundColor Green
        Write-Host ""
        Write-Host "Still watching for the next login..." -ForegroundColor DarkGray
    }

    # Forget PIDs that have exited so a fresh login with the same PID reused by
    # Windows still gets captured.
    $live = @{}
    foreach ($p in $procs) { $live[$p.ProcessId] = $true }
    foreach ($k in @($seen.Keys)) { if (-not $live.ContainsKey($k)) { $seen.Remove($k) } }

    Start-Sleep -Milliseconds 200
}
