# update.ps1 — update upstream llama.cpp and build it.
# Keep the local customization branch rebased on upstream master so the
# custom patch survives source updates.
#
#   .\scripts\update.ps1              # update, rebuild, restart server if it was up
#   .\scripts\update.ps1 -NoRestart   # update + rebuild but leave the server stopped
param([switch]$NoRestart)

$ErrorActionPreference = 'Stop'
$src            = 'D:\llama.cpp\src'
$customBranch   = 'feat/custom'
$upstreamBranch = 'master'
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

# 1. refuse to rebase with local source changes
$changes = @(git -C $src status --porcelain)
if ($LASTEXITCODE -ne 0) { Write-Host "Unable to read git status for $src." -ForegroundColor Red; return }
if ($changes.Count -gt 0) {
    Write-Host "Source tree has uncommitted changes; commit or stash them before updating:" -ForegroundColor Red
    $changes | ForEach-Object { Write-Host "  $_" }
    return
}

# 2. rebase the customization branch onto the latest upstream mainline
Write-Host "Updating $customBranch from origin/$upstreamBranch ..." -ForegroundColor Cyan
git -C $src switch $customBranch
if ($LASTEXITCODE -ne 0) { Write-Host "Failed to switch to $customBranch." -ForegroundColor Red; return }
git -C $src fetch origin $upstreamBranch
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to fetch origin/$upstreamBranch; build not started." -ForegroundColor Red
    return
}
git -C $src rebase "origin/$upstreamBranch"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to rebase $customBranch onto origin/$upstreamBranch; resolve the rebase manually. Build not started." -ForegroundColor Red
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

# 4. configure + build $customBranch inside the VS dev environment (Ninja + CUDA sm_120). Incremental.
Write-Host "Configuring + building $customBranch (CUDA sm_120)..." -ForegroundColor Cyan
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
