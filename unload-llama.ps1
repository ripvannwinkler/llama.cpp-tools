# Unload ALL loaded models to free VRAM, WITHOUT stopping the server.
# The router stays up and will auto-load again on the next API request.
# Use this to free the GPU for another heavy task while keeping llama-server running.
param([int]$Port = 8080)

$base = "http://127.0.0.1:$Port"
try { Invoke-RestMethod "$base/health" -TimeoutSec 3 | Out-Null }
catch { Write-Host "Server not responding on $Port." -ForegroundColor Red; return }

$before = (& nvidia-smi --query-gpu=memory.used --format=csv,noheader)
$ids = (Invoke-RestMethod "$base/v1/models").data.id

foreach ($id in $ids) {
    $body = @{ model = $id } | ConvertTo-Json
    try {
        Invoke-RestMethod "$base/models/unload" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        Write-Host "unloaded: $id" -ForegroundColor Green
    } catch {
        # 400 "model is not running" for anything not loaded (incl. the 'default' pseudo-model) — ignore
    }
}

Start-Sleep -Milliseconds 500
$after = (& nvidia-smi --query-gpu=memory.used --format=csv,noheader)
Write-Host "VRAM used: $before -> $after" -ForegroundColor Cyan
