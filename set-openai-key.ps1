$secureKey = Read-Host "Paste a new OpenAI API key" -AsSecureString
$key = [System.Net.NetworkCredential]::new("", $secureKey).Password

try {
    if([string]::IsNullOrWhiteSpace($key) -or -not $key.StartsWith("sk-")) {
        throw "That does not look like an OpenAI API key."
    }

    [Environment]::SetEnvironmentVariable(
        "OPENAI_API_KEY",
        $key,
        [EnvironmentVariableTarget]::User)

    Write-Host "OpenAI API key saved to your Windows user environment."
    Write-Host "Restart Plotty to enable hosted AI chat."
} finally {
    $key = $null
    $secureKey.Dispose()
}
