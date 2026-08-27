$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "export-egg9000-contract-page.js"
$script = Get-Content -Raw -LiteralPath $scriptPath
Set-Clipboard -Value $script

"Copied EGG9000 page exporter to clipboard."
"Start tools/receive-egg9000-browser-scrape.ps1, open the EGG9000 contract page, then paste this into the browser console."
