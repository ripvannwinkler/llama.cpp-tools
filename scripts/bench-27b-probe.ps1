# Quick decode-throughput probe for one model on the router.
# Usage: .\scripts\bench-27b-probe.ps1 -Name 27B [-Runs 2] [-MaxTokens 768]
# Prints per-run tok/s, draft acceptance, and tokens per main step.
param(
    [string]$Name = '27B',
    [int]$Runs = 2,
    [int]$MaxTokens = 768,
    [int]$Port = 8080
)

$base = "http://127.0.0.1:$Port"

$ids = (Invoke-RestMethod "$base/v1/models" -TimeoutSec 10).data.id | Where-Object { $_ -ne 'default' }
$sel = @($ids | Where-Object { $_ -match [regex]::Escape($Name) })
if ($sel.Count -ne 1) { Write-Error "Model filter '$Name' matched $($sel.Count) models"; return }
$model = $sel[0]

# Make sure it is loaded and ready
try { Invoke-RestMethod "$base/models/load" -Method Post -Body (@{ model = $model } | ConvertTo-Json) -ContentType 'application/json' -TimeoutSec 600 | Out-Null } catch {}
for ($i = 0; $i -lt 600; $i++) {
    try {
        $p = Invoke-RestMethod "$base/props?model=$([uri]::EscapeDataString($model))" -TimeoutSec 5
        if ($p.default_generation_settings.n_ctx) { break }
    } catch {}
    Start-Sleep -Milliseconds 500
}

$prompts = @(
    'Think step by step: prove that the square root of 2 is irrational.',
    'Reason carefully: why can there be no largest prime number? Walk through the classic argument.'
)

$results = @()
for ($r = 0; $r -lt $Runs; $r++) {
    $prompt = $prompts[$r % $prompts.Count]
    $body = @{
        model      = $model
        messages   = @(@{ role = 'user'; content = $prompt })
        max_tokens = $MaxTokens
        stream     = $false
    } | ConvertTo-Json -Depth 5
    $resp = Invoke-RestMethod "$base/v1/chat/completions" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 600
    $t = $resp.timings
    $steps = [Math]::Max(1, $t.draft_n + 1)   # main steps ~= drafted rounds (n-max=1 -> 1:1)
    $row = [pscustomobject]@{
        run         = $r + 1
        tps         = [math]::Round($t.predicted_per_second, 1)
        tok_per_step= [math]::Round($resp.usage.completion_tokens / $steps, 2)
        accept      = [math]::Round(100 * $t.draft_n_accepted / [Math]::Max(1, $t.draft_n), 1)
        out_tokens  = $resp.usage.completion_tokens
        step_ms     = [math]::Round($t.predicted_ms / $steps, 2)
    }
    $results += $row
    $row | Format-Table -AutoSize | Out-Host
}
$avg = [math]::Round(($results | Measure-Object tps -Average).Average, 1)
Write-Host "AVERAGE tps: $avg" -ForegroundColor Cyan
