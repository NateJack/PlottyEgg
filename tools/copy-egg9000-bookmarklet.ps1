$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "export-egg9000-contract-page.js"
$script = Get-Content -Raw -LiteralPath $scriptPath
$singleLine = ($script -replace '/\*[\s\S]*?\*/', '' -replace '\r?\n\s*', ' ' -replace '\s{2,}', ' ').Trim()
$bookmarklet = "javascript:$singleLine"

Set-Clipboard -Value $bookmarklet

"Copied the EGG9000 -> Plotty bookmarklet to your clipboard."
"Create a browser bookmark and paste this as the bookmark URL."
"Suggested bookmark name: Save EGG9000 to Plotty"
