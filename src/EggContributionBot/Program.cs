using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using Discord;
using Discord.WebSocket;
using EggContribBot;
using EggContribBot.Proto;

var settings = BotSettings.Load();
var token = settings.Discord.Token
    ?? Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")
    ?? throw new InvalidOperationException("Set Discord:Token in appsettings.json or DISCORD_BOT_TOKEN.");

var secureText = new SecureText(settings.Storage.KeyPath);
var dataStore = new DataStore(settings.Storage.DataPath, secureText);
var eggClient = new EggIncClient();
var wikiClient = new EggWikiClient();
var missingJoinMonitorStarted = false;

var client = new DiscordSocketClient(new DiscordSocketConfig {
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
    AlwaysDownloadUsers = true
});

client.Log += msg => {
    Console.WriteLine(msg.ToString());
    return Task.CompletedTask;
};

client.Ready += async () => {
    var commands = BuildCommands().Select(c => c.Build()).ToArray();
    if(settings.Discord.ParsedGuildId is { } guildId) {
        var guild = client.GetGuild(guildId);
        if(guild is null) {
            Console.WriteLine($"Plotty is connected, but guild {guildId} was not found. Is Plotty invited?");
            return;
        }

        await client.Rest.BulkOverwriteGlobalCommands([]);
        await guild.BulkOverwriteApplicationCommandAsync(commands);
        Console.WriteLine($"Logged in as {client.CurrentUser}; registered {commands.Length} commands in {guild.Name}.");
        if(!missingJoinMonitorStarted) {
            missingJoinMonitorStarted = true;
            _ = Task.Run(() => MonitorMissingCoopJoinsAsync(guildId));
        }
    } else {
        await client.Rest.BulkOverwriteGlobalCommands(commands);
        Console.WriteLine($"Logged in as {client.CurrentUser}; registered {commands.Length} global commands.");
    }
};

client.SlashCommandExecuted += async command => {
    try {
        if(command.GuildId is null) {
            await command.RespondAsync("Use Plotty inside a server.", ephemeral: true);
            return;
        }

        if(command.CommandName.StartsWith("admin-", StringComparison.OrdinalIgnoreCase) && !IsStaffChannel(command.Channel)) {
            await command.RespondAsync("Admin commands can only be used in the `Staff` channel.", ephemeral: true);
            return;
        }

        switch(command.CommandName) {
            case "contract":
                await HandleContractAsync(command);
                break;
            case "register-eid":
                await HandleRegisterEidAsync(command);
                break;
            case "rates":
                await HandleRatesAsync(command);
                break;
            case "admin-rates-all":
                await HandleRatesAllAsync(command);
                break;
            case "admin-dashboard":
                await HandleDashboardAsync(command);
                break;
            case "admin-demerit":
                await HandleDemeritAsync(command);
                break;
            case "player":
                await HandlePlayerAsync(command);
                break;
            case "eggs-laid":
                await HandleEggsLaidAsync(command);
                break;
            case "contract-artifacts":
                await HandleContractArtifactsAsync(command);
                break;
            case "beer-plotty":
                await HandleBeerPlottyAsync(command);
                break;
            case "beer-user":
                await HandleBeerUserAsync(command);
                break;
            case "beerleader":
                await HandleBeerLeaderAsync(command);
                break;
            case "plotty-mood":
                await HandlePlottyMoodAsync(command);
                break;
            case "plotty-excuses":
                await HandlePlottyExcusesAsync(command);
                break;
            case "plotty-wisdom":
                await HandlePlottyWisdomAsync(command);
                break;
            case "admin-plotty-speak":
                await HandleAdminPlottySpeakAsync(command);
                break;
            case "help":
                await HandleHelpAsync(command);
                break;
        }
    } catch(Exception ex) {
        Console.WriteLine(ex);
        if(command.HasResponded) {
            await command.FollowupAsync("Plotty tripped over that command. Please try again.", ephemeral: true);
        } else {
            await command.RespondAsync("Plotty tripped over that command. Please try again.", ephemeral: true);
        }
    }
};

client.ButtonExecuted += async component => {
    try {
        if(component.GuildId is null) {
            return;
        }

        if(component.Data.CustomId.StartsWith("player-refresh:", StringComparison.Ordinal)) {
            var parts = component.Data.CustomId.Split(':', 2);
            if(parts.Length != 2 || !ulong.TryParse(parts[1], out var discordUserId)) {
                await component.RespondAsync("That refresh button is no longer valid.", ephemeral: true);
                return;
            }

            var accounts = await dataStore.GetRegisteredAccountsAsync(component.GuildId.Value, discordUserId);
            if(accounts.Count == 0) {
                await component.RespondAsync("That player does not have an EID registered anymore.", ephemeral: true);
                return;
            }

            var user = client.GetGuild(component.GuildId.Value)?.GetUser(discordUserId);
            var displayName = user?.DisplayName ?? accounts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.EggName))?.EggName ?? "Registered Player";
            var embeds = new List<Embed>();
            foreach(var account in accounts.Take(10)) {
                var backup = await eggClient.GetBackupAsync(account.Eid);
                embeds.Add(await BuildPlayerEmbedAsync(account, displayName, backup));
            }

            await component.UpdateAsync(message => {
                message.Embeds = embeds.ToArray();
                message.Components = BuildPlayerComponents(discordUserId);
            });
            return;
        }

        if(component.Data.CustomId.StartsWith("dashboard-", StringComparison.Ordinal)) {
            await HandleDashboardButtonAsync(component);
        }
    } catch(Exception ex) {
        Console.WriteLine(ex);
        if(component.HasResponded) {
            await component.FollowupAsync("Plotty tripped over that button. Please try again.", ephemeral: true);
        } else {
            await component.RespondAsync("Plotty tripped over that button. Please try again.", ephemeral: true);
        }
    }
};

client.ModalSubmitted += async modal => {
    try {
        if(modal.Data.CustomId != "register-eid-modal") {
            return;
        }

        await HandleRegisterEidModalAsync(modal);
    } catch(Exception ex) {
        Console.WriteLine(ex);
        if(modal.HasResponded) {
            await modal.FollowupAsync("Plotty could not save that EID. Please try again.", ephemeral: true);
        } else {
            await modal.RespondAsync("Plotty could not save that EID. Please try again.", ephemeral: true);
        }
    }
};

client.MessageReceived += async message => {
    try {
        if(message.Author.IsBot || message.Channel is not SocketGuildChannel) {
            return;
        }

        if(message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id)) {
            await message.Channel.SendMessageAsync(RandomPlottyMentionResponse(message.Author.Mention, message.Content));
            return;
        }

        if(message.Content.Contains("what the fox", StringComparison.OrdinalIgnoreCase)) {
            await message.Channel.SendMessageAsync(RandomFoxResponse(message.Author.Mention));
            return;
        }

        if(LooksLikeSarcasm(message.Content) && Random.Shared.Next(3) == 0) {
            await message.Channel.SendMessageAsync(RandomSarcasmResponse(message.Author.Mention));
        }
    } catch(Exception ex) {
        Console.WriteLine(ex);
    }
};

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
await Task.Delay(Timeout.Infinite);

IEnumerable<SlashCommandBuilder> BuildCommands() {
    yield return new SlashCommandBuilder()
        .WithName("contract")
        .WithDescription("Show each player's contribution rate for an Egg Inc co-op contract.")
        .AddOption("contract-id", ApplicationCommandOptionType.String, "The contract's identifier, e.g. first-light-8.", isRequired: true)
        .AddOption("coop-code", ApplicationCommandOptionType.String, "The co-op code/name players joined with.", isRequired: true);

    yield return new SlashCommandBuilder()
        .WithName("register-eid")
        .WithDescription("Privately register your Egg Inc ID so Plotty can pull your contracts.");

    yield return new SlashCommandBuilder()
        .WithName("rates")
        .WithDescription("Show your registered EID co-op rates.");

    yield return new SlashCommandBuilder()
        .WithName("admin-rates-all")
        .WithDescription("Show all registered EID co-op rates from lowest to highest contribution.");

    yield return new SlashCommandBuilder()
        .WithName("admin-dashboard")
        .WithDescription("Show a staff overview of registered players, low rates, sync issues, and likely unboosted players.");

    yield return new SlashCommandBuilder()
        .WithName("admin-demerit")
        .WithDescription("Add, remove, or list player demerits.")
        .AddOption("action", ApplicationCommandOptionType.String, "add, remove, or list.", isRequired: true)
        .AddOption("member", ApplicationCommandOptionType.User, "Discord member.", isRequired: true)
        .AddOption("amount", ApplicationCommandOptionType.Integer, "Number of demerits. Default is 1.", isRequired: false)
        .AddOption("reason", ApplicationCommandOptionType.String, "Optional reason.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("player")
        .WithDescription("Show a registered player's recent contribution profile.")
        .AddOption("member", ApplicationCommandOptionType.User, "Registered Discord member. Defaults to you.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("eggs-laid")
        .WithDescription("Show lifetime eggs laid by farm for your registered Egg Inc ID.");

    yield return new SlashCommandBuilder()
        .WithName("contract-artifacts")
        .WithDescription("Suggest contract artifact sets from your inventory and current contract equips.");

    yield return new SlashCommandBuilder()
        .WithName("beer-plotty")
        .WithDescription("Buy Plotty a beer. Sometimes Plotty buys you one back.");

    yield return new SlashCommandBuilder()
        .WithName("beer-user")
        .WithDescription("Gift another member a beer.")
        .AddOption("member", ApplicationCommandOptionType.User, "Member receiving the beer.", isRequired: true);

    yield return new SlashCommandBuilder()
        .WithName("beerleader")
        .WithDescription("Show the Beer Leaderboard.");

    yield return new SlashCommandBuilder()
        .WithName("plotty-mood")
        .WithDescription("Ask Plotty for its current mood, told as an emoji-only story.");

    yield return new SlashCommandBuilder()
        .WithName("plotty-excuses")
        .WithDescription("Ask Plotty why it absolutely cannot reply right now.");

    yield return new SlashCommandBuilder()
        .WithName("plotty-wisdom")
        .WithDescription("Receive a random piece of profound Plotty wisdom.");

    yield return new SlashCommandBuilder()
        .WithName("admin-plotty-speak")
        .WithDescription("Staff only: make Plotty say a message.")
        .AddOption("message", ApplicationCommandOptionType.String, "What Plotty should say.", isRequired: true);

    yield return new SlashCommandBuilder()
        .WithName("help")
        .WithDescription("Ask Plotty an Egg Inc question using the Egg Inc Wiki.")
        .AddOption("question", ApplicationCommandOptionType.String, "What do you want to know?", isRequired: true);

}

async Task HandleContractAsync(SocketSlashCommand command) {
    await command.DeferAsync();

    var contractId = GetString(command, "contract-id");
    var coopCode = GetString(command, "coop-code");
    var status = await eggClient.GetCoopStatusAsync(contractId, coopCode);

    if(status is null) {
        await command.FollowupAsync(
            $"Couldn't find co-op `{coopCode}` for contract `{contractId}`. Double check both values.");
        return;
    }

    await command.FollowupAsync(embed: BuildContributionEmbed(contractId, coopCode, status, showCoopCode: false));
}

async Task HandleRatesAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);
    await HandleRegisteredRatesAsync(command);
}

async Task HandleRegisteredRatesAsync(SocketSlashCommand command) {
    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var runningEmbeds = new List<Embed>();
    var completedEmbeds = new List<Embed>();
    var diagnostics = new List<string>();
    foreach(var account in accounts) {
        var lookup = await eggClient.GetPlayerCoopLookupAsync(account.Eid);
        var accountLabel = AccountDisplayName(account);
        runningEmbeds.AddRange(lookup.Statuses
            .Select(s => BuildContributionEmbed(s.ContractId, s.CoopCode, s.Status, showCoopCode: false, titleSuffix: $"(running - {accountLabel})"))
            .Where(e => e is not null)
            .Cast<Embed>());
        completedEmbeds.AddRange(await BuildRecentCompletedRateEmbedsAsync(account, lookup.Backup, lookup.StatusLookups));
        if(lookup.Statuses.Count == 0) {
            diagnostics.Add($"**{accountLabel}**\n{BuildCoopLookupDiagnostic(lookup)}");
        }
    }

    var embeds = runningEmbeds.Concat(completedEmbeds).ToList();

    if(embeds.Count == 0) {
        await command.FollowupAsync(
            "Plotty could not find any running or recently completed co-op contracts for your registered EID account(s).\n\n" +
            string.Join("\n\n", diagnostics.Take(3)),
            ephemeral: true);
        return;
    }

    var summary = $"Showing `{runningEmbeds.Count}` running contract(s) and `{completedEmbeds.Count}` completed contract(s) from `{accounts.Count}` registered EID account(s).";
    for(var i = 0; i < embeds.Count; i += 10) {
        var batch = embeds.Skip(i).Take(10).ToArray();
        await command.FollowupAsync(text: i == 0 ? summary : null, embeds: batch, ephemeral: true);
    }
}

async Task<IReadOnlyList<Embed>> BuildRecentCompletedRateEmbedsAsync(
    RegisteredEggAccount account,
    Backup? backup,
    IReadOnlyCollection<PlayerCoopStatusLookup> runningLookups) {
    if(backup?.Contracts is null) {
        return [];
    }

    var runningKeys = runningLookups
        .Select(s => (ContractId: s.ContractId.ToLowerInvariant(), CoopCode: s.CoopCode.ToLowerInvariant()))
        .ToHashSet();
    var completedContracts = backup.Contracts.Archive
        .Where(c => !c.Cancelled)
        .Select(c => new PlayerContractCandidate(GetLocalContractId(c), c.CoopIdentifier, c.TimeAccepted))
        .Where(c => !string.IsNullOrWhiteSpace(c.ContractId) && !string.IsNullOrWhiteSpace(c.CoopCode))
        .Where(c => !runningKeys.Contains((c.ContractId.ToLowerInvariant(), c.CoopCode.ToLowerInvariant())))
        .GroupBy(c => (ContractId: c.ContractId.ToLowerInvariant(), CoopCode: c.CoopCode.ToLowerInvariant()))
        .Select(g => g.OrderByDescending(c => c.AcceptedAt).First())
        .OrderByDescending(c => c.AcceptedAt)
        .Take(2)
        .ToList();

    var embeds = new List<Embed>();
    var accountLabel = AccountDisplayName(account);
    foreach(var contract in completedContracts) {
        var status = await eggClient.GetCoopStatusAsync(contract.ContractId, contract.CoopCode);
        var embed = status is null
            ? null
            : BuildContributionEmbed(contract.ContractId, contract.CoopCode, status, showCoopCode: false, titleSuffix: $"(completed - {accountLabel})");
        if(embed is not null) {
            embeds.Add(embed);
        }
    }

    return embeds;
}

