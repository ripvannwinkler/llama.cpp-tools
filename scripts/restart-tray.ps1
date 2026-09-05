# Stop LlamaTray process tree
Get-Process -Name "LlamaTray" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -match 'D:\\llama\.cpp\\tray\\LlamaTray\\' } |
    ForEach-Object {
        Write-Host "Stopping $($_.ProcessName) ($($_.Id))..."
        Stop-Process $_.Id -Force -Confirm:$false
    }
Write-Host "Waiting for shutdown..."
Start-Sleep -Seconds 1

# Start tray via publish dir
$exe = Join-Path $PSScriptRoot "..\tray\LlamaTray\publish\LlamaTray.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Rebuilding..."
    dotnet build "${PSScriptRoot}\..\tray\LlamaTray\LlamaTray.csproj" -c Release
}
& $exe
