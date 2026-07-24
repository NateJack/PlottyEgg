using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EggContribBot;
using Microsoft.Data.Sqlite;

public sealed class DataStore : IDisposable {
    private const string LegacyImportKey = "legacy_json_imported";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _databasePath;
    private readonly string _legacyJsonPath;
    private readonly string _connectionString;
    private readonly SecureText _secureText;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public DataStore(string path, SecureText secureText) {
        var configuredPath = Path.GetFullPath(path);
        _databasePath = Path.GetExtension(configuredPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(configuredPath, ".db")
            : configuredPath;
        _legacyJsonPath = Path.GetExtension(configuredPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? configuredPath
            : Path.ChangeExtension(configuredPath, ".json");
        _secureText = secureText;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public void Dispose() {
        SqliteConnection.ClearAllPools();
        _gate.Dispose();
    }

    public Task SaveRegisteredEidAsync(ulong guildId, ulong discordUserId, string eid, string? eggName) =>
        WriteAsync(async (connection, transaction) => {
            var normalized = EggIncClient.NormalizeEggId(eid);
            var command = CreateCommand(connection, transaction, """
                INSERT INTO registered_eids
                    (guild_id, discord_user_id, eid_hash, encrypted_eid, egg_name, updated_at)
                VALUES ($guild, $user, $hash, $eid, $name, $updated)
                ON CONFLICT(guild_id, eid_hash) DO UPDATE SET
                    discord_user_id = excluded.discord_user_id,
                    encrypted_eid = excluded.encrypted_eid,
                    egg_name = excluded.egg_name,
                    updated_at = excluded.updated_at;
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$user", Id(discordUserId));
            Add(command, "$hash", SecureText.Sha256(normalized));
            Add(command, "$eid", _secureText.Encrypt(normalized));
            Add(command, "$name", eggName);
            Add(command, "$updated", Date(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync();
        });

    public async Task<RegisteredEggAccount?> GetRegisteredAccountAsync(ulong guildId, ulong discordUserId) {
        var accounts = await GetRegisteredAccountsAsync(guildId, discordUserId);
        return accounts.FirstOrDefault();
    }

    public Task<IReadOnlyList<RegisteredEggAccount>> GetRegisteredAccountsAsync(ulong guildId, ulong discordUserId) =>
        ReadAsync<IReadOnlyList<RegisteredEggAccount>>(async connection => {
            var command = CreateCommand(connection, null, """
                SELECT discord_user_id, encrypted_eid, egg_name, updated_at
                FROM registered_eids
                WHERE guild_id = $guild AND discord_user_id = $user
                ORDER BY updated_at DESC;
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$user", Id(discordUserId));
            return await ReadAccountsAsync(command);
        });

    public Task<IReadOnlyList<RegisteredEggAccount>> GetRegisteredEidsAsync(ulong guildId) =>
        ReadAsync<IReadOnlyList<RegisteredEggAccount>>(async connection => {
            var command = CreateCommand(connection, null, """
                SELECT discord_user_id, encrypted_eid, egg_name, updated_at
                FROM registered_eids
                WHERE guild_id = $guild
                ORDER BY updated_at DESC;
                """);
            Add(command, "$guild", Id(guildId));
            return await ReadAccountsAsync(command);
        });

    public Task<int> RemoveRegisteredEidsForUserAsync(ulong guildId, ulong discordUserId) =>
        WriteAsync(async (connection, transaction) => {
            var command = CreateCommand(connection, transaction,
                "DELETE FROM registered_eids WHERE guild_id = $guild AND discord_user_id = $user;");
            Add(command, "$guild", Id(guildId));
            Add(command, "$user", Id(discordUserId));
            return await command.ExecuteNonQueryAsync();
        });

    public Task<int> AddDemeritsAsync(
        ulong guildId,
        ulong discordUserId,
        int amount,
        string reason,
        string? contractId,
        string? sourceKey,
        string? playerName = null) =>
        WriteAsync(async (connection, transaction) => {
            var count = Math.Max(1, amount);
            var now = DateTimeOffset.UtcNow;
            for(var i = 0; i < count; i++) {
                await InsertDemeritAsync(
                    connection,
                    transaction,
                    new DemeritEntry(
                        Guid.NewGuid().ToString("N"),
                        guildId,
                        discordUserId,
                        1,
                        reason,
                        contractId,
                        playerName,
                        sourceKey,
                        now,
                        now.AddDays(30),
                        null));
            }
            return count;
        });

    public Task<IReadOnlyList<DemeritEntry>> AddAutoDemeritsAsync(
        ulong guildId,
        IReadOnlyList<AutoDemeritCandidate> candidates) =>
        WriteAsync<IReadOnlyList<DemeritEntry>>(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var added = new List<DemeritEntry>();
            foreach(var candidate in candidates
                .GroupBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())) {
                var exists = CreateCommand(connection, transaction, """
                    SELECT 1
                    FROM demerits
                    WHERE guild_id = $guild
                      AND source_key = $source
                      AND removed_at IS NULL
                      AND expires_at > $now
                    LIMIT 1;
                    """);
                Add(exists, "$guild", Id(guildId));
                Add(exists, "$source", candidate.SourceKey);
                Add(exists, "$now", Date(now));
                if(await exists.ExecuteScalarAsync() is not null) {
                    continue;
                }

                var entry = new DemeritEntry(
                    Guid.NewGuid().ToString("N"),
                    guildId,
                    candidate.DiscordUserId,
                    1,
                    "Auto: under 2q/hr after 18h",
                    candidate.ContractId,
                    candidate.PlayerName,
                    candidate.SourceKey,
                    now,
                    now.AddDays(30),
                    null);
                await InsertDemeritAsync(connection, transaction, entry);
                added.Add(entry);
            }
            return added;
        });

    public Task<int> RemoveDemeritsAsync(ulong guildId, ulong discordUserId, int amount) =>
        WriteAsync(async (connection, transaction) => {
            var command = CreateCommand(connection, transaction, """
                UPDATE demerits
                SET removed_at = $now
                WHERE id IN (
                    SELECT id
                    FROM demerits
                    WHERE guild_id = $guild
                      AND discord_user_id = $user
                      AND removed_at IS NULL
                      AND expires_at > $now
                    ORDER BY created_at DESC
                    LIMIT $amount
                );
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$user", Id(discordUserId));
            Add(command, "$now", Date(DateTimeOffset.UtcNow));
            Add(command, "$amount", Math.Max(1, amount));
            return await command.ExecuteNonQueryAsync();
        });

    public Task<IReadOnlyList<DemeritEntry>> GetActiveDemeritsAsync(ulong guildId, ulong? discordUserId = null) =>
        ReadAsync<IReadOnlyList<DemeritEntry>>(async connection => {
            var command = CreateCommand(connection, null, """
                SELECT id, guild_id, discord_user_id, amount, reason, contract_id, player_name,
                       source_key, created_at, expires_at, removed_at
                FROM demerits
                WHERE guild_id = $guild
                  AND removed_at IS NULL
                  AND expires_at > $now
                  AND ($user IS NULL OR discord_user_id = $user)
                ORDER BY expires_at;
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$user", discordUserId is null ? null : Id(discordUserId.Value));
            Add(command, "$now", Date(DateTimeOffset.UtcNow));
            var result = new List<DemeritEntry>();
            await using var reader = await command.ExecuteReaderAsync();
            while(await reader.ReadAsync()) {
                result.Add(ReadDemerit(reader));
            }
            return result;
        });

    public Task<bool> HasMissingJoinAlertAsync(string key) =>
        ExistsAsync("SELECT 1 FROM missing_join_alerts WHERE key = $key COLLATE NOCASE LIMIT 1;", ("$key", key));

    public Task RecordMissingJoinAlertAsync(string key) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var cleanup = CreateCommand(connection, transaction, "DELETE FROM missing_join_alerts WHERE posted_at < $cutoff;");
            Add(cleanup, "$cutoff", Date(now.AddDays(-90)));
            await cleanup.ExecuteNonQueryAsync();

            var command = CreateCommand(connection, transaction, """
                INSERT INTO missing_join_alerts (key, posted_at)
                VALUES ($key, $posted)
                ON CONFLICT(key) DO NOTHING;
                """);
            Add(command, "$key", key);
            Add(command, "$posted", Date(now));
            await command.ExecuteNonQueryAsync();
        });

    public Task<bool> HasFirstCoopAwardAsync(string key) =>
        ExistsAsync("SELECT 1 FROM first_coop_awards WHERE key = $key COLLATE NOCASE LIMIT 1;", ("$key", key));

    public Task RecordFirstCoopAwardAsync(FirstCoopAward award) =>
        WriteAsync(async (connection, transaction) => {
            var cleanup = CreateCommand(connection, transaction, "DELETE FROM first_coop_awards WHERE awarded_at < $cutoff;");
            Add(cleanup, "$cutoff", Date(DateTimeOffset.UtcNow.AddDays(-180)));
            await cleanup.ExecuteNonQueryAsync();

            await InsertFirstCoopAwardAsync(connection, transaction, award);
        });

    public Task<bool> HasWeeklyTokenLeaderboardPostAsync(ulong guildId, string weekKey) =>
        ExistsAsync("""
            SELECT 1 FROM weekly_token_leaderboard_posts
            WHERE guild_id = $guild AND week_key = $week COLLATE NOCASE LIMIT 1;
            """, ("$guild", Id(guildId)), ("$week", weekKey));

    public Task RecordWeeklyTokenLeaderboardPostAsync(WeeklyTokenLeaderboardPost post) =>
        WriteAsync(async (connection, transaction) => {
            var cleanup = CreateCommand(connection, transaction,
                "DELETE FROM weekly_token_leaderboard_posts WHERE posted_at < $cutoff;");
            Add(cleanup, "$cutoff", Date(DateTimeOffset.UtcNow.AddDays(-180)));
            await cleanup.ExecuteNonQueryAsync();

            await InsertWeeklyTokenPostAsync(connection, transaction, post);
        });

    public Task<ContractLateNotice> RecordContractLateNoticeAsync(
        ulong guildId,
        ulong discordUserId,
        string? contractId,
        string? eta,
        string? note,
        DateTimeOffset? expiresAt = null) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var normalizedContractId = string.IsNullOrWhiteSpace(contractId) ? null : contractId.Trim();
            var notice = new ContractLateNotice(
                guildId,
                discordUserId,
                normalizedContractId,
                eta,
                note,
                now,
                expiresAt is null || expiresAt <= now ? now.AddHours(48) : expiresAt.Value);

            var cleanup = CreateCommand(connection, transaction, "DELETE FROM contract_late_notices WHERE expires_at <= $now;");
            Add(cleanup, "$now", Date(now));
            await cleanup.ExecuteNonQueryAsync();
            await InsertLateNoticeAsync(connection, transaction, notice);
            return notice;
        });

    public Task<IReadOnlySet<ulong>> GetActiveContractLateNoticeUserIdsAsync(ulong guildId, string contractId) =>
        ReadAsync<IReadOnlySet<ulong>>(async connection => {
            var now = DateTimeOffset.UtcNow;
            var command = CreateCommand(connection, null, """
                SELECT DISTINCT discord_user_id
                FROM contract_late_notices
                WHERE guild_id = $guild
                  AND expires_at > $now
                  AND (contract_id IS NULL OR contract_id = '' OR contract_id = $contract COLLATE NOCASE);
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$now", Date(now));
            Add(command, "$contract", contractId);
            var result = new HashSet<ulong>();
            await using var reader = await command.ExecuteReaderAsync();
            while(await reader.ReadAsync()) {
                result.Add(ParseId(reader.GetString(0)));
            }
            return result;
        });

    public Task<int> RemoveContractLateNoticesAsync(ulong guildId, ulong discordUserId, string? contractId) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var command = CreateCommand(connection, transaction, """
                DELETE FROM contract_late_notices
                WHERE guild_id = $guild
                  AND discord_user_id = $user
                  AND expires_at > $now
                  AND ($contract IS NULL OR contract_id = $contract COLLATE NOCASE);
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$user", Id(discordUserId));
            Add(command, "$now", Date(now));
            Add(command, "$contract", string.IsNullOrWhiteSpace(contractId) ? null : contractId.Trim());
            var removed = await command.ExecuteNonQueryAsync();

            var cleanup = CreateCommand(connection, transaction, "DELETE FROM contract_late_notices WHERE expires_at <= $now;");
            Add(cleanup, "$now", Date(now));
            await cleanup.ExecuteNonQueryAsync();
            return removed;
        });

    public Task UpsertShipReturnNotificationAsync(ShipReturnNotification notification) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var cleanup = CreateCommand(connection, transaction, """
                DELETE FROM ship_return_notifications
                WHERE (notified_at IS NOT NULL AND notified_at < $notifiedCutoff)
                   OR (notified_at IS NULL AND return_at < $pendingCutoff);
                """);
            Add(cleanup, "$notifiedCutoff", Date(now.AddDays(-7)));
            Add(cleanup, "$pendingCutoff", Date(now.AddDays(-2)));
            await cleanup.ExecuteNonQueryAsync();
            await InsertShipNotificationAsync(connection, transaction, notification);
        });

    public Task<IReadOnlyList<ShipReturnNotification>> GetDueShipReturnNotificationsAsync(ulong guildId, DateTimeOffset now) =>
        ReadAsync<IReadOnlyList<ShipReturnNotification>>(async connection => {
            var command = CreateCommand(connection, null, """
                SELECT guild_id, discord_user_id, eid_hash, mission_key, ship_name,
                       return_at, created_at, notified_at
                FROM ship_return_notifications
                WHERE guild_id = $guild AND notified_at IS NULL AND return_at <= $now
                ORDER BY return_at;
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$now", Date(now));
            var result = new List<ShipReturnNotification>();
            await using var reader = await command.ExecuteReaderAsync();
            while(await reader.ReadAsync()) {
                result.Add(new ShipReturnNotification(
                    ParseId(reader.GetString(0)),
                    ParseId(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ParseDate(reader.GetString(5)),
                    ParseDate(reader.GetString(6)),
                    NullableDate(reader, 7)));
            }
            return result;
        });

    public Task MarkShipReturnNotificationSentAsync(string key, DateTimeOffset sentAt) =>
        WriteAsync(async (connection, transaction) => {
            var command = CreateCommand(connection, transaction,
                "UPDATE ship_return_notifications SET notified_at = $sent WHERE key = $key COLLATE NOCASE;");
            Add(command, "$sent", Date(sentAt));
            Add(command, "$key", key);
            await command.ExecuteNonQueryAsync();
        });

    public Task<BeerAttemptResult> TryAddPlottyBeerAsync(
        ulong guildId,
        ulong discordUserId,
        bool botBuysBack,
        bool bypassCooldown = false) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var current = await GetBeerStatsAsync(connection, transaction, guildId, discordUserId);
            var nextBeerAt = current?.LastBeerAt.AddHours(1);
            if(!bypassCooldown && nextBeerAt > now) {
                return new BeerAttemptResult(false, current!, nextBeerAt.Value - now);
            }

            current ??= new BeerStats(guildId, discordUserId, 0, 0, now, now, null);
            var updated = current with {
                BeersGivenToBot = current.BeersGivenToBot + 1,
                BeersBoughtByBot = current.BeersBoughtByBot + (botBuysBack ? 1 : 0),
                LastBeerAt = now,
                LastBotBeerAt = botBuysBack ? now : current.LastBotBeerAt
            };
            await UpsertBeerStatsAsync(connection, transaction, updated);
            return new BeerAttemptResult(true, updated, null);
        });

    public Task<BeerAttemptResult> TryGiftBeerAsync(
        ulong guildId,
        ulong giverDiscordUserId,
        ulong recipientDiscordUserId,
        bool bypassCooldown = false) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var giftCommand = CreateCommand(connection, transaction, """
                SELECT gifted_at
                FROM beer_gift_logs
                WHERE guild_id = $guild AND giver_discord_user_id = $giver AND recipient_discord_user_id = $recipient
                ORDER BY gifted_at DESC LIMIT 1;
                """);
            Add(giftCommand, "$guild", Id(guildId));
            Add(giftCommand, "$giver", Id(giverDiscordUserId));
            Add(giftCommand, "$recipient", Id(recipientDiscordUserId));
            var lastGiftValue = await giftCommand.ExecuteScalarAsync();
            var nextGiftAt = lastGiftValue is string value ? ParseDate(value).AddHours(1) : (DateTimeOffset?)null;

            var recipient = await GetBeerStatsAsync(connection, transaction, guildId, recipientDiscordUserId)
                ?? new BeerStats(guildId, recipientDiscordUserId, 0, 0, now, now, null);
            if(!bypassCooldown && nextGiftAt > now) {
                return new BeerAttemptResult(false, recipient, nextGiftAt.Value - now);
            }

            var giver = giverDiscordUserId == recipientDiscordUserId
                ? recipient
                : await GetBeerStatsAsync(connection, transaction, guildId, giverDiscordUserId)
                    ?? new BeerStats(guildId, giverDiscordUserId, 0, 0, now, now, null);
            var updatedRecipient = recipient with {
                BeersReceivedFromMembers = recipient.BeersReceivedFromMembers + 1,
                LastMemberBeerReceivedAt = now
            };
            var updatedGiver = giver with {
                BeersGivenToMembers = giver.BeersGivenToMembers + 1,
                LastMemberBeerGivenAt = now
            };

            if(giverDiscordUserId == recipientDiscordUserId) {
                updatedRecipient = updatedRecipient with {
                    BeersGivenToMembers = updatedRecipient.BeersGivenToMembers + 1,
                    LastMemberBeerGivenAt = now
                };
            } else {
                await UpsertBeerStatsAsync(connection, transaction, updatedGiver);
            }
            await UpsertBeerStatsAsync(connection, transaction, updatedRecipient);

            var log = CreateCommand(connection, transaction, """
                INSERT INTO beer_gift_logs
                    (guild_id, giver_discord_user_id, recipient_discord_user_id, gifted_at)
                VALUES ($guild, $giver, $recipient, $gifted);
                """);
            Add(log, "$guild", Id(guildId));
            Add(log, "$giver", Id(giverDiscordUserId));
            Add(log, "$recipient", Id(recipientDiscordUserId));
            Add(log, "$gifted", Date(now));
            await log.ExecuteNonQueryAsync();
            return new BeerAttemptResult(true, updatedRecipient, null);
        });

    public Task<IReadOnlyList<BeerStats>> GetBeerLeaderboardAsync(ulong guildId) =>
        ReadAsync<IReadOnlyList<BeerStats>>(async connection => {
            var command = CreateCommand(connection, null, """
                SELECT guild_id, discord_user_id, beers_given_to_bot, beers_bought_by_bot,
                       first_beer_at, last_beer_at, last_bot_beer_at,
                       beers_received_from_members, beers_given_to_members,
                       last_member_beer_received_at, last_member_beer_given_at
                FROM beer_stats
                WHERE guild_id = $guild
                ORDER BY beers_bought_by_bot DESC, beers_given_to_bot DESC, first_beer_at;
                """);
            Add(command, "$guild", Id(guildId));
            var result = new List<BeerStats>();
            await using var reader = await command.ExecuteReaderAsync();
            while(await reader.ReadAsync()) {
                result.Add(ReadBeerStats(reader));
            }
            return result;
        });

    public Task<PlottyMemory> RecordPlottyInteractionAsync(
        ulong guildId,
        ulong discordUserId,
        string interactionType) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var current = await GetPlottyMemoryAsync(connection, transaction, guildId, discordUserId)
                ?? new PlottyMemory(guildId, discordUserId, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, now, now);
            var updated = interactionType switch {
                "mention" => current with { TotalInteractions = current.TotalInteractions + 1, Mentions = current.Mentions + 1, LastSeenAt = now },
                "question_mention" => current with { TotalInteractions = current.TotalInteractions + 1, Mentions = current.Mentions + 1, QuestionsDodged = current.QuestionsDodged + 1, LastSeenAt = now },
                "sarcasm" => current with { TotalInteractions = current.TotalInteractions + 1, SarcasmDetected = current.SarcasmDetected + 1, LastSeenAt = now },
                "fox" => current with { TotalInteractions = current.TotalInteractions + 1, FoxMoments = current.FoxMoments + 1, LastSeenAt = now },
                "beer_plotty" or "beer_bot_buyback" => current with { TotalInteractions = current.TotalInteractions + 1, Beers = current.Beers + 1, LastSeenAt = now },
                "registration" => current with { TotalInteractions = current.TotalInteractions + 1, Registrations = current.Registrations + 1, LastSeenAt = now },
                "mood" => current with { TotalInteractions = current.TotalInteractions + 1, MoodChecks = current.MoodChecks + 1, LastSeenAt = now },
                "excuse" => current with { TotalInteractions = current.TotalInteractions + 1, Excuses = current.Excuses + 1, LastSeenAt = now },
                "wisdom" => current with { TotalInteractions = current.TotalInteractions + 1, WisdomRequests = current.WisdomRequests + 1, LastSeenAt = now },
                _ => current with { TotalInteractions = current.TotalInteractions + 1, LastSeenAt = now }
            };
            await UpsertPlottyMemoryAsync(connection, transaction, updated);
            return updated;
        });

    public Task<PlottyConversationState?> GetPlottyConversationAsync(ulong guildId, ulong discordUserId) =>
        ReadAsync<PlottyConversationState?>(async connection => {
            var command = CreateCommand(connection, null, """
                SELECT guild_id, discord_user_id, last_topic, turns, first_seen_at, last_seen_at
                FROM plotty_conversations
                WHERE guild_id = $guild AND discord_user_id = $user AND last_seen_at >= $cutoff
                LIMIT 1;
                """);
            Add(command, "$guild", Id(guildId));
            Add(command, "$user", Id(discordUserId));
            Add(command, "$cutoff", Date(DateTimeOffset.UtcNow.AddHours(-6)));
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadConversation(reader) : null;
        });

    public Task<PlottyConversationState> RecordPlottyConversationAsync(
        ulong guildId,
        ulong discordUserId,
        string topic) =>
        WriteAsync(async (connection, transaction) => {
            var now = DateTimeOffset.UtcNow;
            var cleanup = CreateCommand(connection, transaction, "DELETE FROM plotty_conversations WHERE last_seen_at < $cutoff;");
            Add(cleanup, "$cutoff", Date(now.AddDays(-14)));
            await cleanup.ExecuteNonQueryAsync();

            var cleanTopic = string.IsNullOrWhiteSpace(topic) ? "chat" : topic.Trim();
            if(cleanTopic.Length > 80) {
                cleanTopic = cleanTopic[..80];
            }

            var existing = CreateCommand(connection, transaction, """
                SELECT guild_id, discord_user_id, last_topic, turns, first_seen_at, last_seen_at
                FROM plotty_conversations
                WHERE guild_id = $guild AND discord_user_id = $user LIMIT 1;
                """);
            Add(existing, "$guild", Id(guildId));
            Add(existing, "$user", Id(discordUserId));
            PlottyConversationState? current;
            await using(var reader = await existing.ExecuteReaderAsync()) {
                current = await reader.ReadAsync() ? ReadConversation(reader) : null;
            }
            current ??= new PlottyConversationState(guildId, discordUserId, cleanTopic, 0, now, now);
            var updated = current with { LastTopic = cleanTopic, Turns = current.Turns + 1, LastSeenAt = now };
            await UpsertConversationAsync(connection, transaction, updated);
            return updated;
        });

    public async Task RemoveLegacyPlaintextLinksAsync() {
        await _gate.WaitAsync();
        try {
            await EnsureInitializedAsync();
            if(!File.Exists(_legacyJsonPath)) {
                return;
            }

            var json = await File.ReadAllTextAsync(_legacyJsonPath);
            var root = JsonNode.Parse(json)?.AsObject();
            var linksProperty = root?
                .Select(pair => pair.Key)
                .FirstOrDefault(key => key.Equals("links", StringComparison.OrdinalIgnoreCase));
            if(root is null || linksProperty is null) {
                return;
            }

            root.Remove(linksProperty);
            await WriteAtomicTextAsync(_legacyJsonPath, root.ToJsonString(JsonOptions));
        } finally {
            _gate.Release();
        }
    }

    private Task<bool> ExistsAsync(string sql, params (string Name, object? Value)[] parameters) =>
        ReadAsync(async connection => {
            var command = CreateCommand(connection, null, sql);
            foreach(var parameter in parameters) {
                Add(command, parameter.Name, parameter.Value);
            }
            return await command.ExecuteScalarAsync() is not null;
        });

    private async Task<IReadOnlyList<RegisteredEggAccount>> ReadAccountsAsync(SqliteCommand command) {
        var result = new List<RegisteredEggAccount>();
        await using var reader = await command.ExecuteReaderAsync();
        while(await reader.ReadAsync()) {
            result.Add(new RegisteredEggAccount(
                ParseId(reader.GetString(0)),
                _secureText.Decrypt(reader.GetString(1)),
                NullableString(reader, 2),
                ParseDate(reader.GetString(3))));
        }
        return result;
    }

    private async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> action) {
        await _gate.WaitAsync();
        try {
            await EnsureInitializedAsync();
            await using var connection = await OpenConnectionAsync();
            return await action(connection);
        } finally {
            _gate.Release();
        }
    }

    private async Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> action) {
        await WriteAsync(async (connection, transaction) => {
            await action(connection, transaction);
            return true;
        });
    }

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action) {
        await _gate.WaitAsync();
        try {
            await EnsureInitializedAsync();
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var result = await action(connection, (SqliteTransaction)transaction);
            await transaction.CommitAsync();
            return result;
        } finally {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync() {
        if(_initialized) {
            return;
        }

        await using var connection = await OpenConnectionAsync();
        var schema = CreateCommand(connection, null, SchemaSql);
        await schema.ExecuteNonQueryAsync();

        var imported = CreateCommand(connection, null, "SELECT value FROM metadata WHERE key = $key;");
        Add(imported, "$key", LegacyImportKey);
        if(await imported.ExecuteScalarAsync() is null) {
            await ImportLegacyJsonAsync(connection);
        }
        _initialized = true;
    }

    private async Task ImportLegacyJsonAsync(SqliteConnection connection) {
        LegacyState state = new();
        if(File.Exists(_legacyJsonPath)) {
            await using var stream = File.OpenRead(_legacyJsonPath);
            state = await JsonSerializer.DeserializeAsync<LegacyState>(stream, JsonOptions) ?? new LegacyState();
        }

        await using var transactionBase = await connection.BeginTransactionAsync();
        var transaction = (SqliteTransaction)transactionBase;
        foreach(var entry in state.RegisteredEids) {
            await InsertRegisteredEidAsync(connection, transaction, entry);
        }
        foreach(var entry in state.Demerits) {
            await InsertDemeritAsync(connection, transaction, entry);
        }
        foreach(var entry in state.MissingJoinAlerts) {
            var command = CreateCommand(connection, transaction, """
                INSERT INTO missing_join_alerts (key, posted_at) VALUES ($key, $posted)
                ON CONFLICT(key) DO NOTHING;
                """);
            Add(command, "$key", entry.Key);
            Add(command, "$posted", Date(entry.PostedAt));
            await command.ExecuteNonQueryAsync();
        }
        foreach(var entry in state.FirstCoopAwards) {
            await InsertFirstCoopAwardAsync(connection, transaction, entry);
        }
        foreach(var entry in state.WeeklyTokenLeaderboardPosts) {
            await InsertWeeklyTokenPostAsync(connection, transaction, entry);
        }
        foreach(var entry in state.ContractLateNotices) {
            await InsertLateNoticeAsync(connection, transaction, entry);
        }
        foreach(var entry in state.ShipReturnNotifications) {
            await InsertShipNotificationAsync(connection, transaction, entry);
        }
        foreach(var entry in state.BeerStats) {
            await UpsertBeerStatsAsync(connection, transaction, entry);
        }
        foreach(var entry in state.BeerGiftLogs) {
            var command = CreateCommand(connection, transaction, """
                INSERT INTO beer_gift_logs
                    (guild_id, giver_discord_user_id, recipient_discord_user_id, gifted_at)
                VALUES ($guild, $giver, $recipient, $gifted);
                """);
            Add(command, "$guild", Id(entry.GuildId));
            Add(command, "$giver", Id(entry.GiverDiscordUserId));
            Add(command, "$recipient", Id(entry.RecipientDiscordUserId));
            Add(command, "$gifted", Date(entry.GiftedAt));
            await command.ExecuteNonQueryAsync();
        }
        foreach(var entry in state.PlottyMemories) {
            await UpsertPlottyMemoryAsync(connection, transaction, entry);
        }
        foreach(var entry in state.PlottyConversations) {
            await UpsertConversationAsync(connection, transaction, entry);
        }

        var marker = CreateCommand(connection, transaction, """
            INSERT INTO metadata (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """);
        Add(marker, "$key", LegacyImportKey);
        Add(marker, "$value", Date(DateTimeOffset.UtcNow));
        await marker.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task<SqliteConnection> OpenConnectionAsync() {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragmas.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task InsertRegisteredEidAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RegisteredEid entry) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO registered_eids
                (guild_id, discord_user_id, eid_hash, encrypted_eid, egg_name, updated_at)
            VALUES ($guild, $user, $hash, $eid, $name, $updated)
            ON CONFLICT(guild_id, eid_hash) DO UPDATE SET
                discord_user_id = excluded.discord_user_id,
                encrypted_eid = excluded.encrypted_eid,
                egg_name = excluded.egg_name,
                updated_at = excluded.updated_at;
            """);
        Add(command, "$guild", Id(entry.GuildId));
        Add(command, "$user", Id(entry.DiscordUserId));
        Add(command, "$hash", entry.EidHash);
        Add(command, "$eid", entry.EncryptedEid);
        Add(command, "$name", entry.EggName);
        Add(command, "$updated", Date(entry.UpdatedAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertDemeritAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DemeritEntry entry) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO demerits
                (id, guild_id, discord_user_id, amount, reason, contract_id, player_name,
                 source_key, created_at, expires_at, removed_at)
            VALUES ($id, $guild, $user, $amount, $reason, $contract, $player,
                    $source, $created, $expires, $removed)
            ON CONFLICT(id) DO NOTHING;
            """);
        Add(command, "$id", entry.Id);
        Add(command, "$guild", Id(entry.GuildId));
        Add(command, "$user", Id(entry.DiscordUserId));
        Add(command, "$amount", entry.Amount);
        Add(command, "$reason", entry.Reason);
        Add(command, "$contract", entry.ContractId);
        Add(command, "$player", entry.PlayerName);
        Add(command, "$source", entry.SourceKey);
        Add(command, "$created", Date(entry.CreatedAt));
        Add(command, "$expires", Date(entry.ExpiresAt));
        Add(command, "$removed", NullableDate(entry.RemovedAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertFirstCoopAwardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FirstCoopAward award) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO first_coop_awards
                (key, guild_id, contract_id, coop_code, awarded_at)
            VALUES ($key, $guild, $contract, $coop, $awarded)
            ON CONFLICT(key) DO NOTHING;
            """);
        Add(command, "$key", award.Key);
        Add(command, "$guild", Id(award.GuildId));
        Add(command, "$contract", award.ContractId);
        Add(command, "$coop", award.CoopCode);
        Add(command, "$awarded", Date(award.AwardedAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertWeeklyTokenPostAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WeeklyTokenLeaderboardPost post) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO weekly_token_leaderboard_posts (guild_id, week_key, posted_at)
            VALUES ($guild, $week, $posted)
            ON CONFLICT(guild_id, week_key) DO NOTHING;
            """);
        Add(command, "$guild", Id(post.GuildId));
        Add(command, "$week", post.WeekKey);
        Add(command, "$posted", Date(post.PostedAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLateNoticeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContractLateNotice notice) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO contract_late_notices
                (guild_id, discord_user_id, contract_key, contract_id, eta, note, created_at, expires_at)
            VALUES ($guild, $user, $contractKey, $contract, $eta, $note, $created, $expires)
            ON CONFLICT(guild_id, discord_user_id, contract_key) DO UPDATE SET
                contract_id = excluded.contract_id,
                eta = excluded.eta,
                note = excluded.note,
                created_at = excluded.created_at,
                expires_at = excluded.expires_at;
            """);
        Add(command, "$guild", Id(notice.GuildId));
        Add(command, "$user", Id(notice.DiscordUserId));
        Add(command, "$contractKey", notice.ContractId?.ToUpperInvariant() ?? "");
        Add(command, "$contract", notice.ContractId);
        Add(command, "$eta", notice.Eta);
        Add(command, "$note", notice.Note);
        Add(command, "$created", Date(notice.CreatedAt));
        Add(command, "$expires", Date(notice.ExpiresAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertShipNotificationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ShipReturnNotification notification) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO ship_return_notifications
                (key, guild_id, discord_user_id, eid_hash, mission_key, ship_name,
                 return_at, created_at, notified_at)
            VALUES ($key, $guild, $user, $eid, $mission, $ship, $return, $created, $notified)
            ON CONFLICT(key) DO UPDATE SET
                ship_name = excluded.ship_name,
                return_at = excluded.return_at,
                created_at = excluded.created_at,
                notified_at = excluded.notified_at;
            """);
        Add(command, "$key", notification.Key);
        Add(command, "$guild", Id(notification.GuildId));
        Add(command, "$user", Id(notification.DiscordUserId));
        Add(command, "$eid", notification.EidHash);
        Add(command, "$mission", notification.MissionKey);
        Add(command, "$ship", notification.ShipName);
        Add(command, "$return", Date(notification.ReturnAt));
        Add(command, "$created", Date(notification.CreatedAt));
        Add(command, "$notified", NullableDate(notification.NotifiedAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<BeerStats?> GetBeerStatsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        ulong discordUserId) {
        var command = CreateCommand(connection, transaction, """
            SELECT guild_id, discord_user_id, beers_given_to_bot, beers_bought_by_bot,
                   first_beer_at, last_beer_at, last_bot_beer_at,
                   beers_received_from_members, beers_given_to_members,
                   last_member_beer_received_at, last_member_beer_given_at
            FROM beer_stats
            WHERE guild_id = $guild AND discord_user_id = $user LIMIT 1;
            """);
        Add(command, "$guild", Id(guildId));
        Add(command, "$user", Id(discordUserId));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadBeerStats(reader) : null;
    }

    private static BeerStats ReadBeerStats(SqliteDataReader reader) =>
        new(
            ParseId(reader.GetString(0)),
            ParseId(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetInt32(3),
            ParseDate(reader.GetString(4)),
            ParseDate(reader.GetString(5)),
            NullableDate(reader, 6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            NullableDate(reader, 9),
            NullableDate(reader, 10));

    private static async Task UpsertBeerStatsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BeerStats stats) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO beer_stats
                (guild_id, discord_user_id, beers_given_to_bot, beers_bought_by_bot,
                 first_beer_at, last_beer_at, last_bot_beer_at,
                 beers_received_from_members, beers_given_to_members,
                 last_member_beer_received_at, last_member_beer_given_at)
            VALUES ($guild, $user, $givenBot, $boughtBot, $first, $last, $lastBot,
                    $received, $givenMembers, $lastReceived, $lastGiven)
            ON CONFLICT(guild_id, discord_user_id) DO UPDATE SET
                beers_given_to_bot = excluded.beers_given_to_bot,
                beers_bought_by_bot = excluded.beers_bought_by_bot,
                first_beer_at = excluded.first_beer_at,
                last_beer_at = excluded.last_beer_at,
                last_bot_beer_at = excluded.last_bot_beer_at,
                beers_received_from_members = excluded.beers_received_from_members,
                beers_given_to_members = excluded.beers_given_to_members,
                last_member_beer_received_at = excluded.last_member_beer_received_at,
                last_member_beer_given_at = excluded.last_member_beer_given_at;
            """);
        Add(command, "$guild", Id(stats.GuildId));
        Add(command, "$user", Id(stats.DiscordUserId));
        Add(command, "$givenBot", stats.BeersGivenToBot);
        Add(command, "$boughtBot", stats.BeersBoughtByBot);
        Add(command, "$first", Date(stats.FirstBeerAt));
        Add(command, "$last", Date(stats.LastBeerAt));
        Add(command, "$lastBot", NullableDate(stats.LastBotBeerAt));
        Add(command, "$received", stats.BeersReceivedFromMembers);
        Add(command, "$givenMembers", stats.BeersGivenToMembers);
        Add(command, "$lastReceived", NullableDate(stats.LastMemberBeerReceivedAt));
        Add(command, "$lastGiven", NullableDate(stats.LastMemberBeerGivenAt));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PlottyMemory?> GetPlottyMemoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        ulong discordUserId) {
        var command = CreateCommand(connection, transaction, """
            SELECT guild_id, discord_user_id, total_interactions, mentions, questions_dodged,
                   sarcasm_detected, fox_moments, beers, registrations, mood_checks,
                   excuses, wisdom_requests, first_seen_at, last_seen_at
            FROM plotty_memories
            WHERE guild_id = $guild AND discord_user_id = $user LIMIT 1;
            """);
        Add(command, "$guild", Id(guildId));
        Add(command, "$user", Id(discordUserId));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadPlottyMemory(reader) : null;
    }

    private static PlottyMemory ReadPlottyMemory(SqliteDataReader reader) =>
        new(
            ParseId(reader.GetString(0)),
            ParseId(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            ParseDate(reader.GetString(12)),
            ParseDate(reader.GetString(13)));

    private static async Task UpsertPlottyMemoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlottyMemory memory) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO plotty_memories
                (guild_id, discord_user_id, total_interactions, mentions, questions_dodged,
                 sarcasm_detected, fox_moments, beers, registrations, mood_checks,
                 excuses, wisdom_requests, first_seen_at, last_seen_at)
            VALUES ($guild, $user, $total, $mentions, $dodged, $sarcasm, $fox, $beers,
                    $registrations, $mood, $excuses, $wisdom, $first, $last)
            ON CONFLICT(guild_id, discord_user_id) DO UPDATE SET
                total_interactions = excluded.total_interactions,
                mentions = excluded.mentions,
                questions_dodged = excluded.questions_dodged,
                sarcasm_detected = excluded.sarcasm_detected,
                fox_moments = excluded.fox_moments,
                beers = excluded.beers,
                registrations = excluded.registrations,
                mood_checks = excluded.mood_checks,
                excuses = excluded.excuses,
                wisdom_requests = excluded.wisdom_requests,
                first_seen_at = excluded.first_seen_at,
                last_seen_at = excluded.last_seen_at;
            """);
        Add(command, "$guild", Id(memory.GuildId));
        Add(command, "$user", Id(memory.DiscordUserId));
        Add(command, "$total", memory.TotalInteractions);
        Add(command, "$mentions", memory.Mentions);
        Add(command, "$dodged", memory.QuestionsDodged);
        Add(command, "$sarcasm", memory.SarcasmDetected);
        Add(command, "$fox", memory.FoxMoments);
        Add(command, "$beers", memory.Beers);
        Add(command, "$registrations", memory.Registrations);
        Add(command, "$mood", memory.MoodChecks);
        Add(command, "$excuses", memory.Excuses);
        Add(command, "$wisdom", memory.WisdomRequests);
        Add(command, "$first", Date(memory.FirstSeenAt));
        Add(command, "$last", Date(memory.LastSeenAt));
        await command.ExecuteNonQueryAsync();
    }

    private static PlottyConversationState ReadConversation(SqliteDataReader reader) =>
        new(
            ParseId(reader.GetString(0)),
            ParseId(reader.GetString(1)),
            reader.GetString(2),
            reader.GetInt32(3),
            ParseDate(reader.GetString(4)),
            ParseDate(reader.GetString(5)));

    private static async Task UpsertConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlottyConversationState conversation) {
        var command = CreateCommand(connection, transaction, """
            INSERT INTO plotty_conversations
                (guild_id, discord_user_id, last_topic, turns, first_seen_at, last_seen_at)
            VALUES ($guild, $user, $topic, $turns, $first, $last)
            ON CONFLICT(guild_id, discord_user_id) DO UPDATE SET
                last_topic = excluded.last_topic,
                turns = excluded.turns,
                first_seen_at = excluded.first_seen_at,
                last_seen_at = excluded.last_seen_at;
            """);
        Add(command, "$guild", Id(conversation.GuildId));
        Add(command, "$user", Id(conversation.DiscordUserId));
        Add(command, "$topic", conversation.LastTopic);
        Add(command, "$turns", conversation.Turns);
        Add(command, "$first", Date(conversation.FirstSeenAt));
        Add(command, "$last", Date(conversation.LastSeenAt));
        await command.ExecuteNonQueryAsync();
    }

    private static DemeritEntry ReadDemerit(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            ParseId(reader.GetString(1)),
            ParseId(reader.GetString(2)),
            reader.GetInt32(3),
            reader.GetString(4),
            NullableString(reader, 5),
            NullableString(reader, 6),
            NullableString(reader, 7),
            ParseDate(reader.GetString(8)),
            ParseDate(reader.GetString(9)),
            NullableDate(reader, 10));

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql) {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong ParseId(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);
    private static string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static object? NullableDate(DateTimeOffset? value) => value is null ? null : Date(value.Value);
    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));
    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task WriteAtomicTextAsync(string path, string content) {
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class LegacyState {
        public List<RegisteredEid> RegisteredEids { get; set; } = [];
        public List<DemeritEntry> Demerits { get; set; } = [];
        public List<MissingJoinAlert> MissingJoinAlerts { get; set; } = [];
        public List<FirstCoopAward> FirstCoopAwards { get; set; } = [];
        public List<WeeklyTokenLeaderboardPost> WeeklyTokenLeaderboardPosts { get; set; } = [];
        public List<ContractLateNotice> ContractLateNotices { get; set; } = [];
        public List<ShipReturnNotification> ShipReturnNotifications { get; set; } = [];
        public List<BeerStats> BeerStats { get; set; } = [];
        public List<BeerGiftLog> BeerGiftLogs { get; set; } = [];
        public List<PlottyMemory> PlottyMemories { get; set; } = [];
        public List<PlottyConversationState> PlottyConversations { get; set; } = [];
    }

    private const string SchemaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;

        CREATE TABLE IF NOT EXISTS metadata (
            key TEXT PRIMARY KEY COLLATE NOCASE,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS registered_eids (
            guild_id TEXT NOT NULL,
            discord_user_id TEXT NOT NULL,
            eid_hash TEXT NOT NULL COLLATE NOCASE,
            encrypted_eid TEXT NOT NULL,
            egg_name TEXT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (guild_id, eid_hash)
        );
        CREATE INDEX IF NOT EXISTS ix_registered_eids_guild_user
            ON registered_eids (guild_id, discord_user_id, updated_at DESC);

        CREATE TABLE IF NOT EXISTS demerits (
            id TEXT PRIMARY KEY,
            guild_id TEXT NOT NULL,
            discord_user_id TEXT NOT NULL,
            amount INTEGER NOT NULL,
            reason TEXT NOT NULL,
            contract_id TEXT NULL,
            player_name TEXT NULL,
            source_key TEXT NULL COLLATE NOCASE,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            removed_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_demerits_active_user
            ON demerits (guild_id, discord_user_id, removed_at, expires_at);
        CREATE INDEX IF NOT EXISTS ix_demerits_source
            ON demerits (guild_id, source_key);

        CREATE TABLE IF NOT EXISTS missing_join_alerts (
            key TEXT PRIMARY KEY COLLATE NOCASE,
            posted_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS first_coop_awards (
            key TEXT PRIMARY KEY COLLATE NOCASE,
            guild_id TEXT NOT NULL,
            contract_id TEXT NOT NULL,
            coop_code TEXT NOT NULL,
            awarded_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS weekly_token_leaderboard_posts (
            guild_id TEXT NOT NULL,
            week_key TEXT NOT NULL COLLATE NOCASE,
            posted_at TEXT NOT NULL,
            PRIMARY KEY (guild_id, week_key)
        );

        CREATE TABLE IF NOT EXISTS contract_late_notices (
            guild_id TEXT NOT NULL,
            discord_user_id TEXT NOT NULL,
            contract_key TEXT NOT NULL COLLATE NOCASE,
            contract_id TEXT NULL,
            eta TEXT NULL,
            note TEXT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            PRIMARY KEY (guild_id, discord_user_id, contract_key)
        );
        CREATE INDEX IF NOT EXISTS ix_contract_late_notices_active
            ON contract_late_notices (guild_id, contract_id, expires_at);

        CREATE TABLE IF NOT EXISTS ship_return_notifications (
            key TEXT PRIMARY KEY COLLATE NOCASE,
            guild_id TEXT NOT NULL,
            discord_user_id TEXT NOT NULL,
            eid_hash TEXT NOT NULL,
            mission_key TEXT NOT NULL,
            ship_name TEXT NOT NULL,
            return_at TEXT NOT NULL,
            created_at TEXT NOT NULL,
            notified_at TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_ship_notifications_due
            ON ship_return_notifications (guild_id, notified_at, return_at);

        CREATE TABLE IF NOT EXISTS beer_stats (
            guild_id TEXT NOT NULL,
            discord_user_id TEXT NOT NULL,
            beers_given_to_bot INTEGER NOT NULL,
            beers_bought_by_bot INTEGER NOT NULL,
            first_beer_at TEXT NOT NULL,
            last_beer_at TEXT NOT NULL,
            last_bot_beer_at TEXT NULL,
            beers_received_from_members INTEGER NOT NULL,
            beers_given_to_members INTEGER NOT NULL,
            last_member_beer_received_at TEXT NULL,
            last_member_beer_given_at TEXT NULL,
            PRIMARY KEY (guild_id, discord_user_id)
        );

        CREATE TABLE IF NOT EXISTS beer_gift_logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            guild_id TEXT NOT NULL,
            giver_discord_user_id TEXT NOT NULL,
            recipient_discord_user_id TEXT NOT NULL,
            gifted_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_beer_gift_logs_cooldown
            ON beer_gift_logs (guild_id, giver_discord_user_id, recipient_discord_user_id, gifted_at DESC);

        CREATE TABLE IF NOT EXISTS plotty_memories (
            guild_id TEXT NOT NULL,
            discord_user_id TEXT NOT NULL,
            total_interactions INTEGER NOT NULL,
            mentions INTEGER NOT NULL,
            questions_dodged INTEGER NOT NULL,
            sarcasm_detected INTEGER NOT NULL,
            fox_moments INTEGER NOT NULL,
            beers INTEGER NOT NULL,
            registrations INTEGER NOT NULL,
            mood_checks INTEGER NOT NULL,
            excuses INTEGER NOT NULL,
            wisdom_requests INTEGER NOT NULL,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            PRIMARY KEY (guild_id, discord_user_id)
        );

        CREATE TABLE IF NOT EXISTS plotty_conversations (
            guild_id TEXT NOT NULL,
            discord_user_id TEXT NOT NULL,
            last_topic TEXT NOT NULL,
            turns INTEGER NOT NULL,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            PRIMARY KEY (guild_id, discord_user_id)
        );
        CREATE INDEX IF NOT EXISTS ix_plotty_conversations_last_seen
            ON plotty_conversations (last_seen_at);
        """;
}
