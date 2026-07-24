using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using Discord;
using Discord.WebSocket;
using EggContribBot;
using EggContribBot.Proto;
using EggContribBot.Config;
using EggContribBot.Services;
using EggContribBot.Models;
using EggContribBot.Commands;

var settings = BotSettings.Load();
var plottyAdminUserIds = settings.Discord.ParsedAdminUserIds.ToHashSet();
var token = settings.Discord.Token
    ?? Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")
    ?? throw new InvalidOperationException("Set Discord:Token in appsettings.json or DISCORD_BOT_TOKEN.");

var secureText = new SecureText(settings.Storage.KeyPath);
var dataStore = new DataStore(settings.Storage.DataPath, secureText);
await dataStore.RemoveLegacyPlaintextLinksAsync();
var eggClient = new EggIncClient();
var egg9000Client = new Egg9000Client(settings.Egg9000);
var plottyAiClient = new PlottyAiClient(settings.OpenAi);
var monitorHealth = new MonitorHealthService();
var missingJoinMonitorsStarted = new HashSet<ulong>();
var shipReturnMonitorsStarted = new HashSet<ulong>();
var firstCoopAwardMonitorsStarted = new HashSet<ulong>();
var tokenLeaderboardMonitorsStarted = new HashSet<ulong>();
var contractCommands = new ContractCommands(eggClient);
var funCommands = new FunCommands(dataStore);

const int MaxEggIncAccountConcurrency = 4;

var client = new DiscordSocketClient(new DiscordSocketConfig {
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
    AlwaysDownloadUsers = true
});

Console.WriteLine(plottyAiClient.IsConfigured
    ? $"Plotty AI chat is enabled with model {settings.OpenAi!.EffectiveModel}."
    : "Plotty AI chat is using local no-key conversation replies. Set OPENAI_API_KEY to enable hosted generated replies.");

client.Log += msg => {
    Console.WriteLine(msg.ToString());
    return Task.CompletedTask;
};

client.Ready += async () => {
    var commands = BuildCommands().Select(c => c.Build()).ToArray();
    var guildIds = settings.Discord.ParsedGuildIds.Distinct().ToArray();
    if(guildIds.Length > 0) {
        await client.Rest.BulkOverwriteGlobalCommands([]);
        foreach(var guildId in guildIds) {
            var guild = client.GetGuild(guildId);
            if(guild is null) {
                Console.WriteLine($"Plotty is connected, but guild {guildId} was not found. Is Plotty invited?");
                continue;
            }

            await guild.BulkOverwriteApplicationCommandAsync(commands);
            Console.WriteLine($"Logged in as {client.CurrentUser}; registered {commands.Length} commands in {guild.Name}.");
            if(missingJoinMonitorsStarted.Add(guildId)) {
                _ = Task.Run(() => MonitorMissingCoopJoinsAsync(guildId));
            }
            if(shipReturnMonitorsStarted.Add(guildId)) {
                _ = Task.Run(() => MonitorShipReturnsAsync(guildId));
            }
            if(firstCoopAwardMonitorsStarted.Add(guildId)) {
                _ = Task.Run(() => MonitorFirstCoopAwardsAsync(guildId));
            }
            if(tokenLeaderboardMonitorsStarted.Add(guildId)) {
                _ = Task.Run(() => MonitorWeeklyTokenLeaderboardAsync(guildId));
            }
        }
    } else {
        await client.Rest.BulkOverwriteGlobalCommands(commands);
        Console.WriteLine($"Logged in as {client.CurrentUser}; registered {commands.Length} global commands.");
    }
};

client.UserLeft += async (guild, user) => {
    try {
        var removed = await dataStore.RemoveRegisteredEidsForUserAsync(guild.Id, user.Id);
        if(removed > 0) {
            Console.WriteLine($"Unregistered {removed} EID account(s) for {user.Username} ({user.Id}) after leaving {guild.Name}.");
        }
    } catch(Exception ex) {
        Console.WriteLine($"Could not unregister EIDs for departed user {user.Id} in guild {guild.Id}: {ex}");
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
                await contractCommands.HandleContractAsync(command);
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
            case "admin-member-contract":
                await HandleAdminMemberContractAsync(command);
                break;
            case "admin-dashboard":
                await HandleDashboardAsync(command);
                break;
            case "admin-list-members":
                await HandleAdminListMembersAsync(command);
                break;
            case "admin-e9k-compare":
                await HandleAdminE9kCompareAsync(command);
                break;
            case "admin-ping-unregistered":
                await HandleAdminPingUnregisteredAsync(command);
                break;
            case "admin-health":
                await HandleAdminHealthAsync(command);
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
            case "beverage-plotty":
                await HandleBeerPlottyAsync(command);
                break;
            case "beverage-user":
                await HandleBeerUserAsync(command);
                break;
            case "beverage-leader":
                await HandleBeerLeaderAsync(command);
                break;
            case "token-leaderboard":
                await HandleTokenLeaderboardAsync(command);
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
            case "wiki":
                // TODO: Simplify this command. It's nuts. This should just look stuff up on the Wiki.
                await funCommands.HandleHelpAsync(command);
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

            await component.DeferAsync();

            var accounts = await dataStore.GetRegisteredAccountsAsync(component.GuildId.Value, discordUserId);
            if(accounts.Count == 0) {
                await component.FollowupAsync("That player does not have an EID registered anymore.", ephemeral: true);
                return;
            }

            var user = client.GetGuild(component.GuildId.Value)?.GetUser(discordUserId);
            var displayName = user?.DisplayName ?? accounts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.EggName))?.EggName ?? "Registered Player";
            var lookups = await GetAccountCoopLookupsAsync(accounts.Take(10));
            var embeds = new List<Embed>();
            foreach(var item in lookups) {
                embeds.Add(await BuildPlayerEmbedAsync(item.Account, displayName, item.Lookup));
            }

            await component.ModifyOriginalResponseAsync(message => {
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

        if(message.Content.Contains("what the fox", StringComparison.OrdinalIgnoreCase)) {
            var memory = await dataStore.RecordPlottyInteractionAsync(guildChannel.Guild.Id, message.Author.Id, "fox");
            await message.Channel.SendMessageAsync(PlottyPersonality.FoxResponse(message.Author.Mention, memory));
            return;
        }

        if(IsLateTodayChannel(guildChannel) && message.Author is SocketGuildUser lateUser) {
            await HandleLateTodayMessageAsync(message, lateUser, guildChannel.Guild);
            return;
        }

        var mentionedPlotty = message.MentionedUsers.Any(u => u.Id == client.CurrentUser.Id);
        var repliedToPlotty = false;
        if(!mentionedPlotty && message.Reference?.MessageId.IsSpecified == true) {
            var referencedMessage = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value);
            repliedToPlotty = referencedMessage?.Author.Id == client.CurrentUser.Id;
        }

        if(mentionedPlotty || repliedToPlotty) {
            var prompt = StripBotMention(message.Content);
            var isQuestion = LooksLikeQuestion(prompt);
            var memory = await dataStore.RecordPlottyInteractionAsync(
                guildChannel.Guild.Id,
                message.Author.Id,
                isQuestion ? "question_mention" : "mention");
            var response = await plottyAiClient.GenerateReplyAsync(
                guildChannel.Guild.Id,
                message.Author.Id,
                prompt,
                memory);
            var reply = response is null
                ? $"{message.Author.Mention} I could not form a reply right now. Please try me again in a moment."
                : $"{message.Author.Mention} {response}";
            await message.Channel.SendMessageAsync(reply, allowedMentions: AllowedMentions.None);
            return;
        }

        if(LooksLikeSarcasm(message.Content) && Random.Shared.Next(100) == 0) {
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
        .WithName("admin-member-contract")
        .WithDescription("Staff only: privately show a member's active co-op players and rates.")
        .AddOption("member", ApplicationCommandOptionType.User, "Discord member.", isRequired: true);

    yield return new SlashCommandBuilder()
        .WithName("admin-dashboard")
        .WithDescription("Show a staff overview of registered players, low rates, sync issues, and likely unboosted players.");

    yield return new SlashCommandBuilder()
        .WithName("admin-list-members")
        .WithDescription("Staff only: compare server members with Plotty EID registrations.");

    yield return new SlashCommandBuilder()
        .WithName("admin-e9k-compare")
        .WithDescription("Staff only: compare EGG9000 guild-tag members with Discord server members.");

    yield return new SlashCommandBuilder()
        .WithName("admin-ping-unregistered")
        .WithDescription("Staff only: ping every member who has not registered with Plotty.")
        .AddOption("message", ApplicationCommandOptionType.String, "Message to include before the pings.", isRequired: true);

    yield return new SlashCommandBuilder()
        .WithName("admin-health")
        .WithDescription("Staff only: show Plotty background monitor health.");

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
        .WithName("beverage-plotty")
        .WithDescription("Buy Plotty a beverage. Sometimes Plotty buys you one back.")
        .AddOption(BuildBeverageChoiceOption());

    yield return new SlashCommandBuilder()
        .WithName("beverage-user")
        .WithDescription("Gift another member a beverage.")
        .AddOption("member", ApplicationCommandOptionType.User, "Member receiving the beverage.", isRequired: true)
        .AddOption(BuildBeverageChoiceOption())
        .AddOption("ping", ApplicationCommandOptionType.Boolean, "Ping the member receiving the beverage.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("beverage-leader")
        .WithDescription("Privately show the Beverage Leaderboard.");

    yield return new SlashCommandBuilder()
        .WithName("token-leaderboard")
        .WithDescription("Show this week's Tokie Awards for tokens sent.");

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
        .WithName("wiki")
        .WithDescription("Ask Plotty an Egg Inc question using the Egg Inc Wiki.")
        .AddOption("question", ApplicationCommandOptionType.String, "What do you want to know?", isRequired: true);

}

static SlashCommandOptionBuilder BuildBeverageChoiceOption() =>
    new SlashCommandOptionBuilder()
        .WithName("drink")
        .WithDescription("Which beverage?")
        .WithType(ApplicationCommandOptionType.String)
        .WithRequired(true)
        .AddChoice("Water", "Water")
        .AddChoice("LaCroix", "LaCroix")
        .AddChoice("Milk", "Milk")
        .AddChoice("Beer", "Beer")
        .AddChoice("Wine", "Wine")
        .AddChoice("Soda-Pop", "Soda-Pop")
        .AddChoice("Coffee", "Coffee")
        .AddChoice("Tea", "Tea");

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

    var (notice, reportPosted) = await RecordAndPostContractLateNoticeAsync(
        command.GuildId!.Value,
        user,
        string.IsNullOrWhiteSpace(contractId) ? null : contractId,
        string.IsNullOrWhiteSpace(eta) ? null : eta,
        string.IsNullOrWhiteSpace(note) ? null : note);
    var contractText = string.IsNullOrWhiteSpace(notice.ContractId) ? "current contracts" : $"`{notice.ContractId}`";
    var reportText = reportPosted ? " I created a thread in #plotty-reports without pinging anyone." : " I could not create a #plotty-reports thread, but I saved the flag locally.";
    await command.RespondAsync($"Got it. I marked you late for {contractText} for the next 48 hours, so you will not be added to the 6-hour non-join list while that flag is active.{reportText}", ephemeral: true);
}

async Task HandleLateTodayMessageAsync(SocketMessage message, SocketGuildUser user, SocketGuild guild) {
    var contractId = ExtractLateNoticeContractId(message.Content);
    var note = TrimDiscordMessage(Regex.Replace(message.Content, @"\s+", " ").Trim(), 300);
    var targets = string.IsNullOrWhiteSpace(contractId)
        ? await GetLateTodayTargetContractsAsync()
        : [(ContractId: contractId, ExpiresAt: (DateTimeOffset?)null)];
    if(targets.Count == 0) {
        await message.Channel.SendMessageAsync(
            $"{user.Mention} I could not find the next upcoming contract yet. Please try `/contract-late-notify` with the contract id once it is available.",
            allowedMentions: AllowedMentions.None);
        return;
    }

    var notices = new List<ContractLateNotice>();
    foreach(var target in targets) {
        var (notice, _) = await RecordAndPostContractLateNoticeAsync(
            guild.Id,
            user,
            target.ContractId,
            eta: "late today",
            note: string.IsNullOrWhiteSpace(note) ? null : note,
            expiresAt: target.ExpiresAt);
        notices.Add(notice);
    }

    var contractText = FormatLateNoticeContractList(notices
        .Select(n => n.ContractId)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id!));
    await message.AddReactionAsync(new Emoji("✅"));
    await message.Channel.SendMessageAsync(
        $"{user.Mention} got it. I marked you late for {contractText}.",
        allowedMentions: AllowedMentions.None);
}

