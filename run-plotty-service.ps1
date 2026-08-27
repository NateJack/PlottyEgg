$ErrorActionPreference = "Continue"

$projectDir = Join-Path $PSScriptRoot "src\EggContributionBot"
$exe = Join-Path $projectDir "bin\Debug\net10.0\EggContributionBot.exe"
$stdout = Join-Path $projectDir "run.out.log"
$stderr = Join-Path $projectDir "run.err.log"
$serviceLog = Join-Path $projectDir "service.log"

while($true) {
    try {
        $existing = Get-Process -Name "EggContributionBot" -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $exe } |
            Select-Object -First 1
        if($existing) {
            Add-Content -LiteralPath $serviceLog -Value "$(Get-Date -Format o) Plotty is already running as PID $($existing.Id)."
            Wait-Process -Id $existing.Id -ErrorAction SilentlyContinue
            continue
        }

        if(-not (Test-Path -LiteralPath $exe)) {
            Add-Content -LiteralPath $serviceLog -Value "$(Get-Date -Format o) Plotty executable is missing; retrying in 30 seconds."
            Start-Sleep -Seconds 30
            continue
        }

        Add-Content -LiteralPath $serviceLog -Value "$(Get-Date -Format o) Starting Plotty."
        $process = Start-Process `
            -FilePath $exe `
            -WorkingDirectory $projectDir `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -PassThru `
            -WindowStyle Hidden
        $process.WaitForExit()
        Add-Content -LiteralPath $serviceLog -Value "$(Get-Date -Format o) Plotty exited with code $($process.ExitCode); restarting in 10 seconds."
    } catch {
        Add-Content -LiteralPath $serviceLog -Value "$(Get-Date -Format o) Watchdog error: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 10
}
