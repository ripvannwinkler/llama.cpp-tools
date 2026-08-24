# Stop llama.cpp router server cleanly — kills the WHOLE process tree so model child
# processes don't orphan and keep holding VRAM.
param([int]$Port = 8080)

# taskkill legitimately races a process that's mid-exit (e.g. a tree-killed child
# that already died with its parent) and writes "ERROR: ..." to stderr. Under the
# caller's $ErrorActionPreference = 'Stop' (restart-llama.ps1 sets this), that
# stderr write becomes a terminating NativeCommandError — in both Windows
# PowerShell 5.1 and PowerShell 7 — silently aborting the whole restart before
# start-llama.ps1 ever runs. Swallow it here; a lost race is informational, not
# fatal — the Get-Process check below (and the sweep loop) verifies the real
# outcome instead of trusting taskkill's exit.
function Stop-Tree([int]$TargetPid) {
    try { taskkill /PID $TargetPid /T /F 2>$null | Out-Null } catch { }      # /T = tree, /F = force
}

$parents = (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).OwningProcess | Select-Object -Unique
foreach ($p in $parents) {
    Write-Host "Killing router tree at PID $p ..." -ForegroundColor Cyan
    Stop-Tree $p
}

# sweep any stray llama-server processes (orphans from earlier port-kills).
# retry a few times to avoid the mid-exit race where a child lingers briefly.
for ($pass = 1; $pass -le 5; $pass++) {
    $stray = Get-Process llama-server -ErrorAction SilentlyContinue
    if (-not $stray) { break }
    Write-Host "Sweep pass $pass : $($stray.Count) stray llama-server process(es)..." -ForegroundColor Cyan
    $stray | ForEach-Object { Stop-Tree $_.Id }
    Start-Sleep -Milliseconds 600
}
$left = (Get-Process llama-server -ErrorAction SilentlyContinue).Count
if ($left) { Write-Host "WARNING: $left llama-server process(es) still alive." -ForegroundColor Red }
$used = (& nvidia-smi --query-gpu=memory.used --format=csv,noheader)
Write-Host "Done. GPU memory used now: $used" -ForegroundColor Green