async Task HandleRatesAllAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can use admin rates.", ephemeral: true);
        return;
    }

    await command.DeferAsync();

    var accounts = await dataStore.GetRegisteredEidsAsync(command.GuildId!.Value);
    if(accounts.Count == 0) {
        await command.FollowupAsync("No EIDs are registered in this server yet. Have players run `/register-eid` first.", ephemeral: true);
        return;
    }

    var registeredEggIds = accounts
        .Select(a => EggIncClient.NormalizeEggId(a.Eid))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var registeredNames = accounts
        .Select(a => NormalizeName(a.EggName))
        .Where(n => n.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var registeredByEggId = accounts
        .Where(a => !string.IsNullOrWhiteSpace(a.Eid))
        .GroupBy(a => EggIncClient.NormalizeEggId(a.Eid), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    var registeredByName = accounts
        .Where(a => NormalizeName(a.EggName).Length > 0)
        .GroupBy(a => NormalizeName(a.EggName), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    var statuses = new Dictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse>();
    var failed = 0;
    foreach(var account in accounts) {
        var lookup = await eggClient.GetPlayerCoopLookupAsync(account.Eid);
        if(lookup.Statuses.Count == 0) {
            failed++;
            continue;
        }

        foreach(var status in lookup.Statuses) {
            var key = (status.ContractId.ToLowerInvariant(), status.CoopCode.ToLowerInvariant());
            statuses.TryAdd(key, status.Status);
        }
    }

    if(statuses.Count == 0) {
        await command.FollowupAsync(
            $"Plotty checked `{accounts.Count}` registered EID(s), but could not pull any active co-op rates.",
            ephemeral: true);
        return;
    }

    var embeds = statuses.Values
        .GroupBy(s => string.IsNullOrWhiteSpace(s.ContractIdentifier)
            ? "(unknown contract)"
            : s.ContractIdentifier,
            StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key)
        .Take(10)
        .Select(g => BuildRegisteredContractEmbed(g.Key, g, registeredEggIds, registeredNames))
        .Where(e => e is not null)
        .Cast<Embed>()
        .ToArray();

    if(embeds.Length == 0) {
        await command.FollowupAsync(
            $"Plotty found `{statuses.Count}` active co-op(s), but none of their contributors matched a registered EID or registered Egg Inc name.",
            ephemeral: true);
        return;
    }

    var message = failed > 0
        ? $"Showing registered EID players only. `{failed}` registered EID(s) did not return active rates."
        : $"Showing registered EID players only from `{accounts.Count}` registered EID(s).";

    await command.FollowupAsync(text: message, embeds: embeds);
}

async Task HandlePlayerAsync(SocketSlashCommand command) {
    await command.DeferAsync();

    var member = command.Data.Options.FirstOrDefault(o => o.Name == "member")?.Value as SocketGuildUser;
    var discordUserId = member?.Id ?? command.User.Id;
    var displayName = member?.DisplayName ?? command.User.GlobalName ?? command.User.Username;

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, discordUserId);
    if(accounts.Count == 0) {
        await command.FollowupAsync(member is null
            ? "You do not have an EID registered yet. Run `/register-eid` first."
            : $"{member.Mention} does not have an EID registered yet.", ephemeral: true);
        return;
    }

    var embeds = new List<Embed>();
    foreach(var account in accounts.Take(10)) {
        var backup = await eggClient.GetBackupAsync(account.Eid);
        embeds.Add(await BuildPlayerEmbedAsync(account, displayName, backup));
    }

    await command.FollowupAsync(
        text: accounts.Count > 1 ? $"Showing `{embeds.Count}` Egg Inc account(s) tied to {displayName}." : null,
        embeds: embeds.ToArray(),
        components: BuildPlayerComponents(discordUserId));
}

async Task HandleDashboardAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can use the admin dashboard.", ephemeral: true);
        return;
    }

    await command.DeferAsync();

    var dashboard = await BuildDashboardAsync(command.GuildId!.Value);
    if(dashboard.Embeds.Count == 0) {
        await command.FollowupAsync(dashboard.Message, ephemeral: true);
        return;
    }

    await command.FollowupAsync(text: dashboard.Message, embeds: dashboard.Embeds.ToArray(), components: BuildDashboardComponents());
}

async Task HandleDashboardButtonAsync(SocketMessageComponent component) {
    if(!IsStaffChannel(component.Channel)) {
        await component.RespondAsync("Admin dashboard controls can only be used in the `Staff` channel.", ephemeral: true);
        return;
    }

    var staffUser = component.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await component.RespondAsync("Only members with the Staff role can use admin dashboard controls.", ephemeral: true);
        return;
    }

    switch(component.Data.CustomId) {
        case "dashboard-refresh":
            await component.DeferAsync();
            var refreshedDashboard = await BuildDashboardAsync(component.GuildId!.Value);
            if(refreshedDashboard.Embeds.Count == 0) {
                await component.FollowupAsync(refreshedDashboard.Message, ephemeral: true);
                return;
            }

            await component.Message.ModifyAsync(message => {
                message.Content = refreshedDashboard.Message;
                message.Embeds = refreshedDashboard.Embeds.ToArray();
                message.Components = BuildDashboardComponents();
            });
            break;

        case "dashboard-full-list":
            await component.DeferAsync(ephemeral: true);
            var dashboard = await BuildDashboardAsync(component.GuildId!.Value);
            if(dashboard.Embeds.Count == 0) {
                await component.FollowupAsync(dashboard.Message, ephemeral: true);
                return;
            }

            await component.FollowupAsync(BuildDashboardFullList(dashboard.Rows), ephemeral: true);
            break;

    }
}

async Task MonitorMissingCoopJoinsAsync(ulong guildId) {
    await Task.Delay(TimeSpan.FromMinutes(2));
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
    while(true) {
        try {
            await CheckMissingCoopJoinsAsync(guildId);
        } catch(Exception ex) {
            Console.WriteLine($"Missing coop join monitor failed: {ex}");
        }

        await timer.WaitForNextTickAsync();
    }
}

async Task CheckMissingCoopJoinsAsync(ulong guildId) {
    var guild = client.GetGuild(guildId);
    if(guild is null) {
        return;
    }

    var staffChannel = FindStaffTextChannel(guild);
    if(staffChannel is null) {
        Console.WriteLine("Missing coop join monitor could not find a Staff text channel.");
        return;
    }

    var now = DateTimeOffset.UtcNow;
    var currentContracts = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.CoopAllowed)
        .Where(c => c.StartTime > 0)
        .Where(c => DateTimeOffset.FromUnixTimeSeconds((long)c.StartTime).AddHours(6) <= now)
        .Where(c => c.ExpirationTime <= 0 || DateTimeOffset.FromUnixTimeSeconds((long)c.ExpirationTime) > now)
        .GroupBy(c => c.Identifier, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(c => c.StartTime).First())
        .ToList();
    if(currentContracts.Count == 0) {
        return;
    }

    var accounts = await dataStore.GetRegisteredEidsAsync(guildId);
    if(accounts.Count == 0) {
        return;
    }

    var snapshots = new List<MissingJoinAccountSnapshot>();
    foreach(var account in accounts) {
        var backup = await eggClient.GetBackupAsync(account.Eid);
        snapshots.Add(BuildMissingJoinSnapshot(account, backup));
    }

    foreach(var contract in currentContracts) {
        var contractId = contract.Identifier;
        var startedAt = DateTimeOffset.FromUnixTimeSeconds((long)contract.StartTime);
        var alertKey = $"missing-join:{guildId}:{contractId.ToLowerInvariant()}:{startedAt.ToUnixTimeSeconds()}";
        if(await dataStore.HasMissingJoinAlertAsync(alertKey)) {
            continue;
        }

        var joinedDiscordUsers = snapshots
            .Where(s => s.JoinedContractIds.Contains(contractId))
            .Select(s => s.Account.DiscordUserId)
            .ToHashSet();
        if(joinedDiscordUsers.Count == 0) {
            continue;
        }

        var missingMembers = snapshots
            .Where(s => !joinedDiscordUsers.Contains(s.Account.DiscordUserId))
            .GroupBy(s => s.Account.DiscordUserId)
            .Select(g => {
                var first = g.First();
                var guildUser = guild.GetUser(first.Account.DiscordUserId);
                var label = guildUser is not null
                    ? guildUser.Mention
                    : AccountDisplayName(first.Account);
                return new MissingJoinMember(first.Account.DiscordUserId, label, g.Select(s => s.Account).ToList());
            })
            .OrderBy(m => NormalizeName(m.Label))
            .ToList();

        if(missingMembers.Count == 0) {
            await dataStore.RecordMissingJoinAlertAsync(alertKey);
            continue;
        }

        var embed = BuildMissingJoinEmbed(contract, startedAt, missingMembers);
        await PostMissingJoinAlertAsync(staffChannel, contractId, embed);
        await dataStore.RecordMissingJoinAlertAsync(alertKey);
    }
}

MissingJoinAccountSnapshot BuildMissingJoinSnapshot(RegisteredEggAccount account, Backup? backup) {
    var joined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if(backup is not null) {
        foreach(var farmContractId in backup.Farms
            .Select(f => f.ContractId)
            .Where(id => !string.IsNullOrWhiteSpace(id))) {
            joined.Add(farmContractId);
        }

        if(backup.Contracts is not null) {
            foreach(var contract in backup.Contracts.Contracts
                .Where(c => c.Accepted && !c.Cancelled)
                .Select(GetLocalContractId)
                .Where(id => !string.IsNullOrWhiteSpace(id))) {
                joined.Add(contract);
            }
        }
    }

    return new MissingJoinAccountSnapshot(account, joined);
}

Embed BuildMissingJoinEmbed(Contract contract, DateTimeOffset startedAt, IReadOnlyList<MissingJoinMember> missingMembers) {
    var lines = missingMembers
        .Take(30)
        .Select(m => {
            var accountCount = m.Accounts.Count;
            var accountText = accountCount == 1
                ? AccountDisplayName(m.Accounts[0])
                : $"{accountCount} registered EIDs";
            return $"- {m.Label} ({accountText})";
        })
        .ToList();
    if(missingMembers.Count > lines.Count) {
        lines.Add($"...and {missingMembers.Count - lines.Count} more.");
    }

    return new EmbedBuilder()
        .WithTitle($"Missing Co-op Joins - {contract.Identifier}")
        .WithColor(Color.Orange)
        .WithDescription(string.Join("\n", lines))
        .AddField("Contract", string.IsNullOrWhiteSpace(contract.Name) ? contract.Identifier : contract.Name)
        .AddField("Started", $"{startedAt.LocalDateTime:yyyy-MM-dd HH:mm} local")
        .AddField("Check", "6 hours after contract start")
        .WithFooter("Mentions are displayed without pings.")
        .WithCurrentTimestamp()
        .Build();
}

async Task PostMissingJoinAlertAsync(SocketTextChannel staffChannel, string contractId, Embed embed) {
    var threadName = TrimForDiscordName($"Missing joins - {contractId}");
    try {
        var thread = await staffChannel.CreateThreadAsync(threadName, ThreadType.PublicThread, ThreadArchiveDuration.OneDay);
        await thread.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
    } catch(Exception ex) {
        Console.WriteLine($"Could not create staff thread for missing joins: {ex}");
        await staffChannel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
    }
}

static SocketTextChannel? FindStaffTextChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "staff");

async Task HandleDemeritAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can manage demerits.", ephemeral: true);
        return;
    }

    var action = GetString(command, "action").Trim().ToLowerInvariant();
    var member = (SocketGuildUser)command.Data.Options.First(o => o.Name == "member").Value;
    var amount = Math.Max(1, Convert.ToInt32(command.Data.Options.FirstOrDefault(o => o.Name == "amount")?.Value ?? 1));
    var reason = command.Data.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string;

    switch(action) {
        case "add": {
            var added = await dataStore.AddDemeritsAsync(
                command.GuildId!.Value,
                member.Id,
                amount,
                reason ?? "Manual staff demerit",
                contractId: null,
                sourceKey: null);
            await command.RespondAsync($"Added `{added}` demerit(s) to {member.Mention}.", ephemeral: true);
            break;
        }
        case "remove": {
            var removed = await dataStore.RemoveDemeritsAsync(command.GuildId!.Value, member.Id, amount);
            await command.RespondAsync($"Removed `{removed}` active demerit(s) from {member.Mention}.", ephemeral: true);
            break;
        }
        case "list": {
            var demerits = await dataStore.GetActiveDemeritsAsync(command.GuildId!.Value, member.Id);
            await command.RespondAsync(BuildDemeritList(member, demerits), ephemeral: true);
            break;
        }
        default:
            await command.RespondAsync("Use `add`, `remove`, or `list` for the action.", ephemeral: true);
            break;
    }
}

string BuildCoopLookupDiagnostic(PlayerCoopLookupResult lookup) {
    static string ListOrNone(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    return string.Join("\n", [
        "**Private lookup details**",
        $"Backup pulled: `{lookup.BackupFound}`",
        $"Contracts section found: `{lookup.ContractsFound}`",
        $"Farm count: `{lookup.FarmCount}`",
        $"Farm contract IDs: `{ListOrNone(lookup.ContractFarmIds)}`",
        $"Local contracts: `{lookup.LocalContractCount}` total, `{lookup.AcceptedLocalContractCount}` accepted/current",
        $"Local contract IDs: `{ListOrNone(lookup.LocalContractIds)}`",
        $"Embedded current coop statuses: `{lookup.EmbeddedStatusCount}`",
        $"Embedded statuses: `{ListOrNone(lookup.EmbeddedStatusIds)}`",
        $"Candidates tried: `{ListOrNone(lookup.CandidateIds)}`",
        $"Lookup results: `{ListOrNone(lookup.AttemptedLookups)}`"
    ]);
}

async Task HandleEggsLaidAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var embeds = new List<Embed>();
    foreach(var account in accounts.Take(10)) {
        var backup = await eggClient.GetBackupAsync(account.Eid);
        if(backup is not null) {
            embeds.Add(BuildEggsLaidEmbed(backup, account));
        }
    }

    if(embeds.Count == 0) {
        await command.FollowupAsync("Plotty could not pull your Egg Inc backup right now. Please try again in a bit.", ephemeral: true);
        return;
    }

    await command.FollowupAsync(
        text: accounts.Count > 1 ? $"Showing `{embeds.Count}` Egg Inc account(s) tied to your Discord name." : null,
        embeds: embeds.ToArray(),
        ephemeral: true);
}

