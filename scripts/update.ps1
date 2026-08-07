# update.ps1 — sync llama.cpp source onto the local perf branch and recompile the CUDA build.
# src is kept on nvfp4-quantize (NVFP4 quantize type + chunked gated-delta-net kernels). That
# branch is LOCAL ONLY and is never pushed, so "update" means: fast-forward master from origin,
# merge master into nvfp4-quantize, rebuild — every rebuild keeps the branch optimizations.
# The server is stopped first (a running llama-server.exe locks the binary) and restarted after.
#
#   .\scripts\update.ps1              # sync, rebuild, restart server if it was up
#   .\scripts\update.ps1 -NoRestart   # sync + rebuild but leave the server stopped
#   .\scripts\update.ps1 -SkipPull    # rebuild the current source as-is, no fetch/merge
param([switch]$NoRestart, [switch]$SkipPull)

$ErrorActionPreference = 'Stop'
# git exit codes are checked by hand below; without this, pwsh 7.4+ turns every non-zero
# native exit into a terminating error and the friendly messages never print.
$PSNativeCommandUseErrorActionPreference = $false

$src      = 'D:\llama.cpp\src'
$server   = "$src\build\bin\llama-server.exe"
$branch   = 'nvfp4-quantize'   # local perf branch — never push it (Apache-2.0 ported kernels in an MIT repo)
$upstream = 'master'           # ggml-org/llama.cpp default branch
$vcvars   = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { Write-Host "vcvars64.bat not found at $vcvars" -ForegroundColor Red; return }

function Get-Ver($exe) { if (Test-Path $exe) { (& $exe --version 2>&1 | Select-String 'version:' | Select-Object -First 1).ToString().Trim() } else { 'none' } }

# 1. make sure src is on the perf branch — building off master silently drops the optimizations
$current = git -C $src rev-parse --abbrev-ref HEAD 2>$null
if ($LASTEXITCODE -ne 0) { Write-Host "$src is not a git checkout." -ForegroundColor Red; return }
$dirty = git -C $src status --porcelain
if ($current -ne $branch) {
    if ($dirty) { Write-Host "src is on '$current' with uncommitted changes — refusing to switch to '$branch'. Commit or stash first." -ForegroundColor Red; return }
    Write-Host "Switching src from '$current' to '$branch' ..." -ForegroundColor Cyan
    git -C $src checkout $branch
    if ($LASTEXITCODE -ne 0) { Write-Host "checkout '$branch' failed (exit $LASTEXITCODE)." -ForegroundColor Red; return }
}

# 2. fast-forward local master from origin, then merge it into the perf branch
if ($SkipPull) { Write-Host "Building '$branch' as-is (-SkipPull)." -ForegroundColor DarkGray }
else {
    if ($dirty) { Write-Host "src has uncommitted changes — cannot merge '$upstream'. Commit or stash first." -ForegroundColor Red; return }

    # refspec form updates the local master ref without checking it out (we stay on $branch)
    Write-Host "Fetching origin and fast-forwarding '$upstream' ..." -ForegroundColor Cyan
    git -C $src fetch origin "${upstream}:${upstream}"
    if ($LASTEXITCODE -ne 0) { Write-Host "fetch failed (exit $LASTEXITCODE) — local '$upstream' has probably diverged from origin." -ForegroundColor Red; return }

    $incoming = [int](git -C $src rev-list --count "HEAD..$upstream")
    if ($incoming -eq 0) { Write-Host "'$branch' already contains all of '$upstream'." -ForegroundColor DarkGray }
    else {
        Write-Host "Merging $incoming new '$upstream' commit(s) into '$branch' ..." -ForegroundColor Cyan
        git -C $src merge --no-edit $upstream
        if ($LASTEXITCODE -ne 0) {
            Write-Host "MERGE FAILED (exit $LASTEXITCODE) — nothing was rebuilt." -ForegroundColor Red
            if (Test-Path "$src\.git\MERGE_HEAD") {
                git -C $src merge --abort
                Write-Host "Merge aborted; src restored to its pre-merge state." -ForegroundColor Yellow
            }
            Write-Host "Resolve '$upstream' into '$branch' by hand in $src, then re-run this script." -ForegroundColor DarkGray
            return
        }
    }
}

# 3. remember if the server was running, then stop it (binary is locked while running)
$wasUp = [bool]((Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue).OwningProcess)
if ($wasUp) { Write-Host "Stopping server (binary is locked while running)..." -ForegroundColor Cyan; & D:\llama.cpp\scripts\stop-llama.ps1 | Out-Null }

$oldVer = Get-Ver $server

# 4. configure + build inside the VS dev environment (Ninja + CUDA sm_120). Incremental.
Write-Host "Configuring + building '$branch' (CUDA sm_120)..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$buildCmd = "cd /d `"$src`" && " +
            "cmake -B build -G Ninja -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release -DCMAKE_CUDA_ARCHITECTURES=120 " +
            "-DGGML_CUDA_GRAPHS=ON -DGGML_CUDA_FA_ALL_QUANTS=ON && " +
            "cmake --build build --config Release -j"
cmd /c "`"$vcvars`" >nul 2>&1 && $buildCmd"
$sw.Stop()
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (exit $LASTEXITCODE). Server left stopped." -ForegroundColor Red; return }

# 5. report
$newVer = Get-Ver $server
$head   = git -C $src rev-parse --short HEAD
Write-Host ("`nBuild OK in {0:n0}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host "  branch: $branch @ $head"
Write-Host "  old: $oldVer"
Write-Host "  new: $newVer" -ForegroundColor Green

# 6. restart if it was running
if ($wasUp -and -not $NoRestart) { Write-Host "`nRestarting server..." -ForegroundColor Cyan; & D:\llama.cpp\scripts\start-llama.ps1 }
elseif ($wasUp)                  { Write-Host "`nServer was running but left stopped (-NoRestart). Start with scripts\start-llama.ps1." -ForegroundColor DarkGray }
