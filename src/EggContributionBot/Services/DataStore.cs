using System.Text.Json;
using EggContribBot;

public sealed class DataStore {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SecureText _secureText;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DataStore(string path, SecureText secureText) {
        _path = Path.GetFullPath(path);
        _secureText = secureText;
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
    }

    public async Task LinkAsync(EggUserLink link) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            state.Links.RemoveAll(l =>
                l.GuildId == link.GuildId &&
                l.EggId.Equals(link.EggId, StringComparison.OrdinalIgnoreCase));
            state.Links.Add(link);
            await SaveAsync(state);
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EggUserLink>> GetLinksAsync(ulong guildId) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            return state.Links.Where(l => l.GuildId == guildId).ToList();
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, EggUserLink>> GetLinksByEggIdAsync(ulong guildId) {
        var links = await GetLinksAsync(guildId);
        return links
            .GroupBy(l => l.EggId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveRegisteredEidAsync(ulong guildId, ulong discordUserId, string eid, string? eggName) {
        var normalized = EggIncClient.NormalizeEggId(eid);
        var hash = SecureText.Sha256(normalized);
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            state.RegisteredEids.RemoveAll(e => e.GuildId == guildId && e.EidHash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            state.RegisteredEids.Add(new RegisteredEid(
                guildId,
                discordUserId,
                hash,
                _secureText.Encrypt(normalized),
                eggName,
                DateTimeOffset.UtcNow));
            await SaveAsync(state);
        } finally {
            _gate.Release();
        }
    }

    public async Task<string?> GetRegisteredEidAsync(ulong guildId, ulong discordUserId) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var record = state.RegisteredEids.LastOrDefault(e => e.GuildId == guildId && e.DiscordUserId == discordUserId);
            return record is null ? null : _secureText.Decrypt(record.EncryptedEid);
        } finally {
            _gate.Release();
        }
    }

    public async Task<RegisteredEggAccount?> GetRegisteredAccountAsync(ulong guildId, ulong discordUserId) {
        var accounts = await GetRegisteredAccountsAsync(guildId, discordUserId);
        return accounts.FirstOrDefault();
    }

