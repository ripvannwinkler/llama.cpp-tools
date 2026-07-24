# Stop llama.cpp router server cleanly — kills the WHOLE process tree so model child
# processes don't orphan and keep holding VRAM.
param([int]$Port = 8080)

$parents = (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).OwningProcess | Select-Object -Unique
foreach ($p in $parents) {
    Write-Host "Killing router tree at PID $p ..." -ForegroundColor Cyan
    taskkill /PID $p /T /F | Out-Null      # /T = tree, /F = force
}

# sweep any stray llama-server processes (orphans from earlier port-kills).
# retry a couple times to avoid the mid-exit race where a child lingers briefly.
for ($pass = 1; $pass -le 3; $pass++) {
    $stray = Get-Process llama-server -ErrorAction SilentlyContinue
    if (-not $stray) { break }
    Write-Host "Sweep pass $pass : $($stray.Count) stray llama-server process(es)..." -ForegroundColor Cyan
    $stray | ForEach-Object { taskkill /PID $_.Id /T /F 2>$null | Out-Null }
    Start-Sleep -Milliseconds 600
}
$left = (Get-Process llama-server -ErrorAction SilentlyContinue).Count
if ($left) { Write-Host "WARNING: $left llama-server process(es) still alive." -ForegroundColor Red }
$used = (& nvidia-smi --query-gpu=memory.used --format=csv,noheader)
Write-Host "Done. GPU memory used now: $used" -ForegroundColor Green