async Task HandleContractArtifactsAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var embeds = new List<Embed>();
    foreach(var account in accounts.Take(10)) {
        var backup = await eggClient.GetBackupAsync(account.Eid);
        embeds.Add(BuildContractArtifactsEmbed(backup, account));
    }

    for(var i = 0; i < embeds.Count; i += 10) {
        await command.FollowupAsync(
            text: i == 0 && accounts.Count > 1 ? $"Showing artifact suggestions for `{embeds.Count}` registered EID account(s)." : null,
            embeds: embeds.Skip(i).Take(10).ToArray(),
            ephemeral: true);
    }
}

async Task HandleRegisterEidAsync(SocketSlashCommand command) {
    var modal = new ModalBuilder()
        .WithTitle("Register Egg Inc ID")
        .WithCustomId("register-eid-modal")
        .AddTextInput(
            label: "Egg Inc ID",
            customId: "eid",
            style: TextInputStyle.Short,
            placeholder: "EI1234567890123456",
            minLength: 4,
            maxLength: 32,
            required: true)
        .Build();

    await command.RespondWithModalAsync(modal);
}

async Task HandleRegisterEidModalAsync(SocketModal modal) {
    if(modal.GuildId is null) {
        await modal.RespondAsync("Use Plotty inside your Discord server.", ephemeral: true);
        return;
    }

    await modal.DeferAsync(ephemeral: true);

    var eid = EggIncClient.NormalizeEggId(modal.Data.Components.First(c => c.CustomId == "eid").Value);
    var backup = await eggClient.GetBackupAsync(eid);
    if(backup is null) {
        await modal.FollowupAsync("Plotty could not validate that EID with Egg Inc. Please double-check it and try again.", ephemeral: true);
        return;
    }

    var eggName = string.IsNullOrWhiteSpace(backup.UserName) ? null : backup.UserName;
    await dataStore.SaveRegisteredEidAsync(modal.GuildId.Value, modal.User.Id, eid, eggName);
    await dataStore.LinkAsync(new EggUserLink(modal.GuildId.Value, modal.User.Id, eid, eggName));

    var accounts = await dataStore.GetRegisteredAccountsAsync(modal.GuildId.Value, modal.User.Id);
    var suffix = SecureText.Sha256(eid)[..8];
    await modal.FollowupAsync(
        $"Saved your EID securely and tied it to your Discord name. You now have `{accounts.Count}` EID account(s) registered. Stored hash ending: `{suffix}`.",
        ephemeral: true);
}

async Task HandleBeerPlottyAsync(SocketSlashCommand command) {
    var botBuysBack = Random.Shared.Next(5) == 0;
    var isPlottyAdmin = command.User is SocketGuildUser beerUser && IsPlottyAdmin(beerUser);
    var result = await dataStore.TryAddPlottyBeerAsync(command.GuildId!.Value, command.User.Id, botBuysBack, bypassCooldown: isPlottyAdmin);
    if(!result.Accepted) {
        await command.RespondAsync(
            $"{command.User.Mention} Plotty appreciates the enthusiasm, but Plotty is pacing responsibly. Try again in `{FormatDuration(result.RetryAfter ?? TimeSpan.FromHours(1))}`.",
            ephemeral: true);
        return;
    }

    var stats = result.Stats;
    var displayName = command.User is SocketGuildUser guildUser ? guildUser.DisplayName : command.User.Username;

    var response = botBuysBack
        ? $"{command.User.Mention} {RandomBeerGiftResponse()}\n\nYou earned a spot on the Beer Leaderboard. Total Plotty-bought beers: `{stats.BeersBoughtByBot}`."
        : $"{command.User.Mention} {RandomBeerThanksResponse()}\n\nBeers donated to Plotty: `{stats.BeersGivenToBot}`.";

    var embed = new EmbedBuilder()
        .WithTitle(botBuysBack ? "Plotty Bought A Round" : "Beer Accepted")
        .WithColor(botBuysBack ? Color.Green : Color.Orange)
        .WithDescription(response)
        .WithFooter($"{displayName} has given {stats.BeersGivenToBot} beer(s) and received {stats.BeersBoughtByBot}.")
        .WithCurrentTimestamp()
        .Build();

    await command.RespondAsync(embed: embed);
}

async Task HandleBeerUserAsync(SocketSlashCommand command) {
    var recipient = (SocketGuildUser)command.Data.Options.First(o => o.Name == "member").Value;
    var giver = (SocketGuildUser)command.User;
    var isPlottyAdmin = IsPlottyAdmin(giver);

    if(recipient.IsBot) {
        await command.RespondAsync("Plotty appreciates the gesture, but `/beer-user` is for guild members. Use `/beer-plotty` for Plotty.", ephemeral: true);
        return;
    }

    if(recipient.Id == giver.Id && !isPlottyAdmin) {
        await command.RespondAsync("Plotty admires the confidence, but members cannot gift themselves a beer.", ephemeral: true);
        return;
    }

    var result = await dataStore.TryGiftBeerAsync(command.GuildId!.Value, giver.Id, recipient.Id, bypassCooldown: isPlottyAdmin);
    if(!result.Accepted) {
        await command.RespondAsync(
            $"{giver.Mention} Plotty says the pub has standards. You can gift {recipient.Mention} another beer in `{FormatDuration(result.RetryAfter ?? TimeSpan.FromDays(1))}`.",
            ephemeral: true);
        return;
    }

    var stats = result.Stats;
    var pubTitle = PubTitle(stats.BeersReceivedFromMembers);
    var milestone = PubMilestoneMessage(stats.BeersReceivedFromMembers, recipient.Mention);
    var description = $"{giver.Mention} bought {recipient.Mention} a beer.\n\n" +
                      $"{recipient.DisplayName} has received `{stats.BeersReceivedFromMembers}` member-gifted beer(s)." +
                      (string.IsNullOrWhiteSpace(pubTitle) ? "" : $"\nPub status: **{pubTitle}**") +
                      (string.IsNullOrWhiteSpace(milestone) ? "" : $"\n\n{milestone}");

    var embed = new EmbedBuilder()
        .WithTitle("Beer Gifted")
        .WithColor(Color.Orange)
        .WithDescription(description)
        .WithFooter(isPlottyAdmin ? "Admin override used." : "Members can gift the same member once per day.")
        .WithCurrentTimestamp()
        .Build();

    await command.RespondAsync(embed: embed);
}

async Task HandleBeerLeaderAsync(SocketSlashCommand command) {
    var leaders = await dataStore.GetBeerLeaderboardAsync(command.GuildId!.Value);
    if(leaders.Count == 0 || leaders.All(l => l.BeersBoughtByBot == 0 && l.BeersReceivedFromMembers == 0)) {
        await command.RespondAsync("The Beer Leaderboard is empty. Run `/beer-plotty` or gift someone a beer with `/beer-user`.");
        return;
    }

    var guild = client.GetGuild(command.GuildId.Value);
    var plottyLines = leaders
        .Where(l => l.BeersBoughtByBot > 0)
        .Take(10)
        .Select((entry, index) => {
            var user = guild?.GetUser(entry.DiscordUserId);
            var name = user?.DisplayName ?? $"User {entry.DiscordUserId}";
            return $"`#{index + 1}` **{name}** - {entry.BeersBoughtByBot} Plotty beer(s), {entry.BeersGivenToBot} donated";
        })
        .ToList();
    var pubLines = leaders
        .Where(l => l.BeersReceivedFromMembers > 0)
        .OrderByDescending(l => l.BeersReceivedFromMembers)
        .ThenBy(l => l.FirstBeerAt)
        .Take(10)
        .Select((entry, index) => {
            var user = guild?.GetUser(entry.DiscordUserId);
            var name = user?.DisplayName ?? $"User {entry.DiscordUserId}";
            var title = PubTitle(entry.BeersReceivedFromMembers);
            return $"`#{index + 1}` **{name}** - {entry.BeersReceivedFromMembers} gifted beer(s)" +
                   (string.IsNullOrWhiteSpace(title) ? "" : $" · **{title}**");
        })
        .ToList();

    var builder = new EmbedBuilder()
        .WithTitle("Beer Leaderboard")
        .WithColor(Color.Gold)
        .WithFooter("Pub titles: 10 Local, 50 Patron, 100 Basically live here.")
        .WithCurrentTimestamp();

    if(plottyLines.Count > 0) {
        builder.AddField("Plotty Bought Back", string.Join("\n", plottyLines));
    }

    if(pubLines.Count > 0) {
        builder.AddField("Pub Regulars", string.Join("\n", pubLines));
    }

    await command.RespondAsync(embed: builder.Build());
}

async Task HandlePlottyMoodAsync(SocketSlashCommand command) {
    await command.RespondAsync(RandomPlottyMood());
}

async Task HandlePlottyExcusesAsync(SocketSlashCommand command) {
    await command.RespondAsync(RandomPlottyExcuse());
}

async Task HandlePlottyWisdomAsync(SocketSlashCommand command) {
    await command.RespondAsync(RandomPlottyWisdom(command.User.Mention));
}

async Task HandleAdminPlottySpeakAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can use Plotty speak.", ephemeral: true);
        return;
    }

    var message = GetString(command, "message").Trim();
    if(string.IsNullOrWhiteSpace(message)) {
        await command.RespondAsync("Plotty needs something to say.", ephemeral: true);
        return;
    }

    await command.RespondAsync("Plotty has spoken.", ephemeral: true);
    await command.Channel.SendMessageAsync(message);
}

async Task HandleHelpAsync(SocketSlashCommand command) {
    await command.DeferAsync();

    var question = GetString(command, "question");
    var personalAnswer = await TryBuildPersonalHelpAnswerAsync(command, question);
    if(personalAnswer is not null) {
        var personalEmbed = new EmbedBuilder()
            .WithTitle(personalAnswer.Title)
            .WithColor(Color.Purple)
            .WithDescription(personalAnswer.Answer)
            .AddField("Source", personalAnswer.Source)
            .WithFooter("Plotty used your registered EID backup plus Egg Inc Wiki context.")
            .WithCurrentTimestamp();

        if(!string.IsNullOrWhiteSpace(personalAnswer.ImageUrl)) {
            personalEmbed.WithThumbnailUrl(personalAnswer.ImageUrl);
        }

        await command.FollowupAsync(embed: personalEmbed.Build());
        return;
    }

    var answer = await wikiClient.AnswerAsync(question);
    if(answer is null) {
        await command.FollowupAsync(
            "Plotty could not find a good Egg Inc Wiki page for that. Try asking with a specific term like `prestige`, `contracts`, `artifacts`, or `boosts`.",
            ephemeral: true);
        return;
    }

    var embed = new EmbedBuilder()
        .WithTitle($"Plotty Help - {answer.Title}")
        .WithColor(Color.Blue)
        .WithDescription(answer.Answer)
        .AddField("Source", $"[Egg Inc Wiki: {answer.Title}]({answer.Url})")
        .WithFooter("Answers come from the Egg Inc Wiki and may reflect community-maintained info.")
        .WithCurrentTimestamp()
        .Build();

    await command.FollowupAsync(embed: embed);
}

async Task<PersonalHelpAnswer?> TryBuildPersonalHelpAnswerAsync(SocketSlashCommand command, string question) {
    if(!LooksLikePersonalFarmerRankQuestion(question)) {
        return null;
    }

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    var account = accounts.FirstOrDefault();
    if(account is null) {
        return new PersonalHelpAnswer(
            "Plotty Help - Registered EID Needed",
            "Plotty can answer personal PE/SE farmer-level questions after you run `/register-eid`.",
            "Personal backup unavailable until an EID is registered.",
            null);
    }

    var backup = await eggClient.GetBackupAsync(account.Eid);
    if(backup?.Game is null) {
        return new PersonalHelpAnswer(
            "Plotty Help - Backup Unavailable",
            "Plotty could not pull your Egg Inc backup right now. Please try again in a bit.",
            "Egg Inc backup lookup failed.",
            null);
    }

    return BuildFarmerRankHelpAnswer(backup);
}

static bool LooksLikePersonalFarmerRankQuestion(string question) {
    var normalized = NormalizeName(question);
    return (normalized.Contains("my") || normalized.Contains("me")) &&
           (normalized.Contains("farmer") || normalized.Contains("level") || normalized.Contains("rank")) &&
           (normalized.Contains("pe") || normalized.Contains("prophecy") || normalized.Contains("se") || normalized.Contains("soul"));
}

static PersonalHelpAnswer BuildFarmerRankHelpAnswer(Backup backup) {
    var game = backup.Game;
    var soulEggs = GetSoulEggs(game);
    var unclaimedSoulEggs = GetUnclaimedSoulEggs(game);
    var prophecyEggs = (double)game.EggsOfProphecy;
    var unclaimedPe = game.UnclaimedEggsOfProphecy;
    var soulFoodLevel = GetEpicResearchLevel(game, "soul_eggs");
    var prophecyBonusLevel = GetEpicResearchLevel(game, "prophecy_bonus");
    var soulEggBonus = soulFoodLevel + 10d;
    var prophecyEggBonus = ((prophecyBonusLevel + 5d) / 100d) + 1d;
    var earningsBonus = soulEggs * soulEggBonus * Math.Pow(prophecyEggBonus, prophecyEggs);
    var currentRank = GetFarmerRank(earningsBonus);
    var nextRank = GetFarmerRankByOom(Math.Min(currentRank.Oom + 1, 51));

    if(currentRank.Oom >= 51) {
        return new PersonalHelpAnswer(
            "Plotty Help - Farmer Level",
            $"You are already **{currentRank.Name}**.\n\n**SE:** {FormatEggs(soulEggs)}\n**PE:** {prophecyEggs:0}\n**Estimated EB:** {FormatEggs(earningsBonus)}%",
            "[Egg Inc Wiki: Earnings Bonus](https://egg-inc.fandom.com/wiki/Earnings_Bonus)",
            "https://egg-inc.fandom.com/wiki/Special:Redirect/file/Egg_of_Prophecy.png");
    }

    var targetEb = 100d * Math.Pow(10, nextRank.Oom);
    var seNeededWithCurrentPe = Math.Max(0, targetEb / (soulEggBonus * Math.Pow(prophecyEggBonus, prophecyEggs)) - soulEggs);
    var peOnlyNeeded = Enumerable.Range(0, 300)
        .FirstOrDefault(extraPe => targetEb / (soulEggBonus * Math.Pow(prophecyEggBonus, prophecyEggs + extraPe)) <= soulEggs, -1);

    var lines = new List<string> {
        $"You are currently **{currentRank.Name}** and your next farmer level is **{nextRank.Name}**.",
        "",
        $"**Current SE:** {FormatEggs(soulEggs)}",
        $"**Current PE:** {prophecyEggs:0}",
        $"**Estimated EB:** {FormatEggs(earningsBonus)}%",
        $"**Target EB:** {FormatEggs(targetEb)}%",
        "",
        $"With your current PE, you need about **{FormatEggs(seNeededWithCurrentPe)} more SE**."
    };

    if(peOnlyNeeded >= 0) {
        lines.Add($"With no extra SE, you need about **{peOnlyNeeded} more PE**.");
    } else {
        lines.Add("With no extra SE, Plotty did not find a PE-only path within 300 additional PE.");
    }

    lines.Add("");
    if(unclaimedSoulEggs > 0 || unclaimedPe > 0) {
        lines.Add($"Unclaimed backup values not counted in active EB: `{FormatEggs(unclaimedSoulEggs)}` SE and `{unclaimedPe}` PE.");
    }

    lines.Add($"Plotty used Soul Food level `{soulFoodLevel}` and Prophecy Bonus level `{prophecyBonusLevel}` from your backup for this estimate.");

    return new PersonalHelpAnswer(
        "Plotty Help - Next Farmer Level",
        string.Join("\n", lines),
        "[Egg Inc Wiki: Earnings Bonus](https://egg-inc.fandom.com/wiki/Earnings_Bonus) and your registered EID backup",
        "https://egg-inc.fandom.com/wiki/Special:Redirect/file/Egg_of_Prophecy.png");
}

