# Starts (or resumes) one search process per core. Uses the same results\ files as `SeedSearch serve`, so the
# command line and the web UI can resume each other's searches.
#   .\run_search.ps1                   start, or resume where the workers stopped
#   .\run_search.ps1 -Stop             stop all workers (they save progress first)
#   .\run_search.ps1 -Processes 6      leave two cores free
#   .\run_search.ps1 -Priority Normal  full speed (default BelowNormal keeps the PC usable)
# To start a fresh search, stop it and rename or delete the results folder.
param(
    [int]$Processes = [Environment]::ProcessorCount,
    [string]$Priority = "BelowNormal",
    [switch]$Stop
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here "bin\Release\net9.0\SeedSearch.dll"
$out = Join-Path $here "results"
$stopFile = Join-Path $out "STOP"
New-Item -ItemType Directory -Force $out | Out-Null

if ($Stop) {
    Set-Content $stopFile "stop"
    Write-Host "Stop requested; workers save their progress and exit within a few seconds."
    return
}
Remove-Item $stopFile -ErrorAction SilentlyContinue

# Each worker's seed sequence is fixed by search.json, so a resumed worker continues the same sequence.
$searchFile = Join-Path $out "search.json"
if (-not (Test-Path $searchFile)) {
    $fromBase = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    @{ FromBase = $fromBase; Created = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json | Set-Content $searchFile
}
$fromBase = [uint64](Get-Content $searchFile | ConvertFrom-Json).FromBase

for ($i = 0; $i -lt $Processes; $i++) {
    $state = Join-Path $out "state_$i.json"
    if (Test-Path $state) {
        $s = Get-Content $state | ConvertFrom-Json
        if ($s.Finished) { continue }
        $running = Get-Process -Id $s.Pid -ErrorAction SilentlyContinue
        if ($running -and $running.ProcessName -eq "dotnet") { continue }
    }
    $from = $fromBase + $i
    $p = Start-Process dotnet -WorkingDirectory $here -WindowStyle Hidden -PassThru `
        -ArgumentList "`"$exe`" search --count 1000000000000 --from $from --state `"$state`" --stop `"$stopFile`" --out `"$out\matches_$i.txt`" --near `"$out\near_$i.txt`"" `
        -RedirectStandardOutput "$out\log_$i.txt" -RedirectStandardError "$out\err_$i.txt"
    $p.PriorityClass = $Priority
}
Write-Host "Workers running. Progress and matches are in $out (or run: dotnet bin\Release\net9.0\SeedSearch.dll serve)"
