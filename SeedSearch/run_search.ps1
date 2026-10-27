# Starts one search process per core, each on its own reproducible seed sequence.
# Matches append to results\matches_<n>.txt and progress goes to results\log_<n>.txt.
# Stop everything with:  Get-Process dotnet | Stop-Process
# Searches run at BelowNormal priority so the PC stays usable; pass -Priority Normal for full speed when idle.
param(
    [int]$Processes = [Environment]::ProcessorCount,
    [long]$CountPerProcess = 100000000,
    [long]$FromBase = 1000,
    [string]$Priority = "BelowNormal"
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here "bin\Release\net9.0\SeedSearch.dll"
$out = Join-Path $here "results"
New-Item -ItemType Directory -Force $out | Out-Null

for ($i = 0; $i -lt $Processes; $i++) {
    $from = $FromBase + $i
    $p = Start-Process dotnet -WorkingDirectory $here -WindowStyle Hidden -PassThru `
        -ArgumentList "`"$exe`" search --count $CountPerProcess --from $from --out `"$out\matches_$i.txt`"" `
        -RedirectStandardOutput "$out\log_$i.txt" -RedirectStandardError "$out\err_$i.txt"
    $p.PriorityClass = $Priority
}
Write-Host "Started $Processes searches. Logs and matches are in $out"
