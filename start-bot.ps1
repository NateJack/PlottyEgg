$ErrorActionPreference = "Stop"

$projectDir = Join-Path $PSScriptRoot "src\EggContributionBot"
$exe = Join-Path $projectDir "bin\Debug\net10.0\EggContributionBot.exe"
$config = Join-Path $projectDir "appsettings.json"

if(-not (Test-Path -LiteralPath $config)) {
    throw "Missing appsettings.json. Copy src\EggContributionBot\appsettings.example.json to appsettings.json and add your Discord token."
}

if(-not (Test-Path -LiteralPath $exe)) {
    Push-Location $PSScriptRoot
    try {
        dotnet build EggContributionBot.sln --no-restore
    } finally {
        Pop-Location
    }
}

Write-Host "Starting Egg Contribution Bot. Leave this window open while the bot is running."
Write-Host "Press Ctrl+C to stop it."
& $exe