async Task<(ContractLateNotice Notice, bool ReportPosted)> RecordAndPostContractLateNoticeAsync(
    ulong guildId,
    SocketGuildUser user,
    string? contractId,
    string? eta,
    string? note,
    DateTimeOffset? expiresAt = null) {
    var notice = await dataStore.RecordContractLateNoticeAsync(
        guildId,
        user.Id,
        contractId,
        eta,
        note,
        expiresAt);

    var guild = client.GetGuild(guildId);
    var reportChannel = guild is null ? null : FindPlottyReportsChannel(guild);
    var reportPosted = reportChannel is not null &&
        await TryPostReportThreadAsync(
            reportChannel,
            $"Late notice - {user.DisplayName}",
            BuildContractLateNoticeEmbed(user, notice),
            "plotty-reports");

    return (notice, reportPosted);
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

async Task<IReadOnlyList<(string ContractId, DateTimeOffset? ExpiresAt)>> GetLateTodayTargetContractsAsync() {
    var now = DateTimeOffset.UtcNow;
    var upcomingContracts = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.CoopAllowed)
        .Where(c => c.StartTime > 0)
        .Select(c => new {
            Contract = c,
            StartsAt = DateTimeOffset.FromUnixTimeSeconds((long)c.StartTime)
        })
        .Where(c => c.StartsAt >= now.AddHours(-6))
        .OrderBy(c => c.StartsAt)
        .ThenBy(c => c.Contract.CcOnly)
        .ToList();
    if(upcomingContracts.Count == 0) {
        return [];
    }

    var next = upcomingContracts.FirstOrDefault(c => c.StartsAt >= now) ?? upcomingContracts.First();
    var localNow = ToGuildLocalTime(now);
    var localReleaseDate = ToGuildLocalTime(next.StartsAt).Date;
    var selectedContracts = localNow.DayOfWeek == DayOfWeek.Friday
        ? upcomingContracts
            .Where(c => ToGuildLocalTime(c.StartsAt).Date == localReleaseDate)
            .GroupBy(c => c.Contract.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(c => c.StartsAt).First())
            .ToList()
        : [next];

    return selectedContracts
        .Select(c => (
            ContractId: c.Contract.Identifier,
            ExpiresAt: (DateTimeOffset?)ContractLateNoticeExpiresAt(c.Contract, c.StartsAt)))
        .ToList();
}

static DateTimeOffset ContractLateNoticeExpiresAt(Contract contract, DateTimeOffset startsAt) {
    if(contract.ExpirationTime > 0) {
        return DateTimeOffset.FromUnixTimeSeconds((long)contract.ExpirationTime);
    }

    if(contract.LengthSeconds > 0) {
        return startsAt.AddSeconds(contract.LengthSeconds);
    }

    return startsAt.AddDays(7);
}

static string FormatLateNoticeContractList(IEnumerable<string> contractIds) {
    var ids = contractIds
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(id => $"`{id}`")
        .ToList();
    return ids.Count switch {
        0 => "the next upcoming contract",
        1 => ids[0],
        2 => $"{ids[0]} and {ids[1]}",
        _ => string.Join(", ", ids.Take(ids.Count - 1)) + $", and {ids[^1]}"
    };
}

static DateTimeOffset ToGuildLocalTime(DateTimeOffset value) {
    try {
        var mountain = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");
        return TimeZoneInfo.ConvertTime(value, mountain);
    } catch(TimeZoneNotFoundException) {
        return value.ToLocalTime();
    } catch(InvalidTimeZoneException) {
        return value.ToLocalTime();
    }
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

    var lookups = await GetAccountCoopLookupsAsync(accounts);
    var runningEmbeds = new List<Embed>();
    var completedEmbeds = new List<Embed>();
    var diagnostics = new List<string>();
    var completedContractCount = 0;
    foreach(var item in lookups) {
        var account = item.Account;
        var lookup = item.Lookup;
        var accountLabel = AccountDisplayName(account);
        completedContractCount += CountCompletedContracts(lookup.Backup);
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

    var summary = $"Showing `{runningEmbeds.Count}` running contract(s) and `{completedContractCount}` completed contract(s) from `{accounts.Count}` registered EID account(s).";
    for(var i = 0; i < embeds.Count; i += 10) {
        var batch = embeds.Skip(i).Take(10).ToArray();
        await command.FollowupAsync(text: i == 0 ? summary : null, embeds: batch, ephemeral: true);
    }
}

static int CountCompletedContracts(Backup? backup) {
    if(backup?.Contracts is null) {
        return 0;
    }

    var detailedCompletedCount = backup.Contracts.Archive
        .Concat(backup.Contracts.Contracts.Where(IsCompletedLocalContract))
        .Where(c => !c.Cancelled)
        .Select(c => new PlayerContractCandidate(GetLocalContractId(c), c.CoopIdentifier, c.TimeAccepted))
        .Where(c => !string.IsNullOrWhiteSpace(c.ContractId))
        .GroupBy(c => (
            ContractId: c.ContractId.ToLowerInvariant(),
            CoopCode: string.IsNullOrWhiteSpace(c.CoopCode) ? "" : c.CoopCode.ToLowerInvariant()))
        .Count();

    var seenContractCount = backup.Contracts.ContractIdsSeen
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    return Math.Max(detailedCompletedCount, seenContractCount);
}

static bool IsCompletedLocalContract(LocalContract contract) =>
    contract.Accepted &&
    !contract.Cancelled &&
    (contract.CoopContributionFinalized ||
     contract.NumGoalsAchieved > 0 ||
     contract.LastAmountWhenRewardGiven > 0);

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

    var accountLabel = AccountDisplayName(account);
    var statuses = await SelectWithConcurrencyAsync(
        completedContracts,
        MaxEggIncAccountConcurrency,
        async contract => (Contract: contract, Status: await eggClient.GetCoopStatusAsync(contract.ContractId, contract.CoopCode)));
    var embeds = new List<Embed>();
    foreach(var item in statuses) {
        var contract = item.Contract;
        var status = item.Status;
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

    await command.DeferAsync(ephemeral: true);

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
    var statusResult = await GetRecentRegisteredStatusesAsync(accounts);
    if(statusResult.RecentContractCount == 0) {
        await command.FollowupAsync("I could not find any active contracts released in the past 3 days.", ephemeral: true);
        return;
    }

    if(statusResult.Statuses.Count == 0) {
        await command.FollowupAsync(
            $"I checked `{accounts.Count}` registered EID(s), but none had active co-op rates for contracts released in the past 3 days.",
            ephemeral: true);
        return;
    }

    var embeds = statusResult.Statuses.Values
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
            $"Plotty found `{statusResult.Statuses.Count}` active co-op(s), but none of their contributors matched a registered EID or registered Egg Inc name.",
            ephemeral: true);
        return;
    }

    var message = statusResult.Failed > 0
        ? $"Showing registered EID players only for contracts released in the past 3 days. `{statusResult.Failed}` registered EID(s) did not return active rates."
        : $"Showing registered EID players only for contracts released in the past 3 days from `{accounts.Count}` registered EID(s).";
    if(statusResult.SkippedOldContracts > 0) {
        message += $" Skipped `{statusResult.SkippedOldContracts}` older active co-op lookup(s).";
    }

    await command.FollowupAsync(text: message, embeds: embeds, ephemeral: true);
}

async Task HandleAdminMemberContractAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can view member contracts.", ephemeral: true);
        return;
    }

    var member = ResolveGuildMemberOption(command, "member");
    if(member is null) {
        await command.RespondAsync("I can only look up members in this server.", ephemeral: true);
        return;
    }

    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, member.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync($"{member.DisplayName} does not have an EID registered with Plotty.", ephemeral: true);
        return;
    }

    var lookups = await GetAccountCoopLookupsAsync(accounts);
    var embeds = new List<Embed>();
    var diagnostics = new List<string>();
    foreach(var item in lookups) {
        var account = item.Account;
        var lookup = item.Lookup;
        var accountLabel = AccountDisplayName(account);
        embeds.AddRange(lookup.Statuses
            .Select(s => BuildContributionEmbed(
                s.ContractId,
                s.CoopCode,
                s.Status,
                showCoopCode: false,
                titleSuffix: accounts.Count > 1 ? $"({member.DisplayName} - {accountLabel})" : $"({member.DisplayName})"))
            .Where(e => e is not null)
            .Cast<Embed>());

        if(lookup.Statuses.Count == 0) {
            diagnostics.Add($"**{accountLabel}**\n{BuildCoopLookupDiagnostic(lookup)}");
        }
    }

    if(embeds.Count == 0) {
        var details = diagnostics.Count == 0 ? "" : "\n\n" + string.Join("\n\n", diagnostics.Take(3));
        await command.FollowupAsync(
            $"I could not find an active co-op for {member.DisplayName}'s registered EID account(s).{details}",
            ephemeral: true);
        return;
    }

    var summary = $"Showing `{embeds.Count}` active co-op contract(s) for {member.DisplayName} across `{accounts.Count}` registered EID account(s).";
    await SendPrivateEmbedReportAsync(command, summary, embeds);
}

