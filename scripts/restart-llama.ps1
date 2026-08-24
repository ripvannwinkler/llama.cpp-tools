param(
    [int]$Port = 8080,
    [int]$MaxModels = 1,
    [int]$Ctx = 0,
    [string]$LogFile = 'D:\llama.cpp\server.err.log'
)

$ErrorActionPreference = 'Stop'

& "$PSScriptRoot\stop-llama.ps1" -Port $Port
& "$PSScriptRoot\start-llama.ps1" -Port $Port -MaxModels $MaxModels -Ctx $Ctx -LogFile $LogFile
