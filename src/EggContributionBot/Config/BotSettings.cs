using System.Text.Json;

namespace EggContribBot.Config;

public sealed record BotSettings(
    DiscordSettings Discord,
    StorageSettings Storage,
    Egg9000Settings? Egg9000 = null,
    OpenAiSettings? OpenAi = null) {
    public static BotSettings Load() {
        const string path = "appsettings.json";
        if(File.Exists(path)) {
            var settings = JsonSerializer.Deserialize<BotSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if(settings is not null) {
                return settings with {
                    OpenAi = settings.OpenAi ?? new OpenAiSettings()
                };
            }
        }

        return new BotSettings(
            new DiscordSettings(null, null, null, null),
            new StorageSettings("data/egg-links.db", "data/eid-key.bin"),
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

public sealed record Egg9000Settings(string? BaseUrl = "https://egg9000.com/", string? ApiKey = null) {
    public string? EffectiveApiKey =>
        string.IsNullOrWhiteSpace(ApiKey)
            ? Environment.GetEnvironmentVariable("EGG9000_API_KEY")
            : ApiKey;

    public string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl)
            ? "https://egg9000.com/"
            : BaseUrl;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(EffectiveApiKey);
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
