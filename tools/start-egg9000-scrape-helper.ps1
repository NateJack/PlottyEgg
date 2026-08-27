$ErrorActionPreference = "Stop"

$defaultUrl = "https://egg9000.com/Contract/Details?GuildId=656455567858073601&ContractId=largest-omelette-2021&League=5"
$url = if ($args.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($args[0])) { $args[0] } else { $defaultUrl }

& (Join-Path $PSScriptRoot "copy-egg9000-bookmarklet.ps1")

""
"Opening EGG9000. If you already created the bookmark, click 'Save EGG9000 to Plotty' on the contract page."
"Leave this PowerShell window open while exporting."
Start-Process $url

& (Join-Path $PSScriptRoot "receive-egg9000-browser-scrape.ps1")
