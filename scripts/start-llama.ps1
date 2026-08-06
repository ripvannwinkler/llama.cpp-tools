# Start llama.cpp router server (CUDA) — OpenAI-compatible API on http://127.0.0.1:8080/v1
# Model switching: set the "model" field per request (ids come from GET /v1/models).
param(
    [int]$Port = 8080,
    [int]$MaxModels = 1,          # keep 1: only one model in VRAM at a time (32 GB is tight)
    [int]$Ctx = 0                 # 0 = use per-model ctx-size from models.ini; >0 overrides ALL models
)

$ErrorActionPreference = 'Stop'
$bin       = 'D:\llama.cpp\src\build\bin\llama-server.exe'
$modelsDir = 'D:\llama.cpp\models'
$preset    = 'D:\llama.cpp\models.ini'   # per-model overrides (ngl, flash-attn, ctx, kv quant, ...)

# refuse to double-start
$existing = (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).OwningProcess | Select-Object -Unique
if ($existing) { Write-Host "Already listening on $Port (PID $existing). Run stop-llama.ps1 first." -ForegroundColor Yellow; return }

# NOTE: -ngl / -fa intentionally NOT passed here — they live in models.ini [*] so
# they stay per-model overridable (a CLI arg would override every preset value).
$argList = @('--models-dir', $modelsDir, '--models-max', $MaxModels,
             '--parallel', 1,
             '--port', $Port, '--host', '127.0.0.1')
if (Test-Path $preset) { $argList += @('--models-preset', $preset) }
if ($Ctx -gt 0)        { $argList += @('-c', $Ctx) }   # optional global ctx override

Start-Process -FilePath $bin -ArgumentList $argList `
    -RedirectStandardOutput 'D:\llama.cpp\server.out.log' `
    -RedirectStandardError  'D:\llama.cpp\server.err.log' -WindowStyle Hidden

# wait for health
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null
          Write-Host "llama-server up: http://127.0.0.1:$Port/v1  (models-max=$MaxModels)" -ForegroundColor Green
          Invoke-RestMethod "http://127.0.0.1:$Port/v1/models" | Select-Object -ExpandProperty data |
              ForEach-Object { Write-Host "  - $($_.id)" }
          return } catch { Start-Sleep -Milliseconds 700 }
}
Write-Host "Server did not become healthy; check D:\llama.cpp\server.err.log" -ForegroundColor Red