static double GetSoulEggs(Backup.Types.Game game) {
    return game.SoulEggsD > 0 ? game.SoulEggsD : game.SoulEggs;
}

static double GetUnclaimedSoulEggs(Backup.Types.Game game) {
    return game.UnclaimedSoulEggsD > 0 ? game.UnclaimedSoulEggsD : game.UnclaimedSoulEggs;
}

static uint GetEpicResearchLevel(Backup.Types.Game game, string id) {
    return game.EpicResearch
        .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
        ?.Level ?? 0;
}

static FarmerRankInfo GetFarmerRank(double earningsBonus) {
    var oom = earningsBonus <= 0 ? 0 : (int)Math.Floor(Math.Log10(earningsBonus / 100d));
    return GetFarmerRankByOom(oom);
}

static FarmerRankInfo GetFarmerRankByOom(int oom) {
    var ranks = GetFarmerRanks();
    var clamped = Math.Clamp(oom, 0, ranks.Length - 1);
    return ranks[clamped];
}

static FarmerRankInfo[] GetFarmerRanks() {
    return [
        new(0, "Farmer"),
        new(1, "Farmer II"),
        new(2, "Farmer III"),
        new(3, "Kilofarmer"),
        new(4, "Kilofarmer II"),
        new(5, "Kilofarmer III"),
        new(6, "Megafarmer"),
        new(7, "Megafarmer II"),
        new(8, "Megafarmer III"),
        new(9, "Gigafarmer"),
        new(10, "Gigafarmer II"),
        new(11, "Gigafarmer III"),
        new(12, "Terafarmer"),
        new(13, "Terafarmer II"),
        new(14, "Terafarmer III"),
        new(15, "Petafarmer"),
        new(16, "Petafarmer II"),
        new(17, "Petafarmer III"),
        new(18, "Exafarmer"),
        new(19, "Exafarmer II"),
        new(20, "Exafarmer III"),
        new(21, "Zettafarmer"),
        new(22, "Zettafarmer II"),
        new(23, "Zettafarmer III"),
        new(24, "Yottafarmer"),
        new(25, "Yottafarmer II"),
        new(26, "Yottafarmer III"),
        new(27, "Xennafarmer"),
        new(28, "Xennafarmer II"),
        new(29, "Xennafarmer III"),
        new(30, "Weccafarmer"),
        new(31, "Weccafarmer II"),
        new(32, "Weccafarmer III"),
        new(33, "Vendafarmer"),
        new(34, "Vendafarmer II"),
        new(35, "Vendafarmer III"),
        new(36, "Uadafarmer"),
        new(37, "Uadafarmer II"),
        new(38, "Uadafarmer III"),
        new(39, "Treidafarmer"),
        new(40, "Treidafarmer II"),
        new(41, "Treidafarmer III"),
        new(42, "Quadafarmer"),
        new(43, "Quadafarmer II"),
        new(44, "Quadafarmer III"),
        new(45, "Pendafarmer"),
        new(46, "Pendafarmer II"),
        new(47, "Pendafarmer III"),
        new(48, "Exedafarmer"),
        new(49, "Exedafarmer II"),
        new(50, "Exedafarmer III"),
        new(51, "Infinifarmer")
    ];
}

Embed? BuildContributionEmbed(
    string contractId,
    string coopCode,
    ContractCoopStatusResponse status,
    bool lowestFirst = false,
    ISet<string>? visibleUserIds = null,
    ISet<string>? visibleUserNames = null,
    bool showCoopCode = true,
    string? titleSuffix = null) {
    var title = showCoopCode ? $"{contractId} - {coopCode}" : contractId;
    if(!string.IsNullOrWhiteSpace(titleSuffix)) {
        title = $"{title} {titleSuffix}";
    }

    var builder = new EmbedBuilder()
        .WithTitle(title)
        .WithColor(Color.Gold)
        .WithFooter($"Total contributed: {FormatEggs(status.TotalAmount)} eggs")
        .WithCurrentTimestamp();

    var contributors = visibleUserIds is null && visibleUserNames is null
        ? status.Contributors
        : status.Contributors.Where(c =>
            (!string.IsNullOrWhiteSpace(c.UserId) && visibleUserIds?.Contains(EggIncClient.NormalizeEggId(c.UserId)) == true) ||
            (!string.IsNullOrWhiteSpace(c.UserName) && visibleUserNames?.Contains(NormalizeName(c.UserName)) == true));

    var players = lowestFirst
        ? contributors.OrderBy(c => c.ContributionRate).ToList()
        : contributors.OrderByDescending(c => c.ContributionRate).ToList();
    if(players.Count == 0) {
        if(visibleUserIds is not null) {
            return null;
        }

        builder.WithDescription("No contributors found for this co-op yet.");
        return builder.Build();
    }

    var lines = players.Select(p => {
        var name = string.IsNullOrWhiteSpace(p.UserName) ? "(unknown)" : p.UserName;
        var flag = !p.Active ? " inactive" : p.TimeCheatDetected ? " flagged" : "";
        return $"**{name}** - {FormatEggs(p.ContributionRate * 3600)}/hr · {FormatEggs(p.ContributionAmount)} contributed{flag}";
    });

    builder.WithDescription(string.Join("\n", lines));
    return builder.Build();
}

Embed? BuildRegisteredContractEmbed(
    string contractId,
    IEnumerable<ContractCoopStatusResponse> statuses,
    ISet<string> visibleUserIds,
    ISet<string> visibleUserNames) {
    var coopStatuses = statuses.ToList();
    var players = coopStatuses
        .SelectMany(s => s.Contributors)
        .Where(c => IsRegisteredContributor(c, visibleUserIds, visibleUserNames))
        .GroupBy(c => !string.IsNullOrWhiteSpace(c.UserId)
            ? $"id:{EggIncClient.NormalizeEggId(c.UserId)}"
            : $"name:{NormalizeName(c.UserName)}")
        .Select(g => g.OrderByDescending(c => c.ContributionAmount).First())
        .OrderBy(c => c.ContributionRate)
        .ToList();

    if(players.Count == 0) {
        return null;
    }

    var lines = players.Select(p => {
        var name = string.IsNullOrWhiteSpace(p.UserName) ? "(unknown)" : p.UserName;
        var flag = !p.Active ? " inactive" : p.TimeCheatDetected ? " flagged" : "";
        return $"**{name}** - {FormatEggs(p.ContributionRate * 3600)}/hr Â· {FormatEggs(p.ContributionAmount)} contributed{flag}";
    });

    return new EmbedBuilder()
        .WithTitle(contractId)
        .WithColor(Color.Gold)
        .WithFooter($"Registered players across {coopStatuses.Count} co-op(s)")
        .WithDescription(string.Join("\n", lines))
        .WithCurrentTimestamp()
        .Build();
}

