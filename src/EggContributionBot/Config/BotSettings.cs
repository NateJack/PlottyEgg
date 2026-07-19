using System.Text.Json;

public sealed record BotSettings(DiscordSettings Discord, StorageSettings Storage) {
    public static BotSettings Load() {
        const string path = "appsettings.json";
        if(File.Exists(path)) {
            var settings = JsonSerializer.Deserialize<BotSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if(settings is not null) {
                return settings;
            }
        }

        return new BotSettings(new DiscordSettings(null, null, null, null), new StorageSettings("data/egg-links.json", "data/eid-key.bin"));
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
