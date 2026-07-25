param(
    [string]$Configuration = "Release",
    [string]$OutputPath = "./publish"
)

dotnet build -c $Configuration -o $OutputPath