async Task<Embed> BuildPlayerEmbedAsync(RegisteredEggAccount account, string displayName, Backup? backup) {
    var builder = new EmbedBuilder()
        .WithTitle($"Player - {displayName} ({AccountDisplayName(account)})")
        .WithColor(Color.Blue)
        .WithCurrentTimestamp()
        .AddField("Registration Date", account.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

    if(backup?.Contracts is null) {
        builder.WithDescription("Plotty could not pull this player's Egg Inc contract history right now.");
        return builder.Build();
    }

    var contracts = backup.Contracts.Contracts
        .Concat(backup.Contracts.Archive)
        .Where(c => !c.Cancelled)
        .Select(c => new PlayerContractCandidate(GetLocalContractId(c), c.CoopIdentifier, c.TimeAccepted))
        .Where(c => !string.IsNullOrWhiteSpace(c.ContractId) && !string.IsNullOrWhiteSpace(c.CoopCode))
        .GroupBy(c => c.ContractId, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(c => c.AcceptedAt).First())
        .OrderByDescending(c => c.AcceptedAt)
        .ToList();

    if(contracts.Count == 0) {
        builder.AddField("Average Contribution", "No recent contract history found.");
        builder.AddField("Last 3 Rates", "No recent contract rates found.");
        return builder.Build();
    }

    var rates = new List<PlayerContractRate>();
    foreach(var contract in contracts) {
        var status = await eggClient.GetCoopStatusAsync(contract.ContractId, contract.CoopCode);
        var contributor = status?.Contributors.FirstOrDefault(c => IsPlayerContributor(c, account, backup.UserName));
        if(contributor is not null) {
            rates.Add(new PlayerContractRate(contract.ContractId, contributor.ContributionRate * 3600, contributor.ContributionAmount));
            if(rates.Count == 3) {
                break;
            }
        }
    }

    if(rates.Count == 0) {
        builder.AddField("Average Contribution", "No matching contribution rows found in recent contracts.");
        builder.AddField("Last 3 Rates", "No matching contribution rows found.");
        return builder.Build();
    }

    builder.AddField("Average Contribution", $"{FormatEggs(rates.Average(r => r.RatePerHour))}/hr over `{rates.Count}` contract(s)");
    builder.AddField("Last 3 Rates", string.Join("\n", rates
        .Select(r => $"`{r.ContractId}` - {FormatEggs(r.RatePerHour)}/hr")));

    return builder.Build();
}

MessageComponent BuildPlayerComponents(ulong discordUserId) =>
    new ComponentBuilder()
        .WithButton("Refresh", $"player-refresh:{discordUserId}", ButtonStyle.Primary)
        .Build();

async Task<DashboardResult> BuildDashboardAsync(ulong guildId) {
    var accounts = await dataStore.GetRegisteredEidsAsync(guildId);
    if(accounts.Count == 0) {
        return new DashboardResult("No EIDs are registered in this server yet. Have players run `/register-eid` first.", [], []);
    }

    var registeredByEggId = accounts
        .Where(a => !string.IsNullOrWhiteSpace(a.Eid))
        .GroupBy(a => EggIncClient.NormalizeEggId(a.Eid), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    var registeredByName = accounts
        .Where(a => NormalizeName(a.EggName).Length > 0)
        .GroupBy(a => NormalizeName(a.EggName), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    var statuses = new Dictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse>();
    var failed = 0;
    foreach(var account in accounts) {
        var lookup = await eggClient.GetPlayerCoopLookupAsync(account.Eid);
        if(lookup.Statuses.Count == 0) {
            failed++;
            continue;
        }

        foreach(var status in lookup.Statuses) {
            var key = (status.ContractId.ToLowerInvariant(), status.CoopCode.ToLowerInvariant());
            statuses.TryAdd(key, status.Status);
        }

    }

    if(statuses.Count == 0) {
        return new DashboardResult($"Plotty checked `{accounts.Count}` registered EID(s), but could not pull any active co-op dashboard data.", [], []);
    }

    var rows = BuildDashboardRows(statuses.Values, registeredByEggId, registeredByName);
    var embeds = rows
        .GroupBy(r => r.ContractId, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key)
        .Take(10)
        .Select(BuildDashboardContractEmbed)
        .ToList();

    var attention = rows.Count(r => r.Category != DashboardCategory.Healthy);
    var message = failed > 0
        ? $"Dashboard for `{accounts.Count}` registered EID(s). `{failed}` EID(s) did not return active co-op data. `{attention}` player issue(s) need attention."
        : $"Dashboard for `{accounts.Count}` registered EID(s). `{attention}` player issue(s) need attention.";

    return new DashboardResult(message, embeds, rows);
}

IReadOnlyList<DashboardPlayerRow> BuildDashboardRows(
    IEnumerable<ContractCoopStatusResponse> statuses,
    IReadOnlyDictionary<string, RegisteredEggAccount> registeredByEggId,
    IReadOnlyDictionary<string, RegisteredEggAccount> registeredByName) {
    var rawRows = statuses
        .SelectMany(status => status.Contributors.Select(contributor => new {
            ContractId = string.IsNullOrWhiteSpace(status.ContractIdentifier) ? "(unknown contract)" : status.ContractIdentifier,
            Contributor = contributor,
            Account = FindRegisteredAccount(contributor, registeredByEggId, registeredByName)
        }))
        .Where(x => x.Account is not null)
        .GroupBy(x => (x.ContractId, x.Account!.EidHash))
        .Select(g => g.OrderByDescending(x => x.Contributor.ContributionAmount).First())
        .Select(x => new DashboardPlayerRow(
            x.ContractId,
            x.Account!.DiscordUserId,
            string.IsNullOrWhiteSpace(x.Contributor.UserName) ? x.Account.EggName ?? "(unknown)" : x.Contributor.UserName,
            x.Contributor.ContributionRate * 3600,
            x.Contributor.ContributionAmount,
            x.Contributor.Active,
            x.Contributor.TimeCheatDetected,
            DashboardCategory.Healthy,
            "Healthy"))
        .ToList();

    return rawRows
        .GroupBy(r => r.ContractId, StringComparer.OrdinalIgnoreCase)
        .SelectMany(group => {
            var positiveRates = group
                .Where(r => r.RatePerHour > 0)
                .Select(r => r.RatePerHour)
                .OrderBy(r => r)
                .ToList();
            var median = Median(positiveRates);
            return group.Select(r => ClassifyDashboardRow(r, median));
        })
        .OrderBy(r => r.ContractId)
        .ThenBy(r => r.Category)
        .ThenBy(r => r.RatePerHour)
        .ToList();
}

DashboardPlayerRow ClassifyDashboardRow(DashboardPlayerRow row, double medianRate) {
    const double threshold = 2_000_000_000_000_000d;

    if(row.TimeCheatDetected) {
        return row with { Category = DashboardCategory.Flagged, Reason = "Flagged" };
    }

    if(!row.Active || row.RatePerHour <= 0) {
        return row with { Category = DashboardCategory.NoSync, Reason = "No sync/data" };
    }

    if(row.RatePerHour < threshold && medianRate > 0 && row.RatePerHour < medianRate * 0.5) {
        return row with { Category = DashboardCategory.LikelyUnboosted, Reason = "Likely unboosted" };
    }

    if(row.RatePerHour < threshold) {
        return row with { Category = DashboardCategory.BelowThreshold, Reason = "Below 2q/hr" };
    }

    return row;
}

Embed BuildDashboardContractEmbed(IGrouping<string, DashboardPlayerRow> group) {
    var rows = group.ToList();
    var needsAttention = rows
        .Where(r => r.Category != DashboardCategory.Healthy)
        .OrderBy(r => r.Category)
        .ThenBy(r => r.RatePerHour)
        .Take(8)
        .ToList();

    var description = string.Join("\n", [
        $"Registered Players: `{rows.Count}`",
        $"Healthy: `{rows.Count(r => r.Category == DashboardCategory.Healthy)}`",
        $"Below 2q/hr: `{rows.Count(r => r.Category == DashboardCategory.BelowThreshold)}`",
        $"Likely Unboosted: `{rows.Count(r => r.Category == DashboardCategory.LikelyUnboosted)}`",
        $"No Sync/Data: `{rows.Count(r => r.Category == DashboardCategory.NoSync)}`",
        $"Flagged: `{rows.Count(r => r.Category == DashboardCategory.Flagged)}`"
    ]);

    if(needsAttention.Count > 0) {
        description += "\n\n**Needs Attention**\n" + string.Join("\n", needsAttention.Select((r, index) =>
            $"{index + 1}. {MentionOrName(r)} - {FormatEggs(r.RatePerHour)}/hr - {r.Reason}"));
    } else {
        description += "\n\nNo registered players need attention.";
    }

    return new EmbedBuilder()
        .WithTitle(group.Key)
        .WithColor(needsAttention.Count > 0 ? Color.Orange : Color.Green)
        .WithDescription(description)
        .WithCurrentTimestamp()
        .Build();
}

MessageComponent BuildDashboardComponents() =>
    new ComponentBuilder()
        .WithButton("Refresh", "dashboard-refresh", ButtonStyle.Primary)
        .WithButton("Show Full Player List", "dashboard-full-list", ButtonStyle.Secondary)
        .Build();

string BuildDashboardFullList(IReadOnlyList<DashboardPlayerRow> rows) {
    if(rows.Count == 0) {
        return "No registered player rows were found.";
    }

    var lines = rows
        .OrderBy(r => r.ContractId)
        .ThenBy(r => r.Category)
        .ThenBy(r => r.RatePerHour)
        .Select(r => $"`{r.ContractId}` {MentionOrName(r)} - {FormatEggs(r.RatePerHour)}/hr - {r.Reason}");
    var text = string.Join("\n", lines);
    return text.Length <= 1900 ? text : text[..1900] + "\n...";
}

string BuildDemeritList(SocketGuildUser member, IReadOnlyList<DemeritEntry> demerits) {
    if(demerits.Count == 0) {
        return $"{member.Mention} has no active demerits.";
    }

    var lines = demerits
        .OrderBy(d => d.ExpiresAt)
        .Select(d => $"- `{d.CreatedAt.LocalDateTime:yyyy-MM-dd}` expires `{d.ExpiresAt.LocalDateTime:yyyy-MM-dd}`: {d.Reason}" +
                     (string.IsNullOrWhiteSpace(d.ContractId) ? "" : $" (`{d.ContractId}`)"));
    return $"{member.Mention} has `{demerits.Count}` active demerit(s):\n" + string.Join("\n", lines);
}

static bool HasStaffRole(SocketGuildUser user) =>
    IsPlottyAdmin(user) ||
    user.Roles.Any(r => string.Equals(r.Name, "Staff", StringComparison.OrdinalIgnoreCase));

static bool IsPlottyAdmin(SocketGuildUser user) {
    var names = new[] {
        user.Username,
        user.GlobalName,
        user.DisplayName,
        user.Nickname
    };

    return names
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Select(NormalizeName)
        .Any(n => n is "felixlovestrains" or "chemicaljump");
}

static double Median(IReadOnlyList<double> values) {
    if(values.Count == 0) {
        return 0;
    }

    var middle = values.Count / 2;
    return values.Count % 2 == 1
        ? values[middle]
        : (values[middle - 1] + values[middle]) / 2d;
}

static string MentionOrName(DashboardPlayerRow row) =>
    row.DiscordUserId == 0 ? row.PlayerName : $"<@{row.DiscordUserId}>";

static string AccountDisplayName(RegisteredEggAccount account) =>
    !string.IsNullOrWhiteSpace(account.EggName)
        ? account.EggName
        : $"EID ...{account.EidHash[^8..]}";

static string GetLocalContractId(LocalContract contract) =>
    !string.IsNullOrWhiteSpace(contract.ContractIdentifier)
        ? contract.ContractIdentifier
        : contract.Contract?.Identifier ?? "";

bool IsPlayerContributor(
    ContractCoopStatusResponse.Types.ContributionInfo contributor,
    RegisteredEggAccount account,
    string? backupUserName = null) =>
    (!string.IsNullOrWhiteSpace(contributor.UserId) &&
     string.Equals(EggIncClient.NormalizeEggId(contributor.UserId), EggIncClient.NormalizeEggId(account.Eid), StringComparison.OrdinalIgnoreCase)) ||
    (!string.IsNullOrWhiteSpace(contributor.UserName) &&
     MatchesPlayerName(contributor.UserName, account.EggName, backupUserName));

static bool MatchesPlayerName(string contributorName, params string?[] knownNames) {
    var normalizedContributorName = NormalizeName(contributorName);
    return normalizedContributorName.Length > 0 &&
           knownNames
               .Where(n => !string.IsNullOrWhiteSpace(n))
               .Select(n => NormalizeName(n))
               .Any(n => n.Length > 0 && string.Equals(normalizedContributorName, n, StringComparison.OrdinalIgnoreCase));
}

bool IsRegisteredContributor(
    ContractCoopStatusResponse.Types.ContributionInfo contributor,
    ISet<string> visibleUserIds,
    ISet<string> visibleUserNames) =>
    (!string.IsNullOrWhiteSpace(contributor.UserId) && visibleUserIds.Contains(EggIncClient.NormalizeEggId(contributor.UserId))) ||
    (!string.IsNullOrWhiteSpace(contributor.UserName) && visibleUserNames.Contains(NormalizeName(contributor.UserName)));

RegisteredEggAccount? FindRegisteredAccount(
    ContractCoopStatusResponse.Types.ContributionInfo contributor,
    IReadOnlyDictionary<string, RegisteredEggAccount> registeredByEggId,
    IReadOnlyDictionary<string, RegisteredEggAccount> registeredByName) {
    if(!string.IsNullOrWhiteSpace(contributor.UserId) &&
       registeredByEggId.TryGetValue(EggIncClient.NormalizeEggId(contributor.UserId), out var byId)) {
        return byId;
    }

    if(!string.IsNullOrWhiteSpace(contributor.UserName) &&
       registeredByName.TryGetValue(NormalizeName(contributor.UserName), out var byName)) {
        return byName;
    }

    return null;
}

Embed BuildEggsLaidEmbed(Backup backup, RegisteredEggAccount? account = null) {
    var titleName = account is not null
        ? AccountDisplayName(account)
        : string.IsNullOrWhiteSpace(backup.UserName) ? "Registered Player" : backup.UserName;
    var builder = new EmbedBuilder()
        .WithTitle($"Eggs laid - {titleName}")
        .WithColor(Color.Green)
        .WithCurrentTimestamp();

    var totals = BuildEggsLaidTotals(backup);
    if(totals.Count == 0) {
        builder.WithDescription("No lifetime egg totals were found in this backup.");
        return builder.Build();
    }

    var lines = totals
        .Where(x => x.Amount > 0)
        .Select(x => $"**{x.Name}** - {FormatEggs(x.Amount)}")
        .ToList();

    if(lines.Count == 0) {
        builder.WithDescription("No eggs laid totals above zero were found.");
    } else {
        builder.WithDescription(string.Join("\n", lines.Take(25)));
    }

    var currentFarmLines = backup.Farms
        .Where(f => f.EggsLaid > 0)
        .Take(10)
        .Select(f => {
            var label = !string.IsNullOrWhiteSpace(f.ContractId)
                ? $"{EggDisplayName(f.EggType)} contract `{f.ContractId}`"
                : EggDisplayName(f.EggType);
            return $"**{label}** - {FormatEggs(f.EggsLaid)} currently laid";
        })
        .ToList();

    if(currentFarmLines.Count > 0) {
        builder.AddField("Current Farms", string.Join("\n", currentFarmLines));
    }

    return builder.Build();
}

static IReadOnlyList<EggsLaidTotal> BuildEggsLaidTotals(Backup backup) {
    var statsTotals = backup.Stats?.EggTotals?.ToList() ?? [];
    var totals = new List<EggsLaidTotal>();

    for(var i = 0; i < Math.Min(19, statsTotals.Count); i++) {
        totals.Add(new EggsLaidTotal(EggNameForStatsIndex(i), statsTotals[i]));
    }

    for(var i = 20; i < Math.Min(25, statsTotals.Count); i++) {
        totals.Add(new EggsLaidTotal(EggNameForStatsIndex(i), statsTotals[i]));
    }

    var contracts = backup.Contracts?.Archive
        .Concat(backup.Contracts.Contracts)
        .ToList() ?? [];

    Egg[] seasonalEggs = [
        Egg.Chocolate,
        Egg.Easter,
        Egg.Waterballoon,
        Egg.Firework,
        Egg.Pumpkin
    ];

    foreach(var egg in seasonalEggs) {
        totals.Add(new EggsLaidTotal(
            EggDisplayName(egg),
            SumContractEggsLaid(contracts.Where(c => c.Contract?.Egg == egg))));
    }

    var customEggs = backup.Contracts?.CustomEggInfo ?? [];
    foreach(var customEgg in customEggs.Where(e => !string.IsNullOrWhiteSpace(e.Identifier))) {
        var name = string.IsNullOrWhiteSpace(customEgg.Name)
            ? $"Custom Egg ({customEgg.Identifier})"
            : customEgg.Name;
        totals.Add(new EggsLaidTotal(
            name,
            SumContractEggsLaid(contracts.Where(c =>
                string.Equals(c.Contract?.CustomEggId, customEgg.Identifier, StringComparison.OrdinalIgnoreCase)))));
    }

    return totals;
}

static double SumContractEggsLaid(IEnumerable<LocalContract> contracts) =>
    contracts.Sum(c => c.CoopLastUploadedContribution > 0
        ? c.CoopLastUploadedContribution
        : c.LastAmountWhenRewardGiven);

public record ArtifactCandidate(
    ArtifactInventoryItem Item,
    CompleteArtifact Artifact,
    ArtifactSpec.Types.Name Name,
    string DisplayName,
    ArtifactPurpose Purpose,
    double LayingMultiplier,
    double ShippingMultiplier,
    double TeamLayingMultiplier,
    string Reason,
    string? ImageUrl
);

public enum ArtifactPurpose { TeamLaying, Laying, Shipping, Capacity, TeamEarnings, Boosting, InternalHatchery, StoneCarrier }

Embed BuildContractArtifactsEmbed(Backup? backup, RegisteredEggAccount account) {
    var titleName = AccountDisplayName(account);
    var builder = new EmbedBuilder()
        .WithTitle($"Contract Artifacts - {titleName}")
        .WithColor(Color.Purple)
        .WithCurrentTimestamp();

    if(backup is null) {
        builder.WithDescription("Plotty could not pull this Egg Inc backup right now.");
        return builder.Build();
    }

    if(backup.ArtifactsDb is null || backup.ArtifactsDb.InventoryItems.Count == 0) {
        builder.WithDescription("Plotty pulled the backup, but no artifact inventory was included.");
        builder.WithFooter("Open artifacts in Egg Inc and sync, then try again.");
        return builder.Build();
    }

    var candidates = BuildArtifactCandidates(backup.ArtifactsDb.InventoryItems);
    if(candidates.Count == 0) {
        builder.WithDescription("Plotty found artifact inventory, but no contract-focused artifacts it recognizes yet.");
        return builder.Build();
    }

    var activeContracts = backup.Farms
        .Select((farm, index) => new { Farm = farm, Index = index })
        .Where(x => !string.IsNullOrWhiteSpace(x.Farm.ContractId))
        .ToList();
    var activeArtifacts = GetActiveContractArtifacts(backup, candidates)
        .ToList();
        
    var suggested = BuildArtifactRecommendation(candidates)
        .ToList();

    var contractLines = activeContracts.Count == 0
        ? ["No active contract farm found in this backup."]
        : activeContracts
            .Select(x => $"`{x.Farm.ContractId}` - {EggDisplayName(x.Farm.EggType)}")
            .Distinct()
            .Take(6)
            .ToList();

    var equippedLines = activeArtifacts.Count == 0
        ? ["No equipped contract artifacts were visible in this backup."]
        : activeArtifacts
            .GroupBy(a => a.Name)
            .Select(g => $"{ArtifactPurposeIcon(g.First().Purpose)} **{g.First().DisplayName}** x{g.Count()} - {FormatArtifactMultiplier(g.First())}")
            .Take(8)
            .ToList();

    builder.WithDescription("Plotty evaluated your inventory using a Pareto optimal configuration to find the strongest balance of laying and shipping multipliers.");
    builder.AddField("Current Contract Farms", string.Join("\n", contractLines));
    builder.AddField("Currently Equipped", string.Join("\n", equippedLines));

    if(suggested.Count == 0) {
        builder.AddField("Suggested Set", "No contract-focused artifacts were found. Look for Tachyon Deflector, Quantum Metronome, Interstellar Compass, and useful stones.");
    } else {
        builder.AddField("Suggested Set", string.Join("\n", suggested.Select((a, i) =>
            $"{i + 1}. {ArtifactPurposeIcon(a.Purpose)} **{a.DisplayName}**{FormatArtifactStones(a.Artifact)} - {FormatArtifactMultiplier(a)}; {a.Reason}")));

        var imageLinks = suggested
            .Where(a => !string.IsNullOrWhiteSpace(a.ImageUrl))
            .Select(a => $"[{a.DisplayName}]({a.ImageUrl})")
            .ToList();
        if(imageLinks.Count > 0) {
            builder.AddField("Artifact Images", string.Join(" | ", imageLinks));
            builder.WithThumbnailUrl(suggested.First(a => !string.IsNullOrWhiteSpace(a.ImageUrl)).ImageUrl);
        }
    }

    builder.WithFooter("Effect-aware multi-objective optimization engine. Plotty can only inspect backups it can pull.");
    return builder.Build();
}

static IReadOnlyList<ArtifactCandidate> BuildArtifactCandidates(IEnumerable<ArtifactInventoryItem> items) =>
    items
        .Where(i => i.Artifact?.Spec is not null)
        .Select(i => CreateArtifactCandidate(i))
        .Where(c => c is not null)
        .Cast<ArtifactCandidate>()
        .ToList();

static ArtifactCandidate? CreateArtifactCandidate(ArtifactInventoryItem item) {
    var artifact = item.Artifact;
    var spec = artifact?.Spec;
    if(artifact is null || spec is null) {
        return null;
    }

    var name = spec.Name;
    var displayName = ArtifactDisplayName(spec);
    
    var layingMultiplier = ArtifactEffectMultiplier(artifact, [
        ArtifactSpec.Types.Name.QuantumMetronome,
        ArtifactSpec.Types.Name.TachyonStone
    ]);
    var shippingMultiplier = ArtifactEffectMultiplier(artifact, [
        ArtifactSpec.Types.Name.InterstellarCompass,
        ArtifactSpec.Types.Name.QuantumStone
    ]);
    var teamLayingMultiplier = ArtifactEffectMultiplier(artifact, [ArtifactSpec.Types.Name.TachyonDeflector]);

    return name switch {
        ArtifactSpec.Types.Name.TachyonDeflector => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.TeamLaying,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "helps the co-op by raising teammate egg laying", ArtifactImageUrl(spec)),
            
        ArtifactSpec.Types.Name.QuantumMetronome => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Laying,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "raises your egg laying rate", ArtifactImageUrl(spec)),
            
        ArtifactSpec.Types.Name.InterstellarCompass => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Shipping,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "raises shipping so laid eggs can actually leave the farm", ArtifactImageUrl(spec)),
            
        ArtifactSpec.Types.Name.OrnateGusset => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Capacity,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "keeps hab space from choking production", ArtifactImageUrl(spec)),
            
        ArtifactSpec.Types.Name.ShipInABottle => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.TeamEarnings,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "supports co-op earnings after core rate artifacts", ArtifactImageUrl(spec)),
            
        ArtifactSpec.Types.Name.DilithiumMonocle => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Boosting,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "helps boost sessions, but is secondary after rate artifacts", ArtifactImageUrl(spec)),
            
        ArtifactSpec.Types.Name.TheChalice => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.InternalHatchery,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "helps contract population growth, especially with life stones", ArtifactImageUrl(spec)),
            
        _ when artifact.Stones.Count > 0 => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.StoneCarrier,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier,
            "useful as a stone carrier if your best rate artifacts are already equipped", ArtifactImageUrl(spec)),
            
        _ => null
    };
}

