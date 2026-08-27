$ErrorActionPreference = "Stop"

$projectDir = Join-Path $PSScriptRoot "src\EggContributionBot"
$exe = Join-Path $projectDir "bin\Debug\net10.0\EggContributionBot.exe"
$config = Join-Path $projectDir "appsettings.json"
$workerDir = Join-Path $PSScriptRoot "tools\ei_worker"
$workerProcess = $null

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

$workerMagic = [Environment]::GetEnvironmentVariable("EI_WORKER_MAGIC", "User")
$workerIndex = [Environment]::GetEnvironmentVariable("EI_WORKER_INDEX", "User")
$workerMarker = [Environment]::GetEnvironmentVariable("EI_WORKER_MARKER", "User")
$workerReady = (Test-Path -LiteralPath (Join-Path $workerDir "node_modules\wrangler\bin\wrangler.js")) -and
    -not [string]::IsNullOrWhiteSpace($workerMagic) -and
    -not [string]::IsNullOrWhiteSpace($workerIndex) -and
    -not [string]::IsNullOrWhiteSpace($workerMarker)

if($workerReady) {
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if([string]::IsNullOrWhiteSpace($node)) {
        $node = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"
    }

    if(Test-Path -LiteralPath $node) {
        $env:MAGIC = $workerMagic
        $env:INDEX = $workerIndex
        $env:MARKER = $workerMarker
        $env:EGG_INC_WORKER_URL = "http://127.0.0.1:8787/"
        $workerOut = Join-Path $workerDir "worker.out.log"
        $workerErr = Join-Path $workerDir "worker.err.log"
        $workerProcess = Start-Process -FilePath $node -ArgumentList @(
            "node_modules\wrangler\bin\wrangler.js",
            "dev",
            "--ip",
            "127.0.0.1",
            "--port",
            "8787"
        ) -WorkingDirectory $workerDir -RedirectStandardOutput $workerOut -RedirectStandardError $workerErr -PassThru -WindowStyle Hidden
        Write-Host "Started the local Egg Inc worker on http://127.0.0.1:8787/."
    } else {
        Write-Warning "The local Egg Inc worker is configured, but Node.js could not be found."
    }
} else {
    Write-Warning "Season CS is disabled. The local worker needs dependencies plus EI_WORKER_MAGIC, EI_WORKER_INDEX, and EI_WORKER_MARKER."
}

try {
    Write-Host "Starting Egg Contribution Bot. Leave this window open while the bot is running."
    Write-Host "Press Ctrl+C to stop it."
    & $exe
} finally {
    if($workerProcess -and -not $workerProcess.HasExited) {
        Stop-Process -Id $workerProcess.Id
    }
}
