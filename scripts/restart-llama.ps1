param(
    [int]$Port = 8080,
    [int]$MaxModels = 1,
    [int]$Ctx = 0,
    [string]$LogFile = 'D:\llama.cpp\server.err.log',
    [switch]$WaitForHealth,
    [ValidateRange(1, 3600)]
    [int]$HealthTimeoutSec = 10,
    [ValidateRange(1, 30)]
    [int]$HealthRequestTimeoutSec = 2,
    [ValidateRange(50, 10000)]
    [int]$HealthPollMs = 700
)

$ErrorActionPreference = 'Stop'

& "$PSScriptRoot\stop-llama.ps1" -Port $Port

$startArgs = @{
    Port      = $Port
    MaxModels = $MaxModels
    Ctx       = $Ctx
    LogFile   = $LogFile
}

if ($WaitForHealth) {
    $startArgs.WaitForHealth = $true
    $startArgs.HealthTimeoutSec = $HealthTimeoutSec
    $startArgs.HealthRequestTimeoutSec = $HealthRequestTimeoutSec
    $startArgs.HealthPollMs = $HealthPollMs
}

& "$PSScriptRoot\start-llama.ps1" @startArgs
