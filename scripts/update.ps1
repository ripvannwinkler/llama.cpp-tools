# update.ps1 — update upstream llama.cpp and build it.
# The upstream repository calls its mainline branch "master". The script fast-forwards
# that branch, then stops/rebuilds/restarts the server.
#
#   .\scripts\update.ps1              # update, rebuild, restart server if it was up
#   .\scripts\update.ps1 -NoRestart   # update + rebuild but leave the server stopped
param([switch]$NoRestart)

$ErrorActionPreference = 'Stop'
$src            = 'D:\llama.cpp\src'
$mainBranch     = 'master'
$server         = "$src\build\bin\llama-server.exe"
$vcvars         = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { Write-Host "vcvars64.bat not found at $vcvars" -ForegroundColor Red; return }

function Get-Ver($exe) {
    if (-not (Test-Path $exe)) { return 'none' }
    $ErrorActionPreference = 'Continue' # llama-server writes version information to stderr
    $line = & $exe --version 2>&1 | Select-String 'version:' | Select-Object -First 1
    if ($line) { return $line.ToString().Trim() }
    return 'unknown'
}

# 1. refuse to switch branches with local source changes
$changes = @(git -C $src status --porcelain)
if ($LASTEXITCODE -ne 0) { Write-Host "Unable to read git status for $src." -ForegroundColor Red; return }
if ($changes.Count -gt 0) {
    Write-Host "Source tree has uncommitted changes; commit or stash them before updating:" -ForegroundColor Red
    $changes | ForEach-Object { Write-Host "  $_" }
    return
}

# 2. fast-forward upstream's mainline branch
Write-Host "Updating $mainBranch from origin/$mainBranch ..." -ForegroundColor Cyan
git -C $src switch $mainBranch
if ($LASTEXITCODE -ne 0) { Write-Host "Failed to switch to $mainBranch." -ForegroundColor Red; return }
git -C $src pull --ff-only origin $mainBranch
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to fast-forward $mainBranch; build not started." -ForegroundColor Red
    return
}

# 3. remember if the server was running, then stop it (binary is locked while running)
$wasUp = [bool]((Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue).OwningProcess)
if ($wasUp) {
    Write-Host "Stopping server (binary is locked while running)..." -ForegroundColor Cyan
    try {
        & D:\llama.cpp\scripts\stop-llama.ps1 | Out-Null
    } catch {
        if (Get-Process llama-server -ErrorAction SilentlyContinue) {
            Write-Host "Failed to stop all llama-server processes. Build not started." -ForegroundColor Red
            return
        }
        Write-Host "Server exited during the stop sweep; continuing." -ForegroundColor DarkGray
    }
}

$oldVer = Get-Ver $server

# 4. configure + build $mainBranch inside the VS dev environment (Ninja + CUDA sm_120). Incremental.
Write-Host "Configuring + building $mainBranch (CUDA sm_120)..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$buildCmd = "cd /d `"$src`" && " +
            "cmake -B build -G Ninja -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release -DCMAKE_CUDA_ARCHITECTURES=120 " +
            "-DGGML_CUDA_GRAPHS=ON -DGGML_CUDA_FA_ALL_QUANTS=ON && " +
            "cmake --build build --config Release -j"
& "$env:SystemRoot\System32\cmd.exe" /c "`"$vcvars`" >nul 2>&1 && $buildCmd"
$sw.Stop()
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (exit $LASTEXITCODE). Server left stopped." -ForegroundColor Red; return }

# 5. report
$newVer = Get-Ver $server
Write-Host ("`nBuild OK in {0:n0}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host "  old: $oldVer"
Write-Host "  new: $newVer" -ForegroundColor Green

# 6. restart if it was running
if ($wasUp -and -not $NoRestart) { Write-Host "`nRestarting server..." -ForegroundColor Cyan; & D:\llama.cpp\scripts\start-llama.ps1 }
elseif ($wasUp)                  { Write-Host "`nServer was running but left stopped (-NoRestart). Start with scripts\start-llama.ps1." -ForegroundColor DarkGray }
