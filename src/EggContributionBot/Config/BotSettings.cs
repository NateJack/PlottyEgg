using System.Text.Json;

public sealed record BotSettings(
    DiscordSettings Discord,
    StorageSettings Storage,
    Egg9000Settings? Egg9000 = null,
    EggIncApiSettings? EggIncApi = null,
    OpenAiSettings? OpenAi = null) {
    public static BotSettings Load() {
        const string path = "appsettings.json";
        if(File.Exists(path)) {
            var settings = JsonSerializer.Deserialize<BotSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if(settings is not null) {
                return settings with {
                    EggIncApi = settings.EggIncApi ?? new EggIncApiSettings(),
                    OpenAi = settings.OpenAi ?? new OpenAiSettings()
                };
            }
        }

        return new BotSettings(
            new DiscordSettings(null, null, null, null),
            new StorageSettings("data/egg-links.db", "data/eid-key.bin"),
            EggIncApi: new EggIncApiSettings(),
            OpenAi: new OpenAiSettings());
    }
}

public sealed record DiscordSettings(string? Token, string? GuildId, string[]? GuildIds, string[]? AdminUserIds) {
    public ulong? ParsedGuildId => ulong.TryParse(GuildId, out var id) ? id : null;
    public IEnumerable<ulong> ParsedGuildIds {
        get {
            if(ParsedGuildId is { } guildId) {
                yield return guildId;
            }

            foreach(var value in GuildIds ?? []) {
                if(ulong.TryParse(value, out var parsedGuildId)) {
                    yield return parsedGuildId;
                }
            }
        }
    }

    public IEnumerable<ulong> ParsedAdminUserIds =>
        (AdminUserIds ?? [])
            .Select(id => ulong.TryParse(id, out var parsed) ? parsed : 0)
            .Where(id => id != 0);
}

public sealed record StorageSettings(string DataPath, string KeyPath = "data/eid-key.bin");

public sealed record Egg9000Settings(
    string? BaseUrl = "https://egg9000.com/",
    string? ApiKey = null,
    string? GuildId = "656455567858073601",
    string? GuildTag = "The Plot Chickens",
    int League = 5,
    string? SessionCookie = null,
    string? SessionCookieName = "egg9000Cookie",
    string? CookieHeader = null,
    string? BrowserScrapeDataPath = "data/e9k-browser-scrapes",
    int BrowserScrapeMaxAgeMinutes = 30,
    bool AutoBrowserScrapeEnabled = true,
    int BrowserScrapeRefreshDelaySeconds = 5,
    bool CloseBrowserAfterScrape = true) {
    public string? EffectiveApiKey =>
        string.IsNullOrWhiteSpace(ApiKey)
            ? Environment.GetEnvironmentVariable("EGG9000_API_KEY")
            : ApiKey;

    public string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl)
            ? "https://egg9000.com/"
            : BaseUrl;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(EffectiveApiKey);
    public string EffectiveGuildId => string.IsNullOrWhiteSpace(GuildId) ? "656455567858073601" : GuildId;
    public string EffectiveGuildTag => string.IsNullOrWhiteSpace(GuildTag) ? "The Plot Chickens" : GuildTag;
    public int EffectiveLeague => League <= 0 ? 5 : League;
    public string? EffectiveSessionCookie {
        get {
            var environmentCookie = Environment.GetEnvironmentVariable("EGG9000_SESSION_COOKIE");
            if(string.IsNullOrWhiteSpace(environmentCookie) && OperatingSystem.IsWindows()) {
                environmentCookie = Environment.GetEnvironmentVariable("EGG9000_SESSION_COOKIE", EnvironmentVariableTarget.User);
            }

            return string.IsNullOrWhiteSpace(environmentCookie) ? SessionCookie : environmentCookie;
        }
    }
    public string EffectiveSessionCookieName =>
        string.IsNullOrWhiteSpace(SessionCookieName) ? "egg9000Cookie" : SessionCookieName;
    public string? EffectiveCookieHeader {
        get {
            var environmentHeader = Environment.GetEnvironmentVariable("EGG9000_COOKIE_HEADER");
            if(string.IsNullOrWhiteSpace(environmentHeader) && OperatingSystem.IsWindows()) {
                environmentHeader = Environment.GetEnvironmentVariable("EGG9000_COOKIE_HEADER", EnvironmentVariableTarget.User);
            }

            return string.IsNullOrWhiteSpace(environmentHeader) ? CookieHeader : environmentHeader;
        }
    }
    public string EffectiveBrowserScrapeDataPath =>
        string.IsNullOrWhiteSpace(BrowserScrapeDataPath)
            ? "data/e9k-browser-scrapes"
            : BrowserScrapeDataPath;
    public TimeSpan EffectiveBrowserScrapeMaxAge =>
        TimeSpan.FromMinutes(BrowserScrapeMaxAgeMinutes <= 0 ? 30 : BrowserScrapeMaxAgeMinutes);
    public TimeSpan EffectiveBrowserScrapeRefreshDelay =>
        TimeSpan.FromSeconds(BrowserScrapeRefreshDelaySeconds < 0 ? 5 : BrowserScrapeRefreshDelaySeconds);
}

