# Probe the largest context that leaves the requested dedicated VRAM headroom.
# Uses a temporary router preset so each candidate includes the model's mmproj,
# speculative drafter, KV types, and all other per-model settings.
param(
    [int]$Port = 8091,
    [int]$HeadroomMiB = 2048,
    [int[]]$Candidates = @(8192,16384,24576,32768,40960,49152,57344,65536,73728,81920,90112,98304,106496,114688,122880,131072,139264,147456,155648,163840,172032,180224,188416,196608,204800,212992,221184,229376,237568,245760,253952,262144),
    [string]$ModelFilter
)

$ErrorActionPreference = 'Stop'
$root   = 'D:\llama.cpp'
$bin    = Join-Path $root 'src\build\bin\llama-server.exe'
$models = Join-Path $root 'models'
$preset = Join-Path $root 'models.ini'
$out    = Join-Path $root 'ctx-probe-headroom-results.txt'
$tmp    = Join-Path $env:TEMP 'llama-ctx-probe'

New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Remove-Item $out -ErrorAction SilentlyContinue
$baseText = Get-Content -Raw -Path $preset

# Probe one physical model directory once; low/medium/xhigh sections sharing the
# same GGUF have identical load-time KV/VRAM requirements.
$sections = [regex]::Matches($baseText, '(?ms)^\[([^\r\n]+)\]\r?\n(.*?)(?=^\[|\z)')
$targets = @()
$seenModels = @{}
foreach ($section in $sections) {
    $id = $section.Groups[1].Value.Trim()
    if ($id -eq '*') { continue }
    $modelMatch = [regex]::Match($section.Groups[2].Value, '(?m)^\s*model\s*=\s*(.+?)\s*$')
    if (-not $modelMatch.Success) { continue }
    $modelPath = $modelMatch.Groups[1].Value.Trim()
    if ($seenModels.ContainsKey($modelPath)) { continue }
    $seenModels[$modelPath] = $true
    if ($ModelFilter -and $id -notmatch [regex]::Escape($ModelFilter)) { continue }
    $targets += [pscustomobject]@{ Id = $id; ModelPath = $modelPath; Section = $section }
}

function Stop-ProbeServer($process) {
    if ($process -and -not $process.HasExited) {
        & taskkill /PID $process.Id /T /F 2>$null | Out-Null
    }
    Start-Sleep -Milliseconds 800
}

function Test-Candidate($target, [int]$ctx) {
    $sectionText = $target.Section.Value
    $sectionText = [regex]::Replace(
        $sectionText,
        '(?m)^(\s*ctx-size\s*=\s*)\d+',
        { param($m) $m.Groups[1].Value + $ctx },
        1
    )
    $tempPreset = Join-Path $tmp ('probe-' + [guid]::NewGuid().ToString('N') + '.ini')
    $tempText = $baseText.Substring(0, $target.Section.Index) +
        $sectionText + $baseText.Substring($target.Section.Index + $target.Section.Length)
    Set-Content -Path $tempPreset -Value $tempText -Encoding utf8

    $stdout = Join-Path $tmp 'server.out.log'
    $stderr = Join-Path $tmp 'server.err.log'
    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
    $args = @('--models-dir', $models, '--models-max', '1', '--parallel', '2',
              '--kv-unified', '--port', $Port, '--host', '127.0.0.1',
              '--models-preset', $tempPreset)
    $process = Start-Process -FilePath $bin -ArgumentList $args -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        $base = "http://127.0.0.1:$Port"
        $healthy = $false
        for ($i = 0; $i -lt 120; $i++) {
            try { Invoke-RestMethod "$base/health" -TimeoutSec 2 | Out-Null; $healthy = $true; break } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $healthy) { return [pscustomobject]@{ Fits = $false; Used = 0; Total = 0; Reason = 'router failed to start' } }

        $loadBody = @{ model = $target.Id } | ConvertTo-Json -Compress
        try {
            Invoke-RestMethod "$base/models/load" -Method Post -ContentType 'application/json' -Body $loadBody -TimeoutSec 300 | Out-Null
        } catch {
            $message = $_.ErrorDetails.Message
            if ($message -notmatch 'already running') {
                return [pscustomobject]@{ Fits = $false; Used = 0; Total = 0; Reason = 'model load failed' }
            }
        }

        $encoded = [uri]::EscapeDataString($target.Id)
        $ready = $false
        for ($i = 0; $i -lt 600; $i++) {
            try {
                $props = Invoke-RestMethod "$base/props?model=$encoded" -TimeoutSec 5
                if ($props.default_generation_settings.n_ctx) { $ready = $true; break }
            } catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { return [pscustomobject]@{ Fits = $false; Used = 0; Total = 0; Reason = 'model failed to become ready' } }

        Start-Sleep -Seconds 2
        $gpu = ((& nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader,nounits) | Select-Object -First 1).Trim().Split(',')
        $used = [int]$gpu[0].Trim()
        $total = [int]$gpu[1].Trim()
        [pscustomobject]@{ Fits = (($total - $used) -ge $HeadroomMiB); Used = $used; Total = $total; Reason = '' }
    } finally {
        Stop-ProbeServer $process
        Remove-Item $tempPreset -ErrorAction SilentlyContinue
    }
}

$results = @()
foreach ($target in $targets) {
    Write-Host "`n$($target.Id)" -ForegroundColor Cyan
    $lo = 0; $hi = $Candidates.Count - 1; $answer = $null
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        $ctx = $Candidates[$mid]
        Write-Host "  testing $ctx ..." -NoNewline
        $probe = Test-Candidate $target $ctx
        if ($probe.Used) {
            $free = $probe.Total - $probe.Used
            Write-Host " $($probe.Used)/$($probe.Total) MiB ($free MiB free)" -NoNewline
        }
        if ($probe.Fits) {
            Write-Host ' fits' -ForegroundColor Green
            $answer = [pscustomobject]@{ Context = $ctx; Used = $probe.Used; Total = $probe.Total }
            $lo = $mid + 1
        } else {
            Write-Host " does not meet ${HeadroomMiB} MiB headroom ($($probe.Reason))" -ForegroundColor Yellow
            $hi = $mid - 1
        }
    }
    if ($answer) {
        $line = "$($target.Id)=$($answer.Context) used=$($answer.Used) total=$($answer.Total) headroom=$($answer.Total - $answer.Used)"
        $results += $line
        $line | Tee-Object -FilePath $out -Append
    } else {
        $line = "$($target.Id)=NO-FIT"
        $results += $line
        $line | Tee-Object -FilePath $out -Append
    }
}
'DONE' | Tee-Object -FilePath $out -Append