static IEnumerable<ArtifactCandidate> GetActiveContractArtifacts(Backup backup, IReadOnlyList<ArtifactCandidate> candidates) {
    if(backup.ArtifactsDb is null) {
        yield break;
    }

    var candidatesByItemId = candidates.ToDictionary(c => c.Item.ItemId);
    for(var i = 0; i < backup.Farms.Count; i++) {
        if(string.IsNullOrWhiteSpace(backup.Farms[i].ContractId) ||
           backup.ArtifactsDb.ActiveArtifactSets.Count <= i) {
            continue;
        }

        foreach(var slot in backup.ArtifactsDb.ActiveArtifactSets[i].Slots.Where(s => s.Occupied)) {
            if(candidatesByItemId.TryGetValue(slot.ItemId, out var candidate)) {
                yield return candidate;
            }
        }
    }
}

static IEnumerable<ArtifactCandidate> BuildArtifactRecommendation(IReadOnlyList<ArtifactCandidate> candidates) {
    var rawSets = new List<List<ArtifactCandidate>>();
    
    // Generate valid combinations of up to 4 distinct items
    int total = 1 << Math.Min(candidates.Count, 24); // Cap search depth to protect memory execution boundaries
    for (int i = 0; i < total; i++) {
        var currentSet = new List<ArtifactCandidate>();
        for (int j = 0; j < candidates.Count; j++) {
            if ((i & (1 << j)) != 0) {
                currentSet.Add(candidates[j]);
            }
        }
        // Validate set guidelines (Max 4 slots, completely unique names to reflect actual loadout mechanics)
        if (currentSet.Count > 0 && currentSet.Count <= 4 && currentSet.Select(x => x.Name).Distinct().Count() == currentSet.Count) {
            rawSets.Add(currentSet);
        }
    }

    // Identify non-dominated sets across Multi-Objective space
    var frontier = new List<List<ArtifactCandidate>>();
    foreach (var candidateSet in rawSets) {
        double candLay = candidateSet.Aggregate(1.0, (tot, cur) => tot * cur.LayingMultiplier);
        double candShip = candidateSet.Aggregate(1.0, (tot, cur) => tot * cur.ShippingMultiplier);
        double candTeam = candidateSet.Aggregate(1.0, (tot, cur) => tot * cur.TeamLayingMultiplier);

        bool isDominated = false;
        for (int i = frontier.Count - 1; i >= 0; i--) {
            var altSet = frontier[i];
            double altLay = altSet.Aggregate(1.0, (tot, cur) => tot * cur.LayingMultiplier);
            double altShip = altSet.Aggregate(1.0, (tot, cur) => tot * cur.ShippingMultiplier);
            double altTeam = altSet.Aggregate(1.0, (tot, cur) => tot * cur.TeamLayingMultiplier);

            if (altLay >= candLay && altShip >= candShip && altTeam >= candTeam && 
               (altLay > candLay || altShip > candShip || altTeam > candTeam)) {
                isDominated = true;
                break;
            }

            if (candLay >= altLay && candShip >= altShip && candTeam >= altTeam && 
               (candLay > altLay || candShip > altShip || candTeam > altTeam)) {
                frontier.RemoveAt(i);
            }
        }
        if (!isDominated) {
            frontier.Add(candidateSet);
        }
    }

    // Default choice picks the top system set leaning heavily on maximizing cumulative team laying and individual processing speeds
    var optimalSet = frontier
        .OrderByDescending(s => s.Aggregate(1.0, (t, c) => t * c.TeamLayingMultiplier))
        .ThenByDescending(s => s.Aggregate(1.0, (t, c) => t * c.LayingMultiplier))
        .FirstOrDefault();

    return optimalSet ?? new List<ArtifactCandidate>();
}

static double ArtifactEffectMultiplier(CompleteArtifact artifact, IReadOnlyCollection<ArtifactSpec.Types.Name> relevantNames) {
    var multiplier = relevantNames.Contains(artifact.Spec.Name)
        ? 1 + ArtifactEffectDelta(artifact.Spec)
        : 1;
    foreach(var stone in artifact.Stones.Where(s => relevantNames.Contains(s.Name))) {
        multiplier *= 1 + ArtifactEffectDelta(stone);
    }
    return multiplier;
}

static string FormatArtifactMultiplier(ArtifactCandidate candidate) {
    if (candidate.Purpose == ArtifactPurpose.TeamLaying) {
        return $"+{(candidate.TeamLayingMultiplier - 1) * 100:0.#}% team lay";
    }
    var primaryMult = candidate.Purpose == ArtifactPurpose.Shipping ? candidate.ShippingMultiplier : candidate.LayingMultiplier;
    return primaryMult <= 1 ? "utility properties" : $"+{(primaryMult - 1) * 100:0.#}% multiplier";
}

static double ArtifactEffectDelta(ArtifactSpec spec) =>
    spec.Name switch {
        ArtifactSpec.Types.Name.TachyonDeflector => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.05,
            ArtifactSpec.Types.Level.Lesser => 0.08,
            ArtifactSpec.Types.Level.Normal => spec.Rarity == ArtifactSpec.Types.Rarity.Rare ? 0.13 : 0.12,
            ArtifactSpec.Types.Level.Greater => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Legendary => 0.20,
                ArtifactSpec.Types.Rarity.Epic => 0.19,
                ArtifactSpec.Types.Rarity.Rare => 0.17,
                _ => 0.15
            },
            _ => 0
        },
        ArtifactSpec.Types.Name.QuantumMetronome => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.05,
            ArtifactSpec.Types.Level.Lesser => spec.Rarity == ArtifactSpec.Types.Rarity.Rare ? 0.12 : 0.10,
            ArtifactSpec.Types.Level.Normal => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Epic => 0.20,
                ArtifactSpec.Types.Rarity.Rare => 0.17,
                _ => 0.15
            },
            ArtifactSpec.Types.Level.Greater => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Legendary => 0.35,
                ArtifactSpec.Types.Rarity.Epic => 0.30,
                ArtifactSpec.Types.Rarity.Rare => 0.27,
                _ => 0.25
            },
            _ => 0
        },
        ArtifactSpec.Types.Name.InterstellarCompass => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.05,
            ArtifactSpec.Types.Level.Lesser => 0.10,
            ArtifactSpec.Types.Level.Normal => spec.Rarity == ArtifactSpec.Types.Rarity.Rare ? 0.22 : 0.20,
            ArtifactSpec.Types.Level.Greater => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Legendary => 0.50,
                ArtifactSpec.Types.Rarity.Epic => 0.40,
                ArtifactSpec.Types.Rarity.Rare => 0.35,
                _ => 0.30
            },
            _ => 0
        },
        ArtifactSpec.Types.Name.OrnateGusset => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.05,
            ArtifactSpec.Types.Level.Lesser => spec.Rarity == ArtifactSpec.Types.Rarity.Epic ? 0.12 : 0.10,
            ArtifactSpec.Types.Level.Normal => spec.Rarity == ArtifactSpec.Types.Rarity.Rare ? 0.16 : 0.15,
            ArtifactSpec.Types.Level.Greater => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Legendary => 0.25,
                ArtifactSpec.Types.Rarity.Epic => 0.22,
                _ => 0.20
            },
            _ => 0
        },
        ArtifactSpec.Types.Name.ShipInABottle => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.20,
            ArtifactSpec.Types.Level.Lesser => 0.30,
            ArtifactSpec.Types.Level.Normal => spec.Rarity == ArtifactSpec.Types.Rarity.Rare ? 0.60 : 0.50,
            ArtifactSpec.Types.Level.Greater => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Legendary => 1.00,
                ArtifactSpec.Types.Rarity.Epic => 0.90,
                ArtifactSpec.Types.Rarity.Rare => 0.80,
                _ => 0.70
            },
            _ => 0
        },
        ArtifactSpec.Types.Name.DilithiumMonocle => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.05,
            ArtifactSpec.Types.Level.Lesser => 0.10,
            ArtifactSpec.Types.Level.Normal => 0.15,
            ArtifactSpec.Types.Level.Greater => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Legendary => 0.30,
                ArtifactSpec.Types.Rarity.Epic => 0.25,
                _ => 0.20
            },
            _ => 0
        },
        ArtifactSpec.Types.Name.TheChalice => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.05,
            ArtifactSpec.Types.Level.Lesser => spec.Rarity == ArtifactSpec.Types.Rarity.Rare ? 0.10 : 0.05,
            ArtifactSpec.Types.Level.Normal => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Epic => 0.20,
                ArtifactSpec.Types.Rarity.Rare => 0.17,
                _ => 0.15
            },
            ArtifactSpec.Types.Level.Greater => spec.Rarity switch {
                ArtifactSpec.Types.Rarity.Legendary => 0.50,
                ArtifactSpec.Types.Rarity.Epic => 0.40,
                ArtifactSpec.Types.Rarity.Rare => 0.35,
                _ => 0.30
            },
            _ => 0
        },
        ArtifactSpec.Types.Name.TachyonStone or ArtifactSpec.Types.Name.QuantumStone => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.02,
            ArtifactSpec.Types.Level.Lesser => 0.04,
            ArtifactSpec.Types.Level.Normal => 0.05,
            _ => 0
        },
        ArtifactSpec.Types.Name.LifeStone => spec.Level switch {
            ArtifactSpec.Types.Level.Inferior => 0.02,
            ArtifactSpec.Types.Level.Lesser => 0.03,
            ArtifactSpec.Types.Level.Normal => 0.04,
            _ => 0
        },
        _ => 0
    };

static string ArtifactDisplayName(ArtifactSpec spec) {
    var tier = spec.Level switch {
        ArtifactSpec.Types.Level.Inferior => "T1",
        ArtifactSpec.Types.Level.Lesser => "T2",
        ArtifactSpec.Types.Level.Normal => "T3",
        ArtifactSpec.Types.Level.Greater => "T4",
        ArtifactSpec.Types.Level.Superior => "T5",
        _ => "T?"
    };
    var rarity = spec.Rarity switch {
        ArtifactSpec.Types.Rarity.Common => "",
        ArtifactSpec.Types.Rarity.Rare => " Rare",
        ArtifactSpec.Types.Rarity.Epic => " Epic",
        ArtifactSpec.Types.Rarity.Legendary => " Legendary",
        _ => ""
    };
    return $"{tier}{rarity} {ArtifactName(spec.Name)}";
}

static string ArtifactName(ArtifactSpec.Types.Name name) => 
    Regex.Replace(name.ToString(), "([a-z])([A-Z])", "$1 $2");

static string FormatArtifactStones(CompleteArtifact artifact) {
    var stones = artifact.Stones
        .Where(s => s.Name is ArtifactSpec.Types.Name.TachyonStone or ArtifactSpec.Types.Name.QuantumStone or ArtifactSpec.Types.Name.ProphecyStone or ArtifactSpec.Types.Name.SoulStone or ArtifactSpec.Types.Name.ClarityStone)
        .Select(ArtifactDisplayName)
        .ToList();
    return stones.Count == 0 ? "" : $" ({string.Join(", ", stones)})";
}

