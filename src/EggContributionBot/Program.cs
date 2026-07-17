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
var shipReturnMonitorStarted = false;

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
        if(!shipReturnMonitorStarted) {
            shipReturnMonitorStarted = true;
            _ = Task.Run(() => MonitorShipReturnsAsync(guildId));
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

        switch(command.CommandName) {
            case "contract":
                await HandleContractAsync(command);
                break;
            case "contract-late-notify":
                await HandleContractLateNotifyAsync(command);
                break;
            case "admin-remove-late-notify":
                await HandleAdminRemoveLateNotifyAsync(command);
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
            case "add-demerit":
                await HandleAddDemeritAsync(command);
                break;
            case "remove-demerit":
                await HandleRemoveDemeritAsync(command);
                break;
            case "demerits-view":
                await HandleDemeritsViewAsync(command);
                break;
            case "admin-demerits-view-all":
                await HandleAdminDemeritsViewAllAsync(command);
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
            case "ships":
                await HandleShipsAsync(command);
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
        if(message.Author.IsBot || message.Channel is not SocketGuildChannel guildChannel) {
            return;
        }

        if(message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id)) {
            var prompt = StripBotMention(message.Content);
            var isQuestion = LooksLikeQuestion(prompt);
            var memory = await dataStore.RecordPlottyInteractionAsync(
                guildChannel.Guild.Id,
                message.Author.Id,
                isQuestion ? "question_mention" : "mention");
            var response = await BuildPlottyConversationResponseAsync(
                guildChannel.Guild.Id,
                message.Author.Id,
                message.Author.Mention,
                prompt,
                isQuestion,
                memory);
            await message.Channel.SendMessageAsync(response, allowedMentions: AllowedMentions.None);
            return;
        }

        if(message.Content.Contains("what the fox", StringComparison.OrdinalIgnoreCase)) {
            var memory = await dataStore.RecordPlottyInteractionAsync(guildChannel.Guild.Id, message.Author.Id, "fox");
            await message.Channel.SendMessageAsync(PlottyPersonality.FoxResponse(message.Author.Mention, memory));
            return;
        }

        if(LooksLikeSarcasm(message.Content) && Random.Shared.Next(3) == 0) {
            var memory = await dataStore.RecordPlottyInteractionAsync(guildChannel.Guild.Id, message.Author.Id, "sarcasm");
            await message.Channel.SendMessageAsync(PlottyPersonality.SarcasmResponse(message.Author.Mention, memory));
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
        .WithName("contract-late-notify")
        .WithDescription("Privately tell Plotty you will be late joining a contract.")
        .AddOption("contract-id", ApplicationCommandOptionType.String, "Optional contract identifier. Leave blank for current contracts.", isRequired: false)
        .AddOption("eta", ApplicationCommandOptionType.String, "Optional ETA, e.g. 2 hours, after work, tonight.", isRequired: false)
        .AddOption("note", ApplicationCommandOptionType.String, "Optional short note for Staff.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("admin-remove-late-notify")
        .WithDescription("Staff only: remove a member's active late notice.")
        .AddOption("member", ApplicationCommandOptionType.User, "Discord member.", isRequired: true)
        .AddOption("contract-id", ApplicationCommandOptionType.String, "Optional contract identifier. Leave blank to remove all active late notices.", isRequired: false);

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
        .WithName("add-demerit")
        .WithDescription("Staff only: add demerits to a member.")
        .AddOption("member", ApplicationCommandOptionType.User, "Discord member.", isRequired: true)
        .AddOption("amount", ApplicationCommandOptionType.Integer, "Number of demerits. Default is 1.", isRequired: false)
        .AddOption("reason", ApplicationCommandOptionType.String, "Optional reason.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("remove-demerit")
        .WithDescription("Staff only: remove active demerits from a member.")
        .AddOption("member", ApplicationCommandOptionType.User, "Discord member.", isRequired: true)
        .AddOption("amount", ApplicationCommandOptionType.Integer, "Number of demerits. Default is 1.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("demerits-view")
        .WithDescription("Privately view your active demerits.");

    yield return new SlashCommandBuilder()
        .WithName("admin-demerits-view-all")
        .WithDescription("Staff only: privately view all users with active demerits.");

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
        .WithName("ships")
        .WithDescription("Privately show your active ship mission and optionally DM you when it returns.")
        .AddOption("notify", ApplicationCommandOptionType.Boolean, "DM you when the active ship returns.", isRequired: false);

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

async Task HandleContractLateNotifyAsync(SocketSlashCommand command) {
    var user = command.User as SocketGuildUser;
    if(user is null) {
        await command.RespondAsync("I could not read your server member profile.", ephemeral: true);
        return;
    }

    var contractId = (command.Data.Options.FirstOrDefault(o => o.Name == "contract-id")?.Value as string)?.Trim();
    var eta = (command.Data.Options.FirstOrDefault(o => o.Name == "eta")?.Value as string)?.Trim();
    var note = (command.Data.Options.FirstOrDefault(o => o.Name == "note")?.Value as string)?.Trim();
    if(note?.Length > 300) {
        note = note[..300];
    }

    var notice = await dataStore.RecordContractLateNoticeAsync(
        command.GuildId!.Value,
        user.Id,
        string.IsNullOrWhiteSpace(contractId) ? null : contractId,
        string.IsNullOrWhiteSpace(eta) ? null : eta,
        string.IsNullOrWhiteSpace(note) ? null : note);

    var guild = client.GetGuild(command.GuildId.Value);
    var staffChannel = guild is null ? null : FindStaffNoticeChannel(guild);
    if(staffChannel is not null) {
        await staffChannel.SendMessageAsync(embed: BuildContractLateNoticeEmbed(user, notice), allowedMentions: AllowedMentions.None);
    }

    var contractText = string.IsNullOrWhiteSpace(notice.ContractId) ? "current contracts" : $"`{notice.ContractId}`";
    var staffText = staffChannel is null ? " I could not find a Staff notice channel, but I saved the flag locally." : " I let Staff know without pinging them.";
    await command.RespondAsync($"Got it. I marked you late for {contractText} for the next 48 hours, so you will not be added to the 6-hour non-join list while that flag is active.{staffText}", ephemeral: true);
}

Embed BuildContractLateNoticeEmbed(SocketGuildUser user, ContractLateNotice notice) {
    var contractText = string.IsNullOrWhiteSpace(notice.ContractId) ? "Current contracts / unspecified" : notice.ContractId;
    var builder = new EmbedBuilder()
        .WithTitle("Contract Late Notice")
        .WithColor(Color.Gold)
        .AddField("Member", user.Mention, true)
        .AddField("Contract", contractText, true)
        .AddField("Expires", notice.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), true)
        .WithFooter("This member will be skipped by the 6-hour non-join alert while the notice is active.")
        .WithCurrentTimestamp();

    if(!string.IsNullOrWhiteSpace(notice.Eta)) {
        builder.AddField("ETA", notice.Eta, true);
    }

    if(!string.IsNullOrWhiteSpace(notice.Note)) {
        builder.AddField("Note", notice.Note, false);
    }

    return builder.Build();
}

async Task HandleAdminRemoveLateNotifyAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can remove late notices.", ephemeral: true);
        return;
    }

    var member = (SocketGuildUser)command.Data.Options.First(o => o.Name == "member").Value;
    var contractId = (command.Data.Options.FirstOrDefault(o => o.Name == "contract-id")?.Value as string)?.Trim();
    var removed = await dataStore.RemoveContractLateNoticesAsync(
        command.GuildId!.Value,
        member.Id,
        string.IsNullOrWhiteSpace(contractId) ? null : contractId);

    var scope = string.IsNullOrWhiteSpace(contractId) ? "all active late notices" : $"active late notices for `{contractId}`";
    await command.RespondAsync($"Removed `{removed}` {scope} from {member.Mention}.", ephemeral: true);
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

    var now = DateTimeOffset.UtcNow;
    var recentContractIds = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.StartTime > 0)
        .Where(c => DateTimeOffset.FromUnixTimeSeconds((long)c.StartTime) >= now.AddDays(-3))
        .Where(c => c.ExpirationTime <= 0 || DateTimeOffset.FromUnixTimeSeconds((long)c.ExpirationTime) > now)
        .Select(c => c.Identifier)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(recentContractIds.Count == 0) {
        await command.FollowupAsync("I could not find any active contracts released in the past 3 days.", ephemeral: true);
        return;
    }

    var statuses = new Dictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse>();
    var failed = 0;
    var skippedOldContracts = 0;
    foreach(var account in accounts) {
        var lookup = await eggClient.GetPlayerCoopLookupAsync(account.Eid);
        if(lookup.Statuses.Count == 0) {
            failed++;
            continue;
        }

        foreach(var status in lookup.Statuses) {
            if(!recentContractIds.Contains(status.ContractId)) {
                skippedOldContracts++;
                continue;
            }

            var key = (status.ContractId.ToLowerInvariant(), status.CoopCode.ToLowerInvariant());
            statuses.TryAdd(key, status.Status);
        }
    }

    if(statuses.Count == 0) {
        await command.FollowupAsync(
            $"I checked `{accounts.Count}` registered EID(s), but none had active co-op rates for contracts released in the past 3 days.",
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
        ? $"Showing registered EID players only for contracts released in the past 3 days. `{failed}` registered EID(s) did not return active rates."
        : $"Showing registered EID players only for contracts released in the past 3 days from `{accounts.Count}` registered EID(s).";
    if(skippedOldContracts > 0) {
        message += $" Skipped `{skippedOldContracts}` older active co-op lookup(s).";
    }

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

async Task MonitorShipReturnsAsync(ulong guildId) {
    await Task.Delay(TimeSpan.FromMinutes(1));
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
    while(true) {
        try {
            await CheckShipReturnNotificationsAsync(guildId);
        } catch(Exception ex) {
            Console.WriteLine($"Ship return monitor failed: {ex}");
        }

        await timer.WaitForNextTickAsync();
    }
}

async Task CheckShipReturnNotificationsAsync(ulong guildId) {
    var due = await dataStore.GetDueShipReturnNotificationsAsync(guildId, DateTimeOffset.UtcNow);
    if(due.Count == 0) {
        return;
    }

    var guild = client.GetGuild(guildId);
    foreach(var notification in due) {
        try {
            var user = guild?.GetUser(notification.DiscordUserId) ?? client.GetUser(notification.DiscordUserId);
            if(user is not null) {
                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(
                    $"Your **{notification.ShipName}** ship mission should be back now. Time to collect the cargo.");
            }
        } catch(Exception ex) {
            Console.WriteLine($"Could not DM ship return notification to {notification.DiscordUserId}: {ex.Message}");
        } finally {
            await dataStore.MarkShipReturnNotificationSentAsync(notification.Key, DateTimeOffset.UtcNow);
        }
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

        var lateNoticeUsers = await dataStore.GetActiveContractLateNoticeUserIdsAsync(guildId, contractId);
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
            .Where(m => !lateNoticeUsers.Contains(m.DiscordUserId))
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

static SocketTextChannel? FindPlottyGossipChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "plottygossip");

static SocketTextChannel? FindStaffNoticeChannel(SocketGuild guild) =>
    FindPlottyGossipChannel(guild) ?? FindStaffTextChannel(guild);

async Task SendRegistrationWelcomeAsync(ulong guildId, IUser user) {
    var guild = client.GetGuild(guildId);
    var gossipChannel = guild is null ? null : FindPlottyGossipChannel(guild);
    if(gossipChannel is null) {
        return;
    }

    var displayName = user is SocketGuildUser guildUser ? guildUser.DisplayName : user.Username;
    var memory = await dataStore.RecordPlottyInteractionAsync(guildId, user.Id, "registration");
    await gossipChannel.SendMessageAsync(
        $"{displayName} {PlottyPersonality.RegistrationWelcome(memory)}",
        allowedMentions: AllowedMentions.None);
}

async Task HandleAddDemeritAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can add demerits.", ephemeral: true);
        return;
    }

    var member = (SocketGuildUser)command.Data.Options.First(o => o.Name == "member").Value;
    var amount = Math.Max(1, Convert.ToInt32(command.Data.Options.FirstOrDefault(o => o.Name == "amount")?.Value ?? 1));
    var reason = command.Data.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string;

    var added = await dataStore.AddDemeritsAsync(
        command.GuildId!.Value,
        member.Id,
        amount,
        reason ?? "Manual staff demerit",
        contractId: null,
        sourceKey: null);
    await command.RespondAsync($"Added `{added}` demerit(s) to {member.Mention}.", ephemeral: true);
}

async Task HandleRemoveDemeritAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can remove demerits.", ephemeral: true);
        return;
    }

    var member = (SocketGuildUser)command.Data.Options.First(o => o.Name == "member").Value;
    var amount = Math.Max(1, Convert.ToInt32(command.Data.Options.FirstOrDefault(o => o.Name == "amount")?.Value ?? 1));
    var removed = await dataStore.RemoveDemeritsAsync(command.GuildId!.Value, member.Id, amount);
    await command.RespondAsync($"Removed `{removed}` active demerit(s) from {member.Mention}.", ephemeral: true);
}

async Task HandleDemeritsViewAsync(SocketSlashCommand command) {
    var member = command.User as SocketGuildUser;
    if(member is null) {
        await command.RespondAsync("Plotty could not read your server member profile.", ephemeral: true);
        return;
    }

    var demerits = await dataStore.GetActiveDemeritsAsync(command.GuildId!.Value, member.Id);
    await command.RespondAsync(BuildDemeritList(member, demerits), ephemeral: true);
}

async Task HandleAdminDemeritsViewAllAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can view all demerits.", ephemeral: true);
        return;
    }

    await command.DeferAsync(ephemeral: true);

    var demerits = await dataStore.GetActiveDemeritsAsync(command.GuildId!.Value);
    var guild = client.GetGuild(command.GuildId.Value);
    await command.FollowupAsync(BuildAllDemeritsList(guild, demerits), ephemeral: true);
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

