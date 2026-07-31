# load.ps1 — switch the loaded model (starts the server first if needed).
#   .\scripts\load.ps1              # numbered menu of all models
#   .\scripts\load.ps1 gemma        # fuzzy match: load the model whose id contains "gemma"
#   .\scripts\load.ps1 -Name lite   # same, explicit param
# With --models-max 1 this evicts the currently loaded model.
param([string]$Name, [int]$Port = 8080)

$base = "http://127.0.0.1:$Port"

# 1. start the server if it isn't healthy (probe /health, not the TCP table —
#    Get-NetTCPConnection can transiently report nothing right after a boot).
#    Any /health response counts as "running": during a model load the status is
#    "loading model", and we still want to reuse that server rather than restart.
$healthy = $false
try { Invoke-RestMethod "$base/health" -TimeoutSec 3 | Out-Null; $healthy = $true } catch { $healthy = $false }
if (-not $healthy) {
    Write-Host "Server not responding on $Port — starting it..." -ForegroundColor Yellow
    & D:\llama.cpp\scripts\start-llama.ps1 | Out-Null
}

# 2. fetch model ids
try { $ids = (Invoke-RestMethod "$base/v1/models" -TimeoutSec 10).data.id | Where-Object { $_ -ne 'default' } }
catch { Write-Host "Could not reach the server on $Port." -ForegroundColor Red; return }
if (-not $ids) { Write-Host "No models available." -ForegroundColor Red; return }

# 3. select a model
$sel = $null
if ($Name) {
    $hits = @($ids | Where-Object { $_ -match [regex]::Escape($Name) })   # case-insensitive substring
    if ($hits.Count -eq 1) { $sel = $hits[0] }
    elseif ($hits.Count -eq 0) { Write-Host "No model matches '$Name'. Available:" -ForegroundColor Red; $ids | ForEach-Object { "  $_" }; return }
    else {
        Write-Host "Multiple matches for '$Name':" -ForegroundColor Yellow
        for ($i = 0; $i -lt $hits.Count; $i++) { "  [{0}] {1}" -f $i, $hits[$i] }
        $c = Read-Host "Select number"
        if ($c -match '^\d+$' -and [int]$c -lt $hits.Count) { $sel = $hits[[int]$c] } else { Write-Host "Invalid." -ForegroundColor Red; return }
    }
} else {
    Write-Host "`nModels:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $ids.Count; $i++) { "  [{0}] {1}" -f $i, $ids[$i] }
    $c = Read-Host "`nSelect model to load"
    if ($c -match '^\d+$' -and [int]$c -lt $ids.Count) { $sel = $ids[[int]$c] } else { Write-Host "Invalid." -ForegroundColor Red; return }
}

# 4. request load (evicts current model under --models-max 1). /models/load is async.
Write-Host "Loading $sel ..." -ForegroundColor Cyan
$already = $false
try {
    Invoke-RestMethod "$base/models/load" -Method Post -TimeoutSec 300 -ContentType 'application/json' -Body (@{ model = $sel } | ConvertTo-Json) | Out-Null
} catch {
    $msg = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
    if ($msg -match 'already running') { $already = $true } else { Write-Host "Load failed: $msg" -ForegroundColor Red; return }
}

# 5. wait for the model to actually finish loading (poll /props), then report real VRAM
$enc = [uri]::EscapeDataString($sel); $nctx = $null
for ($i = 0; $i -lt 120; $i++) {
    try { $nctx = (Invoke-RestMethod "$base/props?model=$enc" -TimeoutSec 5).default_generation_settings.n_ctx; if ($nctx) { break } }
    catch { if (($_.ErrorDetails.Message) -match 'failed to load') { Write-Host "Load failed (OOM at this model's ctx). See server.err.log." -ForegroundColor Red; return } }
    Start-Sleep -Milliseconds 500
}
if ($nctx) {
    $verb = if ($already) { 'Already loaded' } else { 'Loaded' }
    Write-Host ("{0}: {1}  (n_ctx {2})" -f $verb, $sel, $nctx) -ForegroundColor Green
    Write-Host ("VRAM: " + (& nvidia-smi --query-gpu=memory.used --format=csv,noheader))
} else {
    Write-Host "Load requested but model not ready after 60s — check server.err.log." -ForegroundColor Yellow
}
