# bench.ps1 — benchmark a local model with llama-bench.
# Lists models, lets you pick one (or pass -Model <id>), applies that model's KV-quant
# from models.ini, frees VRAM first, then runs llama-bench (prompt-processing + gen speed).
#
# Examples:
#   .\scripts\bench.ps1                                  # interactive menu, default 1024 prompt / 256 gen
#   .\scripts\bench.ps1 -Model lmstudio-community__Qwen3-VL-8B-Instruct-GGUF
#   .\scripts\bench.ps1 -PromptTokens 2048 -GenTokens 256 -Depth 4096 -Reps 5
param(
    [string]$Model,                 # model id (folder name); if omitted, prompts interactively
    [int]$PromptTokens = 1024,       # -p  : tokens to process for prompt-eval (pp) test
    [int]$GenTokens = 256,       # -n  : tokens to generate for token-gen (tg) test
    [int]$Depth = 0,         # -d  : prefill depth before the test (tests speed at context depth)
    [int]$Reps = 3,         # -r  : repetitions per test
    [int]$NGL = 99,        # -ngl: layers offloaded to GPU
    [ValidateSet('on', 'off', 'auto')][string]$FlashAttn = 'on',
    [switch]$NoUnload               # skip freeing VRAM from the running server first
)

$ErrorActionPreference = 'Stop'
$bench = 'D:\llama.cpp\src\build\bin\llama-bench.exe'
$root = 'D:\llama.cpp\models'
$iniPath = 'D:\llama.cpp\models.ini'
if (-not (Test-Path $bench)) { Write-Host "llama-bench not found at $bench" -ForegroundColor Red; return }

# ---- enumerate models: <root>\<id>\*.gguf (skip mmproj, prefer first shard) + top-level *.gguf ----
$entries = @()
Get-ChildItem $root -Directory | ForEach-Object {
    $gg = Get-ChildItem $_.FullName -Filter *.gguf | Where-Object { $_.Name -notmatch 'mmproj' }
    $main = $gg | Where-Object { $_.Name -match '-00001-of-' } | Select-Object -First 1
    if (-not $main) { $main = $gg | Select-Object -First 1 }
    if ($main) { $entries += [pscustomobject]@{ id = $_.Name; path = $main.FullName } }
}
Get-ChildItem $root -Filter *.gguf -File -ErrorAction SilentlyContinue |
Where-Object { $_.Name -notmatch 'mmproj' } |
ForEach-Object { $entries += [pscustomobject]@{ id = $_.BaseName; path = $_.FullName } }

if (-not $entries) { Write-Host "No models found under $root" -ForegroundColor Red; return }

# ---- select ----
if ($Model) {
    $sel = $entries | Where-Object { $_.id -eq $Model } | Select-Object -First 1
    if (-not $sel) { Write-Host "Model '$Model' not found. Available:" -ForegroundColor Red; $entries.id | ForEach-Object { "  $_" }; return }
}
else {
    Write-Host "`nAvailable models:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $entries.Count; $i++) { "  [{0}] {1}" -f $i, $entries[$i].id }
    $choice = Read-Host "`nSelect model number"
    if ($choice -notmatch '^\d+$' -or [int]$choice -ge $entries.Count) { Write-Host "Invalid selection." -ForegroundColor Red; return }
    $sel = $entries[[int]$choice]
}

# ---- pull this model's cache-type-k/v from models.ini ([model] overrides [*]) ----
$ctk = $null; $ctv = $null
if (Test-Path $iniPath) {
    $section = $null; $ini = @{}
    foreach ($line in Get-Content $iniPath) {
        $l = $line.Trim()
        if ($l -eq '' -or $l -match '^[;#]') { continue }
        if ($l -match '^\[(.+)\]$') { $section = $matches[1]; if (-not $ini.ContainsKey($section)) { $ini[$section] = @{} }; continue }
        if ($section -and $l -match '^([^=]+)=(.*)$') { $ini[$section][$matches[1].Trim()] = (($matches[2] -split '[;#]', 2)[0]).Trim() }
    }
    foreach ($s in @('*', $sel.id)) {
        # '*' first, model second so model wins
        if ($ini[$s]) {
            if ($ini[$s]['cache-type-k']) { $ctk = $ini[$s]['cache-type-k'] }
            if ($ini[$s]['cache-type-v']) { $ctv = $ini[$s]['cache-type-v'] }
        }
    }
}

# ---- free VRAM: unload models from the running server so llama-bench won't OOM ----
if (-not $NoUnload) {
    try {
        Invoke-RestMethod "http://127.0.0.1:8080/health" -TimeoutSec 2 | Out-Null
        $ids = (Invoke-RestMethod "http://127.0.0.1:8080/v1/models").data.id
        foreach ($id in $ids) {
            try {
                Invoke-RestMethod "http://127.0.0.1:8080/models/unload" -Method Post -TimeoutSec 30 `
                    -Body (@{model = $id } | ConvertTo-Json) -ContentType 'application/json' | Out-Null 
            }
            catch {}
        }
        Write-Host "Freed VRAM from running server (server still up)." -ForegroundColor DarkGray
    }
    catch { }   # server not running — nothing to free
}

# ---- build args + run ----
$args = @('-m', $sel.path, '-ngl', $NGL, '-fa', $FlashAttn, '-p', $PromptTokens, '-n', $GenTokens, '-r', $Reps)
if ($Depth -gt 0) { $args += @('-d', $Depth) }
if ($ctk) { $args += @('-ctk', $ctk) }
if ($ctv) { $args += @('-ctv', $ctv) }

Write-Host "`nBenchmarking: $($sel.id)" -ForegroundColor Green
Write-Host "  file : $($sel.path)"
Write-Host "  args : llama-bench $($args -join ' ')`n" -ForegroundColor DarkGray
& $bench @args
