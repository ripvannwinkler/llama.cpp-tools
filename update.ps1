# update.ps1 — pull the latest llama.cpp source and recompile the CUDA build.
# Stops the server first (a running llama-server.exe locks the binary), then git pull,
# reconfigure + build under the VS dev environment, and restart if it was running.
#
#   .\update.ps1              # update, rebuild, restart server if it was up
#   .\update.ps1 -NoRestart   # update + rebuild but leave the server stopped
param([switch]$NoRestart)

$ErrorActionPreference = 'Stop'
$src    = 'D:\llama.cpp\src'
$server = "$src\build\bin\llama-server.exe"
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { Write-Host "vcvars64.bat not found at $vcvars" -ForegroundColor Red; return }

function Get-Ver($exe) { if (Test-Path $exe) { (& $exe --version 2>&1 | Select-String 'version:' | Select-Object -First 1).ToString().Trim() } else { 'none' } }

# 1. remember if the server was running, then stop it (binary is locked while running)
$wasUp = [bool]((Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue).OwningProcess)
if ($wasUp) { Write-Host "Stopping server (binary is locked while running)..." -ForegroundColor Cyan; & D:\llama.cpp\stop-llama.ps1 | Out-Null }

$oldVer = Get-Ver $server

# 2. pull latest source
Write-Host "Pulling latest source into $src ..." -ForegroundColor Cyan
git -C $src pull --ff-only
if ($LASTEXITCODE -ne 0) { Write-Host "WARNING: git pull failed (exit $LASTEXITCODE) — recompiling current source anyway." -ForegroundColor Yellow }

# 3. configure + build inside the VS dev environment (Ninja + CUDA sm_120). Incremental.
Write-Host "Configuring + building (CUDA sm_120)..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$buildCmd = "cd /d `"$src`" && " +
            "cmake -B build -G Ninja -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release -DCMAKE_CUDA_ARCHITECTURES=120 && " +
            "cmake --build build --config Release -j"
cmd /c "`"$vcvars`" >nul 2>&1 && $buildCmd"
$sw.Stop()
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (exit $LASTEXITCODE). Server left stopped." -ForegroundColor Red; return }

# 4. report
$newVer = Get-Ver $server
Write-Host ("`nBuild OK in {0:n0}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host "  old: $oldVer"
Write-Host "  new: $newVer" -ForegroundColor Green

# 5. restart if it was running
if ($wasUp -and -not $NoRestart) { Write-Host "`nRestarting server..." -ForegroundColor Cyan; & D:\llama.cpp\start-llama.ps1 }
elseif ($wasUp)                  { Write-Host "`nServer was running but left stopped (-NoRestart). Start with start-llama.ps1." -ForegroundColor DarkGray }
