# Benchmark speculative-decoding settings through the *server*.
#
# Why this exists: llama-bench (and so scripts\bench.ps1) ignores every spec-*
# setting, so it cannot measure speculative decoding at all. The only way to see
# the effect is the `timings` object the server returns on each
# /v1/chat/completions response (predicted_per_second, draft_n, draft_n_accepted).
#
# For each requested config this script writes a temporary copy of models.ini with
# the spec-* keys rewritten, starts its own llama-server on an isolated port, loads
# the model, runs three fixed workloads, and records the median tok/s.
#
# IMPORTANT: rank configs on tok/s, never on draft acceptance. High acceptance does
# not mean the drafter pays for itself -- see the speculative-decoding section of
# AGENTS.md, where draft-mtp lost to no speculation at all despite 92-95% acceptance.
#
# The production router on 8080 holds ~23 GiB of VRAM. This script asks it to unload
# every model first, but the tray app can reload one behind your back -- run
# scripts\stop-llama.ps1 if you want a guaranteed-clean GPU.
#
# Upstream also ships src\tools\server\bench\speed-bench\speed_bench.py, which reads
# the same timings fields but needs the nvidia/SPEED-Bench HF dataset and only talks
# to an already-running server. Useful as an independent cross-check, not a
# replacement -- it cannot sweep models.ini configs.
#
# ---------------------------------------------------------------------------
# READ THIS BEFORE TRUSTING A RESULT. Measured 2026-08-26 while tuning Ornith.
#
# This harness reliably separates spec-type = none from ngram-mod (a 30-75% gap).
# It CANNOT reliably resolve differences between ngram-mod parameter settings,
# which land in the 1-5% range. Three confounds, in the order they bite:
#
# 1. Output diverges between configs. ngram-mod's hash table persists across
#    requests, so drafts differ -> batch shapes differ -> float reduction order
#    differs -> greedy flips a token and the whole generation goes elsewhere.
#    Configs then get benchmarked on *different text*. Observed: the same copy
#    prompt yielding 881 tokens under one config and 421 under another.
# 2. Length drives tok/s on its own. Per-token speed decays as the KV cache
#    grows, so an arm that happens to stop early posts a higher tok/s regardless
#    of how well it drafted. -IgnoreEos pins the token count but does NOT fix
#    confound 1 -- it makes it worse, since post-EOS rambling varies wildly in
#    how repetitive (and so how draftable) it is.
# 3. Cross-invocation drift is ~3-5%, far larger than the ~0.5% within a single
#    invocation. Only compare configs measured in the SAME invocation; pass a
#    config twice in -Configs to get a repeatability control.
#
# If you need a clean parameter comparison, restart the server for every single
# request (fresh state -> identical output across configs, verified) and accept
# the ~35s-per-measurement cost. Otherwise treat sub-5% differences as noise.
# ---------------------------------------------------------------------------
#
# Examples:
#   .\scripts\bench-spec.ps1 -Model Ornith-1.5-35B-A3B-APEX-MTP-Quality
#   .\scripts\bench-spec.ps1 -Model Ornith-1.5-35B-A3B-APEX-MTP-Quality -Configs 16/24/8,24/24/8 -Reps 5
param(
    [Parameter(Mandatory = $true)][string]$Model,
    [int]$Port = 8092,
    [int]$Reps = 3,
    # Each entry is "n_match/n_max/n_min", or the literal "none" for a no-speculation control.
    [string[]]$Configs = @('none', '48/24/8', '24/64/48'),
    [int]$MaxTokens = 1024,
    # Force every arm to emit exactly MaxTokens tokens. Without this, arms whose
    # generation happens to stop early score artificially high: per-token speed
    # decays as the KV cache grows, so a 559-token run beats a 1024-token run on
    # tok/s regardless of how well it drafted. Use this for any decisive comparison.
    [switch]$IgnoreEos,
    [string]$OutFile = 'D:\llama.cpp\spec-tune-results.csv',
    [int]$RouterPort = 8080
)

$ErrorActionPreference = 'Stop'
$root    = 'D:\llama.cpp'
$bin     = Join-Path $root 'src\build\bin\llama-server.exe'
$models  = Join-Path $root 'models'
$preset  = Join-Path $root 'models.ini'
$tmp     = Join-Path $env:TEMP 'llama-spec-bench'

New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$baseText = Get-Content -Raw -Path $preset

# Locate the requested section. Same section regex probe-ctx-headroom.ps1 uses.
$sections = [regex]::Matches($baseText, '(?ms)^\[([^\r\n]+)\]\r?\n(.*?)(?=^\[|\z)')
$target = $null
foreach ($section in $sections) {
    if ($section.Groups[1].Value.Trim() -eq $Model) { $target = $section; break }
}
if (-not $target) {
    $known = ($sections | ForEach-Object { $_.Groups[1].Value.Trim() } | Where-Object { $_ -ne '*' }) -join ', '
    throw "no [$Model] section in $preset. Known models: $known"
}

