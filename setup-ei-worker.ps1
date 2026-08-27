$ErrorActionPreference = "Stop"

$workerDir = Join-Path $PSScriptRoot "tools\ei_worker"
$repository = "https://github.com/tylertms/ei_worker.git"

if(-not (Test-Path -LiteralPath (Join-Path $workerDir "package.json"))) {
    New-Item -ItemType Directory -Path (Split-Path $workerDir) -Force | Out-Null
    git clone $repository $workerDir
}

$pnpm = (Get-Command pnpm -ErrorAction SilentlyContinue).Source
if([string]::IsNullOrWhiteSpace($pnpm)) {
    $pnpm = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\bin\fallback\pnpm.cmd"
}
if(-not (Test-Path -LiteralPath $pnpm)) {
    throw "pnpm was not found. Install Node.js 22 or newer, then run this script again."
}

Push-Location $workerDir
try {
    & $pnpm install --frozen-lockfile=false
} finally {
    Pop-Location
}

Write-Host "The local worker is installed."
Write-Host "Set EI_WORKER_MAGIC, EI_WORKER_INDEX, and EI_WORKER_MARKER as user environment variables, then restart Plotty with start-bot.ps1."