async Task HandlePlayerAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

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

    var lookups = await GetAccountCoopLookupsAsync(accounts.Take(10));
    var embeds = new List<Embed>();
    foreach(var item in lookups) {
        embeds.Add(await BuildPlayerEmbedAsync(item.Account, displayName, item.Lookup));
    }

    await command.FollowupAsync(
        text: accounts.Count > 1 ? $"Showing `{embeds.Count}` Egg Inc account(s) tied to {displayName}." : null,
        embeds: embeds.ToArray(),
        components: BuildPlayerComponents(discordUserId),
        ephemeral: true);
}

async Task HandleDashboardAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can use the admin dashboard.", ephemeral: true);
        return;
    }

    await command.DeferAsync(ephemeral: true);

    var dashboard = await BuildDashboardAsync(command.GuildId!.Value);
    if(dashboard.Embeds.Count == 0) {
        await command.FollowupAsync(dashboard.Message, ephemeral: true);
        return;
    }

    await command.FollowupAsync(text: dashboard.Message, embeds: dashboard.Embeds.ToArray(), components: BuildDashboardComponents(), ephemeral: true);
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

async Task HandleAdminListMembersAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can list registered members.", ephemeral: true);
        return;
    }

    await command.DeferAsync(ephemeral: true);

    var guild = client.GetGuild(command.GuildId!.Value);
    if(guild is null) {
        await command.FollowupAsync("I could not read this server's member list.", ephemeral: true);
        return;
    }

    try {
        await guild.DownloadUsersAsync();
    } catch(Exception ex) {
        Console.WriteLine($"Could not refresh guild users for admin-list-members: {ex.Message}");
    }

    var registeredAccounts = await dataStore.GetRegisteredEidsAsync(guild.Id);
    var registeredByUser = registeredAccounts
        .GroupBy(a => a.DiscordUserId)
        .ToDictionary(g => g.Key, g => g.ToList());
    var members = guild.Users
        .Where(u => !u.IsBot)
        .OrderBy(DiscordAccountSortName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(u => u.Id)
        .ToList();
    var registeredMembers = members
        .Where(u => registeredByUser.ContainsKey(u.Id))
        .ToList();
    var unregisteredMembers = members
        .Where(u => !registeredByUser.ContainsKey(u.Id))
        .ToList();
    var orphanedRegistrations = registeredByUser
        .Where(kvp => members.All(u => u.Id != kvp.Key))
        .OrderBy(kvp => kvp.Key)
        .ToList();

    IReadOnlyList<Egg9000LeaderboardItem> egg9000Members = [];
    var egg9000Status = "";
    if(egg9000Client.IsConfigured) {
        try {
            egg9000Members = await egg9000Client.GetLeaderboardAsync();
            egg9000Status = $" | E9K roster: `{egg9000Members.Count}`";
        } catch(Exception ex) {
            egg9000Status = " | E9K roster: unavailable";
            Console.WriteLine($"Could not fetch E9K leaderboard API for admin-list-members: {ex.Message}");
        }
    }

    var summary =
        $"Server members: `{members.Count}` | Registered: `{registeredMembers.Count}` | Not registered: `{unregisteredMembers.Count}`";
    if(orphanedRegistrations.Count > 0) {
        summary += $" | Registered but not in server cache: `{orphanedRegistrations.Count}`";
    }
    summary += egg9000Status;

    var embeds = BuildMemberRegistrationEmbeds(
        guild,
        registeredMembers,
        unregisteredMembers,
        orphanedRegistrations,
        registeredByUser,
        egg9000Members);

    await SendPrivateEmbedReportAsync(command, summary, embeds);
}

async Task HandleAdminE9kCompareAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can compare E9K members.", ephemeral: true);
        return;
    }

    await command.DeferAsync(ephemeral: true);

    if(!egg9000Client.IsConfigured) {
        await command.FollowupAsync("EGG9000 API access is not configured. Set `EGG9000_API_KEY` for Plotty, then restart the bot.", ephemeral: true);
        return;
    }

    var guild = client.GetGuild(command.GuildId!.Value);
    if(guild is null) {
        await command.FollowupAsync("I could not read this server's member list.", ephemeral: true);
        return;
    }

    try {
        await guild.DownloadUsersAsync();
    } catch(Exception ex) {
        Console.WriteLine($"Could not refresh guild users for admin-e9k-compare: {ex.Message}");
    }

    IReadOnlyList<Egg9000LeaderboardItem> egg9000Members;
    try {
        egg9000Members = await egg9000Client.GetLeaderboardAsync();
    } catch(Exception ex) {
        Console.WriteLine($"Could not fetch E9K leaderboard API for admin-e9k-compare: {ex.Message}");
        await command.FollowupAsync("I could not reach the EGG9000 roster API right now.", ephemeral: true);
        return;
    }

    if(egg9000Members.Count == 0) {
        await command.FollowupAsync("The EGG9000 roster API returned no members for this key.", ephemeral: true);
        return;
    }

    var discordMembers = guild.Users
        .Where(u => !u.IsBot)
        .OrderBy(DiscordAccountSortName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(u => u.Id)
        .ToList();
    var discordMemberIds = discordMembers.Select(m => m.Id).ToHashSet();
    var egg9000ByDiscordId = egg9000Members
        .Where(m => m.DiscordId != 0)
        .GroupBy(m => m.DiscordId)
        .ToDictionary(g => g.Key, g => g.ToList());

    var inBoth = discordMembers
        .Where(m => egg9000ByDiscordId.ContainsKey(m.Id))
        .ToList();
    var e9kOnly = egg9000ByDiscordId
        .Where(kvp => !discordMemberIds.Contains(kvp.Key))
        .OrderBy(kvp => E9kSortName(kvp.Value), StringComparer.OrdinalIgnoreCase)
        .ThenBy(kvp => kvp.Key)
        .ToList();
    var discordOnly = discordMembers
        .Where(m => !egg9000ByDiscordId.ContainsKey(m.Id))
        .ToList();

    var summary = $"Discord members: `{discordMembers.Count}` | E9K guild-tag members: `{egg9000ByDiscordId.Count}` | In both: `{inBoth.Count}` | E9K only: `{e9kOnly.Count}` | Discord only: `{discordOnly.Count}`";
    var embeds = BuildE9kCompareEmbeds(guild, inBoth, e9kOnly, discordOnly, egg9000ByDiscordId);
    await SendPrivateEmbedReportAsync(command, summary, embeds);
}

async Task HandleAdminPingUnregisteredAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can ping unregistered members.", ephemeral: true);
        return;
    }

    var message = GetString(command, "message").Trim();
    if(string.IsNullOrWhiteSpace(message)) {
        await command.RespondAsync("Give Plotty a message to send with the pings.", ephemeral: true);
        return;
    }

    if(message.Length > 1200) {
        await command.RespondAsync("Please keep the custom message under 1200 characters so the pings fit in one Discord message.", ephemeral: true);
        return;
    }

    await command.DeferAsync(ephemeral: true);

    var guild = client.GetGuild(command.GuildId!.Value);
    if(guild is null) {
        await command.FollowupAsync("I could not read this server's member list.", ephemeral: true);
        return;
    }

    var unregisteredMembers = await GetUnregisteredHumanMembersAsync(guild, "admin-ping-unregistered");
    if(unregisteredMembers.Count == 0) {
        await command.FollowupAsync("Everyone in this server is registered with Plotty.", ephemeral: true);
        return;
    }

    var mentionLines = BuildMentionChunks(message, unregisteredMembers.Select(u => u.Id), maxLength: 2000);
    if(mentionLines.Count != 1) {
        await command.FollowupAsync(
            $"That would require `{mentionLines.Count}` Discord messages. Shorten the custom message or ping fewer unregistered members so Plotty can keep it to one message.",
            ephemeral: true);
        return;
    }

    await command.Channel.SendMessageAsync(
        mentionLines[0],
        allowedMentions: new AllowedMentions { UserIds = unregisteredMembers.Select(u => u.Id).ToList() });
    await command.FollowupAsync($"Pinged `{unregisteredMembers.Count}` unregistered member(s).", ephemeral: true);
}

async Task SendPrivateEmbedReportAsync(SocketSlashCommand command, string summary, IReadOnlyList<Embed> embeds) {
    if(embeds.Count == 0) {
        await command.FollowupAsync(summary, ephemeral: true);
        return;
    }

    await command.FollowupAsync(text: summary, embed: embeds[0], ephemeral: true);
    foreach(var embed in embeds.Skip(1)) {
        await command.FollowupAsync(embed: embed, ephemeral: true);
    }
}

async Task HandleAdminHealthAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can use admin health.", ephemeral: true);
        return;
    }

    var snapshots = monitorHealth.GetSnapshots(command.GuildId!.Value);
    var builder = new EmbedBuilder()
        .WithTitle("Plotty Monitor Health")
        .WithColor(snapshots.Any(s => s.FailureCount > 0) ? Color.Orange : Color.Green)
        .WithCurrentTimestamp();

    if(snapshots.Count == 0) {
        builder.WithDescription("No monitor check-ins have been recorded yet. Give me a minute after startup.");
    } else {
        foreach(var snapshot in snapshots) {
            var lines = new List<string>();
            lines.Add(snapshot.LastSuccessAt is null
                ? "**Last success:** never"
                : $"**Last success:** <t:{snapshot.LastSuccessAt.Value.ToUnixTimeSeconds()}:R>");
            if(snapshot.LastFailureAt is not null) {
                lines.Add($"**Last failure:** <t:{snapshot.LastFailureAt.Value.ToUnixTimeSeconds()}:R>");
                lines.Add($"**Failures:** {snapshot.FailureCount}");
                if(!string.IsNullOrWhiteSpace(snapshot.LastError)) {
                    lines.Add($"**Last error:** {TrimDiscordMessage(snapshot.LastError, 250)}");
                }
            } else {
                lines.Add("**Failures:** 0");
            }

            builder.AddField(snapshot.Name, string.Join("\n", lines), inline: false);
        }
    }

    await command.RespondAsync(embed: builder.Build(), ephemeral: true);
}