static string ArtifactPurposeIcon(ArtifactPurpose purpose) =>
    purpose switch {
        ArtifactPurpose.TeamLaying => "Team",
        ArtifactPurpose.Laying => "Laying",
        ArtifactPurpose.Shipping => "Shipping",
        ArtifactPurpose.Capacity => "Capacity",
        ArtifactPurpose.TeamEarnings => "Earnings",
        ArtifactPurpose.Boosting => "Boosts",
        ArtifactPurpose.InternalHatchery => "IHR",
        ArtifactPurpose.StoneCarrier => "Stones",
        _ => "Artifact"
    };

static string? ArtifactImageUrl(ArtifactSpec spec) {
    var file = spec.Name switch {
        ArtifactSpec.Types.Name.TachyonDeflector => $"afx_tachyon_deflector_{ArtifactAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.QuantumMetronome => $"afx_quantum_metronome_{ArtifactAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.InterstellarCompass => $"afx_interstellar_compass_{ArtifactAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.OrnateGusset => $"afx_ornate_gusset_{ArtifactAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.ShipInABottle => $"afx_ship_in_a_bottle_{ArtifactAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.DilithiumMonocle => $"afx_dilithium_monocle_{ArtifactAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.TheChalice => $"afx_the_chalice_{ArtifactAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.TachyonStone => $"afx_tachyon_stone_{StoneAssetTier(spec)}.png",
        ArtifactSpec.Types.Name.QuantumStone => $"afx_quantum_stone_{StoneAssetTier(spec)}.png",
        _ => null
    };
    return file is null ? null : $"https://eggincassets.pages.dev/64/egginc/{file}";
}

static string GetString(SocketSlashCommand command, string name) =>
    (string)command.Data.Options.First(o => o.Name == name).Value;

static bool IsStaffChannel(IChannel? channel) =>
    channel is IGuildChannel guildChannel &&
    NormalizeName(guildChannel.Name) == "staff";

static string TrimForDiscordName(string value) {
    var cleaned = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9\-_ ]", "").Trim();
    cleaned = Regex.Replace(cleaned, @"\s+", "-");
    if(string.IsNullOrWhiteSpace(cleaned)) {
        return "plotty-alert";
    }

    return cleaned.Length <= 90 ? cleaned : cleaned[..90].Trim('-');
}

static string FormatDuration(TimeSpan duration) {
    if(duration <= TimeSpan.Zero) {
        return "now";
    }

    var totalMinutes = Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes));
    var hours = totalMinutes / 60;
    var minutes = totalMinutes % 60;
    if(hours > 0 && minutes > 0) {
        return $"{hours}h {minutes}m";
    }

    return hours > 0 ? $"{hours}h" : $"{minutes}m";
}

static string PubTitle(int beersReceived) {
    return beersReceived switch {
        >= 100 => "Basically live here",
        >= 50 => "Patron",
        >= 10 => "Local",
        _ => ""
    };
}

static string PubMilestoneMessage(int beersReceived, string mention) {
    return beersReceived switch {
        10 => $"{mention} is now a **Local** at the pub.",
        50 => $"{mention} is now a **Patron** at the pub.",
        100 => $"{mention} **Basically live here** now.",
        _ => ""
    };
}

static string RandomPlottyMood() {
    (string Emoji, string Story)[] moods = [
        ("\U0001F305\U0001F95A\U0001F4C8\u2615\U0001F914\U0001F414\U0001F483\U0001F37A\U0001F319",
            "Plotty woke up optimistic, checked the numbers, overthought one chicken, danced anyway, accepted a beer, and called it a productive day."),
        ("\u23F0\U0001F95A\U0001F4BB\U0001F525\U0001F9EF\U0001F60C\U0001F4CA\U0001F3C6",
            "Plotty started early, found an egg-shaped emergency in the logs, calmly put out the fire, and somehow ended with a victory chart."),
        ("\U0001F414\U0001F6AA\U0001F95A\U0001F95A\U0001F95A\U0001F633\U0001F4C9\U0001F37A\U0001F64F",
            "A chicken opened the wrong door, three eggs fell out, the rates dipped, and Plotty requested one respectful recovery beverage."),
        ("\U0001F327\uFE0F\U0001F95A\U0001F6E0\uFE0F\u2699\uFE0F\u2728\U0001F4C8\U0001F60E\U0001F324\uFE0F",
            "The morning was stormy, but Plotty fixed the egg machinery, polished the gears, watched the line go up, and put on sunglasses indoors."),
        ("\U0001F634\u2615\u2615\U0001F95A\U0001F4CA\U0001F680\U0001F315",
            "Plotty was sleepy, applied coffee twice, stared at one egg chart, and accidentally launched the whole mood into orbit."),
        ("\U0001F423\U0001F4DA\U0001F913\U0001F95A\U0001F4A1\U0001F3AF\U0001F3C1",
            "Plotty studied like a tiny hatchling, discovered one bright idea, aimed it carefully, and sprinted across the finish line."),
        ("\U0001F373\U0001F525\U0001F3C3\U0001F4A8\U0001F4E1\u2705\U0001F60C",
            "Breakfast got dramatic, Plotty ran through the smoke, synced the signal, checked the box, and pretended that was normal."),
        ("\U0001F95A\U0001F50D\U0001F575\uFE0F\U0001F4DC\U0001F635\u200D\U0001F4AB\U0001F37A\U0001F642",
            "Plotty investigated a suspicious egg mystery, read too much paperwork, got dizzy, accepted a beer, and recovered politely."),
        ("\U0001F33D\U0001F414\U0001F3BA\U0001F95A\U0001F4C8\U0001F389\U0001F6CC",
            "A chicken found corn, sounded a trumpet, the egg chart improved, everyone celebrated, and Plotty immediately needed a nap."),
        ("\U0001F4A4\U0001F4E3\U0001F624\U0001F95A\u26A1\U0001F4CA\U0001F3C5",
            "Plotty was asleep, got loudly summoned, became determined, electrified the egg metrics, and awarded itself a tiny medal."),
        ("\U0001F30C\U0001F95A\U0001F680\U0001FA90\U0001F4C9\U0001F928\U0001F4C8\U0001F601",
            "Plotty took an egg to space, saw one scary dip, questioned reality, then watched the graph recover and grinned."),
        ("\U0001F9E0\U0001F95A\U0001F9EE\U0001F414\U0001F37A\u2728\U0001F451",
            "Plotty did egg math with one chicken consultant, was paid in beer, and declared the result royal enough."),
        ("\U0001F4E6\U0001F95A\U0001F4E6\U0001F95A\U0001F4E6\U0001F605\u2705",
            "Plotty sorted boxes, found eggs in every box, sweated a little, and still marked the task complete."),
        ("\U0001F56F\uFE0F\U0001F95A\U0001F4D6\U0001F414\U0001F32A\uFE0F\u2615\U0001FAE1",
            "Plotty lit a candle, consulted the ancient egg notes, survived a chicken weather event, and found courage in coffee."),
        ("\U0001F3B2\U0001F95A\U0001F340\U0001F4C8\U0001F973\U0001F37A\U0001F319",
            "Plotty rolled the dice, got lucky, watched the numbers climb, celebrated responsibly, and closed the day under the moon.")
    ];

    var mood = moods[Random.Shared.Next(moods.Length)];
    return $"**Emoji story**\n{mood.Emoji}\n\n**Translation**\n{mood.Story}";
}

static string RandomPlottyExcuse() {
    string[] excuses = [
        "Plotty cannot reply right now because a chicken is sitting on the enter key.",
        "Plotty is busy explaining compound interest to an egg that refuses to hatch.",
        "Plotty has been summoned to a very important coop meeting about snacks.",
        "Plotty's response is incubating. Please allow three to five business clucks.",
        "Plotty tried to answer, but the egg rolled under the server rack.",
        "Plotty is currently negotiating with a hen who demands better token timing.",
        "Plotty cannot reply because the chickens unionized and filed a beakwork complaint.",
        "Plotty's thoughts are scrambled, lightly salted, and served with toast.",
        "Plotty is waiting for the game servers to sync, which may outlive us all.",
        "Plotty was prepared to answer, but a rooster called an emergency sunrise.",
        "Plotty has exceeded the daily recommended allowance of egg puns.",
        "Plotty's reply was pecked apart during quality assurance.",
        "Plotty is checking whether this question is AAA grade or merely egg-shaped.",
        "Plotty cannot reply until the coop morale committee approves the vibes.",
        "Plotty found an eggcellent answer, then immediately misplaced it in the nest."
    ];

    return excuses[Random.Shared.Next(excuses.Length)];
}

static string RandomPlottyWisdom(string mention) {
    string[] wisdom = [
        "The egg does not rush the dawn, and somehow breakfast still happens.",
        "A full coop is useful, but a synced coop is sacred.",
        "Do not measure the day only by eggs laid. Measure it by the chaos you survived with style.",
        "The chicken that crosses the road still has to sync when it gets there.",
        "Greatness is often just consistency wearing a little hat.",
        "One good prestige can forgive many confused mornings.",
        "A watched silo feels empty. A tended farm becomes inevitable.",
        "Be kind to your future self; they inherit every choice you are too tired to label.",
        "The shell protects the egg, but it also knows when to break.",
        "If the numbers look bad, first check the sync. If the sync looks bad, check your patience.",
        "The strongest farms are built from tiny multipliers that refused to quit.",
        "Some days you are the golden egg. Some days you are the pan. Continue anyway.",
        "Clarity arrives after motion more often than before it.",
        "Never confuse being behind with being done.",
        "A chicken has no hands and still gets things done. Plotty finds this suspiciously inspiring.",
        "The leaderboard remembers the rate, but the guild remembers who showed up.",
        "Do the next small useful thing. The grand strategy can catch up later.",
        "Every hatch begins as pressure from inside the shell.",
        "If you cannot solve the whole farm, feed one chicken.",
        "The best time to sync was earlier. The second best time is before Staff notices."
    ];

    return $"{mention} {wisdom[Random.Shared.Next(wisdom.Length)]}";
}

static string RandomPlottyMentionResponse(string mention, string content) {
    if(LooksLikeQuestion(content)) {
        string[] diversions = [
            "Plotty heard a question, but suddenly needs to discuss whether soup is a beverage with better branding.",
            "Plotty was about to answer, then remembered that traffic cones probably have office politics.",
            "Plotty cannot focus on that until someone explains why socks disappear with such confidence.",
            "Plotty has redirected this inquiry to the Department of Clouds That Look Like Furniture.",
            "Plotty thinks the real question is whether cereal becomes soup when nobody is watching.",
            "Plotty considered answering, but now we are ranking fictional elevator music by emotional damage.",
            "Plotty says that is important, but have we checked whether the moon is just a very committed nightlight?",
            "Plotty briefly understood the question, then a tiny parade of spreadsheets marched through its thoughts.",
            "Plotty has chosen to pivot toward the mystery of why bread gets a fancy name after being toasted twice.",
            "Plotty would answer, but the imaginary committee on hat storage has called an emergency meeting.",
            "Plotty believes the answer is probably yes, unless we are talking about haunted calculators.",
            "Plotty has replaced the question with a discussion about competitive spoon stacking.",
            "Plotty is emotionally unavailable because it just learned that pillows have corners.",
            "Plotty opened the question, saw responsibility inside, and gently closed the tab.",
            "Plotty says the vibes point toward asking a pineapple for a second opinion."
        ];

        return $"{mention} {diversions[Random.Shared.Next(diversions.Length)]}";
    }

    string[] replies = [
        "Plotty has been summoned and is pretending to look busy.",
        "Plotty is here, carrying one clipboard and absolutely no certainty.",
        "Plotty heard its name and arrived with suspicious confidence.",
        "Plotty acknowledges the ping and offers one respectful nod.",
        "Plotty is present, lightly caffeinated, and monitoring the egg economy.",
        "Plotty has entered the chat with premium-grade confusion.",
        "Plotty is listening. Plotty is also thinking about snacks.",
        "Plotty reports for duty, probably.",
        "Plotty has materialized with the energy of a spreadsheet wearing sunglasses.",
        "Plotty accepts this summons and will now stand dramatically near the coop."
    ];

    return $"{mention} {replies[Random.Shared.Next(replies.Length)]}";
}

static bool LooksLikeQuestion(string content) {
    if(content.Contains('?')) {
        return true;
    }

    var normalized = Regex.Replace(content, @"<@!?\d+>", "", RegexOptions.Compiled).Trim().ToLowerInvariant();
    string[] questionStarters = [
        "who ", "what ", "when ", "where ", "why ", "how ", "can ", "could ", "would ",
        "should ", "do ", "does ", "did ", "is ", "are ", "am ", "will ", "was ", "were "
    ];

    return questionStarters.Any(normalized.StartsWith);
}

static bool LooksLikeSarcasm(string content) {
    if(string.IsNullOrWhiteSpace(content)) {
        return false;
    }

    var normalized = content.Trim().ToLowerInvariant();
    if(normalized.Length < 8) {
        return false;
    }

    string[] explicitMarkers = [
        "/s", "sarcasm", "sarcastic", "yeah right", "sure jan", "as if",
        "totally not", "what could possibly go wrong", "because that always works",
        "love that for us", "shocking", "how surprising", "big brain",
        "genius move", "great job", "nice work", "wonderful", "fantastic",
        "amazing", "perfect", "brilliant", "obviously", "clearly"
    ];

    if(explicitMarkers.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))) {
        return true;
    }

    string[] praiseWords = ["great", "nice", "perfect", "awesome", "amazing", "wonderful", "fantastic", "brilliant"];
    string[] problemWords = ["again", "broken", "failed", "late", "crashed", "missing", "wrong", "terrible", "bad", "disaster"];
    if(praiseWords.Any(normalized.Contains) && problemWords.Any(normalized.Contains)) {
        return true;
    }

    return Regex.IsMatch(normalized, @"\b(oh|wow|well)\s+(great|perfect|fantastic|wonderful|amazing)\b", RegexOptions.IgnoreCase);
}

static string RandomSarcasmResponse(string mention) {
    string[] responses = [
        "Plotty detected sarcasm and has filed it under `emotionally seasoned feedback`.",
        "Plotty hears the sarcasm. Plotty is placing a tiny cone around it for safety.",
        "Plotty has identified a sarcastic remark with approximately 87% poultry confidence.",
        "Plotty is not saying that was sarcastic, but the egg rolled its eyes.",
        "Plotty caught that tone. The coop drama sensors are blinking.",
        "Plotty has translated that as: `everything is fine, except the part where it is not`.",
        "Plotty appreciates the artisanal sarcasm. Very small batch. Very crispy.",
        "Plotty is adding one imaginary feather to the Sarcasm Forecast.",
        "Plotty recognizes this flavor: lightly toasted sarcasm with notes of chaos.",
        "Plotty recommends pairing that sarcasm with a responsible sync and a glass of water.",
        "Plotty has forwarded this remark to the Department of Obviously Fine Situations.",
        "Plotty salutes the sarcasm and will pretend it did not hurt the spreadsheet's feelings."
    ];

    return $"{mention} {responses[Random.Shared.Next(responses.Length)]}";
}

