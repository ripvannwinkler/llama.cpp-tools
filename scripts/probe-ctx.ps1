# probe-ctx.ps1 — find the max context that loads for each model at q8_0 KV cache.
# Probes each model directly with llama-cli (loads weights + allocates KV, then exits).
# Binary-searches a ladder of context sizes; writes "id=maxctx" lines to ctx-probe-results.txt.
$bench = 'D:\llama.cpp\src\build\bin\llama-bench.exe'
$root  = 'D:\llama.cpp\models'
$out  = 'D:\llama.cpp\ctx-probe-results.txt'
Remove-Item $out -ErrorAction SilentlyContinue

$cands = 8192,16384,24576,32768,49152,65536,98304,131072,196608,262144

# enumerate id -> main gguf (skip mmproj, prefer first shard)
$models = @()
Get-ChildItem $root -Directory | ForEach-Object {
    $gg   = Get-ChildItem $_.FullName -Filter *.gguf | Where-Object { $_.Name -notmatch 'mmproj' }
    $main = $gg | Where-Object { $_.Name -match '-00001-of-' } | Select-Object -First 1
    if (-not $main) { $main = $gg | Select-Object -First 1 }
    if ($main) { $models += [pscustomobject]@{ id = $_.Name; path = $main.FullName } }
}

function Fits($gguf, $ctx) {
    # llama-bench loads the model + allocates ~$ctx of KV (via -d), then self-exits.
    # exit 0 = fits, non-zero = OOM / too big.
    & $bench -m $gguf -d $ctx -p 0 -n 1 -ngl 99 -fa on -ctk q8_0 -ctv q8_0 -r 1 *> $null 2>&1
    return ($LASTEXITCODE -eq 0)
}

foreach ($m in $models) {
    $lo = 0; $hi = $cands.Count - 1; $ans = -1
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        if (Fits $m.path $cands[$mid]) { $ans = $mid; $lo = $mid + 1 } else { $hi = $mid - 1 }
    }
    $maxctx = if ($ans -ge 0) { $cands[$ans] } else { 0 }
    "$($m.id)=$maxctx" | Tee-Object -FilePath $out -Append
}
"DONE" | Out-File -FilePath $out -Append