    public async Task<IReadOnlyList<RegisteredEggAccount>> GetRegisteredAccountsAsync(ulong guildId, ulong discordUserId) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            return state.RegisteredEids
                .Where(e => e.GuildId == guildId && e.DiscordUserId == discordUserId)
                .OrderByDescending(e => e.UpdatedAt)
                .Select(e => new RegisteredEggAccount(e.DiscordUserId, _secureText.Decrypt(e.EncryptedEid), e.EggName, e.UpdatedAt))
                .ToList();
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RegisteredEggAccount>> GetRegisteredEidsAsync(ulong guildId) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            return state.RegisteredEids
                .Where(e => e.GuildId == guildId)
                .GroupBy(e => e.EidHash, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.UpdatedAt).First())
                .Select(e => new RegisteredEggAccount(e.DiscordUserId, _secureText.Decrypt(e.EncryptedEid), e.EggName, e.UpdatedAt))
                .ToList();
        } finally {
            _gate.Release();
        }
    }

    public async Task<int> AddDemeritsAsync(
        ulong guildId,
        ulong discordUserId,
        int amount,
        string reason,
        string? contractId,
        string? sourceKey,
        string? playerName = null) {
        amount = Math.Max(1, amount);
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            for(var i = 0; i < amount; i++) {
                state.Demerits.Add(new DemeritEntry(
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
                    RemovedAt: null));
            }

            await SaveAsync(state);
            return amount;
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DemeritEntry>> AddAutoDemeritsAsync(
        ulong guildId,
        IReadOnlyList<AutoDemeritCandidate> candidates) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var activeSourceKeys = state.Demerits
                .Where(d => d.GuildId == guildId && d.RemovedAt is null && d.ExpiresAt > now && !string.IsNullOrWhiteSpace(d.SourceKey))
                .Select(d => d.SourceKey!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = new List<DemeritEntry>();
            foreach(var candidate in candidates
                .GroupBy(c => c.SourceKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())) {
                if(activeSourceKeys.Contains(candidate.SourceKey)) {
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
                    RemovedAt: null);
                state.Demerits.Add(entry);
                added.Add(entry);
                activeSourceKeys.Add(candidate.SourceKey);
            }

            if(added.Count > 0) {
                await SaveAsync(state);
            }

            return added;
        } finally {
            _gate.Release();
        }
    }

    public async Task<int> RemoveDemeritsAsync(ulong guildId, ulong discordUserId, int amount) {
        amount = Math.Max(1, amount);
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var active = state.Demerits
                .Where(d => d.GuildId == guildId && d.DiscordUserId == discordUserId && d.RemovedAt is null && d.ExpiresAt > now)
                .OrderByDescending(d => d.CreatedAt)
                .Take(amount)
                .ToList();

            foreach(var entry in active) {
                var index = state.Demerits.FindIndex(d => d.Id == entry.Id);
                if(index >= 0) {
                    state.Demerits[index] = entry with { RemovedAt = now };
                }
            }

            if(active.Count > 0) {
                await SaveAsync(state);
            }

            return active.Count;
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DemeritEntry>> GetActiveDemeritsAsync(ulong guildId, ulong? discordUserId = null) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            return state.Demerits
                .Where(d => d.GuildId == guildId && d.RemovedAt is null && d.ExpiresAt > now)
                .Where(d => discordUserId is null || d.DiscordUserId == discordUserId.Value)
                .OrderBy(d => d.ExpiresAt)
                .ToList();
        } finally {
            _gate.Release();
        }
    }

    public async Task<bool> HasMissingJoinAlertAsync(string key) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            return state.MissingJoinAlerts.Any(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        } finally {
            _gate.Release();
        }
    }

    public async Task RecordMissingJoinAlertAsync(string key) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            if(state.MissingJoinAlerts.Any(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
            state.MissingJoinAlerts.RemoveAll(a => a.PostedAt < cutoff);
            state.MissingJoinAlerts.Add(new MissingJoinAlert(key, DateTimeOffset.UtcNow));
            await SaveAsync(state);
        } finally {
            _gate.Release();
        }
    }

    public async Task<ContractLateNotice> RecordContractLateNoticeAsync(
        ulong guildId,
        ulong discordUserId,
        string? contractId,
        string? eta,
        string? note) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var normalizedContractId = string.IsNullOrWhiteSpace(contractId) ? null : contractId.Trim();
            state.ContractLateNotices.RemoveAll(n =>
                n.GuildId == guildId &&
                n.DiscordUserId == discordUserId &&
                string.Equals(n.ContractId ?? "", normalizedContractId ?? "", StringComparison.OrdinalIgnoreCase));
            state.ContractLateNotices.RemoveAll(n => n.ExpiresAt <= now);

            var notice = new ContractLateNotice(
                guildId,
                discordUserId,
                normalizedContractId,
                eta,
                note,
                now,
                now.AddHours(48));
            state.ContractLateNotices.Add(notice);
            await SaveAsync(state);
            return notice;
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlySet<ulong>> GetActiveContractLateNoticeUserIdsAsync(ulong guildId, string contractId) {
        await _gate.WaitAsync();
        try {
            var now = DateTimeOffset.UtcNow;
            var state = await LoadAsync();
            state.ContractLateNotices.RemoveAll(n => n.ExpiresAt <= now);
            return state.ContractLateNotices
                .Where(n => n.GuildId == guildId && n.ExpiresAt > now)
                .Where(n => string.IsNullOrWhiteSpace(n.ContractId) || string.Equals(n.ContractId, contractId, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.DiscordUserId)
                .ToHashSet();
        } finally {
            _gate.Release();
        }
    }

    public async Task<int> RemoveContractLateNoticesAsync(ulong guildId, ulong discordUserId, string? contractId) {
        await _gate.WaitAsync();
        try {
            var now = DateTimeOffset.UtcNow;
            var state = await LoadAsync();
            var removed = state.ContractLateNotices.RemoveAll(n =>
                n.GuildId == guildId &&
                n.DiscordUserId == discordUserId &&
                n.ExpiresAt > now &&
                (string.IsNullOrWhiteSpace(contractId) || string.Equals(n.ContractId, contractId, StringComparison.OrdinalIgnoreCase)));
            state.ContractLateNotices.RemoveAll(n => n.ExpiresAt <= now);

            if(removed > 0) {
                await SaveAsync(state);
            }

            return removed;
        } finally {
            _gate.Release();
        }
    }

    public async Task UpsertShipReturnNotificationAsync(ShipReturnNotification notification) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            state.ShipReturnNotifications.RemoveAll(n =>
                n.Key.Equals(notification.Key, StringComparison.OrdinalIgnoreCase) ||
                (n.NotifiedAt is not null && n.NotifiedAt < now.AddDays(-7)) ||
                (n.NotifiedAt is null && n.ReturnAt < now.AddDays(-2)));
            state.ShipReturnNotifications.Add(notification);
            await SaveAsync(state);
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShipReturnNotification>> GetDueShipReturnNotificationsAsync(ulong guildId, DateTimeOffset now) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            return state.ShipReturnNotifications
                .Where(n => n.GuildId == guildId && n.NotifiedAt is null && n.ReturnAt <= now)
                .OrderBy(n => n.ReturnAt)
                .ToList();
        } finally {
            _gate.Release();
        }
    }

    public async Task MarkShipReturnNotificationSentAsync(string key, DateTimeOffset sentAt) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var index = state.ShipReturnNotifications.FindIndex(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if(index < 0) {
                return;
            }

            state.ShipReturnNotifications[index] = state.ShipReturnNotifications[index] with { NotifiedAt = sentAt };
            await SaveAsync(state);
        } finally {
            _gate.Release();
        }
    }

    public async Task<BeerAttemptResult> TryAddPlottyBeerAsync(ulong guildId, ulong discordUserId, bool botBuysBack, bool bypassCooldown = false) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var index = state.BeerStats.FindIndex(b => b.GuildId == guildId && b.DiscordUserId == discordUserId);
            var current = index >= 0
                ? state.BeerStats[index]
                : new BeerStats(guildId, discordUserId, 0, 0, now, now, null);
            var nextBeerAt = current.LastBeerAt.AddHours(1);
            if(!bypassCooldown && index >= 0 && nextBeerAt > now) {
                return new BeerAttemptResult(false, current, nextBeerAt - now);
            }

            var updated = current with {
                BeersGivenToBot = current.BeersGivenToBot + 1,
                BeersBoughtByBot = current.BeersBoughtByBot + (botBuysBack ? 1 : 0),
                LastBeerAt = now,
                LastBotBeerAt = botBuysBack ? now : current.LastBotBeerAt
            };

            if(index >= 0) {
                state.BeerStats[index] = updated;
            } else {
                state.BeerStats.Add(updated);
            }

            await SaveAsync(state);
            return new BeerAttemptResult(true, updated, null);
        } finally {
            _gate.Release();
        }
    }

    public async Task<BeerAttemptResult> TryGiftBeerAsync(ulong guildId, ulong giverDiscordUserId, ulong recipientDiscordUserId, bool bypassCooldown = false) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var lastGift = state.BeerGiftLogs
                .Where(g => g.GuildId == guildId && g.GiverDiscordUserId == giverDiscordUserId && g.RecipientDiscordUserId == recipientDiscordUserId)
                .OrderByDescending(g => g.GiftedAt)
                .FirstOrDefault();
            var nextGiftAt = lastGift?.GiftedAt.AddDays(1);

            var recipientIndex = state.BeerStats.FindIndex(b => b.GuildId == guildId && b.DiscordUserId == recipientDiscordUserId);
            var recipient = recipientIndex >= 0
                ? state.BeerStats[recipientIndex]
                : new BeerStats(guildId, recipientDiscordUserId, 0, 0, now, now, null);

            if(!bypassCooldown && nextGiftAt is not null && nextGiftAt > now) {
                return new BeerAttemptResult(false, recipient, nextGiftAt.Value - now);
            }

            var giverIndex = state.BeerStats.FindIndex(b => b.GuildId == guildId && b.DiscordUserId == giverDiscordUserId);
            var giver = giverIndex >= 0
                ? state.BeerStats[giverIndex]
                : new BeerStats(guildId, giverDiscordUserId, 0, 0, now, now, null);

            var updatedRecipient = recipient with {
                BeersReceivedFromMembers = recipient.BeersReceivedFromMembers + 1,
                LastMemberBeerReceivedAt = now
            };
            var updatedGiver = giver with {
                BeersGivenToMembers = giver.BeersGivenToMembers + 1,
                LastMemberBeerGivenAt = now
            };

            if(recipientIndex >= 0) {
                state.BeerStats[recipientIndex] = updatedRecipient;
            } else {
                state.BeerStats.Add(updatedRecipient);
            }

            if(giverDiscordUserId == recipientDiscordUserId) {
                var selfIndex = state.BeerStats.FindIndex(b => b.GuildId == guildId && b.DiscordUserId == giverDiscordUserId);
                state.BeerStats[selfIndex] = updatedRecipient with {
                    BeersGivenToMembers = updatedRecipient.BeersGivenToMembers + 1,
                    LastMemberBeerGivenAt = now
                };
            } else if(giverIndex >= 0) {
                state.BeerStats[giverIndex] = updatedGiver;
            } else {
                state.BeerStats.Add(updatedGiver);
            }

            state.BeerGiftLogs.Add(new BeerGiftLog(guildId, giverDiscordUserId, recipientDiscordUserId, now));
            await SaveAsync(state);
            return new BeerAttemptResult(true, updatedRecipient, null);
        } finally {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BeerStats>> GetBeerLeaderboardAsync(ulong guildId) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            return state.BeerStats
                .Where(b => b.GuildId == guildId)
                .OrderByDescending(b => b.BeersBoughtByBot)
                .ThenByDescending(b => b.BeersGivenToBot)
                .ThenBy(b => b.FirstBeerAt)
                .ToList();
        } finally {
            _gate.Release();
        }
    }

    public async Task<PlottyMemory> RecordPlottyInteractionAsync(ulong guildId, ulong discordUserId, string interactionType) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var index = state.PlottyMemories.FindIndex(m => m.GuildId == guildId && m.DiscordUserId == discordUserId);
            var current = index >= 0
                ? state.PlottyMemories[index]
                : new PlottyMemory(guildId, discordUserId, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, now, now);

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

            if(index >= 0) {
                state.PlottyMemories[index] = updated;
            } else {
                state.PlottyMemories.Add(updated);
            }

            await SaveAsync(state);
            return updated;
        } finally {
            _gate.Release();
        }
    }

    public async Task<PlottyConversationState?> GetPlottyConversationAsync(ulong guildId, ulong discordUserId) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
            return state.PlottyConversations
                .Where(c => c.GuildId == guildId && c.DiscordUserId == discordUserId && c.LastSeenAt >= cutoff)
                .OrderByDescending(c => c.LastSeenAt)
                .FirstOrDefault();
        } finally {
            _gate.Release();
        }
    }

    public async Task<PlottyConversationState> RecordPlottyConversationAsync(ulong guildId, ulong discordUserId, string topic) {
        await _gate.WaitAsync();
        try {
            var state = await LoadAsync();
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-14);
            state.PlottyConversations.RemoveAll(c => c.LastSeenAt < cutoff);

            var cleanTopic = string.IsNullOrWhiteSpace(topic) ? "chat" : topic.Trim();
            if(cleanTopic.Length > 80) {
                cleanTopic = cleanTopic[..80];
            }

            var index = state.PlottyConversations.FindIndex(c => c.GuildId == guildId && c.DiscordUserId == discordUserId);
            var current = index >= 0
                ? state.PlottyConversations[index]
                : new PlottyConversationState(guildId, discordUserId, cleanTopic, 0, now, now);
            var updated = current with {
                LastTopic = cleanTopic,
                Turns = current.Turns + 1,
                LastSeenAt = now
            };

            if(index >= 0) {
                state.PlottyConversations[index] = updated;
            } else {
                state.PlottyConversations.Add(updated);
            }

            await SaveAsync(state);
            return updated;
        } finally {
            _gate.Release();
        }
    }

    private async Task<State> LoadAsync() {
        if(!File.Exists(_path)) {
            return new State();
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<State>(stream, JsonOptions) ?? new State();
    }

    private async Task SaveAsync(State state) {
        var tmp = _path + ".tmp";
        await using(var stream = File.Create(tmp)) {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class State {
        public List<EggUserLink> Links { get; set; } = [];
        public List<RegisteredEid> RegisteredEids { get; set; } = [];
        public List<DemeritEntry> Demerits { get; set; } = [];
        public List<MissingJoinAlert> MissingJoinAlerts { get; set; } = [];
        public List<ContractLateNotice> ContractLateNotices { get; set; } = [];
        public List<ShipReturnNotification> ShipReturnNotifications { get; set; } = [];
        public List<BeerStats> BeerStats { get; set; } = [];
        public List<BeerGiftLog> BeerGiftLogs { get; set; } = [];
        public List<PlottyMemory> PlottyMemories { get; set; } = [];
        public List<PlottyConversationState> PlottyConversations { get; set; } = [];
    }
}