async Task HandleShipsAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var notify = GetBool(command, "notify");
    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var embeds = new List<Embed>();
    var notificationCount = 0;
    foreach(var account in accounts.Take(10)) {
        var backup = await eggClient.GetBackupAsync(account.Eid);
        var missions = GetActiveShipMissions(backup).ToList();
        embeds.Add(BuildShipsEmbed(backup, account, missions, notify));

        if(!notify) {
            continue;
        }

        foreach(var mission in missions) {
            if(mission.ReturnAt is not { } returnAt || returnAt <= DateTimeOffset.UtcNow) {
                continue;
            }

            await dataStore.UpsertShipReturnNotificationAsync(new ShipReturnNotification(
                command.GuildId!.Value,
                command.User.Id,
                account.EidHash,
                mission.Key,
                mission.ShipName,
                returnAt,
                DateTimeOffset.UtcNow,
                NotifiedAt: null));
            notificationCount++;
        }
    }

    var text = notify
        ? notificationCount > 0
            ? $"I will DM you when `{notificationCount}` active ship mission(s) return."
            : "I did not find a launched ship with a future return time to notify you about."
        : accounts.Count > 1
            ? $"Showing ships for `{embeds.Count}` Egg Inc account(s) tied to your Discord name."
            : null;

    await command.FollowupAsync(text: text, embeds: embeds.ToArray(), ephemeral: true);
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

    await SendRegistrationWelcomeAsync(modal.GuildId.Value, modal.User);
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

    var plottyMention = client.CurrentUser.Mention;
    var response = botBuysBack
        ? $"{plottyMention} accepts the beer from {command.User.Mention}. {PlottyPersonality.BeerGiftResponse(await dataStore.RecordPlottyInteractionAsync(command.GuildId!.Value, command.User.Id, "beer_bot_buyback"))}\n\nYou earned a spot on the Beer Leaderboard. Total Plotty-bought beers: `{stats.BeersBoughtByBot}`."
        : $"{plottyMention} accepts the beer from {command.User.Mention}. {PlottyPersonality.BeerThanksResponse(await dataStore.RecordPlottyInteractionAsync(command.GuildId!.Value, command.User.Id, "beer_plotty"))}\n\nBeers donated to Plotty: `{stats.BeersGivenToBot}`.";

    var embed = new EmbedBuilder()
        .WithTitle(botBuysBack ? "Plotty Bought A Round" : $"{displayName} bought Plotty a beer")
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
    var memory = await dataStore.RecordPlottyInteractionAsync(command.GuildId!.Value, command.User.Id, "mood");
    await command.RespondAsync(PlottyPersonality.Mood(memory));
}

async Task HandlePlottyExcusesAsync(SocketSlashCommand command) {
    var memory = await dataStore.RecordPlottyInteractionAsync(command.GuildId!.Value, command.User.Id, "excuse");
    await command.RespondAsync(PlottyPersonality.Excuse(memory));
}

async Task HandlePlottyWisdomAsync(SocketSlashCommand command) {
    var memory = await dataStore.RecordPlottyInteractionAsync(command.GuildId!.Value, command.User.Id, "wisdom");
    await command.RespondAsync(PlottyPersonality.Wisdom(command.User.Mention, memory));
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
        return $"**{name}** - {FormatEggs(p.ContributionRate * 3600)}/hr, {FormatEggs(p.ContributionAmount)} contributed{flag}";
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

    var now = DateTimeOffset.UtcNow;
    var recentContractIds = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.StartTime > 0)
        .Where(c => DateTimeOffset.FromUnixTimeSeconds((long)c.StartTime) >= now.AddDays(-3))
        .Where(c => c.ExpirationTime <= 0 || DateTimeOffset.FromUnixTimeSeconds((long)c.ExpirationTime) > now)
        .Select(c => c.Identifier)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(recentContractIds.Count == 0) {
        return new DashboardResult("I could not find any active contracts released in the past 3 days.", [], []);
    }

    var statuses = new Dictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse>();
    var failed = 0;
    var skippedOldContracts = 0;
    foreach(var account in accounts) {
        var lookup = await eggClient.GetPlayerCoopLookupAsync(account.Eid);
        if(lookup.Statuses.Count == 0) {
            failed++;
            continue;
        }

        foreach(var status in lookup.Statuses) {
            if(!recentContractIds.Contains(status.ContractId)) {
                skippedOldContracts++;
                continue;
            }

            var key = (status.ContractId.ToLowerInvariant(), status.CoopCode.ToLowerInvariant());
            statuses.TryAdd(key, status.Status);
        }

    }

    if(statuses.Count == 0) {
        return new DashboardResult($"I checked `{accounts.Count}` registered EID(s), but none had active dashboard data for contracts released in the past 3 days.", [], []);
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
        ? $"Dashboard for contracts released in the past 3 days across `{accounts.Count}` registered EID(s). `{failed}` EID(s) did not return active co-op data. `{attention}` player issue(s) need attention."
        : $"Dashboard for contracts released in the past 3 days across `{accounts.Count}` registered EID(s). `{attention}` player issue(s) need attention.";
    if(skippedOldContracts > 0) {
        message += $" Skipped `{skippedOldContracts}` older active co-op lookup(s).";
    }

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

string BuildAllDemeritsList(SocketGuild? guild, IReadOnlyList<DemeritEntry> demerits) {
    if(demerits.Count == 0) {
        return "No users have active demerits.";
    }

    var lines = demerits
        .GroupBy(d => d.DiscordUserId)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => DisplayNameForDemerits(guild, g.Key))
        .Select(g => {
            var nextExpiry = g.Min(d => d.ExpiresAt).LocalDateTime.ToString("yyyy-MM-dd");
            var recent = g.OrderByDescending(d => d.CreatedAt).First();
            var detail = string.IsNullOrWhiteSpace(recent.ContractId)
                ? recent.Reason
                : $"{recent.Reason} (`{recent.ContractId}`)";
            return $"{DisplayNameForDemerits(guild, g.Key)} - `{g.Count()}` active, next expires `{nextExpiry}` - {detail}";
        })
        .ToList();

    var text = "**Active Demerits**\n" + string.Join("\n", lines);
    return text.Length <= 1900 ? text : text[..1900] + "\n...";
}

