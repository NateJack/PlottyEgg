using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EggContribBot;

public sealed class Egg9000Client {
    private static readonly Regex RowRegex = new("<tr\\b[^>]*>(?<body>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CellRegex = new("<td\\b[^>]*>(?<body>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UserNameRegex = new("x-text=[\"']user\\.Name[\"'][^>]*>(?<value>.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UserGuildRegex = new("x-text=[\"']user\\.Guild[\"'][^>]*>(?<value>.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Singleline | RegexOptions.Compiled);
    private readonly Egg9000Settings? _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _leaderboardGate = new(1, 1);
    private IReadOnlyList<Egg9000LeaderboardItem>? _leaderboardCache;
    private DateTimeOffset _leaderboardCacheUntil;

    public Egg9000Client(Egg9000Settings? settings, HttpClient? httpClient = null) {
        _settings = settings;
        _http = httpClient ?? new HttpClient(new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public bool IsConfigured => _settings?.IsConfigured == true;

    public async Task<IReadOnlyList<Egg9000LeaderboardItem>> GetLeaderboardAsync(CancellationToken cancellationToken = default) {
        if(_settings is null || !_settings.IsConfigured) {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        if(_leaderboardCache is not null && _leaderboardCacheUntil > now) {
            return _leaderboardCache;
        }

        await _leaderboardGate.WaitAsync(cancellationToken);
        try {
            now = DateTimeOffset.UtcNow;
            if(_leaderboardCache is not null && _leaderboardCacheUntil > now) {
                return _leaderboardCache;
            }

            _leaderboardCache = await FetchLeaderboardAsync(cancellationToken);
            _leaderboardCacheUntil = _leaderboardCache.Count == 0
                ? now.AddSeconds(30)
                : now.AddMinutes(2);
            return _leaderboardCache;
        } finally {
            _leaderboardGate.Release();
        }
    }

    private async Task<IReadOnlyList<Egg9000LeaderboardItem>> FetchLeaderboardAsync(CancellationToken cancellationToken) {
        if(_settings is null) {
            return [];
        }

        var baseUri = new Uri(_settings.EffectiveBaseUrl, UriKind.Absolute);
        var uri = new Uri(baseUri, "Home/LeaderboardJson");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Api-Key", _settings.EffectiveApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        if(!response.IsSuccessStatusCode) {
            return [];
        }

        return await response.Content.ReadFromJsonAsync<List<Egg9000LeaderboardItem>>(cancellationToken) ?? [];
    }

    public async Task<Egg9000ContractScrape?> GetContractDetailsAsync(
        string contractId,
        CancellationToken cancellationToken = default) {
        if(_settings is null || string.IsNullOrWhiteSpace(contractId)) {
            return null;
        }

        var baseUri = new Uri(_settings.EffectiveBaseUrl, UriKind.Absolute);
        var path = $"Contract/Details?GuildId={Uri.EscapeDataString(_settings.EffectiveGuildId)}&ContractId={Uri.EscapeDataString(contractId)}&League={_settings.EffectiveLeague}";
        var uri = new Uri(baseUri, path);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("text/html");
        request.Headers.UserAgent.ParseAdd("Plotty/1.0 (+discord bot contract monitor)");
        if(!string.IsNullOrWhiteSpace(_settings.EffectiveApiKey)) {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _settings.EffectiveApiKey);
        }
        var cookieHeader = BuildCookieHeader(_settings);
        if(!string.IsNullOrWhiteSpace(cookieHeader)) {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if(!response.IsSuccessStatusCode) {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var hasEmbeddedContractData = html.Contains("const _activeRaw =", StringComparison.Ordinal);
        var players = ParseContractPlayers(html);
        if(players.Count == 0 || !hasEmbeddedContractData || LooksLikeLoginPage(html)) {
            var browserScrape = TryLoadBrowserScrape(contractId);
            if(browserScrape is not null) {
                return browserScrape;
            }
        }

        return new Egg9000ContractScrape(
            contractId,
            _settings.EffectiveLeague,
            players,
            LooksLikeLoginPage(html),
            hasEmbeddedContractData);
    }

    private Egg9000ContractScrape? TryLoadBrowserScrape(string contractId) {
        if(_settings is null) {
            return null;
        }

        var path = BrowserScrapePath(contractId);
        if(!File.Exists(path)) {
            return null;
        }

        try {
            var payload = JsonSerializer.Deserialize<BrowserContractScrapePayload>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if(payload is null ||
               !string.Equals(payload.ContractId, contractId, StringComparison.OrdinalIgnoreCase) ||
               DateTimeOffset.UtcNow - payload.ExportedAt > _settings.EffectiveBrowserScrapeMaxAge) {
                return null;
            }

            var players = payload.Players
                .Where(player => !string.IsNullOrWhiteSpace(player.Name))
                .Select(player => new Egg9000ContractPlayer(
                    player.Name.Trim(),
                    string.IsNullOrWhiteSpace(player.GuildTag) ? null : player.GuildTag.Trim(),
                    player.Chickens?.Trim() ?? "",
                    player.Rate?.Trim() ?? "",
                    ParseEggRatePerHour(player.Rate ?? ""),
                    player.Projected?.Trim() ?? "",
                    player.Joined,
                    player.CoopStatus?.Trim() ?? "",
                    player.CoopFinished || IsCompletedCoopStatus(player.CoopStatus, player.HoursToFinish)))
                .ToList();

            return new Egg9000ContractScrape(
                contractId,
                payload.League <= 0 ? _settings.EffectiveLeague : payload.League,
                players,
                LooksLikeLoginPage: false,
                HasEmbeddedContractData: true);
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    private string BrowserScrapePath(string contractId) {
        var safeContractId = Regex.Replace(contractId, @"[^A-Za-z0-9_.-]", "_");
        return Path.Combine(
            _settings!.EffectiveBrowserScrapeDataPath,
            $"{safeContractId}-league-{_settings.EffectiveLeague}.json");
    }

    private static IReadOnlyList<Egg9000ContractPlayer> ParseContractPlayers(string html) {
        var embeddedPlayers = ParseEmbeddedContractPlayers(html);
        if(embeddedPlayers.Count > 0) {
            return embeddedPlayers;
        }

        var players = new List<Egg9000ContractPlayer>();
        foreach(Match row in RowRegex.Matches(html)) {
            var cells = CellRegex.Matches(row.Groups["body"].Value)
                .Select(match => match.Groups["body"].Value)
                .ToList();
            if(cells.Count < 5) {
                continue;
            }

            var name = CleanHtmlValue(UserNameRegex.Match(cells[0]).Groups["value"].Value);
            if(string.IsNullOrWhiteSpace(name)) {
                name = CleanHtmlValue(cells[0]);
            }

            var guild = CleanHtmlValue(UserGuildRegex.Match(cells[0]).Groups["value"].Value);
            var firstCell = CleanHtmlValue(cells[0]);
            if(string.IsNullOrWhiteSpace(guild)) {
                var bracketStart = firstCell.LastIndexOf('[');
                var bracketEnd = firstCell.LastIndexOf(']');
                if(bracketStart >= 0 && bracketEnd > bracketStart) {
                    guild = firstCell[(bracketStart + 1)..bracketEnd].Trim();
                    if(string.IsNullOrWhiteSpace(name)) {
                        name = firstCell[..bracketStart].Trim();
                    }
                }
            }

            if(string.IsNullOrWhiteSpace(name) || string.Equals(name, "No Users", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var status = CleanHtmlValue(cells[4]);
            players.Add(new Egg9000ContractPlayer(
                name,
                string.IsNullOrWhiteSpace(guild) ? null : guild,
                CleanHtmlValue(cells[1]),
                CleanHtmlValue(cells[2]),
                ParseEggRatePerHour(CleanHtmlValue(cells[2])),
                CleanHtmlValue(cells[3]),
                IsJoinedStatus(status)));
        }

        return players;
    }

    private static IReadOnlyList<Egg9000ContractPlayer> ParseEmbeddedContractPlayers(string html) {
        var json = ExtractJsArrayAfter(html, "const _activeRaw =");
        if(string.IsNullOrWhiteSpace(json)) {
            return [];
        }

        try {
            using var document = JsonDocument.Parse(json);
            var players = new List<Egg9000ContractPlayer>();
            foreach(var coop in document.RootElement.EnumerateArray()) {
                var coopStatus = JsonString(coop, "Status");
                var hoursToFinish = JsonDouble(coop, "HoursToFinish");
                var coopFinished = IsCompletedCoopStatus(coopStatus, hoursToFinish);
                if(!coop.TryGetProperty("Users", out var users) || users.ValueKind != JsonValueKind.Array) {
                    continue;
                }

                foreach(var user in users.EnumerateArray()) {
                    var name = JsonString(user, "Name");
                    if(string.IsNullOrWhiteSpace(name)) {
                        continue;
                    }

                    var guild = JsonString(user, "Guild");
                    var rate = JsonString(user, "Rate");
                    var status = JsonString(user, "Status");
                    players.Add(new Egg9000ContractPlayer(
                        name,
                        string.IsNullOrWhiteSpace(guild) ? null : guild,
                        JsonString(user, "NumChickens"),
                        rate,
                        ParseEggRatePerHour(rate),
                        JsonString(user, "Projected"),
                        IsJoinedStatus(status),
                        coopStatus,
                        coopFinished));
                }
            }

            return players;
        } catch(JsonException) {
            return [];
        }
    }

    private static string? ExtractJsArrayAfter(string html, string marker) {
        var markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        if(markerIndex < 0) {
            return null;
        }

        var start = html.IndexOf('[', markerIndex);
        if(start < 0) {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for(var i = start; i < html.Length; i++) {
            var c = html[i];
            if(inString) {
                if(escaped) {
                    escaped = false;
                } else if(c == '\\') {
                    escaped = true;
                } else if(c == '"') {
                    inString = false;
                }

                continue;
            }

            if(c == '"') {
                inString = true;
                continue;
            }

            if(c == '[') {
                depth++;
            } else if(c == ']') {
                depth--;
                if(depth == 0) {
                    return html[start..(i + 1)];
                }
            }
        }

        return null;
    }

    private static string JsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : "";

    private static double? JsonDouble(JsonElement element, string propertyName) {
        if(!element.TryGetProperty(propertyName, out var property)) {
            return null;
        }

        if(property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)) {
            return number;
        }

        return double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    public static bool IsCompletedCoopStatus(string? status, double? hoursToFinish = null) {
        var normalized = status?.Trim() ?? "";
        return normalized.Equals("Finished", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Complete once", StringComparison.OrdinalIgnoreCase) ||
               hoursToFinish is <= 0;
    }

    private static string CleanHtmlValue(string value) {
        if(string.IsNullOrWhiteSpace(value)) {
            return "";
        }

        var withoutTags = HtmlTagRegex.Replace(value, "");
        return WebUtility.HtmlDecode(withoutTags).Replace("\u00a0", " ").Trim();
    }

    private static bool LooksLikeLoginPage(string html) =>
        html.Contains("ExternalLogin", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase);

    private static string? BuildCookieHeader(Egg9000Settings settings) {
        if(!string.IsNullOrWhiteSpace(settings.EffectiveCookieHeader)) {
            return NormalizeCookieHeader(settings.EffectiveCookieHeader);
        }

        var sessionCookie = settings.EffectiveSessionCookie?.Trim();
        if(string.IsNullOrWhiteSpace(sessionCookie)) {
            return null;
        }

        return sessionCookie.Contains('=') || sessionCookie.Contains(';')
            ? NormalizeCookieHeader(sessionCookie)
            : $"{settings.EffectiveSessionCookieName}={sessionCookie}";
    }

    private static string NormalizeCookieHeader(string value) {
        var cookie = value.Trim();
        if(cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) {
            cookie = cookie["Cookie:".Length..].Trim();
        }

        return cookie.Replace("\r", "").Replace("\n", "").Trim();
    }

    private static bool IsJoinedStatus(string status) =>
        status.Contains('\u2714') ||
        status.Contains('\u2705') ||
        status.Equals("check", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("joined", StringComparison.OrdinalIgnoreCase);

    private static double ParseEggRatePerHour(string value) {
        var clean = value.Trim();
        var slash = clean.IndexOf('/');
        if(slash >= 0) {
            clean = clean[..slash];
        }

        var match = Regex.Match(clean, @"^\s*(?<value>-?\d+(?:\.\d+)?)\s*(?<suffix>[A-Za-z]?)\s*$");
        if(!match.Success ||
           !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return 0;
        }

        return number * SuffixMultiplier(match.Groups["suffix"].Value);
    }

    private static double SuffixMultiplier(string suffix) => suffix switch {
        "" => 1,
        "K" => 1e3,
        "M" => 1e6,
        "B" => 1e9,
        "T" => 1e12,
        "q" => 1e15,
        "Q" => 1e18,
        "s" => 1e21,
        "S" => 1e24,
        "o" => 1e27,
        "N" => 1e30,
        "d" => 1e33,
        "U" => 1e36,
        "D" => 1e39,
        _ => 1
    };

    private sealed record BrowserContractScrapePayload(
        string ContractId,
        int League,
        DateTimeOffset ExportedAt,
        IReadOnlyList<BrowserContractScrapePlayer> Players);

    private sealed record BrowserContractScrapePlayer(
        string Name,
        string? GuildTag,
        string? Chickens,
        string? Rate,
        string? Projected,
        bool Joined,
        string? CoopStatus = null,
        double? HoursToFinish = null,
        bool CoopFinished = false);
}
