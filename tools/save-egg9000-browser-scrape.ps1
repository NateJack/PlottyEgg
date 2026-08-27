$ErrorActionPreference = "Stop"

$json = Get-Clipboard -Raw
if ([string]::IsNullOrWhiteSpace($json)) {
    throw "Clipboard is empty. Run tools/export-egg9000-contract-page.js in the EGG9000 page console first."
}

$payload = $json | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($payload.contractId)) {
    throw "Clipboard JSON does not include a contractId."
}

$league = if ($payload.league) { [int]$payload.league } else { 5 }
$safeContractId = $payload.contractId -replace '[^A-Za-z0-9_.-]', '_'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot "src\EggContributionBot\data\e9k-browser-scrapes"
$outPath = Join-Path $outDir "$safeContractId-league-$league.json"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$json | Set-Content -LiteralPath $outPath -Encoding UTF8

$plotChickenCount = @($payload.players | Where-Object { $_.guildTag -eq "The Plot Chickens" }).Count
"Saved EGG9000 browser scrape: $outPath"
"Players: $(@($payload.players).Count); The Plot Chickens: $plotChickenCount"
