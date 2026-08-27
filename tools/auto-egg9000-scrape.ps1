[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Url,

    [string]$OutDir = "src/EggContributionBot/data/e9k-browser-scrapes",
    [int]$Port = 9222,
    [string]$UserDataDir = "$env:LOCALAPPDATA\PlottyEgg9000Chrome",
    [string]$ChromePath = "",
    [int]$RefreshDelaySeconds = 5,
    [switch]$CloseWhenDone
)

$ErrorActionPreference = "Stop"

function Find-Chrome {
    param([string]$ConfiguredPath)

    if($ConfiguredPath -and (Test-Path -LiteralPath $ConfiguredPath)) {
        return $ConfiguredPath
    }

    $candidates = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    )

    foreach($candidate in $candidates) {
        if($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Could not find chrome.exe. Install Google Chrome or pass -ChromePath."
}

function Test-DebugPort {
    param([int]$Port)

    try {
        Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/version" -TimeoutSec 2 | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Start-DebugChrome {
    param(
        [string]$ChromePath,
        [string]$UserDataDir,
        [int]$Port
    )

    New-Item -ItemType Directory -Force -Path $UserDataDir | Out-Null
    $arguments = @(
        "--remote-debugging-port=$Port",
        "--user-data-dir=$UserDataDir",
        "--no-first-run",
        "--disable-popup-blocking",
        "about:blank"
    )
    Start-Process -FilePath $ChromePath -ArgumentList $arguments | Out-Null

    $deadline = (Get-Date).AddSeconds(20)
    while((Get-Date) -lt $deadline) {
        if(Test-DebugPort -Port $Port) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Chrome remote debugging did not start on port $Port."
}

function New-CdpTab {
    param(
        [int]$Port,
        [string]$TargetUrl
    )

    $encoded = [Uri]::EscapeDataString($TargetUrl)
    try {
        return Invoke-RestMethod -Method Put -Uri "http://127.0.0.1:$Port/json/new?$encoded" -TimeoutSec 10
    } catch {
        return Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/new?$encoded" -TimeoutSec 10
    }
}

function Stop-DebugChrome {
    param([int]$Port)

    try {
        Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/json/close" -TimeoutSec 5 | Out-Null
    } catch {
        try {
            $tabs = @(Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/list" -TimeoutSec 5)
            foreach($tab in $tabs) {
                if($tab.id) {
                    try {
                        Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/close/$($tab.id)" -TimeoutSec 5 | Out-Null
                    } catch {
                        # Best effort; any remaining Chrome window can be closed manually.
                    }
                }
            }
        } catch {
            # Best effort; avoid failing a successful scrape just because Chrome stayed open.
        }
    }
}

function Invoke-Cdp {
    param(
        [string]$WebSocketUrl,
        [string]$Method,
        [hashtable]$Params
    )

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $socket.ConnectAsync([Uri]$WebSocketUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    try {
        $id = [int](Get-Random -Minimum 1000 -Maximum 999999)
        $message = @{
            id = $id
            method = $Method
            params = $Params
        } | ConvertTo-Json -Depth 20 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($message)
        $segment = [ArraySegment[byte]]::new($bytes)
        $socket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

        $buffer = New-Object byte[] 1048576
        while($true) {
            $builder = [Text.StringBuilder]::new()
            do {
                $receiveSegment = [ArraySegment[byte]]::new($buffer)
                $result = $socket.ReceiveAsync($receiveSegment, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
                if($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                    throw "Chrome DevTools websocket closed unexpectedly."
                }

                [void]$builder.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
            } while(-not $result.EndOfMessage)

            $response = $builder.ToString() | ConvertFrom-Json
            if($response.id -eq $id) {
                return $response
            }
        }
    } finally {
        if($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $socket.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }

        $socket.Dispose()
    }
}

function Save-ContractScrape {
    param(
        [string]$WebSocketUrl,
        [string]$OutDir,
        [int]$RefreshDelaySeconds
    )

    Invoke-Cdp -WebSocketUrl $WebSocketUrl -Method "Page.enable" -Params @{} | Out-Null
    Invoke-Cdp -WebSocketUrl $WebSocketUrl -Method "Page.reload" -Params @{ ignoreCache = $true } | Out-Null
    Start-Sleep -Seconds ([Math]::Max(0, $RefreshDelaySeconds))

    $expression = @'
(async () => {
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
  function extractArrayAfter(text, marker) {
    const markerIndex = text.indexOf(marker);
    if (markerIndex < 0) return null;
    const start = text.indexOf("[", markerIndex);
    if (start < 0) return null;
    let depth = 0, inString = false, escaped = false;
    for (let i = start; i < text.length; i++) {
      const c = text[i];
      if (inString) {
        if (escaped) escaped = false;
        else if (c === "\\") escaped = true;
        else if (c === "\"") inString = false;
        continue;
      }
      if (c === "\"") inString = true;
      else if (c === "[") depth++;
      else if (c === "]" && --depth === 0) return text.slice(start, i + 1);
    }
    return null;
  }

  const deadline = Date.now() + 30000;
  let raw = null;
  while (Date.now() < deadline) {
    if (document.readyState !== "complete") {
      await sleep(500);
      continue;
    }

    const scripts = [...document.scripts].map(script => script.textContent || "").join("\n");
    raw = extractArrayAfter(scripts, "const _activeRaw =");
    if (raw) break;
    await sleep(500);
  }

  if (!raw) {
    return JSON.stringify({ ok: false, error: "Could not find EGG9000 active co-op data on this page.", url: location.href, title: document.title });
  }

  const url = new URL(location.href);
  const contractId = url.searchParams.get("ContractId") || "";
  const league = Number(url.searchParams.get("League") || "5");
  const coops = JSON.parse(raw);
  const players = coops.flatMap(coop => (coop.Users || [])
    .filter(user => user.Name)
    .map(user => ({
      name: String(user.Name || ""),
      guildTag: user.Guild ? String(user.Guild) : null,
      chickens: String(user.NumChickens || ""),
      rate: String(user.Rate || ""),
      projected: String(user.Projected || ""),
      joined: String(user.Status || "").includes("\u2714") || String(user.Status || "").includes("\u2705"),
      coopStatus: String(coop.Status || ""),
      hoursToFinish: coop.HoursToFinish !== null && coop.HoursToFinish !== undefined && Number.isFinite(Number(coop.HoursToFinish)) ? Number(coop.HoursToFinish) : null,
      coopFinished: String(coop.Status || "").toLowerCase() === "finished" ||
        String(coop.Status || "").toLowerCase().startsWith("complete once") ||
        (coop.HoursToFinish !== null && coop.HoursToFinish !== undefined && Number.isFinite(Number(coop.HoursToFinish)) && Number(coop.HoursToFinish) <= 0)
    })));

  return JSON.stringify({
    ok: true,
    payload: {
      contractId,
      league,
      exportedAt: new Date().toISOString(),
      sourceUrl: location.href,
      players
    }
  });
})()
'@

    $response = Invoke-Cdp -WebSocketUrl $WebSocketUrl -Method "Runtime.evaluate" -Params @{
        expression = $expression
        awaitPromise = $true
        returnByValue = $true
    }

    $value = $response.result.result.value
    if(-not $value) {
        throw "Chrome did not return a scrape payload."
    }

    $result = $value | ConvertFrom-Json
    if(-not $result.ok) {
        throw $result.error
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $contractId = [string]$result.payload.contractId
    $league = [int]$result.payload.league
    $safeContractId = $contractId -replace '[^A-Za-z0-9_.-]', '_'
    $outPath = Join-Path $OutDir "$safeContractId-league-$league.json"
    $result.payload | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $outPath -Encoding UTF8
    $playerCount = @($result.payload.players).Count
    "Saved $playerCount players for $contractId league $league to $outPath"
}

$urls = $Url |
    ForEach-Object { $_ -split '\|' } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$chrome = Find-Chrome -ConfiguredPath $ChromePath
if(-not (Test-DebugPort -Port $Port)) {
    Start-DebugChrome -ChromePath $chrome -UserDataDir $UserDataDir -Port $Port
}

try {
    foreach($target in $urls) {
        if([string]::IsNullOrWhiteSpace($target)) {
            continue
        }

        $tab = New-CdpTab -Port $Port -TargetUrl $target
        if(-not $tab.webSocketDebuggerUrl) {
            throw "Chrome did not return a DevTools websocket URL for $target"
        }

        Save-ContractScrape -WebSocketUrl $tab.webSocketDebuggerUrl -OutDir $OutDir -RefreshDelaySeconds $RefreshDelaySeconds
    }
} finally {
    if($CloseWhenDone) {
        Stop-DebugChrome -Port $Port
    }
}