# ---------------------------------------------------------------- workloads
# The same three used for the 2026-08-25 table in AGENTS.md, so numbers stay
# comparable. copy/agentic have heavy input->output overlap (where ngram-mod
# should win); novel has none (where it should merely tie the baseline).
$classCode = @'
public class InventoryItem
{
    private readonly string _sku;
    private int _quantity;
    private decimal _unitPrice;

    public InventoryItem(string sku, int quantity, decimal unitPrice)
    {
        _sku = sku ?? throw new ArgumentNullException(nameof(sku));
        _quantity = quantity;
        _unitPrice = unitPrice;
    }

    public string Sku => _sku;

    public int Quantity
    {
        get => _quantity;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _quantity = value;
        }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (value < 0m) throw new ArgumentOutOfRangeException(nameof(value));
            _unitPrice = value;
        }
    }

    public decimal TotalValue => _quantity * _unitPrice;

    public bool IsDepleted() => _quantity == 0;
}
'@

$promptCopy    = "Repeat the following C# class back to me verbatim in a single code block, changing only the class name from InventoryItem to StockItem. Output the code block and nothing else.`n`n$classCode"
$promptAgentic = "Repeat the following C# class back to me in a single code block, keeping every existing member exactly as written, and add two members: a method ApplyDiscount(decimal percent) that reduces UnitPrice, and an override of ToString(). Output the full class and nothing else.`n`n$classCode"
$promptNovel   = "Write exactly 400 words of original prose describing a lighthouse keeper's final night on duty before the light is automated. Do not restate this instruction."

$workloads = @(
    @{ Name = 'copy';    Prompt = $promptCopy },
    @{ Name = 'agentic'; Prompt = $promptAgentic },
    @{ Name = 'novel';   Prompt = $promptNovel }
)

