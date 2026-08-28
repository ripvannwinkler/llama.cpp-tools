# Compare no speculation, draft-MTP, and an external DFlash2 drafter.
# The script uses an isolated temporary preset/server and never edits models.ini.
# Results are ranked by completion tok/s; acceptance is recorded but is not the goal.
#
# Example:
#   .\scripts\bench-dflash2.ps1 -Model Qwen3.8-27B-NVFP4-Quality-v2 `
#     -DflashModel D:\llama.cpp\models\Qwen3.8-27B-DFlash2\dflash-Qwen3.8-27B-DFlash2-Q8_0.gguf
param(
    [Parameter(Mandatory = $true)][string]$Model,
    [Parameter(Mandatory = $true)][string]$DflashModel,
    [int]$Port = 8093,
    [int]$Reps = 5,
    [int]$MaxTokens = 1024,
    [int[]]$DflashNMax = @(4, 6, 7, 8),
    [switch]$IgnoreEos,
    [string]$OutFile = 'D:\llama.cpp\dflash2-results.csv'
)

$ErrorActionPreference = 'Stop'
$root = 'D:\llama.cpp'
$bin = Join-Path $root 'src\build\bin\llama-server.exe'
$preset = Join-Path $root 'models.ini'
$models = Join-Path $root 'models'
$tmp = Join-Path $env:TEMP 'llama-dflash2-bench'

if (-not (Test-Path $bin)) { throw "llama-server not found: $bin" }
if (-not (Test-Path $DflashModel)) { throw "DFlash2 model not found: $DflashModel" }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$baseText = Get-Content -Raw -Path $preset
$sections = [regex]::Matches($baseText, '(?ms)^\[([^\r\n]+)\]\r?\n(.*?)(?=^\[|\z)')
$target = $null
foreach ($section in $sections) {
    if ($section.Groups[1].Value.Trim() -eq $Model) { $target = $section; break }
}
if (-not $target) { throw "no [$Model] section in $preset" }

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
    public int Quantity { get => _quantity; set => _quantity = value; }
    public decimal UnitPrice { get => _unitPrice; set => _unitPrice = value; }
    public decimal TotalValue => _quantity * _unitPrice;
    public bool IsDepleted() => _quantity == 0;
}
'@
$workloads = @(
    @{ Name = 'copy'; Prompt = "Repeat this C# class verbatim, changing only InventoryItem to StockItem. Output code only.`n`n$classCode" },
    @{ Name = 'agentic'; Prompt = "Repeat this C# class, preserve all members, and add ApplyDiscount(decimal percent) plus ToString(). Output code only.`n`n$classCode" },
    @{ Name = 'novel'; Prompt = 'Write exactly 400 words of original prose about a lighthouse keeper final night before automation. Do not restate this instruction.' }
)

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0 }
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

function New-Preset([string]$Config) {
    $sectionText = $target.Value
    $sectionText = [regex]::Replace($sectionText, '(?m)^[ \t]*spec-(type|draft-model|draft-n-max|draft-p-min)[ \t]*=[^\r\n]*\r?\n', '')
    $sectionText = $sectionText.TrimEnd() + "`r`n"
    if ($Config -eq 'draft-mtp') {
        $sectionText += "spec-type          = draft-mtp`r`n"
        $sectionText += "spec-draft-n-max   = 3`r`n"
        $sectionText += "spec-draft-p-min   = 0`r`n"
    } elseif ($Config.StartsWith('dflash/')) {
        $nMax = $Config.Substring(7)
        $sectionText += "spec-type          = draft-dflash`r`n"
        $sectionText += "spec-draft-model   = $DflashModel`r`n"
        $sectionText += "spec-draft-n-max   = $nMax`r`n"
        $sectionText += "spec-draft-p-min   = 0`r`n"
    }
    $sectionText += "`r`n"
    $temp = Join-Path $tmp ('preset-' + [guid]::NewGuid().ToString('N') + '.ini')
    Set-Content -Path $temp -Value ($baseText.Substring(0, $target.Index) + $sectionText + $baseText.Substring($target.Index + $target.Length)) -Encoding utf8
    return $temp
}

function Stop-Server($Process) {
    if ($Process -and -not $Process.HasExited) { & taskkill /PID $Process.Id /T /F 2>$null | Out-Null }
    Start-Sleep -Milliseconds 800
}

