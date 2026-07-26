# BSGO local server launcher
# MongoDB runs as a Windows service (MongoDB.Server 8.3.4) -> no manual mongod needed.
# Server targets netcoreapp3.0; we roll forward onto the installed .NET 10 runtime.

$ErrorActionPreference = 'Stop'
$root = "C:\Users\uruskan\Desktop\BSGOBABA\server\BSGO Server\BSGO Server"
$dll  = Join-Path $root "bin\Debug\netcoreapp3.0\BSGO Server.dll"

$svc = Get-Service -Name MongoDB -ErrorAction SilentlyContinue
if ($null -eq $svc) { throw "MongoDB service not found. Install: winget install --id MongoDB.Server" }
if ($svc.Status -ne 'Running') { Start-Service MongoDB; Write-Host "MongoDB started." }

if (-not (Test-Path $dll)) {
    Write-Host "Build missing, compiling..."
    dotnet build "C:\Users\uruskan\Desktop\BSGOBABA\server\BSGO Server\BSGO Server.sln" -v q --nologo
}

Write-Host "Game port 27050 / chat port 9338. Ctrl+C to stop."
dotnet exec --roll-forward LatestMajor $dll