# ---------------------------------------------------------------- helpers
function Get-Median([double[]]$values) {
    if ($values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

function New-SpecPreset([string]$config) {
    $sectionText = $target.Value
    # Drop whatever spec keys the section already carries, then append our own.
    $sectionText = [regex]::Replace($sectionText, '(?m)^[ \t]*spec-(type|ngram-mod-n-(min|max|match))[ \t]*=[^\r\n]*\r?\n', '')
    $sectionText = $sectionText.TrimEnd() + "`r`n"
    if ($config -ne 'none') {
        $p = $config.Split('/')
        $sectionText += "spec-type              = ngram-mod`r`n"
        $sectionText += "spec-ngram-mod-n-match = $($p[0])`r`n"
        $sectionText += "spec-ngram-mod-n-max   = $($p[1])`r`n"
        $sectionText += "spec-ngram-mod-n-min   = $($p[2])`r`n"
    }
    $sectionText += "`r`n"

    $tempPreset = Join-Path $tmp ('spec-' + [guid]::NewGuid().ToString('N') + '.ini')
    $tempText = $baseText.Substring(0, $target.Index) + $sectionText + $baseText.Substring($target.Index + $target.Length)
    Set-Content -Path $tempPreset -Value $tempText -Encoding utf8
    return $tempPreset
}

function Stop-BenchServer($process) {
    if ($process -and -not $process.HasExited) {
        & taskkill /PID $process.Id /T /F 2>$null | Out-Null
    }
    Start-Sleep -Milliseconds 800
}

function Invoke-Generation([string]$base, [string]$prompt) {
    $body = @{
        model       = $Model
        messages    = @(@{ role = 'user'; content = $prompt })
        temperature = 0      # greedy. NOTE: spec decoding changes batch shapes, so
                             # float reduction order shifts and greedy output still
                             # diverges between arms -- hence -IgnoreEos below.
        max_tokens  = $MaxTokens
        ignore_eos  = [bool]$IgnoreEos
        stream      = $false
    } | ConvertTo-Json -Depth 6 -Compress

    $response = Invoke-RestMethod "$base/v1/chat/completions" -Method Post `
        -ContentType 'application/json' -Body $body -TimeoutSec 600
    return $response.timings
}

# ---------------------------------------------------------------- free the GPU
try {
    $routerBase = "http://127.0.0.1:$RouterPort"
    $listed = Invoke-RestMethod "$routerBase/models" -TimeoutSec 3
    foreach ($entry in $listed.data) {
        $unloadBody = @{ model = $entry.id } | ConvertTo-Json -Compress
        try {
            Invoke-RestMethod "$routerBase/models/unload" -Method Post `
                -ContentType 'application/json' -Body $unloadBody -TimeoutSec 120 | Out-Null
        } catch { }
    }
    Write-Host "unloaded models on the production router (port $RouterPort)" -ForegroundColor DarkGray
} catch {
    Write-Host "no production router reachable on port $RouterPort - continuing" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- run
if (-not (Test-Path $OutFile)) {
    Set-Content -Path $OutFile -Encoding utf8 `
        -Value 'timestamp,model,spec_type,n_match,n_max,n_min,workload,tok_s,completion_tokens,draft_n,draft_accepted'
}

$summary = @()
foreach ($config in $Configs) {
    $specType = if ($config -eq 'none') { 'none' } else { 'ngram-mod' }
    $nMatch = ''; $nMax = ''; $nMin = ''
    if ($config -ne 'none') { $parts = $config.Split('/'); $nMatch = $parts[0]; $nMax = $parts[1]; $nMin = $parts[2] }

    $label = if ($config -eq 'none') { '' } else { "match=$nMatch max=$nMax min=$nMin" }
    Write-Host "`n=== $Model  spec=$specType $label" -ForegroundColor Cyan

    $tempPreset = New-SpecPreset $config
    $stdout = Join-Path $tmp 'server.out.log'
    $stderr = Join-Path $tmp 'server.err.log'
    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue

    # --parallel 3 matches scripts\start-llama.ps1 (NOT probe-ctx-headroom.ps1's 2).
    $serverArgs = @('--models-dir', $models, '--models-max', '1', '--parallel', '3',
                    '--kv-unified', '--port', $Port, '--host', '127.0.0.1',
                    '--models-preset', $tempPreset)
    $process = Start-Process -FilePath $bin -ArgumentList $serverArgs `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

    try {
        $base = "http://127.0.0.1:$Port"
        $healthy = $false
        for ($i = 0; $i -lt 120; $i++) {
            try { Invoke-RestMethod "$base/health" -TimeoutSec 2 | Out-Null; $healthy = $true; break } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $healthy) { Write-Host '  router failed to start - skipping' -ForegroundColor Red; continue }

        $loadBody = @{ model = $Model } | ConvertTo-Json -Compress
        try {
            Invoke-RestMethod "$base/models/load" -Method Post -ContentType 'application/json' -Body $loadBody -TimeoutSec 600 | Out-Null
        } catch {
            if ($_.ErrorDetails.Message -notmatch 'already running') { Write-Host '  model load failed - skipping' -ForegroundColor Red; continue }
        }

        $encoded = [uri]::EscapeDataString($Model)
        $ready = $false
        for ($i = 0; $i -lt 600; $i++) {
            try {
                $props = Invoke-RestMethod "$base/props?model=$encoded" -TimeoutSec 5
                if ($props.default_generation_settings.n_ctx) { $ready = $true; break }
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { Write-Host '  model never became ready - skipping' -ForegroundColor Red; continue }

        foreach ($workload in $workloads) {
            $rates = @(); $tokens = 0; $draftN = 0; $draftAcc = 0
            # First rep is a discarded warm-up: it pays prompt processing and cache setup.
            Invoke-Generation $base $workload.Prompt | Out-Null
            for ($r = 0; $r -lt $Reps; $r++) {
                $t = Invoke-Generation $base $workload.Prompt
                $rates += [double]$t.predicted_per_second
                $tokens = [int]$t.predicted_n
                $draftN += [int]$t.draft_n
                $draftAcc += [int]$t.draft_n_accepted
            }
            $median = [math]::Round((Get-Median $rates), 1)
            $accPct = if ($draftN -gt 0) { [math]::Round(100 * $draftAcc / $draftN, 1) } else { 0 }
            Write-Host ("  {0,-8} {1,7} tok/s  ({2} tokens, draft acceptance {3}%)" -f $workload.Name, $median, $tokens, $accPct)

            Add-Content -Path $OutFile -Encoding utf8 -Value (
                '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}' -f `
                (Get-Date -Format 's'), $Model, $specType, $nMatch, $nMax, $nMin,
                $workload.Name, $median, $tokens, $draftN, $draftAcc)

            $summary += [pscustomobject]@{ Config = $config; Workload = $workload.Name; TokS = $median }
        }
    } finally {
        Stop-BenchServer $process
        Remove-Item $tempPreset -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- ranking
Write-Host "`n=== ranking for $Model (summed tok/s across workloads)" -ForegroundColor Cyan
$summary | Group-Object Config | ForEach-Object {
    [pscustomobject]@{
        Config = $_.Name
        Total  = [math]::Round((($_.Group | Measure-Object TokS -Sum).Sum), 1)
        Detail = (($_.Group | ForEach-Object { "$($_.Workload)=$($_.TokS)" }) -join '  ')
    }
} | Sort-Object Total -Descending | Format-Table -AutoSize

Write-Host "results appended to $OutFile" -ForegroundColor DarkGray