async Task MonitorMissingCoopJoinsAsync(ulong guildId) {
    await Task.Delay(TimeSpan.FromMinutes(2));
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
    while(true) {
        try {
            await CheckMissingCoopJoinsAsync(guildId);
            monitorHealth.ReportSuccess("missing-coop-joins", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("missing-coop-joins", guildId, ex);
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
            monitorHealth.ReportSuccess("ship-returns", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("ship-returns", guildId, ex);
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

    var reportChannel = FindNaughtyListChannel(guild);
    if(reportChannel is null) {
        Console.WriteLine("Missing coop join monitor could not find the naughty-list text channel.");
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

    var snapshots = await SelectWithConcurrencyAsync(
        accounts,
        MaxEggIncAccountConcurrency,
        async account => BuildMissingJoinSnapshot(account, await eggClient.GetBackupAsync(account.Eid)));

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
        var posted = await TryPostReportThreadAsync(
            reportChannel,
            $"Missing joins - {contractId}",
            embed,
            "naughty-list");
        if(!posted) {
            continue;
        }

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

async Task MonitorFirstCoopAwardsAsync(ulong guildId) {
    try {
        await CheckFirstCoopAwardsAsync(guildId);
        monitorHealth.ReportSuccess("first-coop-awards", guildId);
    } catch(Exception ex) {
        monitorHealth.ReportFailure("first-coop-awards", guildId, ex);
        Console.WriteLine($"First coop award monitor failed: {ex}");
    }

    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
    while(await timer.WaitForNextTickAsync()) {
        try {
            await CheckFirstCoopAwardsAsync(guildId);
            monitorHealth.ReportSuccess("first-coop-awards", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("first-coop-awards", guildId, ex);
            Console.WriteLine($"First coop award monitor failed: {ex}");
        }
    }
}

async Task MonitorWeeklyTokenLeaderboardAsync(ulong guildId) {
    try {
        await CheckWeeklyTokenLeaderboardPostAsync(guildId);
        monitorHealth.ReportSuccess("weekly-token-leaderboard", guildId);
    } catch(Exception ex) {
        monitorHealth.ReportFailure("weekly-token-leaderboard", guildId, ex);
        Console.WriteLine($"Weekly token leaderboard monitor failed: {ex}");
    }

    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while(await timer.WaitForNextTickAsync()) {
        try {
            await CheckWeeklyTokenLeaderboardPostAsync(guildId);
            monitorHealth.ReportSuccess("weekly-token-leaderboard", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("weekly-token-leaderboard", guildId, ex);
            Console.WriteLine($"Weekly token leaderboard monitor failed: {ex}");
        }
    }
}

async Task CheckWeeklyTokenLeaderboardPostAsync(ulong guildId) {
    var zone = WeeklyTokenLeaderboard.MountainTimeZone();
    var now = DateTimeOffset.UtcNow;
    var localNow = TimeZoneInfo.ConvertTime(now, zone);
    if(localNow.DayOfWeek != DayOfWeek.Monday || localNow.Hour != 10) {
        return;
    }

    var guild = client.GetGuild(guildId);
    var generalChannel = guild is null ? null : FindGeneralChannel(guild);
    if(guild is null || generalChannel is null) {
        Console.WriteLine("Weekly token leaderboard monitor could not find the guild or general text channel.");
        return;
    }

    var currentWeek = WeeklyTokenLeaderboard.CurrentWeek(now, zone);
    var weekStart = currentWeek.Start.AddDays(-7);
    var weekEnd = currentWeek.Start;
    var weekKey = WeeklyTokenLeaderboard.WeekKey(weekStart, zone);
    if(await dataStore.HasWeeklyTokenLeaderboardPostAsync(guildId, weekKey)) {
        return;
    }

    var entries = await BuildTokenLeaderboardAsync(guildId, weekStart, weekEnd);
    var embed = BuildTokenLeaderboardEmbed(guild, entries, weekStart, weekEnd, zone, completedWeek: true);
    await generalChannel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
    await dataStore.RecordWeeklyTokenLeaderboardPostAsync(
        new WeeklyTokenLeaderboardPost(guildId, weekKey, DateTimeOffset.UtcNow));
}

async Task HandleTokenLeaderboardAsync(SocketSlashCommand command) {
    await command.DeferAsync();
    var zone = WeeklyTokenLeaderboard.MountainTimeZone();
    var week = WeeklyTokenLeaderboard.CurrentWeek(DateTimeOffset.UtcNow, zone);
    var entries = await BuildTokenLeaderboardAsync(command.GuildId!.Value, week.Start, week.End);
    var guild = client.GetGuild(command.GuildId.Value);
    if(guild is null) {
        await command.FollowupAsync("I could not find this server.");
        return;
    }

    await command.FollowupAsync(
        embed: BuildTokenLeaderboardEmbed(guild, entries, week.Start, week.End, zone, completedWeek: false),
        allowedMentions: AllowedMentions.None);
}

async Task<IReadOnlyList<TokenLeaderboardEntry>> BuildTokenLeaderboardAsync(
    ulong guildId,
    DateTimeOffset weekStart,
    DateTimeOffset weekEnd) {
    var accounts = await dataStore.GetRegisteredEidsAsync(guildId);
    var backups = await GetAccountBackupsAsync(accounts);
    return backups
        .Select(item => {
            var result = WeeklyTokenLeaderboard.CountTokens(item.Backup, weekStart, weekEnd);
            return new TokenLeaderboardEntry(
                item.Account.DiscordUserId,
                AccountDisplayName(item.Account),
                result.TokensSent,
                result.ContractCount);
        })
        .OrderByDescending(entry => entry.TokensSent)
        .ThenBy(entry => entry.PlayerName, StringComparer.OrdinalIgnoreCase)
        .Take(10)
        .ToList();
}

Embed BuildTokenLeaderboardEmbed(
    SocketGuild guild,
    IReadOnlyList<TokenLeaderboardEntry> entries,
    DateTimeOffset weekStart,
    DateTimeOffset weekEnd,
    TimeZoneInfo zone,
    bool completedWeek) {
    var lines = entries
        .Select((entry, index) => {
            var member = guild.GetUser(entry.DiscordUserId);
            var name = !string.IsNullOrWhiteSpace(entry.PlayerName)
                ? entry.PlayerName
                : member?.DisplayName ?? member?.Username ?? $"Discord user {entry.DiscordUserId}";
            var contractText = entry.ContractCount == 1 ? "1 contract" : $"{entry.ContractCount} contracts";
            return $"**#{index + 1} {name}** - {entry.TokensSent:N0} sent ({contractText})";
        })
        .ToList();
    if(lines.Count == 0) {
        lines.Add("No token gifts were found for registered players this week.");
    }

    var localStart = TimeZoneInfo.ConvertTime(weekStart, zone);
    var localEnd = TimeZoneInfo.ConvertTime(weekEnd, zone);
    return new EmbedBuilder()
        .WithTitle("The Tokie Awards")
        .WithColor(Color.Gold)
        .WithDescription(string.Join("\n", lines))
        .AddField(
            completedWeek ? "Award week" : "Current week",
            $"{localStart:MMM d, yyyy h:mm tt} to {localEnd:MMM d, yyyy h:mm tt} Mountain Time")
        .WithFooter("Registered EIDs only. Counts tokens sent on contracts joined during the award week.")
        .WithCurrentTimestamp()
        .Build();
}

async Task CheckFirstCoopAwardsAsync(ulong guildId) {
    var guild = client.GetGuild(guildId);
    if(guild is null) {
        return;
    }

    var generalChannel = FindGeneralChannel(guild);
    if(generalChannel is null) {
        Console.WriteLine("First coop award monitor could not find the general text channel.");
        return;
    }

    var accounts = await dataStore.GetRegisteredEidsAsync(guildId);
    if(accounts.Count == 0) {
        return;
    }

    var now = DateTimeOffset.UtcNow;
    var recentContractIds = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.CoopAllowed)
        .Where(c => c.StartTime > 0)
        .Where(c => DateTimeOffset.FromUnixTimeSeconds((long)c.StartTime) >= now.AddDays(-3))
        .Select(c => c.Identifier)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(recentContractIds.Count == 0) {
        return;
    }

    var lookups = await GetAccountCoopLookupsAsync(accounts);
    var completedStatuses = lookups
        .SelectMany(item => item.Lookup.StatusLookups
            .Where(s => s.Status.AllGoalsAchieved)
            .Where(s => recentContractIds.Contains(s.ContractId))
            .Select(s => new FirstCoopCandidate(item.Account, s.ContractId, s.CoopCode, s.Status, s.AcceptedAt)))
        .GroupBy(s => (ContractId: s.ContractId.ToLowerInvariant(), CoopCode: s.CoopCode.ToLowerInvariant()))
        .Select(g => g.OrderByDescending(s => s.AcceptedAt).First())
        .ToList();
    if(completedStatuses.Count == 0) {
        return;
    }

    foreach(var winner in completedStatuses
        .GroupBy(s => s.ContractId, StringComparer.OrdinalIgnoreCase)
        .Select(g => g
            .OrderBy(s => Math.Max(0, s.Status.SecondsSinceAllGoalsAchieved))
            .ThenByDescending(s => s.Status.TotalAmount)
            .First())) {
        var awardKey = $"first-coop:{guildId}:{winner.ContractId.ToLowerInvariant()}";
        if(await dataStore.HasFirstCoopAwardAsync(awardKey)) {
            continue;
        }

        var embed = BuildFirstCoopAwardEmbed(winner.Status);
        await generalChannel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
        await dataStore.RecordFirstCoopAwardAsync(new FirstCoopAward(
            awardKey,
            guildId,
            winner.ContractId,
            winner.CoopCode,
            DateTimeOffset.UtcNow));
    }
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

Embed BuildFirstCoopAwardEmbed(ContractCoopStatusResponse status) {
    var contractName = string.IsNullOrWhiteSpace(status.ContractIdentifier)
        ? "A contract"
        : status.ContractIdentifier;
    var topContributors = status.Contributors
        .OrderByDescending(c => c.ContributionAmount)
        .Take(5)
        .Select((c, index) => {
            var name = string.IsNullOrWhiteSpace(c.UserName) ? "(unknown)" : c.UserName;
            return $"{index + 1}. **{name}** - {FormatEggs(c.ContributionAmount)} contributed";
        })
        .ToList();
    if(topContributors.Count == 0) {
        topContributors.Add("No contributor rows were available.");
    }

    var completedAgo = status.SecondsSinceAllGoalsAchieved > 0
        ? $"Completed about {FormatDuration(TimeSpan.FromSeconds(status.SecondsSinceAllGoalsAchieved))} ago."
        : "Completed moments ago.";

    return new EmbedBuilder()
        .WithTitle("First Co-op Finish Award")
        .WithColor(Color.Green)
        .WithDescription($"A registered co-op finished **{contractName}** first. Nicely done.")
        .AddField("Result", completedAgo, true)
        .AddField("Members", status.Contributors.Count.ToString("0"), true)
        .AddField("Total Eggs", FormatEggs(status.TotalAmount), true)
        .AddField("Top Contributors", string.Join("\n", topContributors), false)
        .WithFooter("Co-op name hidden for privacy.")
        .WithCurrentTimestamp()
        .Build();
}

async Task<bool> TryPostReportThreadAsync(SocketTextChannel reportChannel, string threadName, Embed embed, string channelLabel) {
    try {
        var thread = await reportChannel.CreateThreadAsync(
            TrimForDiscordThreadName(threadName),
            ThreadType.PublicThread,
            ThreadArchiveDuration.OneDay);
        await thread.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
        return true;
    } catch(Exception ex) {
        Console.WriteLine($"Could not create {channelLabel} report thread: {ex}");
        return false;
    }
}

static SocketTextChannel? FindPlottyReportsChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "plottyreports");

static SocketTextChannel? FindNaughtyListChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "naughtylist");

static SocketTextChannel? FindGeneralChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "general");

static SocketTextChannel? FindPlottyQuestionsChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "plottyquestions");

static SocketTextChannel? FindModLogChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "modlog");

async Task SendRegistrationWelcomeAsync(ulong guildId, IUser user) {
    var guild = client.GetGuild(guildId);
    var questionsChannel = guild is null ? null : FindPlottyQuestionsChannel(guild);
    if(questionsChannel is null) {
        return;
    }

    var displayName = user is SocketGuildUser guildUser ? guildUser.DisplayName : user.Username;
    var memory = await dataStore.RecordPlottyInteractionAsync(guildId, user.Id, "registration");
    await questionsChannel.SendMessageAsync(
        $"{displayName} {PlottyPersonality.RegistrationWelcome(memory)}",
        allowedMentions: AllowedMentions.None);
}

async Task HandleAddDemeritAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can add demerits.", ephemeral: true);
        return;
    }

    var member = ResolveGuildMemberOption(command, "member");
    if(member is null) {
        await command.RespondAsync("I can only add demerits to members in this server.", ephemeral: true);
        return;
    }

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

    var member = ResolveGuildMemberOption(command, "member");
    if(member is null) {
        await command.RespondAsync("I can only remove demerits from members in this server.", ephemeral: true);
        return;
    }

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

Task<IReadOnlyList<(RegisteredEggAccount Account, Backup? Backup)>> GetAccountBackupsAsync(
    IEnumerable<RegisteredEggAccount> accounts) =>
    SelectWithConcurrencyAsync(
        accounts.ToList(),
        MaxEggIncAccountConcurrency,
        async account => (account, await eggClient.GetBackupAsync(account.Eid)));

Task<IReadOnlyList<(RegisteredEggAccount Account, PlayerCoopLookupResult Lookup)>> GetAccountCoopLookupsAsync(
    IEnumerable<RegisteredEggAccount> accounts) =>
    SelectWithConcurrencyAsync(
        accounts.ToList(),
        MaxEggIncAccountConcurrency,
        async account => (account, await eggClient.GetPlayerCoopLookupAsync(account.Eid)));

async Task<(
    IReadOnlyDictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse> Statuses,
    int Failed,
    int SkippedOldContracts,
    int RecentContractCount)> GetRecentRegisteredStatusesAsync(IReadOnlyList<RegisteredEggAccount> accounts) {
    var now = DateTimeOffset.UtcNow;
    var recentContractIds = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.StartTime > 0)
        .Where(c => DateTimeOffset.FromUnixTimeSeconds((long)c.StartTime) >= now.AddDays(-3))
        .Where(c => c.ExpirationTime <= 0 || DateTimeOffset.FromUnixTimeSeconds((long)c.ExpirationTime) > now)
        .Select(c => c.Identifier)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(recentContractIds.Count == 0) {
        return (new Dictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse>(), 0, 0, 0);
    }

    var statuses = new Dictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse>();
    var failed = 0;
    var skippedOldContracts = 0;
    var lookups = await GetAccountCoopLookupsAsync(accounts);
    foreach(var item in lookups) {
        var lookup = item.Lookup;
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

    return (statuses, failed, skippedOldContracts, recentContractIds.Count);
}

async Task HandleEggsLaidAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    var embeds = new List<Embed>();
    foreach(var item in backups) {
        var account = item.Account;
        var backup = item.Backup;
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

    var lookups = await GetAccountCoopLookupsAsync(accounts.Take(10));
    var embeds = lookups
        .Select(item => {
            var backup = item.Lookup.Backup;
            var status = FindCurrentCoopStatus(backup, item.Lookup);
            var coopContext = CoopArtifactAnalyzer.Analyze(status, item.Account, backup?.UserName);
            return BuildContractArtifactsEmbed(backup, item.Account, coopContext);
        })
        .ToList();

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

    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    var embeds = new List<Embed>();
    var notificationCount = 0;
    foreach(var item in backups) {
        var account = item.Account;
        var backup = item.Backup;
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
    var validation = await eggClient.ValidateEggIdAsync(eid);
    if(!validation.IsValid) {
        await modal.FollowupAsync("Plotty could not validate that EID with Egg Inc. Please double-check it and try again.", ephemeral: true);
        return;
    }

    var eggName = string.IsNullOrWhiteSpace(validation.EggName) ? null : validation.EggName;
    await dataStore.SaveRegisteredEidAsync(modal.GuildId.Value, modal.User.Id, eid, eggName);

    var accounts = await dataStore.GetRegisteredAccountsAsync(modal.GuildId.Value, modal.User.Id);
    var suffix = SecureText.Sha256(eid)[..8];
    var parseNote = validation.BackupParseLimited
        ? " Egg Inc returned one malformed optional backup field, so I saved the EID without an Egg Inc display name for now."
        : "";
    await modal.FollowupAsync(
        $"Saved your EID securely and tied it to your Discord name. You now have `{accounts.Count}` EID account(s) registered. Stored hash ending: `{suffix}`.{parseNote}",
        ephemeral: true);

    await SendRegistrationWelcomeAsync(modal.GuildId.Value, modal.User);
}

async Task HandleBeerPlottyAsync(SocketSlashCommand command) {
    var drink = GetString(command, "drink");
    var botBuysBack = Random.Shared.Next(5) == 0;
    var isPlottyAdmin = command.User is SocketGuildUser beerUser && IsPlottyAdmin(beerUser);
    var bypassCooldown = isPlottyAdmin || !IsCooldownLimitedBeverage(drink);
    var result = await dataStore.TryAddPlottyBeerAsync(command.GuildId!.Value, command.User.Id, botBuysBack, bypassCooldown);
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
        ? $"{plottyMention} accepts the {drink} from {command.User.Mention}. {PlottyPersonality.BeerGiftResponse(await dataStore.RecordPlottyInteractionAsync(command.GuildId!.Value, command.User.Id, "beer_bot_buyback"))}\n\nYou earned a spot on the Beverage Leaderboard. Total Plotty-bought beverages: `{stats.BeersBoughtByBot}`."
        : $"{plottyMention} accepts the {drink} from {command.User.Mention}. {PlottyPersonality.BeerThanksResponse(await dataStore.RecordPlottyInteractionAsync(command.GuildId!.Value, command.User.Id, "beer_plotty"))}\n\nBeverages donated to Plotty: `{stats.BeersGivenToBot}`.";

    var embed = new EmbedBuilder()
        .WithTitle(botBuysBack ? "Plotty Bought A Round" : $"{displayName} bought Plotty a beverage")
        .WithColor(botBuysBack ? Color.Green : Color.Orange)
        .WithDescription(response)
        .WithFooter($"{displayName} has given {stats.BeersGivenToBot} beverage(s) and received {stats.BeersBoughtByBot}.")
        .WithCurrentTimestamp()
        .Build();

    await command.RespondAsync(embed: embed);
}

async Task HandleBeerUserAsync(SocketSlashCommand command) {
    var recipient = (SocketGuildUser)command.Data.Options.First(o => o.Name == "member").Value;
    var drink = GetString(command, "drink");
    var pingRecipient = GetBool(command, "ping");
    var giver = (SocketGuildUser)command.User;
    var isPlottyAdmin = IsPlottyAdmin(giver);
    var bypassCooldown = isPlottyAdmin || !IsCooldownLimitedBeverage(drink);

    if(recipient.IsBot) {
        await command.RespondAsync("Plotty appreciates the gesture, but `/beverage-user` is for guild members. Use `/beverage-plotty` for Plotty.", ephemeral: true);
        return;
    }

    if(recipient.Id == giver.Id && !isPlottyAdmin) {
        await command.RespondAsync("Plotty admires the confidence, but members cannot gift themselves a beverage.", ephemeral: true);
        return;
    }

    var result = await dataStore.TryGiftBeerAsync(command.GuildId!.Value, giver.Id, recipient.Id, bypassCooldown);
    if(!result.Accepted) {
        await command.RespondAsync(
            $"{giver.Mention} Plotty says the town has standards. You can gift {recipient.Mention} another beverage in `{FormatDuration(result.RetryAfter ?? TimeSpan.FromDays(1))}`.",
            ephemeral: true);
        return;
    }

    var stats = result.Stats;
    var townTitle = TownTitle(stats.BeersReceivedFromMembers);
    var milestone = TownMilestoneMessage(stats.BeersReceivedFromMembers, recipient.Mention);
    var description = $"{giver.Mention} bought {recipient.Mention} a {drink}.\n\n" +
                      $"{recipient.DisplayName} has received `{stats.BeersReceivedFromMembers}` member-gifted beverage(s)." +
                      (string.IsNullOrWhiteSpace(townTitle) ? "" : $"\nTown status: **{townTitle}**") +
                      (string.IsNullOrWhiteSpace(milestone) ? "" : $"\n\n{milestone}");

    var embed = new EmbedBuilder()
        .WithTitle("Beverage Gifted")
        .WithColor(Color.Orange)
        .WithDescription(description)
        .WithFooter("Beer and wine are limited to once per hour. Other beverages are unlimited.")
        .WithCurrentTimestamp()
        .Build();

    await command.RespondAsync(
        text: pingRecipient ? recipient.Mention : null,
        embed: embed,
        allowedMentions: pingRecipient
            ? new AllowedMentions { UserIds = [recipient.Id] }
            : AllowedMentions.None);
}

async Task HandleBeerLeaderAsync(SocketSlashCommand command) {
    var leaders = await dataStore.GetBeerLeaderboardAsync(command.GuildId!.Value);
    if(leaders.Count == 0 || leaders.All(l => l.BeersBoughtByBot == 0 && l.BeersReceivedFromMembers == 0)) {
        await command.RespondAsync("The Beverage Leaderboard is empty. Run `/beverage-plotty` or gift someone a beverage with `/beverage-user`.", ephemeral: true);
        return;
    }

    var guild = client.GetGuild(command.GuildId.Value);
    var plottyLines = leaders
        .Where(l => l.BeersBoughtByBot > 0)
        .Take(10)
        .Select((entry, index) => {
            var user = guild?.GetUser(entry.DiscordUserId);
            var name = user?.DisplayName ?? $"User {entry.DiscordUserId}";
            return $"`#{index + 1}` **{name}** - {entry.BeersBoughtByBot} Plotty beverage(s), {entry.BeersGivenToBot} donated";
        })
        .ToList();
    var townLines = leaders
        .Where(l => l.BeersReceivedFromMembers > 0)
        .OrderByDescending(l => l.BeersReceivedFromMembers)
        .ThenBy(l => l.FirstBeerAt)
        .Take(10)
        .Select((entry, index) => {
            var user = guild?.GetUser(entry.DiscordUserId);
            var name = user?.DisplayName ?? $"User {entry.DiscordUserId}";
            var title = TownTitle(entry.BeersReceivedFromMembers);
            return $"`#{index + 1}` **{name}** - {entry.BeersReceivedFromMembers} gifted beverage(s)" +
                   (string.IsNullOrWhiteSpace(title) ? "" : $" - **{title}**");
        })
        .ToList();

    var builder = new EmbedBuilder()
        .WithTitle("Beverage Leaderboard")
        .WithColor(Color.Gold)
        .WithFooter("Town titles: 10 Local, 50 Patron, 100 Basically live here.")
        .WithCurrentTimestamp();

    if(plottyLines.Count > 0) {
        builder.AddField("Plotty Bought Back", string.Join("\n", plottyLines));
    }

    if(townLines.Count > 0) {
        builder.AddField("Town Regulars", string.Join("\n", townLines));
    }

    await command.RespondAsync(embed: builder.Build(), ephemeral: true);
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

    var targetChannel = ResolveCachedInteractionMessageChannel(command);
    if(targetChannel is null) {
        await command.RespondAsync("Plotty could not access this channel as a normal bot message. Check Plotty's channel permissions and try again.", ephemeral: true);
        return;
    }

    var content = TrimDiscordMessage(message, 2000);
    await command.RespondAsync("Plotty has spoken.", ephemeral: true);
    await targetChannel.SendMessageAsync(
        content,
        allowedMentions: new AllowedMentions(AllowedMentionTypes.Users | AllowedMentionTypes.Roles));
    await LogPlottySpeakAsync(command, staffUser, content);
}

async Task LogPlottySpeakAsync(SocketSlashCommand command, SocketGuildUser staffUser, string message) {
    try {
        var guild = client.GetGuild(command.GuildId!.Value);
        var modLog = guild is null ? null : FindModLogChannel(guild);
        if(modLog is null) {
            return;
        }

        var sourceChannel = command.Channel is SocketGuildChannel channel
            ? $"<#{channel.Id}>"
            : command.ChannelId is ulong channelId
                ? $"<#{channelId}>"
                : "Unknown channel";
        var embed = new EmbedBuilder()
            .WithTitle("Plotty Speak Used")
            .WithColor(Color.DarkGrey)
            .AddField("Used by", $"{staffUser.Mention} (`{staffUser.Username}`)", true)
            .AddField("Channel", sourceChannel, true)
            .AddField("Message", TrimDiscordMessage(message, 1000))
            .WithCurrentTimestamp()
            .Build();

        await modLog.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
    } catch(Exception ex) {
        Console.WriteLine($"Could not write Plotty speak audit log: {ex.GetType().Name}: {ex.Message}");
    }
}

IMessageChannel? ResolveCachedInteractionMessageChannel(SocketSlashCommand command) {
    if(command.Channel is IMessageChannel channel) {
        return channel;
    }

    if(command.ChannelId is not ulong channelId) {
        return null;
    }

    var guild = command.GuildId is ulong guildId ? client.GetGuild(guildId) : null;
    if(guild?.GetChannel(channelId) is IMessageChannel guildChannel) {
        return guildChannel;
    }

    return client.GetChannel(channelId) as IMessageChannel;
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
        return $"**{name}** - {FormatEggs(p.ContributionRate * 3600)}/hr, {FormatEggs(p.ContributionAmount)} contributed{flag}";
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

async Task<Embed> BuildPlayerEmbedAsync(RegisteredEggAccount account, string displayName, PlayerCoopLookupResult lookup) {
    var backup = lookup.Backup;
    var builder = new EmbedBuilder()
        .WithTitle($"Player - {displayName} ({AccountDisplayName(account)})")
        .WithColor(Color.Blue)
        .WithCurrentTimestamp()
        .AddField("Registration Date", account.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

    if(backup?.Contracts is null) {
        builder.WithDescription("Plotty could not pull this player's Egg Inc contract history right now.");
        return builder.Build();
    }

    var contracts = BuildPlayerRateCandidates(backup);

    if(contracts.Count == 0) {
        builder.AddField("Average Contribution", "No recent contract history found.");
        builder.AddField("Last 3 Rates", "No recent contract rates found.");
        return builder.Build();
    }

    var rates = new List<PlayerContractRate>();
    foreach(var statusLookup in lookup.StatusLookups.OrderByDescending(s => s.AcceptedAt)) {
        var contributor = statusLookup.Status.Contributors.FirstOrDefault(c => IsPlayerContributor(c, account, backup.UserName));
        if(contributor is not null) {
            rates.Add(new PlayerContractRate(statusLookup.ContractId, contributor.ContributionRate * 3600, contributor.ContributionAmount));
            if(rates.Count == 3) {
                break;
            }
        }
    }

    foreach(var contract in contracts) {
        if(rates.Any(r => string.Equals(r.ContractId, contract.ContractId, StringComparison.OrdinalIgnoreCase))) {
            continue;
        }

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

static IReadOnlyList<PlayerContractCandidate> BuildPlayerRateCandidates(Backup backup) {
    if(backup.Contracts is null) {
        return [];
    }

    var localCandidates = backup.Contracts.Contracts
        .Concat(backup.Contracts.Archive)
        .Where(c => !c.Cancelled)
        .Select(c => new PlayerContractCandidate(GetLocalContractId(c), c.CoopIdentifier, c.TimeAccepted));

    var embeddedCandidates = backup.Contracts.CurrentCoopStatuses
        .Where(s => !string.IsNullOrWhiteSpace(s.ContractIdentifier) && !string.IsNullOrWhiteSpace(s.CoopIdentifier))
        .Select(s => new PlayerContractCandidate(s.ContractIdentifier, s.CoopIdentifier, 0));

    return localCandidates
        .Concat(embeddedCandidates)
        .Where(c => !string.IsNullOrWhiteSpace(c.ContractId) && !string.IsNullOrWhiteSpace(c.CoopCode))
        .GroupBy(c => (ContractId: c.ContractId.ToLowerInvariant(), CoopCode: c.CoopCode.ToLowerInvariant()))
        .Select(g => g.OrderByDescending(c => c.AcceptedAt).First())
        .OrderByDescending(c => c.AcceptedAt)
        .ToList();
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

    var statusResult = await GetRecentRegisteredStatusesAsync(accounts);
    if(statusResult.RecentContractCount == 0) {
        return new DashboardResult("I could not find any active contracts released in the past 3 days.", [], []);
    }

    if(statusResult.Statuses.Count == 0) {
        return new DashboardResult($"I checked `{accounts.Count}` registered EID(s), but none had active dashboard data for contracts released in the past 3 days.", [], []);
    }

    var rows = BuildDashboardRows(statusResult.Statuses.Values, registeredByEggId, registeredByName);
    var embeds = rows
        .GroupBy(r => r.ContractId, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key)
        .Take(10)
        .Select(BuildDashboardContractEmbed)
        .ToList();

    var attention = rows.Count(r => r.Category != DashboardCategory.Healthy);
    var message = statusResult.Failed > 0
        ? $"Dashboard for contracts released in the past 3 days across `{accounts.Count}` registered EID(s). `{statusResult.Failed}` EID(s) did not return active co-op data. `{attention}` player issue(s) need attention."
        : $"Dashboard for contracts released in the past 3 days across `{accounts.Count}` registered EID(s). `{attention}` player issue(s) need attention.";
    if(statusResult.SkippedOldContracts > 0) {
        message += $" Skipped `{statusResult.SkippedOldContracts}` older active co-op lookup(s).";
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

SocketGuildUser? ResolveGuildMemberOption(SocketSlashCommand command, string optionName) {
    var user = command.Data.Options.FirstOrDefault(o => o.Name == optionName)?.Value as IUser;
    if(user is null || command.GuildId is null) {
        return null;
    }

    return client.GetGuild(command.GuildId.Value)?.GetUser(user.Id);
}

static IReadOnlyList<Embed> BuildMemberRegistrationEmbeds(
    SocketGuild guild,
    IReadOnlyList<SocketGuildUser> registeredMembers,
    IReadOnlyList<SocketGuildUser> unregisteredMembers,
    IReadOnlyList<KeyValuePair<ulong, List<RegisteredEggAccount>>> orphanedRegistrations,
    IReadOnlyDictionary<ulong, List<RegisteredEggAccount>> registeredByUser,
    IReadOnlyList<Egg9000LeaderboardItem>? egg9000Members = null) {
    var embeds = new List<Embed>();
    AddMemberSection(
        embeds,
        "Registered Members",
        registeredMembers.Select(member => FormatRegisteredMemberLine(member, registeredByUser[member.Id])),
        Color.Green);
    AddMemberSection(
        embeds,
        "Not Registered",
        unregisteredMembers.Select(FormatUnregisteredMemberLine),
        Color.Orange);

    if(orphanedRegistrations.Count > 0) {
        AddMemberSection(
            embeds,
            "Registered But Not In Server Cache",
            orphanedRegistrations.Select(kvp => FormatOrphanedRegistrationLine(kvp.Key, kvp.Value)),
            Color.DarkGrey);
    }

    if(egg9000Members is { Count: > 0 }) {
        var egg9000ByDiscordId = egg9000Members
            .Where(m => m.DiscordId != 0)
            .GroupBy(m => m.DiscordId)
            .ToDictionary(g => g.Key, g => g.ToList());
        AddMemberSection(
            embeds,
            "E9K Roster Not Registered With Plotty",
            egg9000ByDiscordId
                .Where(kvp => !registeredByUser.ContainsKey(kvp.Key))
                .OrderBy(kvp => DiscordAccountSortNameOrEmpty(guild.GetUser(kvp.Key)), StringComparer.OrdinalIgnoreCase)
                .ThenBy(kvp => kvp.Key)
                .Select(kvp => FormatEgg9000MemberLine(guild.GetUser(kvp.Key), kvp.Key, kvp.Value)),
            Color.Blue);
        AddMemberSection(
            embeds,
            "Plotty Registered Not Found In E9K Roster",
            registeredMembers
                .Where(member => !egg9000ByDiscordId.ContainsKey(member.Id))
                .OrderBy(DiscordAccountSortName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.Id)
                .Select(member => FormatRegisteredMemberLine(member, registeredByUser[member.Id])),
            Color.Purple);
    }

    return embeds.Count == 0
        ? [new EmbedBuilder()
            .WithTitle("Member Registration List")
            .WithDescription("No members found.")
            .WithColor(Color.LightGrey)
            .WithCurrentTimestamp()
            .Build()]
        : embeds;

    static void AddMemberSection(
        List<Embed> target,
        string title,
        IEnumerable<string> sourceLines,
        Color color) {
        var lines = sourceLines.ToList();
        if(lines.Count == 0) {
            lines.Add("None");
        }

        var page = 1;
        var current = new List<string>();
        var currentLength = 0;
        foreach(var line in lines) {
            var nextLength = currentLength + line.Length + 1;
            if(current.Count > 0 && nextLength > 3200) {
                target.Add(BuildMemberSectionEmbed(title, page++, current, color));
                current = [];
                currentLength = 0;
            }

            current.Add(line);
            currentLength += line.Length + 1;
        }

        if(current.Count > 0) {
            target.Add(BuildMemberSectionEmbed(title, page, current, color));
        }
    }

    static Embed BuildMemberSectionEmbed(string title, int page, IReadOnlyList<string> lines, Color color) =>
        new EmbedBuilder()
            .WithTitle(page == 1 ? title : $"{title} ({page})")
            .WithColor(color)
            .WithDescription(string.Join("\n", lines))
            .WithFooter("EIDs are not shown in this report.")
            .WithCurrentTimestamp()
            .Build();

    static string FormatRegisteredMemberLine(SocketGuildUser member, IReadOnlyList<RegisteredEggAccount> accounts) {
        var accountNames = accounts
            .Select(a => string.IsNullOrWhiteSpace(a.EggName) ? "unnamed Egg Inc account" : a.EggName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var extra = accounts.Count > accountNames.Count ? $", +{accounts.Count - accountNames.Count} more" : "";
        return $"- {MemberLabel(member)} - `{accounts.Count}` account(s): {string.Join(", ", accountNames)}{extra}";
    }

    static string FormatUnregisteredMemberLine(SocketGuildUser member) =>
        $"- {MemberLabel(member)}";

    static string FormatOrphanedRegistrationLine(ulong userId, IReadOnlyList<RegisteredEggAccount> accounts) =>
        $"- User ID `{userId}` - `{accounts.Count}` registered account(s)";

    static string FormatEgg9000MemberLine(SocketGuildUser? member, ulong userId, IReadOnlyList<Egg9000LeaderboardItem> accounts) {
        var names = accounts
            .Select(a => string.IsNullOrWhiteSpace(a.EggIncName) ? "unnamed Egg Inc account" : a.EggIncName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var extra = accounts.Count > names.Count ? $", +{accounts.Count - names.Count} more" : "";
        var label = member is null ? $"User ID `{userId}`" : MemberLabel(member);
        return $"- {label} - `{accounts.Count}` E9K account(s): {string.Join(", ", names)}{extra}";
    }

    static string MemberLabel(SocketGuildUser member) {
        var username = string.IsNullOrWhiteSpace(member.GlobalName)
            ? member.Username
            : member.GlobalName;
        return $"{member.DisplayName} (`{username}`)";
    }
}

static IReadOnlyList<Embed> BuildE9kCompareEmbeds(
    SocketGuild guild,
    IReadOnlyList<SocketGuildUser> inBoth,
    IReadOnlyList<KeyValuePair<ulong, List<Egg9000LeaderboardItem>>> e9kOnly,
    IReadOnlyList<SocketGuildUser> discordOnly,
    IReadOnlyDictionary<ulong, List<Egg9000LeaderboardItem>> egg9000ByDiscordId) {
    var embeds = new List<Embed>();
    AddSection(
        embeds,
        "In Discord And EGG9000",
        inBoth.Select(member => FormatE9kDiscordMemberLine(member, egg9000ByDiscordId[member.Id])),
        Color.Green);
    AddSection(
        embeds,
        "In EGG9000 But Not Discord",
        e9kOnly.Select(kvp => FormatE9kOnlyLine(kvp.Key, kvp.Value)),
        Color.Orange);
    AddSection(
        embeds,
        "In Discord But Not EGG9000",
        discordOnly.Select(member => $"- {MemberLabel(member)}"),
        Color.Red);

    return embeds;

    static void AddSection(List<Embed> target, string title, IEnumerable<string> sourceLines, Color color) {
        var lines = sourceLines.ToList();
        if(lines.Count == 0) {
            lines.Add("None");
        }

        var page = 1;
        var current = new List<string>();
        var currentLength = 0;
        foreach(var line in lines) {
            var nextLength = currentLength + line.Length + 1;
            if(current.Count > 0 && nextLength > 3200) {
                target.Add(BuildSectionEmbed(title, page++, current, color));
                current = [];
                currentLength = 0;
            }

            current.Add(line);
            currentLength += line.Length + 1;
        }

        if(current.Count > 0) {
            target.Add(BuildSectionEmbed(title, page, current, color));
        }
    }

    static Embed BuildSectionEmbed(string title, int page, IReadOnlyList<string> lines, Color color) =>
        new EmbedBuilder()
            .WithTitle(page == 1 ? title : $"{title} ({page})")
            .WithColor(color)
            .WithDescription(string.Join("\n", lines))
            .WithFooter("EGG9000 comparison uses Discord ID matching.")
            .WithCurrentTimestamp()
            .Build();

    static string FormatE9kDiscordMemberLine(SocketGuildUser member, IReadOnlyList<Egg9000LeaderboardItem> accounts) =>
        $"- {MemberLabel(member)} - {FormatE9kAccounts(accounts)}";

    static string FormatE9kOnlyLine(ulong discordId, IReadOnlyList<Egg9000LeaderboardItem> accounts) =>
        $"- User ID `{discordId}` - {FormatE9kAccounts(accounts)}";

    static string FormatE9kAccounts(IReadOnlyList<Egg9000LeaderboardItem> accounts) {
        var names = accounts
            .Select(a => string.IsNullOrWhiteSpace(a.EggIncName) ? "unnamed Egg Inc account" : a.EggIncName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var extra = accounts.Count > names.Count ? $", +{accounts.Count - names.Count} more" : "";
        return $"`{accounts.Count}` E9K account(s): {string.Join(", ", names)}{extra}";
    }

    static string MemberLabel(SocketGuildUser member) {
        var username = string.IsNullOrWhiteSpace(member.GlobalName)
            ? member.Username
            : member.GlobalName;
        return $"{member.DisplayName} (`{username}`)";
    }
}

async Task<IReadOnlyList<SocketGuildUser>> GetUnregisteredHumanMembersAsync(SocketGuild guild, string logContext) {
    try {
        await guild.DownloadUsersAsync();
    } catch(Exception ex) {
        Console.WriteLine($"Could not refresh guild users for {logContext}: {ex.Message}");
    }

    var registeredAccounts = await dataStore.GetRegisteredEidsAsync(guild.Id);
    var registeredUserIds = registeredAccounts
        .Select(a => a.DiscordUserId)
        .ToHashSet();

    return guild.Users
        .Where(u => !u.IsBot)
        .Where(u => !registeredUserIds.Contains(u.Id))
        .OrderBy(DiscordAccountSortName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(u => u.Id)
        .ToList();
}

static IReadOnlyList<string> BuildMentionChunks(string message, IEnumerable<ulong> userIds, int maxLength) {
    var chunks = new List<string>();
    var current = message.Trim();
    foreach(var userId in userIds) {
        var mention = $"<@{userId}>";
        var candidate = $"{current} {mention}";
        if(candidate.Length > maxLength) {
            chunks.Add(current);
            current = mention;
            continue;
        }

        current = candidate;
    }

    if(!string.IsNullOrWhiteSpace(current)) {
        chunks.Add(current);
    }

    return chunks;
}

static string DiscordAccountSortName(SocketGuildUser member) =>
    string.IsNullOrWhiteSpace(member.GlobalName)
        ? member.Username
        : member.GlobalName;

static string DiscordAccountSortNameOrEmpty(SocketGuildUser? member) =>
    member is null
        ? ""
        : DiscordAccountSortName(member);

static string E9kSortName(IReadOnlyList<Egg9000LeaderboardItem> accounts) =>
    accounts
        .Select(a => string.IsNullOrWhiteSpace(a.DiscordName) ? a.EggIncName : a.DiscordName)
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "";

bool HasStaffRole(SocketGuildUser user) =>
    IsPlottyAdmin(user) ||
    user.Roles.Any(r => string.Equals(r.Name, "Staff", StringComparison.OrdinalIgnoreCase));

bool IsPlottyAdmin(SocketGuildUser user) =>
    plottyAdminUserIds.Contains(user.Id);

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

Embed BuildContractArtifactsEmbed(
    Backup? backup,
    RegisteredEggAccount account,
    CoopArtifactContext? coopContext) {
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
    var suggestions = BuildArtifactRecommendations(availableCandidates, stoneOptions, activeArtifacts, coopContext)
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

    builder.WithDescription("Plotty evaluated your current contract inventory against the latest co-op artifact and production reports available from Egg Inc.");
    builder.AddField("Current Contract Farm", string.Join("\n", contractLines));
    builder.AddField("Currently Equipped", string.Join("\n", equippedLines));
    builder.AddField("Co-op Artifact Support", BuildCoopArtifactSummary(coopContext));

    if(bestSuggestion is null || bestSuggestion.Set.Count == 0) {
        builder.AddField("Suggested Set", "No complete contract-focused artifact set could be built. Look for Tachyon Deflector, Quantum Metronome, Interstellar Compass, and Tachyon/Quantum stones.");
    } else {
        builder.AddField($"Best Set - {bestSuggestion.Label}", FormatArtifactSet(bestSuggestion.Set));
        var bestEvaluation = CoopArtifactAnalyzer.EvaluateSet(coopContext, activeArtifacts, bestSuggestion.Set);
        if(bestEvaluation.UsesLiveProduction) {
            builder.AddField(
                "Estimated Result",
                $"Player bottleneck: **{FormatEggs(Math.Min(bestEvaluation.PlayerLayingRate, bestEvaluation.PlayerShippingRate) * 3600)}/hr** ({FormatRatioChange(bestEvaluation.PlayerOutputRatio)})\n" +
                $"Total reporting co-op output: **{FormatRatioChange(bestEvaluation.CoopOutputRatio)}**");
        }

        var alternatives = suggestions
            .Where(s => !ReferenceEquals(s, bestSuggestion) && s.Set.Count > 0)
            .OrderByDescending(s => s.Score)
            .Take(3)
            .Select(s => $"**{s.Label}** - {FormatArtifactSetOneLine(s.Set, activeArtifacts, coopContext)}")
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

    var coopReportFooter = coopContext is null
        ? "Co-op artifact status unavailable."
        : $"Co-op artifact reports: {coopContext.ReportingMemberCount}/{coopContext.Members.Count}.";
    builder.WithFooter($"Data: Egg Inc co-op status and EGG9000 eiafx-data.json. {coopReportFooter} Recognized {availableCandidates.Select(c => c.Name).Distinct().Count()} artifact families and {stoneOptions.Count} loose laying/shipping stones.");
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

static ContractCoopStatusResponse? FindCurrentCoopStatus(
    Backup? backup,
    PlayerCoopLookupResult lookup) {
    if(backup is null) {
        return null;
    }

    var currentContractId = GetCurrentContractFarms(backup)
        .Select(snapshot => snapshot.Farm.ContractId)
        .FirstOrDefault();
    if(string.IsNullOrWhiteSpace(currentContractId)) {
        return null;
    }

    return lookup.StatusLookups
        .Where(status => string.Equals(status.ContractId, currentContractId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(status => status.AcceptedAt)
        .Select(status => status.Status)
        .FirstOrDefault();
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
    IReadOnlyList<ArtifactCandidate> currentSet,
    CoopArtifactContext? coopContext) {
    var fixedStonePool = BuildRecommendationPool(candidates);
    var changedStonePool = BuildRecommendationPool(ExpandWithStoneOptions(candidates, stones));

    var noDeflector = BuildBestArtifactSet(fixedStonePool, requireDeflector: false, currentSet, coopContext);
    if(noDeflector.Count > 0) {
        yield return new ArtifactSetSuggestion("No Deflector", noDeflector, ArtifactSetScore(noDeflector, currentSet, coopContext));
    }

    var withDeflector = BuildBestArtifactSet(fixedStonePool, requireDeflector: true, currentSet, coopContext);
    if(withDeflector.Count > 0) {
        yield return new ArtifactSetSuggestion("With Deflector", withDeflector, ArtifactSetScore(withDeflector, currentSet, coopContext));
    }

    if(stones.Count == 0) {
        yield break;
    }

    var noDeflectorChanged = BuildBestArtifactSet(changedStonePool, requireDeflector: false, currentSet, coopContext);
    if(noDeflectorChanged.Count > 0) {
        yield return new ArtifactSetSuggestion("No Deflector, changing stones", noDeflectorChanged, ArtifactSetScore(noDeflectorChanged, currentSet, coopContext));
    }

    var withDeflectorChanged = BuildBestArtifactSet(changedStonePool, requireDeflector: true, currentSet, coopContext);
    if(withDeflectorChanged.Count > 0) {
        yield return new ArtifactSetSuggestion("With Deflector, changing stones", withDeflectorChanged, ArtifactSetScore(withDeflectorChanged, currentSet, coopContext));
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
    IReadOnlyList<ArtifactCandidate> currentSet,
    CoopArtifactContext? coopContext) {
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
        .Select(s => (
            Set: s,
            Evaluation: CoopArtifactAnalyzer.EvaluateSet(coopContext, currentSet, s),
            Multipliers: ScoreLayingSet(s)))
        .OrderByDescending(s => s.Evaluation.Score)
        .ThenByDescending(s => Math.Min(s.Multipliers.Laying, s.Multipliers.Shipping))
        .ThenByDescending(s => s.Multipliers.Laying * s.Multipliers.Shipping)
        .ThenByDescending(s => s.Multipliers.Deflector)
        .ThenByDescending(s => s.Set.Count(a => a.Purpose is ArtifactPurpose.TeamLaying or ArtifactPurpose.Laying or ArtifactPurpose.Shipping))
        .ThenByDescending(s => s.Set.Sum(ArtifactCandidateStrength))
        .ThenByDescending(s => s.Set.Sum(ArtifactQualityScore))
        .Select(s => (IReadOnlyList<ArtifactCandidate>)s.Set)
        .FirstOrDefault() ?? [];

    void BuildSets(int start, List<ArtifactCandidate> current) {
        if(current.Count == targetSetSize) {
            sets.Add(current.ToList());
            return;
        }

        if(current.Count + (searchPool.Count - start) < targetSetSize) {
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

static double ArtifactSetScore(
    IReadOnlyList<ArtifactCandidate> set,
    IReadOnlyList<ArtifactCandidate> currentSet,
    CoopArtifactContext? coopContext) =>
    CoopArtifactAnalyzer.EvaluateSet(coopContext, currentSet, set).Score;

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

static string FormatArtifactSetOneLine(
    IReadOnlyList<ArtifactCandidate> set,
    IReadOnlyList<ArtifactCandidate> currentSet,
    CoopArtifactContext? coopContext) {
    var evaluation = CoopArtifactAnalyzer.EvaluateSet(coopContext, currentSet, set);
    var multipliers = ScoreLayingSet(set);
    var scoreText = evaluation.UsesLiveProduction
        ? $"{FormatRatioChange(evaluation.CoopOutputRatio)} estimated co-op output"
        : $"{Math.Min(multipliers.Laying, multipliers.Shipping):0.###}x bottleneck";
    return scoreText + " | " + string.Join(", ", set.Select(a => a.DisplayName + (a.StonesChanged ? "*" : "")));
}

static string BuildCoopArtifactSummary(CoopArtifactContext? context) {
    if(context is null) {
        return "Current co-op artifact details were unavailable. Suggestions use the player's synced inventory only.";
    }

    var lines = new List<string> {
        $"Artifact reports: **{context.ReportingMemberCount}/{context.Members.Count}** members",
        $"Live production reports: **{context.LiveProductionMemberCount}/{context.Members.Count}** members",
        $"Teammate Deflectors: **{context.TeammateDeflectorCount}** ({FormatArtifactBonus(context.TeammateDeflectorBonus)} laying)",
        $"Teammate Ships in a Bottle: **{context.TeammateEarningsArtifactCount}** ({FormatArtifactBonus(context.TeammateEarningsBonus)} earnings)"
    };

    var player = context.RequestingPlayer;
    if(player is null) {
        lines.Add("Your contributor row could not be matched, so live co-op scoring is unavailable.");
    } else if(player.EggLayingRate > 0 && player.ShippingRate > 0) {
        var bottleneck = player.EggLayingRate > player.ShippingRate * 1.03
            ? "shipping"
            : player.ShippingRate > player.EggLayingRate * 1.03
                ? "egg laying"
                : "balanced";
        lines.Add($"Current player bottleneck: **{bottleneck}**");
    }

    if(context.MissingReportCount > 0) {
        lines.Add($"{context.MissingReportCount} member(s) have not synced farm artifact details yet.");
    }

    return string.Join("\n", lines);
}

static string FormatArtifactBonus(double bonus) => $"+{Math.Max(0, bonus) * 100:0.#}%";

static string FormatRatioChange(double ratio) {
    var percent = (ratio - 1) * 100;
    return percent > 0.05
        ? $"+{percent:0.#}%"
        : percent < -0.05
            ? $"{percent:0.#}%"
            : "no material change";
}

static string GetString(SocketSlashCommand command, string name) =>
    (string)command.Data.Options.First(o => o.Name == name).Value;

static bool GetBool(SocketSlashCommand command, string name) =>
    command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value is bool value && value;

static string TrimForDiscordThreadName(string value) {
    var cleaned = Regex.Replace(value, @"\s+", " ").Trim();
    if(string.IsNullOrWhiteSpace(cleaned)) {
        return "Plotty report";
    }

    return cleaned.Length <= 90 ? cleaned : cleaned[..90].Trim();
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

static string TownTitle(int beersReceived) {
    return beersReceived switch {
        >= 100 => "Basically live here",
        >= 50 => "Patron",
        >= 10 => "Local",
        _ => ""
    };
}

static string TownMilestoneMessage(int beersReceived, string mention) {
    return beersReceived switch {
        10 => $"{mention} is now a **Local** in town.",
        50 => $"{mention} is now a **Patron** in town.",
        100 => $"{mention} **Basically live here** now.",
        _ => ""
    };
}

static bool IsCooldownLimitedBeverage(string drink) =>
    drink.Equals("Beer", StringComparison.OrdinalIgnoreCase) ||
    drink.Equals("Wine", StringComparison.OrdinalIgnoreCase);

static string StripBotMention(string content) =>
    Regex.Replace(content, @"<@!?\d+>", "", RegexOptions.Compiled).Trim();

static bool IsLateTodayChannel(SocketGuildChannel channel) =>
    NormalizeName(channel.Name) == "iamlatetoday";

static string? ExtractLateNoticeContractId(string content) {
    var contractMatch = Regex.Match(
        content,
        @"(?:contract|contract-id|contract id)\s*[:=]?\s*`?(?<id>[a-z0-9][a-z0-9\-]{2,})`?",
        RegexOptions.IgnoreCase);
    if(contractMatch.Success) {
        return contractMatch.Groups["id"].Value;
    }

    var taggedMatch = Regex.Match(content, @"#(?<id>[a-z0-9][a-z0-9\-]{2,})", RegexOptions.IgnoreCase);
    return taggedMatch.Success ? taggedMatch.Groups["id"].Value : null;
}

static string TrimDiscordMessage(string value, int maxLength) =>
    value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

static async Task<IReadOnlyList<TResult>> SelectWithConcurrencyAsync<TSource, TResult>(
    IEnumerable<TSource> source,
    int maxConcurrency,
    Func<TSource, Task<TResult>> selector) {
    var items = source.ToList();
    if(items.Count == 0) {
        return [];
    }

    using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
    var tasks = items.Select(async (item, index) => {
        await gate.WaitAsync();
        try {
            return (Index: index, Result: await selector(item));
        } finally {
            gate.Release();
        }
    });

    var results = await Task.WhenAll(tasks);
    return results
        .OrderBy(r => r.Index)
        .Select(r => r.Result)
        .ToList();
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