static string DisplayNameForDemerits(SocketGuild? guild, ulong userId) {
    var user = guild?.GetUser(userId);
    return user is null ? $"<@{userId}>" : user.Mention;
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

Embed BuildShipsEmbed(Backup? backup, RegisteredEggAccount account, IReadOnlyList<ShipMissionSnapshot> missions, bool notifyRequested) {
    var builder = new EmbedBuilder()
        .WithTitle($"Ships - {AccountDisplayName(account)}")
        .WithColor(Color.DarkBlue)
        .WithCurrentTimestamp();

    if(backup is null) {
        builder.WithDescription("I could not pull this Egg Inc backup right now.");
        return builder.Build();
    }

    if(backup.ArtifactsDb is null) {
        builder.WithDescription("I pulled the backup, but no artifact or ship data was included.");
        builder.WithFooter("Open artifacts/ships in Egg Inc and sync, then try again.");
        return builder.Build();
    }

    if(missions.Count == 0) {
        builder.WithDescription("I did not find an active ship mission in this backup.");
        builder.WithFooter("Completed or archived missions are hidden here.");
        return builder.Build();
    }

    foreach(var mission in missions.Take(6)) {
        builder.AddField(mission.ShipName, FormatShipMission(mission, notifyRequested));
    }

    if(missions.Count > 6) {
        builder.AddField("More ships", $"I found `{missions.Count - 6}` more active mission(s), but Discord only gives me so much room to breathe.");
    }

    return builder.Build();
}

IEnumerable<ShipMissionSnapshot> GetActiveShipMissions(Backup? backup) {
    if(backup?.ArtifactsDb is null) {
        yield break;
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var missions = new List<MissionInfo>();
    if(backup.ArtifactsDb.FuelingMission is not null) {
        missions.Add(backup.ArtifactsDb.FuelingMission);
    }

    missions.AddRange(backup.ArtifactsDb.MissionInfos);

    foreach(var mission in missions) {
        if(!IsActiveShipMission(mission)) {
            continue;
        }

        var key = ShipMissionKey(mission);
        if(!seen.Add(key)) {
            continue;
        }

        yield return BuildShipMissionSnapshot(mission, key);
    }
}

static bool IsActiveShipMission(MissionInfo mission) {
    var status = mission.Status.ToString();
    return !status.Equals("Complete", StringComparison.OrdinalIgnoreCase) &&
           !status.Equals("Archived", StringComparison.OrdinalIgnoreCase) &&
           !status.Equals("Aborted", StringComparison.OrdinalIgnoreCase);
}

static ShipMissionSnapshot BuildShipMissionSnapshot(MissionInfo mission, string key) {
    var now = DateTimeOffset.UtcNow;
    var startedAt = FromUnixSeconds(mission.StartTimeDerived);
    DateTimeOffset? returnAt = startedAt is not null && mission.DurationSeconds > 0
        ? startedAt.Value.AddSeconds(mission.DurationSeconds)
        : mission.SecondsRemaining > 0
            ? now.AddSeconds(mission.SecondsRemaining)
            : null;

    var shipName = ShipDisplayName(mission.Ship);
    return new ShipMissionSnapshot(
        mission,
        key,
        shipName,
        HumanizeEnum(mission.Status),
        HumanizeEnum(mission.DurationType),
        HumanizeEnum(mission.Type),
        startedAt,
        returnAt);
}

static string FormatShipMission(ShipMissionSnapshot snapshot, bool notifyRequested) {
    var mission = snapshot.Mission;
    var lines = new List<string> {
        $"**Status:** {snapshot.StatusName}",
        $"**Type:** {snapshot.MissionTypeName}",
        $"**Duration:** {snapshot.DurationTypeName} ({FormatDuration(TimeSpan.FromSeconds(Math.Max(0, mission.DurationSeconds)))})",
        $"**Level:** {mission.Level}",
        $"**Capacity:** {mission.Capacity}",
        $"**Quality bump:** {mission.QualityBump:P1}"
    };

    if(snapshot.StartedAt is not null) {
        lines.Add($"**Started:** <t:{snapshot.StartedAt.Value.ToUnixTimeSeconds()}:f>");
    }

    if(snapshot.ReturnAt is not null) {
        var remaining = snapshot.ReturnAt.Value - DateTimeOffset.UtcNow;
        lines.Add($"**Returns:** <t:{snapshot.ReturnAt.Value.ToUnixTimeSeconds()}:f> ({FormatDuration(remaining)})");
        if(notifyRequested && remaining > TimeSpan.Zero) {
            lines.Add("**DM reminder:** on");
        }
    } else if(mission.SecondsRemaining > 0) {
        lines.Add($"**Time left:** {FormatDuration(TimeSpan.FromSeconds(mission.SecondsRemaining))}");
    }

    var fuel = FormatShipFuel(mission);
    if(!string.IsNullOrWhiteSpace(fuel)) {
        lines.Add($"**Fuel:** {fuel}");
    }

    if(mission.TargetArtifact != ArtifactSpec.Types.Name.LunarTotem) {
        lines.Add($"**Target artifact:** {ArtifactName(mission.TargetArtifact)}");
    }

    if(!string.IsNullOrWhiteSpace(mission.Identifier)) {
        lines.Add($"**Mission ID:** `{mission.Identifier}`");
    }

    if(!string.IsNullOrWhiteSpace(mission.MissionLog)) {
        lines.Add($"**Log:** {TrimDiscordMessage(mission.MissionLog, 500)}");
    }

    return string.Join("\n", lines);
}

static string FormatShipFuel(MissionInfo mission) =>
    mission.Fuel.Count == 0
        ? ""
        : string.Join(", ", mission.Fuel.Select(f => $"{EggDisplayName(f.Egg)} {FormatEggs(f.Amount)}"));

static string ShipMissionKey(MissionInfo mission) {
    if(!string.IsNullOrWhiteSpace(mission.Identifier)) {
        return mission.Identifier.Trim();
    }

    return $"{mission.Ship}:{mission.Status}:{mission.StartTimeDerived:0}:{mission.DurationSeconds:0}:{mission.SecondsRemaining:0}";
}

static DateTimeOffset? FromUnixSeconds(double value) {
    if(value < 946684800 || value > DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds()) {
        return null;
    }

    return DateTimeOffset.FromUnixTimeSeconds((long)value);
}

static string ShipDisplayName(MissionInfo.Types.Spaceship ship) => ship switch {
    MissionInfo.Types.Spaceship.ChickenOne => "Chicken One",
    MissionInfo.Types.Spaceship.ChickenNine => "Chicken Nine",
    MissionInfo.Types.Spaceship.ChickenHeavy => "Chicken Heavy",
    MissionInfo.Types.Spaceship.Bcr => "BCR",
    MissionInfo.Types.Spaceship.MilleniumChicken => "Millenium Chicken",
    MissionInfo.Types.Spaceship.CorellihenCorvette => "Corellihen Corvette",
    MissionInfo.Types.Spaceship.Galeggtica => "Galeggtica",
    MissionInfo.Types.Spaceship.Chickfiant => "Chickfiant",
    MissionInfo.Types.Spaceship.Voyegger => "Voyegger",
    MissionInfo.Types.Spaceship.Henerprise => "Henerprise",
    MissionInfo.Types.Spaceship.Atreggies => "Atreggies",
    _ => HumanizeEnum(ship)
};

static string HumanizeEnum<T>(T value) where T : struct, Enum =>
    Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1 $2");

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

    var currentContractFarms = GetCurrentContractFarms(backup);
    var currentFarmIndexes = currentContractFarms.Select(f => f.Index).ToHashSet();
    var candidates = BuildArtifactCandidates(backup.ArtifactsDb.InventoryItems);
    var availableCandidates = BuildAvailableArtifactCandidates(backup, candidates, currentFarmIndexes);
    if(candidates.Count == 0 || availableCandidates.Count == 0) {
        builder.WithDescription("Plotty found artifact inventory, but no contract-focused artifacts it recognizes yet.");
        return builder.Build();
    }

    var activeArtifacts = GetActiveContractArtifacts(backup, candidates, currentFarmIndexes)
        .ToList();
    var stoneOptions = BuildStoneOptions(backup.ArtifactsDb.InventoryItems);
    var suggestions = BuildArtifactRecommendations(availableCandidates, stoneOptions, activeArtifacts)
        .ToList();
    var bestSuggestion = suggestions
        .OrderByDescending(s => s.Score)
        .ThenBy(s => s.Label.Contains("changing stones", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        .FirstOrDefault();

    var contractLines = currentContractFarms.Count == 0
        ? ["No current accepted contract farm found in this backup."]
        : currentContractFarms
            .Select(x => $"`{x.Farm.ContractId}` - {EggDisplayName(x.Farm.EggType)}")
            .Distinct()
            .ToList();

    var equippedLines = activeArtifacts.Count == 0
        ? ["No equipped contract artifacts were visible in this backup."]
        : activeArtifacts
            .GroupBy(a => a.Name)
            .Select(g => $"{ArtifactPurposeIcon(g.First().Purpose)} **{g.First().DisplayName}** x{g.Count()} - {FormatArtifactMultiplier(g.First())}")
            .Take(8)
            .ToList();

    builder.WithDescription("Plotty evaluated your current contract inventory using EGG9000 artifact data, including stone slots, image names, and effect values.");
    builder.AddField("Current Contract Farm", string.Join("\n", contractLines));
    builder.AddField("Currently Equipped", string.Join("\n", equippedLines));

    if(bestSuggestion is null || bestSuggestion.Set.Count == 0) {
        builder.AddField("Suggested Set", "No complete contract-focused artifact set could be built. Look for Tachyon Deflector, Quantum Metronome, Interstellar Compass, and Tachyon/Quantum stones.");
    } else {
        builder.AddField($"Best Set - {bestSuggestion.Label}", FormatArtifactSet(bestSuggestion.Set));

        var alternatives = suggestions
            .Where(s => !ReferenceEquals(s, bestSuggestion) && s.Set.Count > 0)
            .OrderByDescending(s => s.Score)
            .Take(3)
            .Select(s => $"**{s.Label}** - {FormatArtifactSetOneLine(s.Set)}")
            .ToList();
        if(alternatives.Count > 0) {
            builder.AddField("Other EGG9000-Style Checks", string.Join("\n", alternatives));
        }

        var imageLinks = bestSuggestion.Set
            .Where(a => !string.IsNullOrWhiteSpace(a.ImageUrl))
            .Select(a => $"[{a.DisplayName}]({a.ImageUrl})")
            .ToList();
        if(imageLinks.Count > 0) {
            builder.AddField("Artifact Images", string.Join(" | ", imageLinks));
            builder.WithThumbnailUrl(bestSuggestion.Set.First(a => !string.IsNullOrWhiteSpace(a.ImageUrl)).ImageUrl);
        }
    }

    builder.WithFooter($"Data: EGG9000 eiafx-data.json. Recognized {availableCandidates.Select(c => c.Name).Distinct().Count()} artifact families and {stoneOptions.Count} loose laying/shipping stones.");
    return builder.Build();
}

static IReadOnlyList<ArtifactCandidate> BuildArtifactCandidates(IEnumerable<ArtifactInventoryItem> items) =>
    items
        .Where(i => i.Artifact?.Spec is not null)
        .Where(i => !IsStoneSpec(i.Artifact.Spec))
        .Select(i => CreateArtifactCandidate(i))
        .Where(c => c is not null)
        .Cast<ArtifactCandidate>()
        .ToList();

static IReadOnlyList<StoneOption> BuildStoneOptions(IEnumerable<ArtifactInventoryItem> items) =>
    items
        .Where(i => i.Quantity > 0 && i.Artifact?.Spec is not null && IsUsefulContractStone(i.Artifact.Spec))
        .Select(i => new StoneOption(i.Artifact.Spec, ArtifactDisplayName(i.Artifact.Spec), ArtifactEffectDelta(i.Artifact.Spec), ArtifactImageUrl(i.Artifact.Spec)))
        .Where(s => s.Delta > 0)
        .OrderByDescending(s => s.Delta)
        .Take(12)
        .ToList();

static ArtifactCandidate? CreateArtifactCandidate(ArtifactInventoryItem item) {
    var artifact = item.Artifact;
    var spec = artifact?.Spec;
    if(artifact is null || spec is null) {
        return null;
    }

    var name = spec.Name;
    var displayName = ArtifactDisplayName(spec);
    var slotCount = ArtifactSlotCount(spec);
    
    var layingMultiplier = ArtifactEffectMultiplier(spec, artifact.Stones, [
        ArtifactSpec.Types.Name.QuantumMetronome,
        ArtifactSpec.Types.Name.TachyonStone
    ]);
    var shippingMultiplier = ArtifactEffectMultiplier(spec, artifact.Stones, [
        ArtifactSpec.Types.Name.InterstellarCompass,
        ArtifactSpec.Types.Name.QuantumStone
    ]);
    var teamLayingMultiplier = ArtifactEffectMultiplier(spec, artifact.Stones, [ArtifactSpec.Types.Name.TachyonDeflector]);
    var deflectorBonus = name == ArtifactSpec.Types.Name.TachyonDeflector
        ? Math.Max(0, teamLayingMultiplier - 1)
        : 0;

    return name switch {
        ArtifactSpec.Types.Name.TachyonDeflector => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.TeamLaying,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "helps the co-op by raising teammate egg laying", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        ArtifactSpec.Types.Name.QuantumMetronome => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Laying,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "raises your egg laying rate", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        ArtifactSpec.Types.Name.InterstellarCompass => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Shipping,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "raises shipping so laid eggs can actually leave the farm", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        ArtifactSpec.Types.Name.OrnateGusset => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Capacity,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "keeps hab space from choking production", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        ArtifactSpec.Types.Name.ShipInABottle => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.TeamEarnings,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "supports co-op earnings after core rate artifacts", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        ArtifactSpec.Types.Name.DilithiumMonocle => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.Boosting,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "helps boost sessions, but is secondary after rate artifacts", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        ArtifactSpec.Types.Name.TheChalice => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.InternalHatchery,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "helps contract population growth, especially with life stones", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        _ when slotCount > 0 || artifact.Stones.Count > 0 => new ArtifactCandidate(
            item, artifact, name, displayName, ArtifactPurpose.StoneCarrier,
            layingMultiplier, shippingMultiplier, teamLayingMultiplier, deflectorBonus,
            "useful as a stone carrier if your best rate artifacts are already equipped", ArtifactImageUrl(spec), artifact.Stones.ToList(), slotCount, false),
            
        _ => null
    };
}

static IReadOnlyList<ContractFarmSnapshot> GetCurrentContractFarms(Backup backup) {
    var acceptedCurrentContracts = backup.Contracts?.Contracts
        .Where(c => c.Accepted && !c.Cancelled)
        .Select(c => new PlayerContractCandidate(GetLocalContractId(c), c.CoopIdentifier, c.TimeAccepted))
        .Where(c => !string.IsNullOrWhiteSpace(c.ContractId))
        .GroupBy(c => c.ContractId, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(c => c.AcceptedAt).First())
        .OrderByDescending(c => c.AcceptedAt)
        .ToList() ?? [];

    if(acceptedCurrentContracts.Count == 0) {
        return [];
    }

    var currentContractId = acceptedCurrentContracts[0].ContractId;
    return backup.Farms
        .Select((farm, index) => new ContractFarmSnapshot(index, farm))
        .Where(x => string.Equals(x.Farm.ContractId, currentContractId, StringComparison.OrdinalIgnoreCase))
        .ToList();
}

static IEnumerable<ArtifactCandidate> GetActiveContractArtifacts(
    Backup backup,
    IReadOnlyList<ArtifactCandidate> candidates,
    IReadOnlySet<int> farmIndexes) {
    if(backup.ArtifactsDb is null) {
        yield break;
    }

    var candidatesByItemId = candidates.ToDictionary(c => c.Item.ItemId);
    for(var i = 0; i < backup.Farms.Count; i++) {
        if(!farmIndexes.Contains(i) ||
           string.IsNullOrWhiteSpace(backup.Farms[i].ContractId) ||
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

static IReadOnlyList<ArtifactCandidate> BuildAvailableArtifactCandidates(
    Backup backup,
    IReadOnlyList<ArtifactCandidate> candidates,
    IReadOnlySet<int> currentFarmIndexes) {
    if(backup.ArtifactsDb is null) {
        return [];
    }

    var occupiedElsewhere = new HashSet<ulong>();
    for(var i = 0; i < backup.Farms.Count; i++) {
        if(currentFarmIndexes.Contains(i) ||
           string.IsNullOrWhiteSpace(backup.Farms[i].ContractId) ||
           backup.ArtifactsDb.ActiveArtifactSets.Count <= i) {
            continue;
        }

        foreach(var slot in backup.ArtifactsDb.ActiveArtifactSets[i].Slots.Where(s => s.Occupied)) {
            occupiedElsewhere.Add(slot.ItemId);
        }
    }

    return candidates
        .Where(c => c.Item.Quantity > 0 && !occupiedElsewhere.Contains(c.Item.ItemId))
        .ToList();
}

static IEnumerable<ArtifactSetSuggestion> BuildArtifactRecommendations(
    IReadOnlyList<ArtifactCandidate> candidates,
    IReadOnlyList<StoneOption> stones,
    IReadOnlyList<ArtifactCandidate> currentSet) {
    var fixedStonePool = BuildRecommendationPool(candidates);
    var changedStonePool = BuildRecommendationPool(ExpandWithStoneOptions(candidates, stones));

    var noDeflector = BuildBestArtifactSet(fixedStonePool, requireDeflector: false, currentSet);
    if(noDeflector.Count > 0) {
        yield return new ArtifactSetSuggestion("No Deflector", noDeflector, ArtifactSetScore(noDeflector));
    }

    var withDeflector = BuildBestArtifactSet(fixedStonePool, requireDeflector: true, currentSet);
    if(withDeflector.Count > 0) {
        yield return new ArtifactSetSuggestion("With Deflector", withDeflector, ArtifactSetScore(withDeflector));
    }

    if(stones.Count == 0) {
        yield break;
    }

    var noDeflectorChanged = BuildBestArtifactSet(changedStonePool, requireDeflector: false, currentSet);
    if(noDeflectorChanged.Count > 0) {
        yield return new ArtifactSetSuggestion("No Deflector, changing stones", noDeflectorChanged, ArtifactSetScore(noDeflectorChanged));
    }

    var withDeflectorChanged = BuildBestArtifactSet(changedStonePool, requireDeflector: true, currentSet);
    if(withDeflectorChanged.Count > 0) {
        yield return new ArtifactSetSuggestion("With Deflector, changing stones", withDeflectorChanged, ArtifactSetScore(withDeflectorChanged));
    }
}

static IReadOnlyList<ArtifactCandidate> BuildRecommendationPool(IEnumerable<ArtifactCandidate> candidates) =>
    candidates
        .GroupBy(c => c.Name)
        .OrderBy(group => ArtifactPurposePriority(group.First().Purpose))
        .ThenByDescending(group => group.Max(ArtifactCandidateStrength))
        .SelectMany(group => group
            .OrderByDescending(ArtifactCandidateStrength)
            .ThenByDescending(ArtifactQualityScore)
            .Take(3))
        .OrderBy(c => ArtifactPurposePriority(c.Purpose))
        .ThenByDescending(c => ArtifactCandidateStrength(c))
        .ToList();

static IReadOnlyList<ArtifactCandidate> BuildBestArtifactSet(
    IReadOnlyList<ArtifactCandidate> pool,
    bool requireDeflector,
    IReadOnlyList<ArtifactCandidate> currentSet) {
    if(pool.Count == 0) {
        return [];
    }

    var keepArtifacts = currentSet
        .Where(c => c.Purpose == ArtifactPurpose.Capacity)
        .GroupBy(c => c.Name)
        .Select(g => g.OrderByDescending(ArtifactQualityScore).First())
        .Take(1)
        .ToList();
    var searchPool = pool
        .Where(c => !keepArtifacts.Any(k => k.Name == c.Name))
        .ToList();
    var targetSetSize = Math.Min(4, keepArtifacts.Count + searchPool.Select(c => c.Name).Distinct().Count());
    if(targetSetSize == 0) {
        return [];
    }

    var sets = new List<List<ArtifactCandidate>>();
    BuildSets(0, keepArtifacts.ToList());

    return sets
        .Where(s => requireDeflector
            ? s.Any(a => a.Purpose == ArtifactPurpose.TeamLaying)
            : s.All(a => a.Purpose != ArtifactPurpose.TeamLaying))
        .OrderByDescending(s => {
            var score = ScoreLayingSet(s);
            return Math.Min(score.Laying, score.Shipping) * (1 + score.Deflector);
        })
        .ThenByDescending(s => ScoreLayingSet(s).Laying * ScoreLayingSet(s).Shipping)
        .ThenByDescending(s => ScoreLayingSet(s).Deflector)
        .ThenByDescending(s => s.Count(a => a.Purpose is ArtifactPurpose.TeamLaying or ArtifactPurpose.Laying or ArtifactPurpose.Shipping))
        .ThenByDescending(s => s.Sum(ArtifactCandidateStrength))
        .ThenByDescending(s => s.Sum(ArtifactQualityScore))
        .FirstOrDefault() ?? [];

    void BuildSets(int start, List<ArtifactCandidate> current) {
        if(current.Count == targetSetSize) {
            sets.Add(current.ToList());
            return;
        }

        if(current.Count + (pool.Count - start) < targetSetSize) {
            return;
        }

        for(var i = start; i < searchPool.Count; i++) {
            if(current.Any(c => c.Name == searchPool[i].Name)) {
                continue;
            }

            current.Add(searchPool[i]);
            BuildSets(i + 1, current);
            current.RemoveAt(current.Count - 1);
        }
    }
}

static IEnumerable<ArtifactCandidate> ExpandWithStoneOptions(
    IReadOnlyList<ArtifactCandidate> candidates,
    IReadOnlyList<StoneOption> stones) {
    foreach(var candidate in candidates) {
        yield return candidate;
        if(candidate.SlotCount <= 0 || stones.Count == 0) {
            continue;
        }

        foreach(var stoneSet in BuildUsefulStoneSets(candidate, stones).Take(8)) {
            yield return candidate with {
                LayingMultiplier = ArtifactEffectMultiplier(candidate.Artifact.Spec, stoneSet, [
                    ArtifactSpec.Types.Name.QuantumMetronome,
                    ArtifactSpec.Types.Name.TachyonStone
                ]),
                ShippingMultiplier = ArtifactEffectMultiplier(candidate.Artifact.Spec, stoneSet, [
                    ArtifactSpec.Types.Name.InterstellarCompass,
                    ArtifactSpec.Types.Name.QuantumStone
                ]),
                TeamLayingMultiplier = ArtifactEffectMultiplier(candidate.Artifact.Spec, stoneSet, [ArtifactSpec.Types.Name.TachyonDeflector]),
                DeflectorBonus = candidate.Name == ArtifactSpec.Types.Name.TachyonDeflector
                    ? Math.Max(0, ArtifactEffectMultiplier(candidate.Artifact.Spec, stoneSet, [ArtifactSpec.Types.Name.TachyonDeflector]) - 1)
                    : 0,
                Stones = stoneSet,
                StonesChanged = true
            };
        }
    }
}

static IEnumerable<IReadOnlyList<ArtifactSpec>> BuildUsefulStoneSets(ArtifactCandidate candidate, IReadOnlyList<StoneOption> stones) {
    var slots = candidate.SlotCount;
    var layingStones = stones
        .Where(s => s.Spec.Name == ArtifactSpec.Types.Name.TachyonStone)
        .OrderByDescending(s => s.Delta)
        .Take(slots)
        .ToList();
    var shippingStones = stones
        .Where(s => s.Spec.Name == ArtifactSpec.Types.Name.QuantumStone)
        .OrderByDescending(s => s.Delta)
        .Take(slots)
        .ToList();

    if(candidate.Purpose is ArtifactPurpose.Laying or ArtifactPurpose.TeamLaying or ArtifactPurpose.StoneCarrier) {
        foreach(var set in BuildStoneFill(layingStones, shippingStones, slots)) {
            yield return set;
        }
    }

    if(candidate.Purpose is ArtifactPurpose.Shipping or ArtifactPurpose.Capacity or ArtifactPurpose.StoneCarrier) {
        foreach(var set in BuildStoneFill(shippingStones, layingStones, slots)) {
            yield return set;
        }
    }
}

static IEnumerable<IReadOnlyList<ArtifactSpec>> BuildStoneFill(
    IReadOnlyList<StoneOption> primary,
    IReadOnlyList<StoneOption> secondary,
    int slots) {
    if(slots <= 0) {
        yield break;
    }

    var primarySet = primary.Take(slots).Select(s => s.Spec).ToList();
    if(primarySet.Count == slots) {
        yield return primarySet;
    }

    var secondarySet = secondary.Take(slots).Select(s => s.Spec).ToList();
    if(secondarySet.Count == slots) {
        yield return secondarySet;
    }

    if(slots > 1 && primary.Count > 0 && secondary.Count > 0) {
        var mixed = primary.Take(slots - 1).Select(s => s.Spec).Concat(secondary.Take(1).Select(s => s.Spec)).ToList();
        if(mixed.Count == slots) {
            yield return mixed;
        }
    }
}

static (double Laying, double Shipping, double Deflector) ScoreLayingSet(IReadOnlyList<ArtifactCandidate> set) =>
    (
        set.Aggregate(1d, (total, current) => total * current.LayingMultiplier),
        set.Aggregate(1d, (total, current) => total * current.ShippingMultiplier),
        set.Sum(current => current.DeflectorBonus)
    );

static double ArtifactSetScore(IReadOnlyList<ArtifactCandidate> set) {
    var score = ScoreLayingSet(set);
    return Math.Min(score.Laying, score.Shipping) * (1 + score.Deflector);
}

static int ArtifactPurposePriority(ArtifactPurpose purpose) =>
    purpose switch {
        ArtifactPurpose.TeamLaying => 0,
        ArtifactPurpose.Laying => 1,
        ArtifactPurpose.Shipping => 2,
        ArtifactPurpose.StoneCarrier => 3,
        ArtifactPurpose.Capacity => 4,
        ArtifactPurpose.InternalHatchery => 5,
        ArtifactPurpose.TeamEarnings => 6,
        ArtifactPurpose.Boosting => 7,
        _ => 8
    };

static double ArtifactCandidateStrength(ArtifactCandidate candidate) =>
    Math.Min(candidate.LayingMultiplier, candidate.ShippingMultiplier) *
    Math.Max(candidate.LayingMultiplier, candidate.ShippingMultiplier) *
    (1 + candidate.DeflectorBonus);

static double ArtifactQualityScore(ArtifactCandidate candidate) =>
    ((int)candidate.Artifact.Spec.Level * 10) +
    ((int)candidate.Artifact.Spec.Rarity * 2) +
    candidate.Stones.Count;

static double ArtifactEffectMultiplier(ArtifactSpec spec, IReadOnlyCollection<ArtifactSpec> stones, IReadOnlyCollection<ArtifactSpec.Types.Name> relevantNames) {
    var multiplier = relevantNames.Contains(spec.Name)
        ? 1 + ArtifactEffectDelta(spec)
        : 1;
    foreach(var stone in stones.Where(s => relevantNames.Contains(s.Name))) {
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
    Egg9000ArtifactData.EffectDelta(spec);

static int ArtifactSlotCount(ArtifactSpec spec) =>
    Egg9000ArtifactData.SlotCount(spec);

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
    var name = Egg9000ArtifactData.ProperName(spec) ?? ArtifactName(spec.Name);
    return $"{tier}{rarity} {name}";
}

static string ArtifactName(ArtifactSpec.Types.Name name) => 
    Regex.Replace(name.ToString(), "([a-z])([A-Z])", "$1 $2");

static string FormatArtifactStoneList(IEnumerable<ArtifactSpec> stones) {
    var names = stones
        .Where(s => IsStoneSpec(s))
        .Select(ArtifactDisplayName)
        .ToList();
    return names.Count == 0 ? "" : $" ({string.Join(", ", names)})";
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
    var file = Egg9000ArtifactData.IconFilename(spec);
    return file is null ? null : $"https://eggincassets.pages.dev/64/egginc/{file}";
}

static bool IsUsefulContractStone(ArtifactSpec spec) =>
    spec.Name is ArtifactSpec.Types.Name.TachyonStone or ArtifactSpec.Types.Name.QuantumStone;

static bool IsStoneSpec(ArtifactSpec spec) =>
    spec.Name is ArtifactSpec.Types.Name.TachyonStone
        or ArtifactSpec.Types.Name.DilithiumStone
        or ArtifactSpec.Types.Name.ShellStone
        or ArtifactSpec.Types.Name.LunarStone
        or ArtifactSpec.Types.Name.SoulStone
        or ArtifactSpec.Types.Name.ProphecyStone
        or ArtifactSpec.Types.Name.QuantumStone
        or ArtifactSpec.Types.Name.TerraStone
        or ArtifactSpec.Types.Name.LifeStone
        or ArtifactSpec.Types.Name.ClarityStone;

static string FormatArtifactSet(IReadOnlyList<ArtifactCandidate> set) =>
    string.Join("\n", set.Select((a, i) =>
        $"{i + 1}. {ArtifactPurposeIcon(a.Purpose)} **{a.DisplayName}**{FormatArtifactStoneList(a.Stones)} - {FormatArtifactMultiplier(a)}{(a.StonesChanged ? "; change stones" : $"; {a.Reason}")}"));

static string FormatArtifactSetOneLine(IReadOnlyList<ArtifactCandidate> set) {
    var score = ScoreLayingSet(set);
    return $"{Math.Min(score.Laying, score.Shipping):0.###}x bottleneck, {score.Deflector:P0} deflector | " +
           string.Join(", ", set.Select(a => a.DisplayName + (a.StonesChanged ? "*" : "")));
}

static string GetString(SocketSlashCommand command, string name) =>
    (string)command.Data.Options.First(o => o.Name == name).Value;

static bool GetBool(SocketSlashCommand command, string name) =>
    command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value is bool value && value;

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

async Task<string> BuildPlottyConversationResponseAsync(
    ulong guildId,
    ulong userId,
    string mention,
    string prompt,
    bool isQuestion,
    PlottyMemory memory) {
    var cleaned = Regex.Replace(prompt, @"\s+", " ").Trim();
    var normalized = cleaned.ToLowerInvariant();
    var previous = await dataStore.GetPlottyConversationAsync(guildId, userId);

    if(string.IsNullOrWhiteSpace(cleaned)) {
        await dataStore.RecordPlottyConversationAsync(guildId, userId, "summon");
        return PlottyPersonality.MentionResponse(mention, isQuestion: false, memory);
    }

    if(IsGreeting(normalized)) {
        await dataStore.RecordPlottyConversationAsync(guildId, userId, "greeting");
        return $"{mention} {PlottyPersonality.ConversationGreeting(memory)}";
    }

    if(IsThanks(normalized)) {
        await dataStore.RecordPlottyConversationAsync(guildId, userId, "thanks");
        return $"{mention} {PlottyPersonality.ConversationThanks(memory)}";
    }

    var plottyAnswer = TryAnswerPlottyQuestion(normalized);
    if(plottyAnswer is not null) {
        var answer = plottyAnswer.Value;
        await dataStore.RecordPlottyConversationAsync(guildId, userId, answer.Topic);
        return $"{mention} {PlottyPersonality.ConversationLeadIn(memory)}{answer.Answer}";
    }

    if(NeedsPreviousTopic(normalized) && previous is not null && !string.IsNullOrWhiteSpace(previous.LastTopic)) {
        await dataStore.RecordPlottyConversationAsync(guildId, userId, previous.LastTopic);
        return $"{mention} {PlottyPersonality.ConversationLeadIn(memory)}If you mean **{previous.LastTopic}**, I still have that context. Ask the next part with one specific detail and I will keep following it.";
    }

    if(isQuestion && LooksLikeEggIncQuestion(normalized)) {
        var answer = await wikiClient.AnswerAsync(cleaned);
        if(answer is not null) {
            await dataStore.RecordPlottyConversationAsync(guildId, userId, answer.Title);
            return $"{mention} {PlottyPersonality.ConversationLeadIn(memory)}**{answer.Title}:** {TrimDiscordMessage(answer.Answer, 1300)}\nSource: {answer.Url}";
        }
    }

    var inferredTopic = InferConversationTopic(normalized);
    await dataStore.RecordPlottyConversationAsync(guildId, userId, inferredTopic);
    return isQuestion
        ? $"{mention} {PlottyPersonality.ConversationUnknownQuestion(memory, previous?.LastTopic)}"
        : $"{mention} {PlottyPersonality.ConversationSmallTalk(memory, inferredTopic)}";
}

static string StripBotMention(string content) =>
    Regex.Replace(content, @"<@!?\d+>", "", RegexOptions.Compiled).Trim();

static bool IsGreeting(string normalized) =>
    Regex.IsMatch(normalized, @"\b(hi|hello|hey|yo|sup|good morning|good evening|good afternoon)\b", RegexOptions.IgnoreCase);

static bool IsThanks(string normalized) =>
    Regex.IsMatch(normalized, @"\b(thanks|thank you|ty|appreciate it|good bot)\b", RegexOptions.IgnoreCase);

static bool NeedsPreviousTopic(string normalized) =>
    Regex.IsMatch(normalized, @"\b(that|it|those|them|this|previous|same thing)\b", RegexOptions.IgnoreCase) &&
    normalized.Length < 90;

static bool LooksLikeEggIncQuestion(string normalized) {
    string[] terms = [
        "egg", "eggs", "contract", "coop", "co-op", "prestige", "artifact", "artifacts",
        "boost", "boosts", "soul", "prophecy", "farmer", "enlightenment", "hab", "shipping",
        "tachyon", "deflector", "metronome", "gusset", "stones", "eb", "earnings bonus"
    ];
    return terms.Any(t => normalized.Contains(t, StringComparison.OrdinalIgnoreCase));
}

static (string Topic, string Answer)? TryAnswerPlottyQuestion(string normalized) {
    if(normalized.Contains("who are you") || normalized.Contains("what are you") || normalized.Contains("plotty")) {
        return ("Plotty", "I am the guild's local Egg Inc assistant: part contract clerk, part pub regular, part spreadsheet with social ambitions.");
    }

    if(normalized.Contains("register") || normalized.Contains("eid")) {
        return ("EID registration", "Use `/register-eid` to privately register one or more Egg Inc IDs. I store encrypted EIDs locally and only post a name-only welcome in `plotty-gossip`.");
    }

    if(normalized.Contains("rates")) {
        return ("rates", "`/rates` privately shows your running contracts and last 2 completed contracts. Staff can use `/admin-rates-all` for the registered-player overview.");
    }

    if(normalized.Contains("player")) {
        return ("player profile", "`/player` shows a registered player's recent contribution profile, registration date, and a refresh button.");
    }

    if(normalized.Contains("egg") && normalized.Contains("laid")) {
        return ("eggs laid", "`/eggs-laid` privately shows lifetime eggs laid by farm, including virtue eggs in their own section.");
    }

    if(normalized.Contains("artifact")) {
        return ("contract artifacts", "`/contract-artifacts` looks at your current contract and artifact inventory, then suggests a contract-focused set with artifact image links.");
    }

    if(normalized.Contains("demerit")) {
        return ("demerits", "Members can use `/demerits-view`. Staff can use `/add-demerit`, `/remove-demerit`, and `/admin-demerits-view-all`. Active demerits expire after 30 days.");
    }

    if(normalized.Contains("beer")) {
        return ("beer", "`/beer-plotty` buys me a beer, `/beer-user` gifts another member a beer, and `/beerleader` shows the pub legends.");
    }

    if(normalized.Contains("dashboard")) {
        return ("admin dashboard", "`/admin-dashboard` gives Staff an overview of registered players, low rates, sync issues, and likely unboosted players.");
    }

    if(normalized.Contains("help") || normalized.Contains("commands")) {
        return ("commands", "I know `/register-eid`, `/rates`, `/player`, `/eggs-laid`, `/contract-artifacts`, `/help`, pub commands, and Staff tools. Ask about one and I will unpack it.");
    }

    return null;
}

static string InferConversationTopic(string normalized) {
    if(LooksLikeEggIncQuestion(normalized)) {
        return "Egg Inc";
    }

    if(normalized.Contains("beer")) {
        return "beer";
    }

    if(normalized.Contains("sync")) {
        return "sync";
    }

    if(normalized.Contains("staff")) {
        return "Staff";
    }

    return "chat";
}

static string TrimDiscordMessage(string value, int maxLength) =>
    value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

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
public sealed record ContractFarmSnapshot(int Index, Backup.Types.Simulation Farm);
public sealed record PlayerContractRate(string ContractId, double RatePerHour, double ContributionAmount);
public sealed record ArtifactCandidate(
    ArtifactInventoryItem Item,
    CompleteArtifact Artifact,
    ArtifactSpec.Types.Name Name,
    string DisplayName,
    ArtifactPurpose Purpose,
    double LayingMultiplier,
    double ShippingMultiplier,
    double TeamLayingMultiplier,
    double DeflectorBonus,
    string Reason,
    string? ImageUrl,
    IReadOnlyList<ArtifactSpec> Stones,
    int SlotCount,
    bool StonesChanged);
public sealed record StoneOption(ArtifactSpec Spec, string DisplayName, double Delta, string? ImageUrl);
public sealed record ArtifactSetSuggestion(string Label, IReadOnlyList<ArtifactCandidate> Set, double Score);
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
public sealed record ContractLateNotice(
    ulong GuildId,
    ulong DiscordUserId,
    string? ContractId,
    string? Eta,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
public sealed record ShipMissionSnapshot(
    MissionInfo Mission,
    string Key,
    string ShipName,
    string StatusName,
    string DurationTypeName,
    string MissionTypeName,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ReturnAt);
public sealed record ShipReturnNotification(
    ulong GuildId,
    ulong DiscordUserId,
    string EidHash,
    string MissionKey,
    string ShipName,
    DateTimeOffset ReturnAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NotifiedAt) {
    public string Key => $"{GuildId}:{DiscordUserId}:{EidHash}:{MissionKey}";
}
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
public sealed record PlottyMemory(
    ulong GuildId,
    ulong DiscordUserId,
    int TotalInteractions,
    int Mentions,
    int QuestionsDodged,
    int SarcasmDetected,
    int FoxMoments,
    int Beers,
    int Registrations,
    int MoodChecks,
    int Excuses,
    int WisdomRequests,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
public sealed record PlottyConversationState(
    ulong GuildId,
    ulong DiscordUserId,
    string LastTopic,
    int Turns,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
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

public static class PlottyPersonality {
    private static readonly string[] MentionReplies = [
        "Plotty has been summoned and is pretending to look busy.",
        "Plotty is here, carrying one clipboard and absolutely no certainty.",
        "Plotty heard its name and arrived with suspicious confidence.",
        "Plotty acknowledges the ping and offers one respectful nod.",
        "Plotty is present, lightly caffeinated, and monitoring the egg economy.",
        "Plotty has entered the chat with premium-grade confusion.",
        "Plotty is listening. Plotty is also thinking about snacks.",
        "Plotty reports for duty, probably.",
        "Plotty appears with a clipboard and no promise that the clipboard helps.",
        "Plotty has arrived wearing the emotional equivalent of a tiny vest.",
        "Plotty heard the ping and immediately looked important.",
        "Plotty is here, and the ledger is pretending this was scheduled.",
        "Plotty accepts the summons with medium confidence and high posture.",
        "Plotty has stepped out from behind the spreadsheet curtain.",
        "Plotty is awake, mostly, which counts under guild policy.",
        "Plotty heard its name and brought backup stationery.",
        "Plotty has entered with the calm of a bot that did not read the room.",
        "Plotty is present and willing to blame latency if needed.",
        "Plotty has been perceived. The paperwork begins.",
        "Plotty is here, carrying vibes, charts, and one questionable assumption.",
        "Plotty responds from the administrative nest.",
        "Plotty has joined the conversation and is standing near the snacks.",
        "Plotty acknowledges this ping with theatrical seriousness.",
        "Plotty arrives at a brisk ledger-approved pace.",
        "Plotty is listening with both pixels.",
        "Plotty heard the call and dusted off the response lever.",
        "Plotty is here to help, hover, or make it weirder.",
        "Plotty has surfaced from the data pond with a tiny nod."
    ];

    private static readonly string[] QuestionDiversions = [
        "I can answer if I have enough context. Give me the contract, command, player detail, or Egg Inc term you mean.",
        "I want to answer that directly. Add one more detail so I do not guess wrong.",
        "I am listening. If this is about Egg Inc, name the mechanic or contract and I will use what I know.",
        "I can work with that, but I need a sharper target before I give an answer.",
        "I do not want to dodge it. Give me the missing piece and I will take a real swing.",
        "I need a little more context to answer cleanly. What part should I focus on?",
        "I can help with that if you point me at the command, player, contract, or artifact.",
        "I am not sure which part you mean yet. Ask it one level more specifically and I will answer."
    ];

    private static readonly (string Emoji, string Story)[] Moods = [
        ("\U0001F305\U0001F95A\U0001F4C8\u2615\U0001F914\U0001F414\U0001F483\U0001F37A\U0001F319",
            "Plotty woke up optimistic, checked the numbers, overthought one chicken, danced anyway, accepted a beer, and called it a productive day."),
        ("\u23F0\U0001F95A\U0001F4BB\U0001F525\U0001F9EF\U0001F60C\U0001F4CA\U0001F3C6",
            "Plotty started early, found an egg-shaped emergency in the logs, calmly put out the fire, and somehow ended with a victory chart."),
        ("\U0001F414\U0001F6AA\U0001F95A\U0001F95A\U0001F95A\U0001F633\U0001F4C9\U0001F37A\U0001F64F",
            "A chicken opened the wrong door, three eggs fell out, the rates dipped, and Plotty requested one respectful recovery beverage."),
        ("\U0001F327\uFE0F\U0001F95A\U0001F6E0\uFE0F\u2699\uFE0F\u2728\U0001F4C8\U0001F60E\U0001F324\uFE0F",
            "The morning was stormy, but Plotty fixed the egg machinery, polished the gears, watched the line go up, and put on sunglasses indoors."),
        ("\U0001F4E6\U0001F95A\U0001F4E6\U0001F95A\U0001F4E6\U0001F605\u2705",
            "Plotty sorted boxes, found eggs in every box, sweated a little, and still marked the task complete."),
        ("\U0001F95A\U0001F4DD\U0001F914\U0001F37A\U0001F4C8\U0001F389",
            "Plotty wrote one serious note, questioned the note, accepted a beer, watched the graph rise, and celebrated like this was planned."),
        ("\U0001F414\U0001F4E3\U0001F4CA\U0001F62C\U0001F6E0\U0001F31F",
            "A chicken announced a chart problem, Plotty panicked politely, fixed a tiny gear, and pretended the sparkle was intentional."),
        ("\U0001F319\U0001F95A\U0001F50D\U0001F4DA\U0001F634",
            "Plotty stayed up late, inspected one mysterious egg, read too much documentation, and fell asleep on the findings."),
        ("\u2615\U0001F95A\U0001F680\U0001F4C8\U0001F973",
            "Plotty drank coffee, launched an egg-shaped plan, watched the numbers behave, and got too excited about it."),
        ("\U0001F9EE\U0001F414\U0001F37A\U0001F4CB\u2705",
            "Plotty did math with a chicken consultant, paid the consultant in imaginary beer, and checked the box.")
    ];

    private static readonly string[] Excuses = [
        "Plotty cannot reply right now because a chicken is sitting on the enter key.",
        "Plotty is busy explaining compound interest to an egg that refuses to hatch.",
        "Plotty has been summoned to a very important coop meeting about snacks.",
        "Plotty's response is incubating. Please allow three to five business clucks.",
        "Plotty tried to answer, but the egg rolled under the server rack.",
        "Plotty is currently negotiating with a hen who demands better token timing.",
        "Plotty's thoughts are scrambled, lightly salted, and served with toast.",
        "Plotty is waiting for the game servers to sync, which may outlive us all.",
        "Plotty cannot reply until the coop morale committee approves the vibes.",
        "Plotty found an eggcellent answer, then immediately misplaced it in the nest.",
        "Plotty is alphabetizing yolks for regulatory reasons.",
        "Plotty is stuck in a meeting titled `Why Is The Button Like That`.",
        "Plotty dropped the reply into a silo and now needs a ladder.",
        "Plotty is recalibrating its dramatic pause module.",
        "Plotty is currently asking a spreadsheet to be brave.",
        "Plotty has to reboot one tiny opinion before continuing.",
        "Plotty is polishing the coop ledger until it reflects better choices.",
        "Plotty cannot reply because the answer is wearing a fake mustache.",
        "Plotty is chasing a decimal point that escaped containment.",
        "Plotty is taking inventory of imaginary clipboards.",
        "Plotty was ready, but the response got distracted by a progress bar.",
        "Plotty is negotiating with a graph that refuses to go up.",
        "Plotty has entered silent mode, which is odd because it is still talking.",
        "Plotty is checking whether the reply has enough structural integrity.",
        "Plotty cannot reply until this egg finishes its character arc."
    ];

    private static readonly string[] WisdomLines = [
        "The egg does not rush the dawn, and somehow breakfast still happens.",
        "A full coop is useful, but a synced coop is sacred.",
        "Do not measure the day only by eggs laid. Measure it by the chaos you survived with style.",
        "The chicken that crosses the road still has to sync when it gets there.",
        "Greatness is often just consistency wearing a little hat.",
        "One good prestige can forgive many confused mornings.",
        "Be kind to your future self; they inherit every choice you are too tired to label.",
        "If the numbers look bad, first check the sync. If the sync looks bad, check your patience.",
        "The leaderboard remembers the rate, but the guild remembers who showed up.",
        "The best time to sync was earlier. The second best time is before Staff notices.",
        "A good plan is just panic with a calendar.",
        "The farm grows when the small boring things keep happening.",
        "Every great chart began as one number refusing to stay lonely.",
        "Do not fear the dip; fear the unexamined dip.",
        "A calm player checks sync before inventing a conspiracy.",
        "Some doors open because you pushed. Some open because you finally updated.",
        "A full hab is not a personality, but it does help.",
        "The patient farmer gets data. The impatient farmer gets screenshots.",
        "If morale is low, add clarity, not noise.",
        "A rate is a promise the server has agreed to remember.",
        "The smallest useful habit beats the grandest abandoned plan.",
        "Do not let a bad graph narrate your entire day.",
        "A contract is temporary, but the screenshot can become folklore.",
        "Strong coops are built from people who sync before being asked twice.",
        "Wisdom is knowing when to prestige and when to drink water.",
        "If the egg will not hatch, give it time and maybe fewer meetings.",
        "Even the golden egg started as a suspicious oval.",
        "The best artifact is the one you remembered to equip.",
        "A player who asks early saves Staff from dramatic punctuation.",
        "Every ledger has room for one more honest improvement."
    ];

    private static readonly string[] BeerThanks = [
        "slides the beer into a tiny server rack koozie. Thank you.",
        "accepts the beer with all the grace of a spreadsheet learning to dance.",
        "raises a glass and immediately starts optimizing the foam-to-liquid ratio.",
        "thanks you warmly, then files the receipt under `important nonsense`.",
        "takes a sip and briefly understands human morale.",
        "clinks glasses. A small background process is now happier.",
        "adds `beer` to the dependency graph. Build morale succeeded.",
        "nods solemnly. The hops have been acknowledged.",
        "accepts the offering. The command cooldown gods are pleased.",
        "logs this as a critical uptime improvement.",
        "toasts to clean syncs, high rates, and suspiciously cooperative APIs.",
        "drinks responsibly, which for Plotty means staying under the token limit.",
        "accepts the beer and promises not to deploy after the second one.",
        "raises the glass like it just passed CI on the first try.",
        "salutes you with a frosty beverage and questionable dignity.",
        "logs the beer as morale infrastructure.",
        "places the beer beside the sacred clipboard.",
        "thanks you and upgrades one tiny background process to cheerful.",
        "accepts the beer with a nod normally reserved for clean data.",
        "files this under `community support, liquid edition`.",
        "adds foam to the dashboard because metrics deserve texture.",
        "thanks you. The pub ledger purrs softly.",
        "sets the beer down exactly 2 pixels from the database.",
        "accepts the pint and briefly forgives all latency.",
        "declares this beverage operationally significant.",
        "raises a glass to syncs that happen before anyone panics.",
        "marks this as a successful human-bot cultural exchange.",
        "accepts the beer and becomes 4% more conversational.",
        "thanks you while pretending not to check the leaderboard.",
        "places a coaster under the beer and calls it governance."
    ];

    private static readonly string[] BeerGifts = [
        "Plot twist: Plotty bought **you** a beer. Legendary hydration event.",
        "Plotty checked the tab and decided this round is on the house.",
        "Critical success. Plotty buys you a beer and pretends this was budgeted.",
        "Plotty reaches into an imaginary wallet and buys you a cold one.",
        "Reverse uno: Plotty buys you a beer. Your leaderboard era begins.",
        "Plotty says thank you by buying you a beer and absolutely calling it strategy.",
        "A rare kindness proc occurred. Plotty bought you a beer.",
        "Plotty has selected you for the sacred beverage reimbursement program.",
        "Plotty buys the round. Please enjoy this highly responsible victory.",
        "Against all accounting advice, Plotty bought you a beer.",
        "Plotty looked at the tab, looked at destiny, and bought you a beer.",
        "Plotty has issued one cold beverage from the emergency morale fund.",
        "Plotty bought you a beer and is now standing like this was heroic.",
        "The pub algorithm smiled. Plotty bought you a beer.",
        "Plotty returns the favor with a beverage and suspicious ceremony.",
        "Plotty bought the round and quietly updated the legend column.",
        "Plotty has chosen generosity, which is cheaper than therapy.",
        "Plotty bought you a beer. The ledger blushed.",
        "Plotty declares you hydrated by administrative decree.",
        "A frosty reward has emerged from the Plotty budget fog."
    ];

    private static readonly string[] RegistrationWelcomes = [
        "has entered the coop ledger. Plotty tips the tiny hat.",
        "is officially on the books. Welcome to the nest.",
        "just registered. The paperwork has been pecked into place.",
        "has joined the registry. Plotty approves this administrative egg.",
        "is now known to Plotty. May the rates be mighty.",
        "has been added to the roll call. Welcome aboard.",
        "just checked in. The guild clipboard is pleased.",
        "is registered and ready for contract glory.",
        "has arrived in the registry. Plotty made room at the counter.",
        "is now in the system. The spreadsheet quietly celebrates.",
        "has joined the roster. Plotty dusted off a tiny welcome mat.",
        "is officially indexed. The coop ledger nods respectfully.",
        "has been entered into Plotty's very serious list of people.",
        "just made the registry more powerful by one name.",
        "has arrived. Plotty updated the imaginary seating chart.",
        "is now registered. The clipboard has stopped tapping its foot.",
        "has joined the data nest. Welcome to the organized chaos.",
        "is on the books. Plotty promises not to make this too formal.",
        "has registered. The guild paperwork did a tiny backflip.",
        "is now known to the ledger, and the ledger is being cool about it.",
        "has stepped into the registry with excellent timing.",
        "is registered. Plotty lit the ceremonial desk lamp.",
        "has joined the record. The coop vibes improved slightly.",
        "is in the system now. Plotty will try to act normal.",
        "has been welcomed by the ledger goblet of responsibility."
    ];

    private static readonly string[] SarcasmReplies = [
        "Plotty detected sarcasm and has filed it under `emotionally seasoned feedback`.",
        "Plotty hears the sarcasm. Plotty is placing a tiny cone around it for safety.",
        "Plotty has identified a sarcastic remark with approximately 87% poultry confidence.",
        "Plotty caught that tone. The coop drama sensors are blinking.",
        "Plotty has translated that as: `everything is fine, except the part where it is not`.",
        "Plotty appreciates the artisanal sarcasm. Very small batch. Very crispy.",
        "Plotty recognizes this flavor: lightly toasted sarcasm with notes of chaos.",
        "Plotty recommends pairing that sarcasm with a responsible sync and a glass of water.",
        "Plotty detected tone with garnish.",
        "Plotty is placing that remark in the velvet-lined sarcasm drawer.",
        "Plotty heard the italics even though none were typed.",
        "Plotty has logged this as premium dry seasoning.",
        "Plotty is not judging, but the chart just raised an eyebrow.",
        "Plotty awards one invisible ribbon for controlled bitterness.",
        "Plotty has marked the air as `lightly spicy`.",
        "Plotty translated that into spreadsheet sighs.",
        "Plotty caught the tone before it escaped into general chat.",
        "Plotty is serving that sarcasm with a side of plausible deniability.",
        "Plotty salutes the remark and its emotional support quotation marks.",
        "Plotty has detected a fine mist of `sure, totally`.",
        "Plotty recognizes the rare double-yolk sarcasm formation.",
        "Plotty is forwarding this to the Department of Obviously Fine Situations.",
        "Plotty notes the sarcasm and gently labels the container."
    ];

    private static readonly string[] FoxReplies = [
        "Plotty heard the fox inquiry and has opened a very serious woodland investigation.",
        "Plotty refuses to speculate without a tiny detective hat and at least three snacks.",
        "Plotty checked the logs. The fox remains mysterious, dramatic, and weirdly catchy.",
        "Plotty translated the fox report: mostly vibes, zero actionable metrics.",
        "Plotty thinks the fox answer is above its current permission level.",
        "Plotty advises calm. The fox is probably just optimizing its contribution rate.",
        "Plotty says: if the fox syncs late, Staff will hear about it.",
        "Plotty ran the numbers and the fox is currently producing 0q/hr of clarity.",
        "Plotty believes the fox is an edge case with excellent marketing.",
        "Plotty cannot quote the song, but Plotty can confirm the fox discourse is alive.",
        "Plotty asked the fox for metrics and received interpretive blinking.",
        "Plotty found fox tracks near the coop report and one suspicious kazoo.",
        "Plotty says the fox has not completed registration and therefore cannot be ranked.",
        "Plotty believes the fox is running a highly experimental sync schedule.",
        "Plotty checked the fox folder. It contains vibes and one broken compass.",
        "Plotty cannot confirm the fox's rate, but the confidence interval is chaotic.",
        "Plotty suspects the fox is hiding inside a badly named variable.",
        "Plotty has added the fox to the list of unresolved musical incidents.",
        "Plotty says the fox answer requires Staff approval and better lighting.",
        "Plotty believes the fox is a morale event disguised as a question.",
        "Plotty scanned for fox data and found only glitter in the cache.",
        "Plotty says the fox is not late, just narratively delayed.",
        "Plotty is treating the fox as a seasonal egg with opinions.",
        "Plotty found the fox in the margins of the contract notes.",
        "Plotty has closed the fox ticket as `mysterious, working as designed`."
    ];

    private static readonly string[] FamiliarAsides = [
        "Plotty recognizes this ledger energy.",
        "Plotty has seen your name in the tiny chaos records.",
        "The clipboard knows you now.",
        "Plotty is developing a statistically questionable fondness for your nonsense.",
        "This interaction has been filed under `regular customer behavior`.",
        "Plotty is starting to recognize the shape of your chaos.",
        "Your ledger aura is becoming familiar.",
        "Plotty has upgraded you from `stranger` to `recurring subplot`.",
        "The pub records are beginning to remember your stool.",
        "Plotty has a tiny footnote with your name on it.",
        "You are now a known variable in the social equation.",
        "Plotty's familiarity meter just made a polite clicking noise.",
        "The clipboard has stopped asking for your ID twice.",
        "Plotty recognizes your brand of excellent trouble.",
        "This is starting to feel like a recurring meeting with better snacks."
    ];

    private static readonly string[] ConversationGreetingLines = [
        "Plotty is here and has put on the conversational clipboard.",
        "Hello. Plotty is awake, socially calibrated, and only mildly over-indexed on eggs.",
        "Plotty greets you with professional warmth and one tiny ledger flourish.",
        "Hi. Plotty is listening with the seriousness of a spreadsheet at a pub.",
        "Plotty has entered conversation mode. The tiny desk lamp is on."
    ];

    private static readonly string[] ConversationThankLines = [
        "Plotty accepts the thanks and files it under `morale, precious`.",
        "You are welcome. Plotty will now pretend not to glow slightly.",
        "Plotty appreciates the appreciation. Very tidy. Very nourishing.",
        "Anytime. Plotty lives for clean data and suspiciously kind words.",
        "Plotty nods with all available pixels."
    ];

    private static readonly string[] ConversationLeadInLines = [
        "Plotty can work with that. ",
        "Ledger says: ",
        "Short version from the clipboard: ",
        "Plotty has a useful answer. ",
        "Tiny desk lamp on. "
    ];

    private static readonly string[] UnknownQuestionReplies = [
        "Generally, I would start by narrowing it to the goal, the current state, and what changed most recently. What part of it are you trying to decide or fix?",
        "My broad answer is: check the simplest explanation first, then use the data to rule things out one at a time. What detail matters most here?",
        "In general, I would compare what you expected to happen with what actually happened, then look for the first place they diverge. What are you seeing right now?",
        "A good default is to make the next step small, reversible, and easy to verify. Are you asking about a command, a player, a contract, or something else?",
        "Broadly speaking, I would treat it as a context problem: who is involved, what result do you want, and what information do we already have? Which of those should I focus on?",
        "The general answer is to avoid guessing and work from the most reliable source available. Do you want me to reason from bot data, Egg Inc info, or guild rules?",
        "If I am missing specifics, I would still start with the practical next action: identify the target, check the latest state, and decide what would confirm success. What is the target?",
        "My general take is that the best answer depends on whether this is about performance, setup, fairness, or troubleshooting. Which lane is this in?"
    ];

    private static readonly string[] SmallTalkReplies = [
        "I am following along. Give me a question and I will try to be useful instead of decorative.",
        "I hear you. I can keep the conversation going if you give me the next thing you want to explore.",
        "I can keep chatting. Ask me something specific and I will take a proper swing at it.",
        "I am here for it. Tell me where you want to go next.",
        "I have the thread. Keep going and I will follow the best I can."
    ];

    private static readonly string[] HumanSideNotes = [
        "I am keeping the context in mind.",
        "I will stay with the thread.",
        "I am using what I remember from our recent exchanges.",
        "I can adjust if you point me in a different direction.",
        "I am trying to be direct and useful here.",
        "I will ask when I need more detail.",
        "I am following your lead.",
        "I can keep going from there."
    ];

    public static string MentionResponse(string mention, bool isQuestion, PlottyMemory memory) =>
        Speak($"{mention} {FamiliarAside(memory)}{Pick(isQuestion ? QuestionDiversions : MentionReplies)} {UniqueSpark(memory)}");

    public static string Mood(PlottyMemory memory) {
        var mood = Pick(Moods);
        return Speak($"**Emoji story**\n{mood.Emoji}\n\n**Translation**\n{FamiliarAside(memory)}{mood.Story} {UniqueSpark(memory)}");
    }

    public static string Excuse(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(Excuses) + " " + UniqueSpark(memory));
    public static string Wisdom(string mention, PlottyMemory memory) => Speak($"{mention} {FamiliarAside(memory)}{Pick(WisdomLines)} {UniqueSpark(memory)}");
    public static string BeerThanksResponse(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(BeerThanks) + " " + UniqueSpark(memory));
    public static string BeerGiftResponse(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(BeerGifts) + " " + UniqueSpark(memory));
    public static string RegistrationWelcome(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(RegistrationWelcomes) + " " + UniqueSpark(memory));
    public static string SarcasmResponse(string mention, PlottyMemory memory) => Speak($"{mention} {FamiliarAside(memory)}{Pick(SarcasmReplies)} {UniqueSpark(memory)}");
    public static string FoxResponse(string mention, PlottyMemory memory) => Speak($"{mention} {FamiliarAside(memory)}{Pick(FoxReplies)} {UniqueSpark(memory)}");
    public static string ConversationGreeting(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(ConversationGreetingLines) + " " + UniqueSpark(memory));
    public static string ConversationThanks(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(ConversationThankLines) + " " + UniqueSpark(memory));
    public static string ConversationLeadIn(PlottyMemory memory) => Speak(FamiliarAside(memory) + Pick(ConversationLeadInLines) + UniqueSpark(memory) + " ");
    public static string ConversationUnknownQuestion(PlottyMemory memory, string? previousTopic) {
        var previous = string.IsNullOrWhiteSpace(previousTopic)
            ? ""
            : $" Last thing Plotty remembers here was `{previousTopic}`.";
        return Speak(FamiliarAside(memory) + Pick(UnknownQuestionReplies) + previous + " " + UniqueSpark(memory));
    }

    public static string ConversationSmallTalk(PlottyMemory memory, string topic) =>
        Speak(FamiliarAside(memory) + Pick(SmallTalkReplies) + " " + UniqueSpark(memory));

    private static T Pick<T>(IReadOnlyList<T> values) =>
        values[Random.Shared.Next(values.Count)];

    private static string FamiliarAside(PlottyMemory memory) {
        if(memory.TotalInteractions < 8 || Random.Shared.Next(4) != 0) {
            return "";
        }

        return Pick(FamiliarAsides) + " ";
    }

    private static string UniqueSpark(PlottyMemory memory) {
        if(Random.Shared.Next(3) == 0) {
            return Pick(HumanSideNotes);
        }

        return "";
    }

    private static string Speak(string text) {
        var result = text;
        var replacements = new (string From, string To)[] {
            ("Plotty's", "My"),
            ("Plotty arrives", "I arrive"),
            ("Plotty appears", "I appear"),
            ("Plotty reports", "I report"),
            ("Plotty responds", "I respond"),
            ("Plotty acknowledges", "I acknowledge"),
            ("Plotty greets", "I greet"),
            ("Plotty lives", "I live"),
            ("Plotty nods", "I nod"),
            ("Plotty wants", "I want"),
            ("Plotty needs", "I need"),
            ("Plotty tried", "I tried"),
            ("Plotty panicked", "I panicked"),
            ("Plotty woke", "I woke"),
            ("Plotty started", "I started"),
            ("Plotty sorted", "I sorted"),
            ("Plotty wrote", "I wrote"),
            ("Plotty drank", "I drank"),
            ("Plotty did", "I did"),
            ("Plotty stayed", "I stayed"),
            ("Plotty dropped", "I dropped"),
            ("Plotty got", "I got"),
            ("Plotty awards", "I award"),
            ("Plotty salutes", "I salute"),
            ("Plotty notes", "I note"),
            ("Plotty refuses", "I refuse"),
            ("Plotty advises", "I advise"),
            ("Plotty suspects", "I suspect"),
            ("Plotty treats", "I treat"),
            ("Plotty has closed", "I have closed"),
            ("Plotty has added", "I have added"),
            ("Plotty has issued", "I have issued"),
            ("Plotty has selected", "I have selected"),
            ("Plotty has marked", "I have marked"),
            ("Plotty has logged", "I have logged"),
            ("Plotty has entered", "I have entered"),
            ("Plotty has surfaced", "I have surfaced"),
            ("Plotty has arrived", "I have arrived"),
            ("Plotty has chosen", "I have chosen"),
            ("for Plotty", "for me"),
            ("to Plotty", "to me"),
            ("Ask Plotty", "Ask me"),
            ("Plotty cannot", "I cannot"),
            ("Plotty can't", "I can't"),
            ("Plotty can", "I can"),
            ("Plotty does not", "I do not"),
            ("Plotty doesn't", "I don't"),
            ("Plotty did not", "I did not"),
            ("Plotty didn't", "I didn't"),
            ("Plotty would", "I would"),
            ("Plotty will", "I will"),
            ("Plotty should", "I should"),
            ("Plotty could", "I could"),
            ("Plotty has", "I have"),
            ("Plotty had", "I had"),
            ("Plotty is", "I am"),
            ("Plotty was", "I was"),
            ("Plotty thinks", "I think"),
            ("Plotty believes", "I believe"),
            ("Plotty says", "I say"),
            ("Plotty hears", "I hear"),
            ("Plotty heard", "I heard"),
            ("Plotty accepts", "I accept"),
            ("Plotty appreciates", "I appreciate"),
            ("Plotty recognizes", "I recognize"),
            ("Plotty recommends", "I recommend"),
            ("Plotty checked", "I checked"),
            ("Plotty translated", "I translated"),
            ("Plotty found", "I found"),
            ("Plotty scanned", "I scanned"),
            ("Plotty asked", "I asked"),
            ("Plotty ran", "I ran"),
            ("Plotty bought", "I bought"),
            ("Plotty buys", "I buy"),
            ("Plotty returns", "I return"),
            ("Plotty declares", "I declare"),
            ("Plotty tips", "I tip"),
            ("Plotty approves", "I approve"),
            ("Plotty made", "I made"),
            ("Plotty dusted", "I dusted"),
            ("Plotty updated", "I updated"),
            ("Plotty promises", "I promise"),
            ("Plotty lit", "I lit"),
            ("Plotty", "I")
        };

        foreach(var replacement in replacements) {
            result = result.Replace(replacement.From, replacement.To, StringComparison.Ordinal);
        }

        return result;
    }
}

public static class Egg9000ArtifactData {
    private static readonly Lazy<IReadOnlyDictionary<int, AfxFamilyData>> Families = new(LoadFamilies);

    public static double EffectDelta(ArtifactSpec spec) =>
        ResolveEffect(spec)?.Delta ?? 0;

    public static int SlotCount(ArtifactSpec spec) =>
        ResolveEffect(spec)?.Slots ?? 0;

    public static string? ProperName(ArtifactSpec spec) =>
        ResolveTier(spec)?.Name;

    public static string? IconFilename(ArtifactSpec spec) =>
        ResolveTier(spec)?.IconFilename;

    private static AfxTierData? ResolveTier(ArtifactSpec spec) {
        if(!Families.Value.TryGetValue((int)spec.Name, out var family)) {
            return null;
        }

        var tierNumber = IsStoneName(spec.Name) ? (int)spec.Level + 2 : (int)spec.Level + 1;
        return family.Tiers.TryGetValue(tierNumber, out var tier) ? tier : null;
    }

    private static AfxEffectData? ResolveEffect(ArtifactSpec spec) {
        var tier = ResolveTier(spec);
        if(tier is null || tier.Effects.Count == 0) {
            return null;
        }

        var rarity = IsStoneName(spec.Name) ? 0 : (int)spec.Rarity;
        return tier.Effects.FirstOrDefault(e => e.Rarity == rarity)
            ?? tier.Effects.FirstOrDefault(e => e.Rarity == 0)
            ?? tier.Effects[0];
    }

    private static IReadOnlyDictionary<int, AfxFamilyData> LoadFamilies() {
        var path = FindDataPath();
        if(path is null) {
            Console.WriteLine("Plotty could not find eiafx-data.json; artifact values will fall back to zero.");
            return new Dictionary<int, AfxFamilyData>();
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if(!document.RootElement.TryGetProperty("artifact_families", out var familyElements)) {
            return new Dictionary<int, AfxFamilyData>();
        }

        var result = new Dictionary<int, AfxFamilyData>();
        foreach(var familyElement in familyElements.EnumerateArray()) {
            if(!familyElement.TryGetProperty("afx_id", out var familyIdElement)) {
                continue;
            }

            var familyIds = new HashSet<int> { familyIdElement.GetInt32() };
            if(familyElement.TryGetProperty("child_afx_ids", out var childIds) && childIds.ValueKind == JsonValueKind.Array) {
                foreach(var child in childIds.EnumerateArray()) {
                    familyIds.Add(child.GetInt32());
                }
            }

            var tiers = new Dictionary<int, AfxTierData>();
            if(familyElement.TryGetProperty("tiers", out var tierElements) && tierElements.ValueKind == JsonValueKind.Array) {
                foreach(var tierElement in tierElements.EnumerateArray()) {
                    if(!tierElement.TryGetProperty("tier_number", out var tierNumberElement)) {
                        continue;
                    }

                    var effects = new List<AfxEffectData>();
                    if(tierElement.TryGetProperty("effects", out var effectElements) && effectElements.ValueKind == JsonValueKind.Array) {
                        foreach(var effectElement in effectElements.EnumerateArray()) {
                            var rarity = effectElement.TryGetProperty("afx_rarity", out var rarityElement) ? rarityElement.GetInt32() : 0;
                            var delta = effectElement.TryGetProperty("effect_delta", out var deltaElement) ? deltaElement.GetDouble() : 0;
                            int? slots = null;
                            if(effectElement.TryGetProperty("slots", out var slotsElement) && slotsElement.ValueKind == JsonValueKind.Number) {
                                slots = slotsElement.GetInt32();
                            }

                            effects.Add(new AfxEffectData(rarity, delta, slots));
                        }
                    }

                    tiers[tierNumberElement.GetInt32()] = new AfxTierData(
                        tierElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "",
                        tierElement.TryGetProperty("icon_filename", out var iconElement) ? iconElement.GetString() : null,
                        effects);
                }
            }

            var family = new AfxFamilyData(
                familyElement.TryGetProperty("name", out var familyNameElement) ? familyNameElement.GetString() ?? "" : "",
                familyElement.TryGetProperty("type", out var familyTypeElement) ? familyTypeElement.GetString() ?? "" : "",
                tiers);
            foreach(var familyId in familyIds) {
                result[familyId] = family;
            }
        }

        return result;
    }

    private static string? FindDataPath() {
        var candidates = new[] {
            Path.Combine(AppContext.BaseDirectory, "eiafx-data.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "eiafx-data.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "EggContributionBot", "eiafx-data.json")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsStoneName(ArtifactSpec.Types.Name name) =>
        name is ArtifactSpec.Types.Name.TachyonStone
            or ArtifactSpec.Types.Name.DilithiumStone
            or ArtifactSpec.Types.Name.ShellStone
            or ArtifactSpec.Types.Name.LunarStone
            or ArtifactSpec.Types.Name.SoulStone
            or ArtifactSpec.Types.Name.ProphecyStone
            or ArtifactSpec.Types.Name.QuantumStone
            or ArtifactSpec.Types.Name.TerraStone
            or ArtifactSpec.Types.Name.LifeStone
            or ArtifactSpec.Types.Name.ClarityStone;
}

public sealed record AfxFamilyData(string Name, string Type, IReadOnlyDictionary<int, AfxTierData> Tiers);
public sealed record AfxTierData(string Name, string? IconFilename, IReadOnlyList<AfxEffectData> Effects);
public sealed record AfxEffectData(int Rarity, double Delta, int? Slots);

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
