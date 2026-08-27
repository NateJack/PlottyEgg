$ErrorActionPreference = "Stop"

$prefix = "http://127.0.0.1:5199/egg9000-scrape/"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot "src\EggContributionBot\data\e9k-browser-scrapes"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($prefix)
$listener.Start()

try {
    "Listening at $prefix"
    "Open the EGG9000 contract page and run tools/export-egg9000-contract-page.js in the browser console."
    "Press Ctrl+C to stop."

    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $response = $context.Response
        $response.Headers.Add("Access-Control-Allow-Origin", "https://egg9000.com")
        $response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS")
        $response.Headers.Add("Access-Control-Allow-Headers", "Content-Type")

        if ($context.Request.HttpMethod -eq "OPTIONS") {
            $response.StatusCode = 204
            $response.Close()
            continue
        }

        if ($context.Request.HttpMethod -ne "POST") {
            $response.StatusCode = 405
            $bytes = [Text.Encoding]::UTF8.GetBytes("Only POST is supported.")
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
            $response.Close()
            continue
        }

        $reader = [IO.StreamReader]::new($context.Request.InputStream, $context.Request.ContentEncoding)
        $json = $reader.ReadToEnd()
        $reader.Close()

        $payload = $json | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($payload.contractId)) {
            throw "Payload does not include a contractId."
        }

        $league = if ($payload.league) { [int]$payload.league } else { 5 }
        $safeContractId = $payload.contractId -replace '[^A-Za-z0-9_.-]', '_'
        $outPath = Join-Path $outDir "$safeContractId-league-$league.json"
        $json | Set-Content -LiteralPath $outPath -Encoding UTF8

        $plotChickenCount = @($payload.players | Where-Object { $_.guildTag -eq "The Plot Chickens" }).Count
        $message = "Saved $(@($payload.players).Count) players ($plotChickenCount Plot Chickens) to $outPath"
        $bytes = [Text.Encoding]::UTF8.GetBytes($message)
        $response.ContentType = "text/plain"
        $response.StatusCode = 200
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
        $response.Close()
        $message
    }
} finally {
    if ($listener.IsListening) {
        $listener.Stop()
    }

    $listener.Close()
}