function Invoke-Generation([string]$Base, [string]$Prompt) {
    $body = @{ model = $Model; messages = @(@{ role = 'user'; content = $Prompt }); temperature = 0; max_tokens = $MaxTokens; ignore_eos = [bool]$IgnoreEos; stream = $false } | ConvertTo-Json -Depth 6 -Compress
    return Invoke-RestMethod "$Base/v1/chat/completions" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 600
}

if (-not (Test-Path $OutFile)) {
    Set-Content -Path $OutFile -Encoding utf8 -Value 'timestamp,model,config,workload,tok_s,completion_tokens,draft_n,draft_accepted,finish_reason'
}
$configs = @('none', 'draft-mtp') + @($DflashNMax | ForEach-Object { "dflash/$_" })
$summary = @()

foreach ($config in $configs) {
    $tempPreset = New-Preset $config
    $stdout = Join-Path $tmp 'server.out.log'
    $stderr = Join-Path $tmp 'server.err.log'
    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
    $serverArgs = @('--models-dir', $models, '--models-max', '1', '--parallel', '1', '--kv-unified', '--port', $Port, '--host', '127.0.0.1', '--models-preset', $tempPreset)
    $process = Start-Process -FilePath $bin -ArgumentList $serverArgs -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        $base = "http://127.0.0.1:$Port"
        $healthy = $false
        for ($i = 0; $i -lt 120; $i++) { try { Invoke-RestMethod "$base/health" -TimeoutSec 2 | Out-Null; $healthy = $true; break } catch { Start-Sleep -Milliseconds 500 } }
        if (-not $healthy) { throw "server did not become healthy; see $stderr" }
        $load = @{ model = $Model } | ConvertTo-Json -Compress
        try { Invoke-RestMethod "$base/models/load" -Method Post -ContentType 'application/json' -Body $load -TimeoutSec 600 | Out-Null } catch { if ($_.ErrorDetails.Message -notmatch 'already running') { throw } }
        $encoded = [uri]::EscapeDataString($Model)
        $ready = $false
        for ($i = 0; $i -lt 600; $i++) { try { $props = Invoke-RestMethod "$base/props?model=$encoded" -TimeoutSec 5; if ($props.default_generation_settings.n_ctx) { $ready = $true; break } } catch { Start-Sleep -Milliseconds 500 } }
        if (-not $ready) { throw "model never became ready; see $stderr" }
        Write-Host "`n=== $config" -ForegroundColor Cyan
        foreach ($workload in $workloads) {
            Invoke-Generation $base $workload.Prompt | Out-Null
            $rates = @(); $tokens = 0; $draftN = 0; $draftAccepted = 0; $finish = ''
            for ($r = 0; $r -lt $Reps; $r++) {
                $response = Invoke-Generation $base $workload.Prompt
                $t = $response.timings
                $rates += [double]$t.predicted_per_second
                $tokens = [int]$t.predicted_n
                $draftN += [int]$t.draft_n
                $draftAccepted += [int]$t.draft_n_accepted
                $finish = $response.choices[0].finish_reason
            }
            $median = [math]::Round((Get-Median $rates), 1)
            Write-Host ("  {0,-8} {1,7} tok/s  ({2} tokens, accepted {3}/{4})" -f $workload.Name, $median, $tokens, $draftAccepted, $draftN)
            Add-Content -Path $OutFile -Encoding utf8 -Value ('{0},{1},{2},{3},{4},{5},{6},{7},{8}' -f (Get-Date -Format 's'), $Model, $config, $workload.Name, $median, $tokens, $draftN, $draftAccepted, $finish)
            $summary += [pscustomobject]@{ Config = $config; Workload = $workload.Name; TokS = $median }
        }
    } finally { Stop-Server $process; Remove-Item $tempPreset -ErrorAction SilentlyContinue }
}

Write-Host "`n=== ranking (sum of workload medians)" -ForegroundColor Cyan
$summary | Group-Object Config | ForEach-Object { [pscustomobject]@{ Config = $_.Name; TotalTokS = [math]::Round((($_.Group | Measure-Object TokS -Sum).Sum), 1); Detail = (($_.Group | ForEach-Object { "$($_.Workload)=$($_.TokS)" }) -join '  ') } } | Sort-Object TotalTokS -Descending | Format-Table -AutoSize
Write-Host "Results appended to $OutFile" -ForegroundColor DarkGray