public sealed record EggIncApiSettings(string? Salt = null, string? WorkerUrl = null) {
    public string? EffectiveSalt {
        get {
            var environmentSalt = Environment.GetEnvironmentVariable("EGG_INC_API_SALT")
                ?? Environment.GetEnvironmentVariable("egg_inc_api_salt")
                ?? Environment.GetEnvironmentVariable("ConnectionStrings__ApiSalt")
                ?? Environment.GetEnvironmentVariable("ApiSalt");
            if(string.IsNullOrWhiteSpace(environmentSalt) && OperatingSystem.IsWindows()) {
                environmentSalt = Environment.GetEnvironmentVariable("EGG_INC_API_SALT", EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable("egg_inc_api_salt", EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable("ConnectionStrings__ApiSalt", EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable("ApiSalt", EnvironmentVariableTarget.User);
            }

            return string.IsNullOrWhiteSpace(environmentSalt) ? Salt : environmentSalt;
        }
    }

    public string? EffectiveWorkerUrl {
        get {
            var environmentUrl = Environment.GetEnvironmentVariable("EGG_INC_WORKER_URL");
            if(string.IsNullOrWhiteSpace(environmentUrl) && OperatingSystem.IsWindows()) {
                environmentUrl = Environment.GetEnvironmentVariable(
                    "EGG_INC_WORKER_URL",
                    EnvironmentVariableTarget.User);
            }

            var configuredUrl = string.IsNullOrWhiteSpace(environmentUrl) ? WorkerUrl : environmentUrl;
            if(string.IsNullOrWhiteSpace(configuredUrl)) {
                return null;
            }

            return configuredUrl.EndsWith('/') ? configuredUrl : $"{configuredUrl}/";
        }
    }

    public bool HasDirectApiAccess => !string.IsNullOrWhiteSpace(EffectiveSalt);
    public bool HasWorkerAccess => Uri.TryCreate(EffectiveWorkerUrl, UriKind.Absolute, out _);
    public bool IsConfigured => HasDirectApiAccess || HasWorkerAccess;
}

public sealed record OpenAiSettings(
    string? BaseUrl = "https://api.openai.com/v1/",
    string? ApiKey = null,
    string? Model = "gpt-5-nano") {
    public string? EffectiveApiKey {
        get {
            var environmentKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if(string.IsNullOrWhiteSpace(environmentKey) && OperatingSystem.IsWindows()) {
                environmentKey = Environment.GetEnvironmentVariable(
                    "OPENAI_API_KEY",
                    EnvironmentVariableTarget.User);
            }

            return string.IsNullOrWhiteSpace(environmentKey) ? ApiKey : environmentKey;
        }
    }

    public string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl)
            ? "https://api.openai.com/v1/"
            : BaseUrl.EndsWith('/') ? BaseUrl : $"{BaseUrl}/";

    public string EffectiveModel =>
        string.IsNullOrWhiteSpace(Model) ? "gpt-5-nano" : Model;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(EffectiveApiKey);
}