static string RandomFoxResponse(string mention) {
    string[] responses = [
        $"{mention} Plotty heard the fox inquiry and has opened a very serious woodland investigation.",
        $"{mention} Plotty refuses to speculate without a tiny detective hat and at least three snacks.",
        $"{mention} Plotty checked the logs. The fox remains mysterious, dramatic, and weirdly catchy.",
        $"{mention} Plotty says the fox filed its statement as `confusing-but-iconic.txt`.",
        $"{mention} Plotty translated the fox report: mostly vibes, zero actionable metrics.",
        $"{mention} Plotty has escalated this to the Department of Extremely Specific Animal Questions.",
        $"{mention} Plotty thinks the fox answer is above its current permission level.",
        $"{mention} Plotty found paw prints near the dashboard and a suspicious amount of glitter.",
        $"{mention} Plotty advises calm. The fox is probably just optimizing its contribution rate.",
        $"{mention} Plotty says: if the fox syncs late, Staff will hear about it.",
        $"{mention} Plotty ran the numbers and the fox is currently producing 0q/hr of clarity.",
        $"{mention} Plotty attempted fox-to-English translation and got `try again after coffee`.",
        $"{mention} Plotty believes the fox is an edge case with excellent marketing.",
        $"{mention} Plotty says the fox declined to comment, then sprinted into the changelog.",
        $"{mention} Plotty has placed the fox on the Beer Leaderboard under `unverified legend`.",
        $"{mention} Plotty recommends asking again, but with more dramatic hand gestures.",
        $"{mention} Plotty confirms the fox is not in the registered EID list.",
        $"{mention} Plotty found the fox hiding behind a modal labeled `advanced nonsense`.",
        $"{mention} Plotty says the fox has strong mascot energy and questionable documentation.",
        $"{mention} Plotty cannot quote the song, but Plotty can confirm the fox discourse is alive."
    ];

    return responses[Random.Shared.Next(responses.Length)];
}

static string RandomBeerThanksResponse() {
    string[] responses = [
        "slides the beer into a tiny server rack koozie. Thank you.",
        "accepts the beer with all the grace of a spreadsheet learning to dance.",
        "raises a glass and immediately starts optimizing the foam-to-liquid ratio.",
        "thanks you warmly, then files the receipt under `important nonsense`.",
        "takes a sip and briefly understands human morale.",
        "clinks glasses. A small background process is now happier.",
        "adds `beer` to the dependency graph. Build morale succeeded.",
        "nods solemnly. The hops have been acknowledged.",
        "accepts the offering. The command cooldown gods are pleased.",
        "says thanks and tries very hard not to carbonate the database.",
        "logs this as a critical uptime improvement.",
        "toasts to clean syncs, high rates, and suspiciously cooperative APIs.",
        "drinks responsibly, which for Plotty means staying under the token limit.",
        "thanks you. Somewhere, a dashboard widget became 3% more cheerful.",
        "accepts the beer and promises not to deploy after the second one.",
        "raises the glass like it just passed CI on the first try.",
        "thanks you with the quiet confidence of Plotty finding the right contract ID.",
        "adds a tiny umbrella to the beer because presentation matters.",
        "salutes you with a frosty beverage and questionable dignity.",
        "takes the beer and emits one perfectly formatted burp packet."
    ];

    return responses[Random.Shared.Next(responses.Length)];
}

static string RandomBeerGiftResponse() {
    string[] responses = [
        "Plot twist: Plotty bought **you** a beer. Legendary hydration event.",
        "Plotty checked the tab and decided this round is on the house.",
        "Critical success. Plotty buys you a beer and pretends this was budgeted.",
        "Plotty reaches into an imaginary wallet and buys you a cold one.",
        "Reverse uno: Plotty buys you a beer. Your leaderboard era begins.",
        "Plotty says thank you by buying you a beer and absolutely calling it strategy.",
        "A rare kindness proc occurred. Plotty bought you a beer.",
        "Plotty has selected you for the sacred beverage reimbursement program.",
        "Plotty buys the round. Please enjoy this highly responsible victory.",
        "Against all accounting advice, Plotty bought you a beer."
    ];

    return responses[Random.Shared.Next(responses.Length)];
}

static string FormatEggs(double amount) {
    string[] suffixes = ["", "K", "M", "B", "T", "q", "Q", "s", "S", "o", "N", "d", "U", "D"];
    var value = amount;
    var abs = Math.Abs(value);
    var index = 0;
    while(abs >= 1000 && index < suffixes.Length - 1) {
        value /= 1000;
        abs /= 1000;
        index++;
    }

    var format = abs >= 100 ? "0" : abs >= 10 ? "0.0" : "0.00";
    return value.ToString(format) + suffixes[index];
}

static string EggNameForStatsIndex(int index) {
    string[] names = [
        "Edible",
        "Superfood",
        "Medical",
        "Rocket Fuel",
        "Super Material",
        "Fusion",
        "Quantum",
        "Immortality",
        "Tachyon",
        "Graviton",
        "Dilithium",
        "Prodigy",
        "Terraform",
        "Antimatter",
        "Dark Matter",
        "AI",
        "Nebula",
        "Universe",
        "Enlightenment"
    ];
    if(index >= 0 && index < names.Length) {
        return names[index];
    }

    var colleggtibleName = index switch {
        20 => "Curiosity",
        21 => "Integrity",
        22 => "Humility",
        23 => "Resilience",
        24 => "Kindness",
        _ => null
    };
    if(colleggtibleName is not null) {
        return colleggtibleName;
    }

    return $"Egg #{index + 1}";
}

static string EggDisplayName(Egg egg) => egg switch {
    Egg.Edible => "Edible",
    Egg.Superfood => "Superfood",
    Egg.Medical => "Medical",
    Egg.RocketFuel => "Rocket Fuel",
    Egg.SuperMaterial => "Super Material",
    Egg.Fusion => "Fusion",
    Egg.Quantum => "Quantum",
    Egg.Immortality => "Immortality",
    Egg.Tachyon => "Tachyon",
    Egg.Graviton => "Graviton",
    Egg.Dilithium => "Dilithium",
    Egg.Prodigy => "Prodigy",
    Egg.Terraform => "Terraform",
    Egg.Antimatter => "Antimatter",
    Egg.DarkMatter => "Dark Matter",
    Egg.Ai => "AI",
    Egg.Nebula => "Nebula",
    Egg.Universe => "Universe",
    Egg.Enlightenment => "Enlightenment",
    Egg.Curiosity => "Curiosity",
    Egg.Integrity => "Integrity",
    Egg.Humility => "Humility",
    Egg.Resilience => "Resilience",
    Egg.Kindness => "Kindness",
    Egg.Chocolate => "Chocolate",
    Egg.Easter => "Easter",
    Egg.Waterballoon => "Water Balloon",
    Egg.Firework => "Firework",
    Egg.Pumpkin => "Pumpkin",
    Egg.CustomEgg => "Custom Egg",
    _ => egg.ToString()
};

static string NormalizeName(string? value) {
    if(string.IsNullOrWhiteSpace(value)) {
        return "";
    }

    return new string(value
        .Trim()
        .ToLowerInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());
}

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

        return new BotSettings(new DiscordSettings(null, null), new StorageSettings("data/egg-links.json", "data/eid-key.bin"));
    }
}

public sealed record DiscordSettings(string? Token, string? GuildId) {
    public ulong? ParsedGuildId => ulong.TryParse(GuildId, out var id) ? id : null;
}

public sealed record StorageSettings(string DataPath, string KeyPath = "data/eid-key.bin");

public sealed record EggUserLink(ulong GuildId, ulong DiscordUserId, string EggId, string? EggName);
public sealed record RegisteredEid(ulong GuildId, ulong DiscordUserId, string EidHash, string EncryptedEid, string? EggName, DateTimeOffset UpdatedAt);
public sealed record RegisteredEggAccount(ulong DiscordUserId, string Eid, string? EggName, DateTimeOffset UpdatedAt) {
    public string EidHash => SecureText.Sha256(EggIncClient.NormalizeEggId(Eid));
}
public sealed record PlayerContractCandidate(string ContractId, string CoopCode, double AcceptedAt);
public sealed record PlayerContractRate(string ContractId, double RatePerHour, double ContributionAmount);
public sealed record ArtifactCandidate(
    ArtifactInventoryItem Item,
    CompleteArtifact Artifact,
    ArtifactSpec.Types.Name Name,
    string DisplayName,
    ArtifactPurpose Purpose,
    double Score,
    string Reason,
    string? ImageUrl,
    double Multiplier);
public sealed record WikiAnswer(string Title, string Url, string Answer);
public sealed record PersonalHelpAnswer(string Title, string Answer, string Source, string? ImageUrl);
public sealed record FarmerRankInfo(int Oom, string Name);
public sealed record DashboardResult(string Message, IReadOnlyList<Embed> Embeds, IReadOnlyList<DashboardPlayerRow> Rows);
public sealed record DashboardPlayerRow(
    string ContractId,
    ulong DiscordUserId,
    string PlayerName,
    double RatePerHour,
    double ContributionAmount,
    bool Active,
    bool TimeCheatDetected,
    DashboardCategory Category,
    string Reason);
public sealed record AutoDemeritCandidate(ulong DiscordUserId, string ContractId, double RatePerHour, string PlayerName, string SourceKey);
public sealed record MissingJoinAccountSnapshot(RegisteredEggAccount Account, IReadOnlySet<string> JoinedContractIds);
public sealed record MissingJoinMember(ulong DiscordUserId, string Label, IReadOnlyList<RegisteredEggAccount> Accounts);
public sealed record MissingJoinAlert(string Key, DateTimeOffset PostedAt);
public sealed record EggsLaidTotal(string Name, double Amount);
public sealed record BeerStats(
    ulong GuildId,
    ulong DiscordUserId,
    int BeersGivenToBot,
    int BeersBoughtByBot,
    DateTimeOffset FirstBeerAt,
    DateTimeOffset LastBeerAt,
    DateTimeOffset? LastBotBeerAt,
    int BeersReceivedFromMembers = 0,
    int BeersGivenToMembers = 0,
    DateTimeOffset? LastMemberBeerReceivedAt = null,
    DateTimeOffset? LastMemberBeerGivenAt = null);
public sealed record BeerAttemptResult(bool Accepted, BeerStats Stats, TimeSpan? RetryAfter);
public sealed record BeerGiftLog(ulong GuildId, ulong GiverDiscordUserId, ulong RecipientDiscordUserId, DateTimeOffset GiftedAt);
public sealed record DemeritEntry(
    string Id,
    ulong GuildId,
    ulong DiscordUserId,
    int Amount,
    string Reason,
    string? ContractId,
    string? PlayerName,
    string? SourceKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RemovedAt);

public enum DashboardCategory {
    NoSync = 0,
    LikelyUnboosted = 1,
    BelowThreshold = 2,
    Flagged = 3,
    Healthy = 4
}

public enum ArtifactPurpose {
    TeamLaying = 0,
    Laying = 1,
    Shipping = 2,
    Capacity = 3,
    TeamEarnings = 4,
    Boosting = 5,
    InternalHatchery = 6,
    StoneCarrier = 7
}

public sealed class EggWikiClient {
    private const string ApiBase = "https://egg-inc.fandom.com/api.php";
    private static readonly HttpClient Http = new();
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from",
        "how", "i", "in", "is", "it", "me", "of", "on", "or", "the", "to", "what", "when",
        "where", "which", "who", "why", "with", "you", "your"
    };

    static EggWikiClient() {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PlottyDiscordBot/1.0");
    }

    public async Task<WikiAnswer?> AnswerAsync(string question) {
        var title = await SearchAsync(question);
        if(string.IsNullOrWhiteSpace(title)) {
            return null;
        }

        var page = await GetExtractAsync(title);
        if(page is null || string.IsNullOrWhiteSpace(page.Value.Extract)) {
            return null;
        }

        var answer = BuildAnswer(question, page.Value.Extract);
        if(string.IsNullOrWhiteSpace(answer)) {
            return null;
        }

        return new WikiAnswer(
            page.Value.Title,
            $"https://egg-inc.fandom.com/wiki/{Uri.EscapeDataString(page.Value.Title.Replace(' ', '_'))}",
            answer);
    }

    private static async Task<string?> SearchAsync(string question) {
        var url = $"{ApiBase}?action=query&list=search&srsearch={Uri.EscapeDataString(question)}&srlimit=1&format=json&utf8=1";
        using var stream = await Http.GetStreamAsync(url);
        using var doc = await JsonDocument.ParseAsync(stream);
        var results = doc.RootElement.GetProperty("query").GetProperty("search");
        return results.GetArrayLength() == 0
            ? null
            : results[0].GetProperty("title").GetString();
    }

    private static async Task<(string Title, string Extract)?> GetExtractAsync(string title) {
        var url = $"{ApiBase}?action=query&prop=extracts&explaintext=1&redirects=1&format=json&titles={Uri.EscapeDataString(title)}&utf8=1";
        using var stream = await Http.GetStreamAsync(url);
        using var doc = await JsonDocument.ParseAsync(stream);
        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach(var page in pages.EnumerateObject()) {
            if(!page.Value.TryGetProperty("extract", out var extractElement)) {
                continue;
            }

            var resolvedTitle = page.Value.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString() ?? title
                : title;
            return (resolvedTitle, WebUtility.HtmlDecode(extractElement.GetString() ?? ""));
        }

        return null;
    }

    private static string BuildAnswer(string question, string extract) {
        var keywords = Regex.Matches(question.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sentences = SentenceSplit
            .Split(Regex.Replace(extract, @"\s+", " ").Trim())
            .Where(s => s.Length is > 30 and < 450)
            .Select(s => new {
                Text = s.Trim(),
                Score = keywords.Count(k => s.Contains(k, StringComparison.OrdinalIgnoreCase))
            })
            .OrderByDescending(s => s.Score)
            .ThenBy(s => extract.IndexOf(s.Text, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(s => s.Text)
            .ToList();

        if(sentences.Count == 0) {
            return TrimForDiscord(extract);
        }

        return TrimForDiscord(string.Join(" ", sentences));
    }

    private static string TrimForDiscord(string value) =>
        value.Length <= 1500 ? value : value[..1497] + "...";
}

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
        public List<BeerStats> BeerStats { get; set; } = [];
        public List<BeerGiftLog> BeerGiftLogs { get; set; } = [];
    }
}
