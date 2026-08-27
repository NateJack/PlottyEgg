using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using Discord;
using Discord.WebSocket;
using EggContribBot;
using EggContribBot.Proto;

var settings = BotSettings.Load();
var plottyAdminUserIds = settings.Discord.ParsedAdminUserIds.ToHashSet();
var token = settings.Discord.Token
    ?? Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")
    ?? throw new InvalidOperationException("Set Discord:Token in appsettings.json or DISCORD_BOT_TOKEN.");

var secureText = new SecureText(settings.Storage.KeyPath);
using var dataStore = new DataStore(settings.Storage.DataPath, secureText);
await dataStore.RemoveLegacyPlaintextLinksAsync();
var eggClient = new EggIncClient(settings.EggIncApi);
var egg9000Client = new Egg9000Client(settings.Egg9000);
var wikiClient = new EggWikiClient();
var plottyAiClient = new PlottyAiClient(settings.OpenAi);
var monitorHealth = new MonitorHealthService();
var missingJoinMonitorsStarted = new HashSet<ulong>();
var shipReturnMonitorsStarted = new HashSet<ulong>();
var firstCoopAwardMonitorsStarted = new HashSet<ulong>();
var tokenLeaderboardMonitorsStarted = new HashSet<ulong>();
var farmerRankMonitorsStarted = new HashSet<ulong>();
var pollClosureMonitorsStarted = new HashSet<ulong>();
var commandRegistrationsCompleted = new HashSet<ulong>();
var readyGate = new SemaphoreSlim(1, 1);
var globalCommandsCleared = false;
using var shutdown = new CancellationTokenSource();
const int MaxEggIncAccountConcurrency = 4;
const int FirstCoopAwardRecentContractLimit = 4;
string[] tokenLeaderboardExcludedNames = ["giger86"];
string[] pollVoteEmojis = [
    "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣",
    "6️⃣", "7️⃣", "8️⃣", "9️⃣", "🔟",
    "🇦", "🇧", "🇨", "🇩", "🇪",
    "🇫", "🇬", "🇭", "🇮", "🇯"
];

var client = new DiscordSocketClient(new DiscordSocketConfig {
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
    AlwaysDownloadUsers = true
});

Console.CancelKeyPress += (_, eventArgs) => {
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

Console.WriteLine(plottyAiClient.IsConfigured
    ? $"Plotty AI chat is enabled with model {settings.OpenAi!.EffectiveModel}."
    : "Plotty AI chat is using local no-key conversation replies. Set OPENAI_API_KEY to enable hosted generated replies.");

client.Log += msg => {
    Console.WriteLine(msg.ToString());
    return Task.CompletedTask;
};

client.Ready += () => {
    _ = Task.Run(HandleReadyAsync);
    return Task.CompletedTask;
};

async Task HandleReadyAsync() {
    await readyGate.WaitAsync();
    try {
        var commands = BuildCommands().Select(c => c.Build()).ToArray();
        var guildIds = settings.Discord.ParsedGuildIds.Distinct().ToArray();
        if(guildIds.Length > 0) {
            if(!globalCommandsCleared) {
                await client.Rest.BulkOverwriteGlobalCommands([]);
                globalCommandsCleared = true;
            }

            foreach(var guildId in guildIds) {
                var guild = client.GetGuild(guildId);
                if(guild is null) {
                    Console.WriteLine($"Plotty is connected, but guild {guildId} was not found. Is Plotty invited?");
                    continue;
                }

                if(!commandRegistrationsCompleted.Contains(guildId)) {
                    await guild.BulkOverwriteApplicationCommandAsync(commands);
                    commandRegistrationsCompleted.Add(guildId);
                    Console.WriteLine($"Logged in as {client.CurrentUser}; registered {commands.Length} commands in {guild.Name}.");
                }

                if(FindEgg9000ReportChannel(guild) is not null && missingJoinMonitorsStarted.Add(guildId)) {
                    _ = Task.Run(() => MonitorMissingCoopJoinsAsync(guildId, shutdown.Token));
                }
                if(shipReturnMonitorsStarted.Add(guildId)) {
                    _ = Task.Run(() => MonitorShipReturnsAsync(guildId, shutdown.Token));
                }
                if(firstCoopAwardMonitorsStarted.Add(guildId)) {
                    _ = Task.Run(() => MonitorFirstCoopAwardsAsync(guildId, shutdown.Token));
                }
                if(tokenLeaderboardMonitorsStarted.Add(guildId)) {
                    _ = Task.Run(() => MonitorWeeklyTokenLeaderboardAsync(guildId, shutdown.Token));
                }
                if(farmerRankMonitorsStarted.Add(guildId)) {
                    _ = Task.Run(() => MonitorFarmerRankUpsAsync(guildId, shutdown.Token));
                }
                if(pollClosureMonitorsStarted.Add(guildId)) {
                    _ = Task.Run(() => MonitorPendingPollsAsync(guildId, shutdown.Token));
                }
            }
        } else if(!globalCommandsCleared) {
            await client.Rest.BulkOverwriteGlobalCommands(commands);
            globalCommandsCleared = true;
            Console.WriteLine($"Logged in as {client.CurrentUser}; registered {commands.Length} global commands.");
        }
    } catch(Exception ex) {
        Console.WriteLine($"Plotty ready initialization failed: {ex}");
    } finally {
        readyGate.Release();
    }
}

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
                await HandleContractAsync(command);
                break;
            case "contract-late-notify":
                await HandleContractLateNotifyAsync(command);
                break;
            case "mycontract":
                await HandleMyContractAsync(command);
                break;
            case "register-eid":
                await HandleRegisterEidAsync(command);
                break;
            case "unregister-eid":
                await HandleUnregisterEidAsync(command);
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
            case "admin-plotty-report":
                await HandleAdminPlottyReportAsync(command);
                break;
            case "admin-plotty-send-demerit":
                await HandleAdminPlottySendDemeritAsync(command);
                break;
            case "admin-demerit":
                await HandleAdminDemeritAsync(command);
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
            case "myeggcount":
                await HandleMyEggCountAsync(command);
                break;
            case "goldeneggs":
                await HandleGoldenEggsAsync(command);
                break;
            case "egg-milestones":
                await HandleEggMilestonesAsync(command);
                break;
            case "rivalry":
                await HandleRivalryAsync(command);
                break;
            case "egg-flex":
                await HandleEggFlexAsync(command);
                break;
            case "contract-mvp":
                await HandleContractMvpAsync(command);
                break;
            case "contract-predictions":
                await HandleContractPredictionsAsync(command);
                break;
            case "plotty-achievements":
                await HandlePlottyAchievementsAsync(command);
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
            case "plotty-poll":
                await HandlePollAsync(command);
                break;
            case "new-member-poll":
                await HandleNewMemberPollAsync(command);
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
            case "plotty-features":
                await HandlePlottyFeaturesAsync(command);
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

        if(component.Data.CustomId.StartsWith("e9k-report-", StringComparison.Ordinal)) {
            await HandleEgg9000ReportButtonAsync(component);
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
        if(modal.Data.CustomId == "register-eid-modal") {
            await HandleRegisterEidModalAsync(modal);
            return;
        }

        if(modal.Data.CustomId.StartsWith("poll-modal:", StringComparison.Ordinal)) {
            await HandlePollModalAsync(modal);
        }
    } catch(Exception ex) {
        Console.WriteLine(ex);
        if(modal.HasResponded) {
            await modal.FollowupAsync("Plotty tripped over that form. Please try again.", ephemeral: true);
        } else {
            await modal.RespondAsync("Plotty tripped over that form. Please try again.", ephemeral: true);
        }
    }
};

client.MessageReceived += message => {
    if(message.Author.IsBot || message.Channel is not SocketGuildChannel guildChannel) {
        return Task.CompletedTask;
    }

    var allowSarcasm = Random.Shared.Next(100) == 0 && LooksLikeSarcasm(message.Content);
    var couldNeedPlotty =
        message.Content.Contains("what the fox", StringComparison.OrdinalIgnoreCase) ||
        IsLateTodayChannel(guildChannel) ||
        message.MentionedUsers.Any(user => user.Id == client.CurrentUser.Id) ||
        message.Reference?.MessageId.IsSpecified == true ||
        allowSarcasm;
    if(couldNeedPlotty) {
        _ = HandleMessageReceivedAsync(message, allowSarcasm);
    }

    return Task.CompletedTask;
};

async Task HandleMessageReceivedAsync(SocketMessage message, bool allowSarcasm) {
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

        if(allowSarcasm) {
            var memory = await dataStore.RecordPlottyInteractionAsync(guildChannel.Guild.Id, message.Author.Id, "sarcasm");
            await message.Channel.SendMessageAsync(PlottyPersonality.SarcasmResponse(message.Author.Mention, memory));
        }
    } catch(Exception ex) {
        Console.WriteLine(ex);
    }
}

var browserScrapeReceiver = StartEgg9000BrowserScrapeReceiver(shutdown.Token);
await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
try {
    await Task.Delay(Timeout.Infinite, shutdown.Token);
} catch(OperationCanceledException) {
    // Normal shutdown.
}
await client.StopAsync();
await browserScrapeReceiver;

Task StartEgg9000BrowserScrapeReceiver(CancellationToken cancellationToken) {
    int[] ports = [5199, 57291, 58423, 61997];
    TcpListener? listener = null;
    var selectedPort = 0;
    foreach(var port in ports) {
        try {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            selectedPort = port;
            break;
        } catch(Exception ex) when(ex is SocketException or ObjectDisposedException) {
            listener = null;
            Console.WriteLine($"EGG9000 browser scrape receiver could not start on localhost port {port}: {ex.Message}");
        }
    }

    if(listener is null) {
        Console.WriteLine("EGG9000 browser scrape receiver could not start on any fallback localhost port.");
        return Task.CompletedTask;
    }

    Console.WriteLine($"EGG9000 browser scrape receiver listening at http://127.0.0.1:{selectedPort}/egg9000-scrape/");
    var requestGate = new SemaphoreSlim(4, 4);
    return Task.Run(async () => {
        try {
            while(!cancellationToken.IsCancellationRequested) {
                try {
                    var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                    await requestGate.WaitAsync(cancellationToken);
                    _ = Task.Run(async () => {
                        try {
                            await HandleEgg9000BrowserScrapeRequestAsync(tcpClient);
                        } finally {
                            requestGate.Release();
                        }
                    });
                } catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
                    break;
                } catch(Exception ex) when(ex is SocketException or ObjectDisposedException) {
                    break;
                } catch(Exception ex) {
                    Console.WriteLine($"EGG9000 browser scrape receiver failed: {ex}");
                }
            }
        } finally {
            listener.Stop();
        }
    });
}

async Task HandleEgg9000BrowserScrapeRequestAsync(TcpClient tcpClient) {
    await using var stream = tcpClient.GetStream();
    using var clientToClose = tcpClient;
    try {
        if(tcpClient.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint ||
           !IPAddress.IsLoopback(remoteEndPoint.Address)) {
            await WriteBrowserScrapeResponseAsync(stream, 403, "Only localhost requests are accepted.");
            return;
        }

        var (method, path, headers, body) = await ReadLocalHttpRequestAsync(stream);

        if(!string.Equals(path, "/egg9000-scrape/", StringComparison.OrdinalIgnoreCase)) {
            await WriteBrowserScrapeResponseAsync(stream, 404, "Unknown endpoint.");
            return;
        }

        if(method == "OPTIONS") {
            await WriteBrowserScrapeResponseAsync(stream, 204, "");
            return;
        }

        if(method != "POST") {
            await WriteBrowserScrapeResponseAsync(stream, 405, "Only POST is supported.");
            return;
        }

        if(headers.TryGetValue("Origin", out var origin) &&
           !string.Equals(origin, "https://egg9000.com", StringComparison.OrdinalIgnoreCase)) {
            await WriteBrowserScrapeResponseAsync(stream, 403, "Only EGG9000 page exports are accepted.");
            return;
        }

        if(string.IsNullOrWhiteSpace(body)) {
            await WriteBrowserScrapeResponseAsync(stream, 400, "Request body is empty.");
            return;
        }

        var json = body;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var contractId = root.TryGetProperty("contractId", out var contractIdElement)
            ? contractIdElement.GetString()
            : null;
        if(string.IsNullOrWhiteSpace(contractId)) {
            await WriteBrowserScrapeResponseAsync(stream, 400, "Payload does not include a contractId.");
            return;
        }

        var league = root.TryGetProperty("league", out var leagueElement) && leagueElement.TryGetInt32(out var parsedLeague)
            ? parsedLeague
            : settings.Egg9000?.EffectiveLeague ?? 5;
        var playerCount = root.TryGetProperty("players", out var playersElement) && playersElement.ValueKind == JsonValueKind.Array
            ? playersElement.GetArrayLength()
            : 0;
        var configuredGuildTag = NormalizeGuildTag(settings.Egg9000?.EffectiveGuildTag ?? "The Plot Chickens");
        var plotChickenCount = playersElement.ValueKind == JsonValueKind.Array
            ? playersElement.EnumerateArray().Count(player =>
                player.TryGetProperty("guildTag", out var guildTag) &&
                NormalizeGuildTag(guildTag.GetString()) == configuredGuildTag)
            : 0;

        var safeContractId = Regex.Replace(contractId, @"[^A-Za-z0-9_.-]", "_");
        var outDir = settings.Egg9000?.EffectiveBrowserScrapeDataPath ?? "data/e9k-browser-scrapes";
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"{safeContractId}-league-{league}.json");
        await File.WriteAllTextAsync(outPath, json);

        var message = $"Saved {playerCount} players ({plotChickenCount} Plot Chickens) to {outPath}";
        Console.WriteLine($"EGG9000 browser scrape receiver: {message}");
        await WriteBrowserScrapeResponseAsync(stream, 200, message);
    } catch(Exception ex) {
        Console.WriteLine($"EGG9000 browser scrape receiver request failed: {ex}");
        if(stream.CanWrite) {
            await WriteBrowserScrapeResponseAsync(stream, 500, "Plotty could not save that scrape.");
        }
    }
}

static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadLocalHttpRequestAsync(Stream stream) {
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    var requestLine = await reader.ReadLineAsync() ?? "";
    var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var method = requestParts.Length > 0 ? requestParts[0].ToUpperInvariant() : "";
    var path = requestParts.Length > 1 ? requestParts[1] : "";
    var queryStart = path.IndexOf('?');
    if(queryStart >= 0) {
        path = path[..queryStart];
    }

    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? line;
    while(!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) {
        var separator = line.IndexOf(':');
        if(separator > 0) {
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
    }

    var contentLength = headers.TryGetValue("Content-Length", out var value) && int.TryParse(value, out var parsed)
        ? Math.Clamp(parsed, 0, 5_000_000)
        : 0;
    var buffer = new char[contentLength];
    var read = 0;
    while(read < contentLength) {
        var count = await reader.ReadAsync(buffer.AsMemory(read, contentLength - read));
        if(count == 0) {
            break;
        }

        read += count;
    }

    return (method, path, headers, new string(buffer, 0, read));
}

static async Task WriteBrowserScrapeResponseAsync(Stream stream, int statusCode, string message) {
    var reason = statusCode switch {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Internal Server Error"
    };
    var body = Encoding.UTF8.GetBytes(message);
    var header = Encoding.UTF8.GetBytes(
        $"HTTP/1.1 {statusCode} {reason}\r\n" +
        "Access-Control-Allow-Origin: https://egg9000.com\r\n" +
        "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
        "Access-Control-Allow-Headers: Content-Type\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        $"Content-Length: {body.Length}\r\n" +
        "Connection: close\r\n\r\n");
    await stream.WriteAsync(header);
    if(body.Length > 0) {
        await stream.WriteAsync(body);
    }
}

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
        .WithName("mycontract")
        .WithDescription("Privately show your active co-op players, rates, and Plot Chickens membership.");

    yield return new SlashCommandBuilder()
        .WithName("register-eid")
        .WithDescription("Privately register your Egg Inc ID so Plotty can pull your contracts.");

    yield return new SlashCommandBuilder()
        .WithName("unregister-eid")
        .WithDescription("Privately remove all Egg Inc IDs tied to your Discord account in this server.");

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
        .WithName("admin-plotty-report")
        .WithDescription("Staff only: privately scrape the latest EGG9000 AAA contract report.");

    yield return new SlashCommandBuilder()
        .WithName("admin-plotty-send-demerit")
        .WithDescription("Staff only: ping a member with a 6hr/18hr demerit notice.")
        .AddOption("member", ApplicationCommandOptionType.User, "Discord member receiving the demerit notice.", isRequired: true)
        .AddOption(new SlashCommandOptionBuilder()
            .WithName("message")
            .WithDescription("Which demerit notice Plotty should send.")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .AddChoice("6hr join notice", "6hr")
            .AddChoice("18hr rate notice", "18hr"));

    yield return new SlashCommandBuilder()
        .WithName("admin-demerit")
        .WithDescription("Staff only: add or remove active demerits.")
        .AddOption(new SlashCommandOptionBuilder()
            .WithName("action")
            .WithDescription("Whether to add or remove demerits.")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .AddChoice("add", "add")
            .AddChoice("remove", "remove"))
        .AddOption("member", ApplicationCommandOptionType.User, "Discord member.", isRequired: false)
        .AddOption("discord-user-id", ApplicationCommandOptionType.String, "Discord user ID for someone who left the server.", isRequired: false)
        .AddOption("amount", ApplicationCommandOptionType.Integer, "Number of demerits. Default is 1.", isRequired: false)
        .AddOption("reason", ApplicationCommandOptionType.String, "Optional reason when adding demerits.", isRequired: false);

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
        .WithName("myeggcount")
        .WithDescription("Show how many eggs you still need for the 5Q and 10Q challenges.")
        .AddOption("public", ApplicationCommandOptionType.Boolean, "Show the result publicly. Defaults to private.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("goldeneggs")
        .WithDescription("Show how many Golden Eggs you earned during the past 24 hours.")
        .AddOption("public", ApplicationCommandOptionType.Boolean, "Show the result publicly. Defaults to private.", isRequired: false);

    yield return new SlashCommandBuilder()
        .WithName("egg-milestones")
        .WithDescription("Privately show your next Egg Inc account milestones.");

    yield return new SlashCommandBuilder()
        .WithName("rivalry")
        .WithDescription("Privately compare two registered members in a friendly Plotty rivalry.")
        .AddOption("member-one", ApplicationCommandOptionType.User, "First registered Discord member.", isRequired: true)
        .AddOption("member-two", ApplicationCommandOptionType.User, "Second registered Discord member.", isRequired: true);

    yield return new SlashCommandBuilder()
        .WithName("egg-flex")
        .WithDescription("Privately show a brag-card style summary for your registered Egg Inc account(s).");

    yield return new SlashCommandBuilder()
        .WithName("contract-mvp")
        .WithDescription("Privately show MVP badges for your active co-op contract(s).");

    yield return new SlashCommandBuilder()
        .WithName("contract-predictions")
        .WithDescription("Privately estimate active co-op finish health from current contribution rates.");

    yield return new SlashCommandBuilder()
        .WithName("plotty-achievements")
        .WithDescription("Privately show fun Plotty achievement badges from your registered account data.");

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
        .WithName("plotty-poll")
        .WithDescription("Create a Plotty poll with reaction voting.")
        .AddOption(new SlashCommandOptionBuilder()
            .WithName("duration")
            .WithDescription("How long should the poll run?")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .AddChoice("12 hours", "12")
            .AddChoice("24 hours", "24"))
        .AddOption(new SlashCommandOptionBuilder()
            .WithName("audience")
            .WithDescription("Who should be notified?")
            .WithType(ApplicationCommandOptionType.String)
            .WithRequired(true)
            .AddChoice("General poll", "general")
            .AddChoice("New member poll role", "new-member"));

    yield return new SlashCommandBuilder()
        .WithName("new-member-poll")
        .WithDescription("Create a 24-hour yes/no poll for a prospective new member.")
        .AddOption("member-name", ApplicationCommandOptionType.String, "Prospective member name.", isRequired: true)
        .AddOption("eb", ApplicationCommandOptionType.String, "Prospective member EB.", isRequired: true);

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
        .WithName("plotty-features")
        .WithDescription("Privately show Plotty's current feature list.");

    yield return new SlashCommandBuilder()
        .WithName("admin-plotty-speak")
        .WithDescription("Staff only: make Plotty say a message.")
        .AddOption("message", ApplicationCommandOptionType.String, "What Plotty should say.", isRequired: true);

    yield return new SlashCommandBuilder()
        .WithName("help")
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
    var statuses = await AsyncBatch.SelectWithConcurrencyAsync(
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

async Task HandleMyContractAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync(
            "You do not have an EID registered yet. Run `/register-eid` first.",
            ephemeral: true);
        return;
    }

    var lookups = await GetAccountCoopLookupsAsync(accounts.Take(10));
    var activeStatuses = BuildUniqueActiveStatuses(lookups);
    if(activeStatuses.Count == 0) {
        await command.FollowupAsync(
            "I could not find an active co-op for your registered EID account(s).",
            ephemeral: true);
        return;
    }

    var plotChickenRosterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if(egg9000Client.IsConfigured) {
        try {
            var roster = await egg9000Client.GetLeaderboardAsync();
            plotChickenRosterNames.UnionWith(roster
                .Select(member => NormalizeName(member.EggIncName))
                .Where(name => name.Length > 0));
        } catch(Exception ex) {
            Console.WriteLine($"Could not fetch EGG9000 roster for /mycontract: {ex.Message}");
        }
    }

    var embeds = new List<Embed>();
    foreach(var item in activeStatuses) {
        Egg9000ContractScrape? scrape = null;
        try {
            scrape = await egg9000Client.GetContractDetailsAsync(item.Status.ContractIdentifier);
        } catch(Exception ex) {
            Console.WriteLine($"Could not fetch EGG9000 contract membership for /mycontract ({item.Status.ContractIdentifier}): {ex.Message}");
        }

        embeds.Add(BuildMyContractEmbed(
            item.Status,
            scrape,
            plotChickenRosterNames,
            accounts.Count > 1 ? AccountDisplayName(item.Account) : null));
    }

    var summary = $"Showing `{embeds.Count}` active contract(s). Co-op codes are hidden.";
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

async Task MonitorMissingCoopJoinsAsync(ulong guildId, CancellationToken cancellationToken) {
    await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while(!cancellationToken.IsCancellationRequested) {
        try {
            await CheckEgg9000ContractReportsAsync(guildId);
            monitorHealth.ReportSuccess("egg9000-contract-reports", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("egg9000-contract-reports", guildId, ex);
            Console.WriteLine($"EGG9000 contract report monitor failed: {ex}");
        }

        if(!await timer.WaitForNextTickAsync(cancellationToken)) {
            break;
        }
    }
}

async Task MonitorShipReturnsAsync(ulong guildId, CancellationToken cancellationToken) {
    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
    while(!cancellationToken.IsCancellationRequested) {
        try {
            await CheckShipReturnNotificationsAsync(guildId);
            monitorHealth.ReportSuccess("ship-returns", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("ship-returns", guildId, ex);
            Console.WriteLine($"Ship return monitor failed: {ex}");
        }

        if(!await timer.WaitForNextTickAsync(cancellationToken)) {
            break;
        }
    }
}

async Task CheckShipReturnNotificationsAsync(ulong guildId) {
    var due = await dataStore.GetDueShipReturnNotificationsAsync(guildId, DateTimeOffset.UtcNow);
    if(due.Count == 0) {
        return;
    }

    var guild = client.GetGuild(guildId);
    foreach(var notification in due) {
        var sent = false;
        try {
            var user = guild?.GetUser(notification.DiscordUserId) ?? client.GetUser(notification.DiscordUserId);
            if(user is not null) {
                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(
                    $"Your **{notification.ShipName}** ship mission should be back now. Time to collect the cargo.");
                sent = true;
            }
        } catch(Exception ex) {
            Console.WriteLine($"Could not DM ship return notification to {notification.DiscordUserId}: {ex.Message}");
        }

        if(sent) {
            await dataStore.MarkShipReturnNotificationSentAsync(notification.Key, DateTimeOffset.UtcNow);
        }
    }
}

async Task CheckEgg9000ContractReportsAsync(ulong guildId) {
    var guild = client.GetGuild(guildId);
    if(guild is null) {
        return;
    }

    var reportChannel = FindEgg9000ReportChannel(guild);
    if(reportChannel is null) {
        Console.WriteLine($"EGG9000 contract report monitor could not find #plotty-reports or #naughty-list in {guild.Name} ({guild.Id}).");
        return;
    }

    var now = DateTimeOffset.UtcNow;
    var currentContracts = await GetLatestEgg9000ReportContractsAsync(now);
    if(currentContracts.Count == 0) {
        return;
    }

    var dueReports = new List<(Contract Contract, string Checkpoint, string AlertKey)>();

    foreach(var contract in currentContracts) {
        var contractId = contract.Identifier;
        var startedAt = DateTimeOffset.FromUnixTimeSeconds((long)contract.StartTime);
        if(!IsEgg9000ReportReleaseDay(startedAt)) {
            continue;
        }

        foreach(var checkpoint in new[] { "6h", "18h" }) {
            var checkpointAt = startedAt.AddHours(checkpoint == "6h" ? 6 : 18);
            var alertKey = Egg9000ReportKey(guildId, contractId, startedAt, checkpoint);
            if(now >= checkpointAt && !await dataStore.HasMissingJoinAlertAsync(alertKey)) {
                dueReports.Add((contract, checkpoint, alertKey));
            }
        }
    }

    if(dueReports.Count == 0) {
        return;
    }

    var contractsToRefresh = dueReports
        .Select(item => item.Contract)
        .GroupBy(contract => contract.Identifier, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();
    await RefreshEgg9000BrowserScrapesAsync(contractsToRefresh);

    foreach(var report in dueReports) {
        var contract = report.Contract;
        var startedAt = DateTimeOffset.FromUnixTimeSeconds((long)contract.StartTime);
        var players = await BuildEgg9000ReportPlayersAsync(guild, contract, report.Checkpoint);
        if(players is null) {
            Console.WriteLine($"EGG9000 report deferred for {contract.Identifier}/{report.Checkpoint}; scrape data was unavailable and will be retried.");
            continue;
        }

        var posted = await TryPostEgg9000ReportReviewAsync(
            reportChannel,
            contract,
            startedAt,
            report.Checkpoint,
            players);
        if(!posted) {
            Console.WriteLine($"EGG9000 report post failed for {contract.Identifier}/{report.Checkpoint}; it will be retried.");
            continue;
        }

        await dataStore.RecordMissingJoinAlertAsync(report.AlertKey);
        Console.WriteLine($"EGG9000 report posted for {contract.Identifier}/{report.Checkpoint} with {players.Count} member(s) needing attention.");
    }
}

async Task<bool> TryPostEgg9000ReportAsync(
    SocketTextChannel reportChannel,
    Embed embed,
    MessageComponent? components) {
    try {
        await reportChannel.SendMessageAsync(
            embed: embed,
            components: components,
            allowedMentions: AllowedMentions.None);
        return true;
    } catch(Exception ex) {
        Console.WriteLine($"Could not post #plotty-reports message: {ex}");
        return false;
    }
}

async Task<bool> TryPostEgg9000ReportReviewAsync(
    SocketTextChannel reportChannel,
    Contract contract,
    DateTimeOffset startedAt,
    string checkpoint,
    IReadOnlyList<Egg9000ReportPlayer> players) {
    var components = players.Count == 0
        ? null
        : BuildEgg9000ReportComponents(contract.Identifier, checkpoint);
    return await TryPostEgg9000ReportAsync(
        reportChannel,
        BuildEgg9000ReportEmbed(contract, startedAt, checkpoint, players),
        components);
}

async Task HandleAdminPlottyReportAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can run Plotty reports.", ephemeral: true);
        return;
    }

    await command.DeferAsync(ephemeral: true);

    var guild = client.GetGuild(command.GuildId!.Value);
    if(guild is null) {
        await command.FollowupAsync("I could not find this server.", ephemeral: true);
        return;
    }

    var now = DateTimeOffset.UtcNow;
    var contracts = await GetLatestEgg9000ReportContractsAsync(now);
    if(contracts.Count == 0) {
        await command.FollowupAsync("I could not find any active recently released co-op contracts to scrape.", ephemeral: true);
        return;
    }

    await RefreshEgg9000BrowserScrapesAsync(contracts);

    var sentAny = false;
    foreach(var contract in contracts) {
        var startedAt = DateTimeOffset.FromUnixTimeSeconds((long)contract.StartTime);
        foreach(var checkpoint in new[] { "6h", "18h" }) {
            var players = await BuildEgg9000ReportPlayersAsync(guild, contract, checkpoint);
            if(players is null) {
                await command.FollowupAsync($"I could not scrape EGG9000 for `{contract.Identifier}`. EGG9000 returned a sign-in page, so set `EGG9000_SESSION_COOKIE` for Plotty and restart the bot.", ephemeral: true);
                sentAny = true;
                continue;
            }

            var embed = BuildEgg9000ReportEmbed(contract, startedAt, checkpoint, players, includeReviewActions: false);
            await command.FollowupAsync(embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None);
            sentAny = true;
        }
    }

    if(!sentAny) {
        var contractNames = string.Join(", ", contracts.Select(c => $"`{c.Identifier}`"));
        await command.FollowupAsync($"The latest contract batch ({contractNames}) is not at the 6hr or 18hr check yet.", ephemeral: true);
    }
}

async Task<bool> RefreshEgg9000BrowserScrapesAsync(IReadOnlyList<Contract> contracts) {
    var egg9000Settings = settings.Egg9000;
    if(egg9000Settings is null || !egg9000Settings.AutoBrowserScrapeEnabled || contracts.Count == 0) {
        return false;
    }

    var repoRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));
    var scriptPath = Path.Combine(repoRoot, "tools", "auto-egg9000-scrape.ps1");
    if(!File.Exists(scriptPath)) {
        Console.WriteLine($"EGG9000 auto browser scrape helper was not found at {scriptPath}.");
        return false;
    }

    var urls = contracts
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Select(c => BuildEgg9000ContractDetailsUrl(c.Identifier))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if(urls.Count == 0) {
        return false;
    }

    var outDir = Path.GetFullPath(egg9000Settings.EffectiveBrowserScrapeDataPath, Directory.GetCurrentDirectory());
    var shell = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
    var startInfo = new ProcessStartInfo {
        FileName = shell,
        WorkingDirectory = repoRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("-NoProfile");
    if(OperatingSystem.IsWindows()) {
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
    }

    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(scriptPath);
    startInfo.ArgumentList.Add("-Url");
    startInfo.ArgumentList.Add(string.Join("|", urls));

    startInfo.ArgumentList.Add("-OutDir");
    startInfo.ArgumentList.Add(outDir);
    startInfo.ArgumentList.Add("-RefreshDelaySeconds");
    startInfo.ArgumentList.Add(((int)egg9000Settings.EffectiveBrowserScrapeRefreshDelay.TotalSeconds).ToString());
    if(egg9000Settings.CloseBrowserAfterScrape) {
        startInfo.ArgumentList.Add("-CloseWhenDone");
    }

    try {
        using var process = Process.Start(startInfo);
        if(process is null) {
            Console.WriteLine("EGG9000 auto browser scrape helper could not be started.");
            return false;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var exitTask = process.WaitForExitAsync();
        if(await Task.WhenAny(exitTask, timeoutTask) == timeoutTask) {
            try {
                process.Kill(entireProcessTree: true);
            } catch {
                // Best effort only; the next monitor pass can try again.
            }

            Console.WriteLine("EGG9000 auto browser scrape helper timed out.");
            return false;
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        if(!string.IsNullOrWhiteSpace(stdout)) {
            Console.WriteLine($"EGG9000 auto browser scrape: {stdout}");
        }

        if(process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr)) {
            Console.WriteLine($"EGG9000 auto browser scrape warning: exit {process.ExitCode}; {stderr}");
            return false;
        }

        return true;
    } catch(Exception ex) {
        Console.WriteLine($"EGG9000 auto browser scrape failed: {ex.Message}");
        return false;
    }
}

string BuildEgg9000ContractDetailsUrl(string contractId) {
    var egg9000Settings = settings.Egg9000 ?? new Egg9000Settings();
    var baseUri = new Uri(egg9000Settings.EffectiveBaseUrl.EndsWith('/')
        ? egg9000Settings.EffectiveBaseUrl
        : $"{egg9000Settings.EffectiveBaseUrl}/");
    var path = "Contract/Details";
    var query = string.Join("&", [
        $"GuildId={Uri.EscapeDataString(egg9000Settings.EffectiveGuildId)}",
        $"ContractId={Uri.EscapeDataString(contractId)}",
        $"League={egg9000Settings.EffectiveLeague}"
    ]);
    return new Uri(baseUri, $"{path}?{query}").ToString();
}

async Task<IReadOnlyList<Contract>> GetLatestEgg9000ReportContractsAsync(DateTimeOffset now) {
    var uniqueActiveContracts = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.CoopAllowed)
        .Where(c => c.StartTime > 0)
        .Where(c => c.ExpirationTime <= 0 || DateTimeOffset.FromUnixTimeSeconds((long)c.ExpirationTime) > now)
        .GroupBy(c => c.Identifier, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(c => c.StartTime).First())
        .ToList();
    if(uniqueActiveContracts.Count == 0) {
        return [];
    }

    var latestStartTime = uniqueActiveContracts.Max(c => c.StartTime);
    return uniqueActiveContracts
        .Where(c => Math.Abs(c.StartTime - latestStartTime) <= 3600)
        .OrderByDescending(c => c.StartTime)
        .ToList();
}

async Task<IReadOnlyList<Egg9000ReportPlayer>?> BuildEgg9000ReportPlayersAsync(SocketGuild guild, Contract contract, string checkpoint) {
    var scrape = await egg9000Client.GetContractDetailsAsync(contract.Identifier);
    if(scrape is null || (scrape.LooksLikeLoginPage && scrape.Players.Count == 0)) {
        return null;
    }

    var accounts = await dataStore.GetRegisteredEidsAsync(guild.Id);
    var registeredByName = accounts
        .Where(a => NormalizeName(a.EggName).Length > 0)
        .GroupBy(a => NormalizeName(a.EggName), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().DiscordUserId, StringComparer.OrdinalIgnoreCase);
    var egg9000ByName = await GetEgg9000DiscordIdsByEggNameAsync();
    var lateNoticeUsers = checkpoint == "6h"
        ? await dataStore.GetActiveContractLateNoticeUserIdsAsync(guild.Id, contract.Identifier)
        : new HashSet<ulong>();

    var guildTag = NormalizeGuildTag(settings.Egg9000?.EffectiveGuildTag ?? "The Plot Chickens");
    var visibleGuildPlayers = scrape.Players
        .Where(player => NormalizeGuildTag(player.GuildTag) == guildTag)
        .Select(player => {
            var userId = ResolveEgg9000PlayerDiscordId(guild, player.Name, registeredByName, egg9000ByName);
            return new Egg9000ReportPlayer(
                player.Name,
                player.GuildTag,
                player.Chickens,
                player.Rate,
                player.RatePerHour,
                player.Projected,
                player.Joined,
                userId,
                player.CoopFinished);
        })
        .Where(player => !player.CoopFinished)
        .GroupBy(player => NormalizeName(player.PlayerName), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderBy(player => player.RatePerHour).First())
        .ToList();

    if(checkpoint == "6h") {
        await dataStore.SaveEgg9000ReportSnapshotAsync(
            guild.Id,
            contract.Identifier,
            checkpoint,
            visibleGuildPlayers
                .Select(player => new Egg9000ReportSnapshotPlayer(player.PlayerName, player.DiscordUserId))
                .ToList());
    }

    if(checkpoint == "18h") {
        var sixHourSnapshot = await dataStore.GetEgg9000ReportSnapshotAsync(guild.Id, contract.Identifier, "6h");
        if(sixHourSnapshot.Count > 0) {
            var sixHourUserIds = sixHourSnapshot
                .Where(player => player.DiscordUserId is not null)
                .Select(player => player.DiscordUserId!.Value)
                .ToHashSet();
            var sixHourNames = sixHourSnapshot
                .Select(player => NormalizeName(player.PlayerName))
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            visibleGuildPlayers = visibleGuildPlayers
                .Where(player => player.DiscordUserId is { } userId
                    ? sixHourUserIds.Contains(userId) || sixHourNames.Contains(NormalizeName(player.PlayerName))
                    : sixHourNames.Contains(NormalizeName(player.PlayerName)))
                .ToList();
        }
    }

    var candidates = visibleGuildPlayers
        .Where(player => checkpoint == "6h"
            ? !player.Joined
            : player.Joined && player.RatePerHour < 2_000_000_000_000_000d)
        .Where(player => checkpoint != "6h" || player.DiscordUserId is null || !lateNoticeUsers.Contains(player.DiscordUserId.Value))
        .OrderBy(player => player.DiscordUserId is null)
        .ThenBy(player => player.RatePerHour)
        .ThenBy(player => NormalizeName(player.PlayerName))
        .ToList();

    return candidates;
}

async Task<IReadOnlyDictionary<string, ulong>> GetEgg9000DiscordIdsByEggNameAsync() {
    if(!egg9000Client.IsConfigured) {
        return new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    }

    try {
        var members = await egg9000Client.GetLeaderboardAsync();
        return members
            .Where(item => item.DiscordId != 0 && NormalizeName(item.EggIncName).Length > 0)
            .GroupBy(item => NormalizeName(item.EggIncName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DiscordId, StringComparer.OrdinalIgnoreCase);
    } catch(Exception ex) {
        Console.WriteLine($"Could not resolve EGG9000 roster names for scrape report: {ex.Message}");
        return new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    }
}

ulong? ResolveEgg9000PlayerDiscordId(
    SocketGuild guild,
    string playerName,
    IReadOnlyDictionary<string, ulong> registeredByName,
    IReadOnlyDictionary<string, ulong> egg9000ByName) {
    var normalized = NormalizeName(playerName);
    if(normalized.Length == 0) {
        return null;
    }

    if(registeredByName.TryGetValue(normalized, out var registeredUserId)) {
        return registeredUserId;
    }

    if(egg9000ByName.TryGetValue(normalized, out var egg9000UserId) && guild.GetUser(egg9000UserId) is not null) {
        return egg9000UserId;
    }

    var directMember = guild.Users.FirstOrDefault(user =>
        NormalizeName(user.DisplayName) == normalized ||
        NormalizeName(user.Username) == normalized ||
        NormalizeName(user.GlobalName) == normalized);
    return directMember?.Id;
}

static string Egg9000ReportKey(ulong guildId, string contractId, DateTimeOffset startedAt, string checkpoint) =>
    $"e9k-report:{guildId}:{contractId.ToLowerInvariant()}:{startedAt.ToUnixTimeSeconds()}:{checkpoint}";

static bool IsEgg9000ReportReleaseDay(DateTimeOffset startedAt) {
    var localStart = TimeZoneInfo.ConvertTime(startedAt, WeeklyTokenLeaderboard.MountainTimeZone());
    return localStart.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday;
}

async Task MonitorFirstCoopAwardsAsync(ulong guildId, CancellationToken cancellationToken) {
    try {
        await CheckFirstCoopAwardsAsync(guildId);
        monitorHealth.ReportSuccess("first-coop-awards", guildId);
    } catch(Exception ex) {
        monitorHealth.ReportFailure("first-coop-awards", guildId, ex);
        Console.WriteLine($"First coop award monitor failed: {ex}");
    }

    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
    while(await timer.WaitForNextTickAsync(cancellationToken)) {
        try {
            await CheckFirstCoopAwardsAsync(guildId);
            monitorHealth.ReportSuccess("first-coop-awards", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("first-coop-awards", guildId, ex);
            Console.WriteLine($"First coop award monitor failed: {ex}");
        }
    }
}

async Task MonitorWeeklyTokenLeaderboardAsync(ulong guildId, CancellationToken cancellationToken) {
    try {
        await CheckWeeklyTokenLeaderboardPostAsync(guildId);
        monitorHealth.ReportSuccess("weekly-token-leaderboard", guildId);
    } catch(Exception ex) {
        monitorHealth.ReportFailure("weekly-token-leaderboard", guildId, ex);
        Console.WriteLine($"Weekly token leaderboard monitor failed: {ex}");
    }

    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while(await timer.WaitForNextTickAsync(cancellationToken)) {
        try {
            await CheckWeeklyTokenLeaderboardPostAsync(guildId);
            monitorHealth.ReportSuccess("weekly-token-leaderboard", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("weekly-token-leaderboard", guildId, ex);
            Console.WriteLine($"Weekly token leaderboard monitor failed: {ex}");
        }
    }
}

async Task MonitorFarmerRankUpsAsync(ulong guildId, CancellationToken cancellationToken) {
    try {
        await CheckFarmerRankUpsAsync(guildId);
        monitorHealth.ReportSuccess("farmer-rank-ups", guildId);
    } catch(Exception ex) {
        monitorHealth.ReportFailure("farmer-rank-ups", guildId, ex);
        Console.WriteLine($"Farmer rank-up monitor failed: {ex}");
    }

    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
    while(await timer.WaitForNextTickAsync(cancellationToken)) {
        try {
            await CheckFarmerRankUpsAsync(guildId);
            monitorHealth.ReportSuccess("farmer-rank-ups", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("farmer-rank-ups", guildId, ex);
            Console.WriteLine($"Farmer rank-up monitor failed: {ex}");
        }
    }
}

async Task CheckFarmerRankUpsAsync(ulong guildId) {
    var guild = client.GetGuild(guildId);
    var generalChannel = guild is null ? null : FindGeneralChannel(guild);
    if(guild is null || generalChannel is null) {
        Console.WriteLine("Farmer rank-up monitor could not find the guild or general text channel.");
        return;
    }

    var accounts = await dataStore.GetRegisteredEidsAsync(guildId);
    if(accounts.Count == 0) {
        return;
    }

    var backups = await GetAccountBackupsAsync(accounts);
    var previousByHash = (await dataStore.GetFarmerRankSnapshotsAsync(guildId))
        .ToDictionary(snapshot => snapshot.EidHash, StringComparer.OrdinalIgnoreCase);
    var comparisons = new List<(FarmerRankSnapshot Current, FarmerRankSnapshot? Previous)>();
    var goldenEggSnapshots = new List<GoldenEggSnapshot>();
    var capturedAt = DateTimeOffset.UtcNow;
    foreach(var item in backups) {
        var backup = item.Backup;
        if(backup?.Game is null) {
            continue;
        }

        var eb = GetEstimatedEarningsBonus(backup);
        var rank = GetFarmerRank(eb);
        var eggName = string.IsNullOrWhiteSpace(item.Account.EggName)
            ? backup.UserName
            : item.Account.EggName;
        var snapshot = new FarmerRankSnapshot(
            guildId,
            item.Account.DiscordUserId,
            item.Account.EidHash,
            eggName,
            rank.Oom,
            rank.Name,
            eb,
            capturedAt);
        goldenEggSnapshots.Add(new GoldenEggSnapshot(
            guildId,
            item.Account.DiscordUserId,
            item.Account.EidHash,
            eggName,
            backup.Game.GoldenEggsEarned,
            capturedAt));
        previousByHash.TryGetValue(item.Account.EidHash, out var previous);
        comparisons.Add((snapshot, previous));
    }

    await dataStore.UpsertFarmerRankSnapshotsAsync(comparisons.Select(item => item.Current).ToList());
    await dataStore.RecordGoldenEggSnapshotsAsync(goldenEggSnapshots);
    foreach(var comparison in comparisons) {
        var snapshot = comparison.Current;
        var previous = comparison.Previous;

        if(previous is null || snapshot.RankOom <= previous.RankOom) {
            continue;
        }

        var member = guild.GetUser(snapshot.DiscordUserId);
        var embed = BuildFarmerRankUpEmbed(member, snapshot.EggName, previous, snapshot);
        await generalChannel.SendMessageAsync(embed: embed, allowedMentions: AllowedMentions.None);
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

async Task HandlePollAsync(SocketSlashCommand command) {
    var duration = GetString(command, "duration");
    var audience = GetString(command, "audience");
    var modal = new ModalBuilder()
        .WithTitle("Create Plotty Poll")
        .WithCustomId($"poll-modal:{duration}:{audience}")
        .AddTextInput(
            label: "Poll title or question",
            customId: "title",
            style: TextInputStyle.Short,
            placeholder: "What should Plotty ask?",
            minLength: 3,
            maxLength: 200,
            required: true)
        .AddTextInput(
            label: "Poll options, one per line",
            customId: "options",
            style: TextInputStyle.Paragraph,
            placeholder: "Option one\nOption two\nOption three",
            minLength: 3,
            maxLength: 3000,
            required: true)
        .Build();

    await command.RespondWithModalAsync(modal);
}

async Task HandlePollModalAsync(SocketModal modal) {
    if(modal.GuildId is null) {
        await modal.RespondAsync("Use Plotty polls inside a server.", ephemeral: true);
        return;
    }

    var parts = modal.Data.CustomId.Split(':', 3);
    if(parts.Length != 3 || !int.TryParse(parts[1], out var durationHours) || durationHours is not (12 or 24)) {
        await modal.RespondAsync("That poll form expired or had an invalid duration. Run `/plotty-poll` again.", ephemeral: true);
        return;
    }

    var audience = parts[2];
    var title = GetModalValue(modal, "title").Trim();
    var options = GetModalValue(modal, "options")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(option => !string.IsNullOrWhiteSpace(option))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(pollVoteEmojis.Length + 1)
        .ToList();

    if(options.Count < 2) {
        await modal.RespondAsync("A poll needs at least two options, one per line.", ephemeral: true);
        return;
    }

    if(options.Count > pollVoteEmojis.Length) {
        await modal.RespondAsync($"Plotty reaction polls support up to `{pollVoteEmojis.Length}` options. Trim the list and try again.", ephemeral: true);
        return;
    }

    var guild = client.GetGuild(modal.GuildId.Value);
    var role = audience == "new-member" && guild is not null
        ? FindNewMemberPollRole(guild)
        : null;
    if(audience == "new-member" && role is null) {
        await modal.RespondAsync("I could not find a role named `New Member Poll`. Create that role or use the general poll option.", ephemeral: true);
        return;
    }

    if(modal.Channel is not IMessageChannel channel) {
        await modal.RespondAsync("Plotty could not access this channel for the poll.", ephemeral: true);
        return;
    }

    var endAt = DateTimeOffset.UtcNow.AddHours(durationHours);
    var selectedEmojis = pollVoteEmojis.Take(options.Count).ToList();
    var embed = BuildPollEmbed(title, options, selectedEmojis, endAt, closed: false);
    var content = role is null ? null : $"{role.Mention} New member poll: {title}";
    await modal.RespondAsync("Poll created.", ephemeral: true);
    var message = await channel.SendMessageAsync(
        text: content,
        embed: embed,
        allowedMentions: role is null
            ? AllowedMentions.None
            : new AllowedMentions { RoleIds = [role.Id] });
    await dataStore.SavePendingPollAsync(new PendingPoll(
        $"poll:{message.Id}",
        modal.GuildId.Value,
        channel.Id,
        message.Id,
        "general",
        title,
        null,
        options,
        selectedEmojis,
        endAt,
        DateTimeOffset.UtcNow));

    foreach(var emoji in selectedEmojis) {
        await message.AddReactionAsync(new Emoji(emoji));
    }
}

async Task HandleNewMemberPollAsync(SocketSlashCommand command) {
    var memberName = GetString(command, "member-name").Trim();
    var eb = GetString(command, "eb").Trim();
    if(string.IsNullOrWhiteSpace(memberName) || string.IsNullOrWhiteSpace(eb)) {
        await command.RespondAsync("Please include both the member name and EB for the new member poll.", ephemeral: true);
        return;
    }

    var guild = client.GetGuild(command.GuildId!.Value);
    var role = guild is null ? null : FindNewMemberPollRole(guild);
    if(role is null) {
        await command.RespondAsync("I could not find a role named `New Member Poll`. Create that role, then try again.", ephemeral: true);
        return;
    }

    if(command.Channel is not IMessageChannel channel) {
        await command.RespondAsync("I could not access this channel for the poll.", ephemeral: true);
        return;
    }

    var selectedEmojis = new[] { "✅", "❌" };
    var endAt = DateTimeOffset.UtcNow.AddHours(12);
    var embed = BuildNewMemberPollEmbed(memberName, eb, selectedEmojis, endAt, closed: false);

    await command.RespondAsync("New member poll created.", ephemeral: true);
    var message = await channel.SendMessageAsync(
        text: $"{role.Mention} New member poll for {memberName}.",
        embed: embed,
        allowedMentions: new AllowedMentions { RoleIds = [role.Id] });
    await dataStore.SavePendingPollAsync(new PendingPoll(
        $"poll:{message.Id}",
        command.GuildId.Value,
        channel.Id,
        message.Id,
        "new-member",
        memberName,
        eb,
        ["Yes", "No"],
        selectedEmojis,
        endAt,
        DateTimeOffset.UtcNow));

    foreach(var emoji in selectedEmojis) {
        await message.AddReactionAsync(new Emoji(emoji));
    }
}

Embed BuildNewMemberPollEmbed(
    string memberName,
    string eb,
    IReadOnlyList<string> emojis,
    DateTimeOffset endAt,
    bool closed) {
    var endUnix = endAt.ToUnixTimeSeconds();
    return new EmbedBuilder()
        .WithTitle(closed ? $"New Member Poll Closed - {memberName}" : $"New Member Poll - {memberName}")
        .WithColor(closed ? Color.DarkGrey : Color.Teal)
        .WithDescription(string.Join("\n", [
            $"**Member:** {memberName}",
            $"**EB:** {eb}",
            "",
            $"{emojis[0]} Yes",
            $"{emojis[1]} No",
            "",
            closed ? $"Poll closed <t:{endUnix}:R>." : $"Poll ends <t:{endUnix}:R>."
        ]))
        .WithFooter("Vote by reacting below. One vote per member is honor-system for now.")
        .WithCurrentTimestamp()
        .Build();
}

Embed BuildPollEmbed(string title, IReadOnlyList<string> options, IReadOnlyList<string> emojis, DateTimeOffset endAt, bool closed) {
    var lines = options
        .Select((option, index) => $"{emojis[index]} {option}")
        .ToList();
    var endUnix = endAt.ToUnixTimeSeconds();
    lines.Add("");
    lines.Add(closed
        ? $"Poll closed <t:{endUnix}:R>."
        : $"Poll ends <t:{endUnix}:R>.");

    return new EmbedBuilder()
        .WithTitle(closed ? $"Poll Closed - {title}" : title)
        .WithColor(closed ? Color.DarkGrey : Color.Teal)
        .WithDescription(string.Join("\n", lines))
        .WithFooter("Vote by reacting below. One vote per member is honor-system for now.")
        .WithCurrentTimestamp()
        .Build();
}

async Task MonitorPendingPollsAsync(ulong guildId, CancellationToken cancellationToken) {
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while(!cancellationToken.IsCancellationRequested) {
        try {
            await CloseDuePendingPollsAsync(guildId);
            monitorHealth.ReportSuccess("pending-polls", guildId);
        } catch(Exception ex) {
            monitorHealth.ReportFailure("pending-polls", guildId, ex);
            Console.WriteLine($"Pending poll monitor failed: {ex}");
        }

        if(!await timer.WaitForNextTickAsync(cancellationToken)) {
            break;
        }
    }
}

async Task CloseDuePendingPollsAsync(ulong guildId) {
    var polls = await dataStore.GetDuePendingPollsAsync(guildId, DateTimeOffset.UtcNow);
    foreach(var poll in polls) {
        var channel = client.GetChannel(poll.ChannelId) as IMessageChannel;
        if(channel is null) {
            await dataStore.RemovePendingPollAsync(poll.Key);
            continue;
        }

        try {
            if(await channel.GetMessageAsync(poll.MessageId) is not IUserMessage message) {
                await dataStore.RemovePendingPollAsync(poll.Key);
                continue;
            }

            var closedEmbed = string.Equals(poll.PollType, "new-member", StringComparison.OrdinalIgnoreCase)
                ? BuildNewMemberPollEmbed(poll.Title, poll.Subtitle ?? "Unknown", poll.Emojis, poll.EndAt, closed: true)
                : BuildPollEmbed(poll.Title, poll.Options, poll.Emojis, poll.EndAt, closed: true);
            await message.ModifyAsync(properties => properties.Embed = closedEmbed);
            await dataStore.RemovePendingPollAsync(poll.Key);
        } catch(Exception ex) {
            Console.WriteLine($"Could not close Plotty poll {poll.MessageId}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

async Task<IReadOnlyList<TokenLeaderboardEntry>> BuildTokenLeaderboardAsync(
    ulong guildId,
    DateTimeOffset weekStart,
    DateTimeOffset weekEnd) {
    var guild = client.GetGuild(guildId);
    var accounts = (await dataStore.GetRegisteredEidsAsync(guildId))
        .Where(account => !IsTokenLeaderboardExcluded(account, guild))
        .ToList();
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

    var recentContracts = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .Where(c => c.CoopAllowed)
        .Where(c => c.StartTime > 0)
        .OrderByDescending(c => c.StartTime)
        .Take(FirstCoopAwardRecentContractLimit)
        .ToList();
    var recentContractIds = recentContracts
        .Select(c => c.Identifier)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(recentContractIds.Count == 0) {
        Console.WriteLine("First coop award monitor did not find any recent co-op contracts.");
        return;
    }
    Console.WriteLine($"First coop award monitor scanning recent contracts: {string.Join(", ", recentContractIds)}");

    var lookups = await GetAccountCoopLookupsAsync(accounts);
    var completedStatuses = lookups
        .SelectMany(item => item.Lookup.StatusLookups
            .Where(s => s.Status.AllGoalsAchieved)
            .Where(s => recentContractIds.Contains(s.ContractId))
            .Where(s => CoopCodeEndsWithNumber(s.CoopCode))
            .Select(s => new FirstCoopCandidate(item.Account, s.ContractId, s.CoopCode, s.Status, s.AcceptedAt)))
        .GroupBy(s => (ContractId: s.ContractId.ToLowerInvariant(), CoopCode: s.CoopCode.ToLowerInvariant()))
        .Select(g => g.OrderByDescending(s => s.AcceptedAt).First())
        .ToList();
    if(completedStatuses.Count == 0) {
        Console.WriteLine($"First coop award monitor found {recentContractIds.Count} recent contract(s), but no completed registered co-op statuses with numeric co-op codes yet.");
        return;
    }
    Console.WriteLine($"First coop award monitor found {completedStatuses.Count} completed registered numeric-code co-op status(es).");

    foreach(var winner in completedStatuses
        .GroupBy(s => s.ContractId, StringComparer.OrdinalIgnoreCase)
        .Select(g => g
            .OrderBy(s => Math.Max(0, s.Status.SecondsSinceAllGoalsAchieved))
            .ThenByDescending(s => s.Status.TotalAmount)
            .First())) {
        var awardKey = $"first-coop:{guildId}:{winner.ContractId.ToLowerInvariant()}";
        if(await dataStore.HasFirstCoopAwardAsync(awardKey)) {
            Console.WriteLine($"First coop award already recorded for {winner.ContractId} in guild {guildId}.");
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
        Console.WriteLine($"First coop award posted for {winner.ContractId} in guild {guildId}.");
    }
}

static bool CoopCodeEndsWithNumber(string? coopCode) {
    if(string.IsNullOrWhiteSpace(coopCode)) {
        return false;
    }

    var trimmed = coopCode.Trim();
    return trimmed.Length > 0 && char.IsDigit(trimmed[^1]);
}

Embed BuildEgg9000ReportEmbed(
    Contract contract,
    DateTimeOffset startedAt,
    string checkpoint,
    IReadOnlyList<Egg9000ReportPlayer> players,
    bool includeReviewActions = true) {
    var checkText = checkpoint == "6h"
        ? "6 hours after contract start - not joined"
        : "18 hours after contract start - under 2q/hr";
    var lines = players
        .Take(35)
        .Select(player => {
            var memberText = player.DiscordUserId is { } userId
                ? MentionOrFallback(userId, player.PlayerName)
                : $"**{player.PlayerName}** (unresolved Discord user)";
            var detail = checkpoint == "6h"
                ? "not joined"
                : $"{player.Rate} ({FormatEggs(player.RatePerHour)}/hr)";
            return $"- {memberText} - {detail}";
        })
        .ToList();
    if(players.Count > lines.Count) {
        lines.Add($"...and {players.Count - lines.Count} more.");
    }

    var builder = new EmbedBuilder()
        .WithTitle($"EGG9000 AAA Report - {contract.Identifier}")
        .WithColor(Color.Orange)
        .WithDescription(lines.Count == 0 ? "No members need attention." : string.Join("\n", lines))
        .AddField("Contract", string.IsNullOrWhiteSpace(contract.Name) ? contract.Identifier : contract.Name, true)
        .AddField("Check", checkText, true)
        .AddField("Started", $"{startedAt.LocalDateTime:yyyy-MM-dd HH:mm} local", true)
        .WithFooter("Scraped from EGG9000 AAA co-ops for [The Plot Chickens]. The report itself does not ping anyone.")
        .WithCurrentTimestamp();

    var reviewText = players.Count == 0
        ? "Everything looks clear. No notices or demerits are needed."
        : includeReviewActions
            ? "Does this look alright? Sending creates or reuses each matched member's #naughty-list thread and adds the standard demerit. Unresolved members are skipped."
            : "Manual preview only. No messages or demerits can be sent from this report.";
    builder.AddField("Review", reviewText, false);

    return builder.Build();
}

MessageComponent BuildEgg9000ReportComponents(
    string contractId,
    string checkpoint) {
    return new ComponentBuilder()
        .WithButton(
            "Send All Notices",
            $"e9k-report-send:{contractId}:{checkpoint}:all",
            ButtonStyle.Danger)
        .WithButton(
            "Skip Report",
            $"e9k-report-ignore:{contractId}:{checkpoint}:all",
            ButtonStyle.Secondary)
        .Build();
}

async Task HandleEgg9000ReportButtonAsync(SocketMessageComponent component) {
    var staffUser = component.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await component.RespondAsync("Only members with the Staff role can approve EGG9000 reports.", ephemeral: true);
        return;
    }

    var parts = component.Data.CustomId.Split(':', 4);
    if(parts.Length != 4 ||
       !parts[0].StartsWith("e9k-report-", StringComparison.Ordinal) ||
       string.IsNullOrWhiteSpace(parts[1]) ||
       (parts[2] != "6h" && parts[2] != "18h") ||
       (parts[3] != "all" && parts[3] != "unresolved" && !ulong.TryParse(parts[3], out _))) {
        await component.RespondAsync("That EGG9000 report button is no longer valid.", ephemeral: true);
        return;
    }

    var action = parts[0]["e9k-report-".Length..];
    var contractId = parts[1];
    var checkpoint = parts[2];
    var targetText = parts[3];
    if(action == "ignore") {
        await component.UpdateAsync(message => {
            message.Components = new ComponentBuilder()
                .WithButton("Report skipped", $"e9k-report-done:{contractId}:{checkpoint}:{targetText}", ButtonStyle.Secondary, disabled: true)
                .Build();
        });
        await component.FollowupAsync("Okay. I skipped this report and did not send notices or add demerits.", ephemeral: true);
        return;
    }

    if(action != "send") {
        await component.RespondAsync("That EGG9000 report action is not recognized anymore.", ephemeral: true);
        return;
    }

    if(targetText != "all" && !ulong.TryParse(targetText, out _)) {
        await component.RespondAsync("That EGG9000 player is not linked to a Discord member, so I cannot send a notice.", ephemeral: true);
        return;
    }

    await component.DeferAsync(ephemeral: true);

    var guild = component.GuildId is { } guildId ? client.GetGuild(guildId) : null;
    var naughtyChannel = guild is null ? null : FindNaughtyListChannel(guild);
    if(guild is null || naughtyChannel is null) {
        await component.FollowupAsync("I could not find this server or #naughty-list.", ephemeral: true);
        return;
    }

    var contract = (await eggClient.GetCurrentContractsAsync())
        .Where(c => string.Equals(c.Identifier, contractId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(c => c.StartTime)
        .FirstOrDefault();
    if(contract is null) {
        await component.FollowupAsync("I could not find that contract in the current contract list anymore.", ephemeral: true);
        return;
    }

    var players = await BuildEgg9000ReportPlayersAsync(guild, contract, checkpoint);
    if(players is null) {
        await component.FollowupAsync("I could not scrape EGG9000 for that contract right now.", ephemeral: true);
        return;
    }

    var targets = targetText == "all"
        ? players
            .Where(player => player.DiscordUserId is not null)
            .GroupBy(player => player.DiscordUserId!.Value)
            .Select(group => group.First())
            .ToList()
        : players
            .Where(player => player.DiscordUserId?.ToString() == targetText)
            .Take(1)
            .ToList();
    if(targets.Count == 0) {
        await component.FollowupAsync("No matched members are still listed in this report, so I did not send notices or add demerits.", ephemeral: true);
        return;
    }

    var sent = 0;
    var duplicateDemerits = 0;
    var missingMembers = 0;
    var failed = 0;
    foreach(var player in targets) {
        var member = player.DiscordUserId is { } userId ? guild.GetUser(userId) : null;
        if(member is null) {
            missingMembers++;
            continue;
        }

        try {
            var sourceKey = $"e9k-report:{guild.Id}:{contractId.ToLowerInvariant()}:{checkpoint}:{member.Id}";
            var demeritAdded = await SendEgg9000DemeritNoticeAsync(
                guild,
                naughtyChannel,
                member,
                player,
                contractId,
                checkpoint,
                sourceKey);
            sent++;
            if(!demeritAdded) {
                duplicateDemerits++;
            }
        } catch(Exception ex) {
            failed++;
            Console.WriteLine($"Could not send EGG9000 report notice for {member.Id}: {ex}");
        }
    }

    await component.ModifyOriginalResponseAsync(message => {
        message.Components = new ComponentBuilder()
            .WithButton("Report processed", $"e9k-report-done:{contractId}:{checkpoint}:all", ButtonStyle.Secondary, disabled: true)
            .Build();
    });

    var result = $"Processed the combined report: sent {sent} notice(s) in #naughty-list.";
    if(duplicateDemerits > 0) {
        result += $" {duplicateDemerits} duplicate demerit(s) were not added again.";
    }
    if(missingMembers > 0) {
        result += $" Skipped {missingMembers} member(s) who are no longer in the server.";
    }
    if(failed > 0) {
        result += $" {failed} notice(s) failed; check Plotty's log for details.";
    }

    await component.FollowupAsync(result, ephemeral: true, allowedMentions: AllowedMentions.None);
}

async Task<bool> SendEgg9000DemeritNoticeAsync(
    SocketGuild guild,
    SocketTextChannel naughtyChannel,
    SocketGuildUser member,
    Egg9000ReportPlayer player,
    string contractId,
    string checkpoint,
    string sourceKey) {
    var (notice, reason) = DemeritNoticeTemplate(checkpoint == "6h" ? "6hr" : "18hr", manual: false);
    var added = await dataStore.AddDemeritIfSourceNewAsync(
        guild.Id,
        member.Id,
        reason,
        contractId,
        sourceKey,
        player.PlayerName);
    var activeDemeritCount = (await dataStore.GetActiveDemeritsAsync(guild.Id, member.Id)).Count;
    var fullNotice = $"{notice} You currently have {activeDemeritCount} demerits.";
    var thread = await GetOrCreateNaughtyListThreadAsync(guild, naughtyChannel, member, player.PlayerName, addStaff: false);
    var messageText = checkpoint == "18h"
        ? $"{member.Mention} {fullNotice}\n\n{FormatEgg9000FarmStatusLine(player)}"
        : $"{member.Mention} {fullNotice}";
    var message = await thread.SendMessageAsync(
        messageText,
        allowedMentions: new AllowedMentions { UserIds = [member.Id] });
    await PulseStaffMentionAsync(guild, message, messageText);
    return added;
}

static string FormatEgg9000FarmStatusLine(Egg9000ReportPlayer player) {
    var guildTag = string.IsNullOrWhiteSpace(player.GuildTag)
        ? ""
        : $" [{player.GuildTag.Trim()}]";
    return $"{player.PlayerName}{guildTag}    {player.Chickens}    {player.Rate}    {player.Projected}";
}

async Task<SocketThreadChannel> GetOrCreateNaughtyListThreadAsync(
    SocketGuild guild,
    SocketTextChannel naughtyChannel,
    SocketGuildUser member,
    string? threadTitle = null,
    bool addStaff = true) {
    var savedThreadId = await dataStore.GetNaughtyListThreadIdAsync(guild.Id, member.Id);
    if(savedThreadId is { } threadId) {
        var existing = guild.ThreadChannels.FirstOrDefault(thread => thread.Id == threadId)
            ?? client.GetChannel(threadId) as SocketThreadChannel;
        if(existing is not null) {
            await EnsureThreadMemberAsync(existing, member);
            if(addStaff) {
                await AddStaffToThreadAsync(guild, existing);
            }

            return existing;
        }
    }

    var fallback = FindExistingNaughtyListThread(guild, naughtyChannel, member, threadTitle);
    if(fallback is not null) {
        await EnsureThreadMemberAsync(fallback, member);
        if(addStaff) {
            await AddStaffToThreadAsync(guild, fallback);
        }

        await dataStore.UpsertNaughtyListThreadAsync(guild.Id, member.Id, fallback.Id);
        return fallback;
    }

    var title = string.IsNullOrWhiteSpace(threadTitle) ? member.DisplayName : threadTitle;
    var thread = await naughtyChannel.CreateThreadAsync(
        TrimForDiscordThreadName(title),
        ThreadType.PrivateThread,
        ThreadArchiveDuration.OneDay);
    await EnsureThreadMemberAsync(thread, member);
    if(addStaff) {
        await AddStaffToThreadAsync(guild, thread);
    }

    await dataStore.UpsertNaughtyListThreadAsync(guild.Id, member.Id, thread.Id);
    return thread;
}

SocketThreadChannel? FindExistingNaughtyListThread(
    SocketGuild guild,
    SocketTextChannel naughtyChannel,
    SocketGuildUser member,
    string? threadTitle) {
    var possibleNames = new[] {
            threadTitle,
            member.DisplayName,
            member.Username,
            member.GlobalName
        }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => NormalizeName(name!))
        .Where(name => name.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(possibleNames.Count == 0) {
        return null;
    }

    return guild.ThreadChannels
        .Where(thread => thread.ParentChannel?.Id == naughtyChannel.Id)
        .Where(thread => possibleNames.Contains(NormalizeName(thread.Name)))
        .OrderByDescending(thread => thread.CreatedAt)
        .FirstOrDefault();
}

async Task AddStaffToThreadAsync(SocketGuild guild, SocketThreadChannel thread) {
    foreach(var staffMember in guild.Users.Where(user => !user.IsBot && HasStaffRole(user))) {
        await EnsureThreadMemberAsync(thread, staffMember);
    }
}

async Task PulseStaffMentionAsync(SocketGuild guild, IUserMessage message, string originalText) {
    var staffRole = FindStaffRole(guild);
    if(staffRole is null) {
        Console.WriteLine($"Could not pulse Staff mention in {guild.Name} ({guild.Id}); Staff role was not found.");
        return;
    }

    try {
        await Task.Delay(TimeSpan.FromSeconds(1));
        await message.ModifyAsync(properties => {
            properties.Content = $"{originalText}\n{staffRole.Mention}";
            properties.AllowedMentions = new AllowedMentions { RoleIds = [staffRole.Id] };
        });
        await Task.Delay(TimeSpan.FromSeconds(2));
        await message.ModifyAsync(properties => {
            properties.Content = originalText;
            properties.AllowedMentions = AllowedMentions.None;
        });
    } catch(Exception ex) {
        Console.WriteLine($"Could not pulse Staff mention for message {message.Id} in {guild.Name} ({guild.Id}): {ex.Message}");
    }
}

static async Task EnsureThreadMemberAsync(SocketThreadChannel thread, SocketGuildUser member) {
    try {
        await thread.AddUserAsync(member);
    } catch(Exception ex) {
        Console.WriteLine($"Could not add {member.Id} to thread {thread.Id}: {ex.Message}");
    }
}

Embed BuildFirstCoopAwardEmbed(ContractCoopStatusResponse status) {
    var contractName = string.IsNullOrWhiteSpace(status.ContractIdentifier)
        ? "A contract"
        : status.ContractIdentifier;
    var contributorLines = status.Contributors
        .OrderByDescending(c => c.ContributionAmount)
        .Select((c, index) => {
            var name = string.IsNullOrWhiteSpace(c.UserName) ? "(unknown)" : c.UserName;
            return $"{index + 1}. **{name}** - {FormatEggs(c.ContributionAmount)} contributed";
        })
        .ToList();
    if(contributorLines.Count == 0) {
        contributorLines.Add("No contributor rows were available.");
    }

    var completedAgo = status.SecondsSinceAllGoalsAchieved > 0
        ? $"Completed about {FormatDuration(TimeSpan.FromSeconds(status.SecondsSinceAllGoalsAchieved))} ago."
        : "Completed moments ago.";

    var builder = new EmbedBuilder()
        .WithTitle("First Co-op Finish Award")
        .WithColor(Color.Green)
        .WithDescription($"A registered co-op finished **{contractName}** first. Nicely done.")
        .AddField("Result", completedAgo, true)
        .AddField("Members", status.Contributors.Count.ToString("0"), true)
        .AddField("Total Eggs", FormatEggs(status.TotalAmount), true)
        .WithFooter("Co-op name hidden for privacy.")
        .WithCurrentTimestamp();

    var allChunks = BuildDiscordFieldChunks(contributorLines, maxLength: 1000);
    var chunks = allChunks.Take(20).ToList();
    for(var i = 0; i < chunks.Count; i++) {
        builder.AddField(i == 0 ? "Contributors" : $"Contributors {i + 1}", chunks[i], false);
    }

    if(allChunks.Count > chunks.Count) {
        builder.AddField("More Contributors", "The contributor list was too long for one Discord embed.", false);
    }

    return builder.Build();
}

async Task<bool> TryPostReportThreadAsync(
    SocketTextChannel reportChannel,
    string threadName,
    Embed embed,
    string channelLabel,
    MessageComponent? components = null) {
    try {
        var thread = await reportChannel.CreateThreadAsync(
            TrimForDiscordThreadName(threadName),
            ThreadType.PublicThread,
            ThreadArchiveDuration.OneDay);
        await thread.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None);
        return true;
    } catch(Exception ex) {
        Console.WriteLine($"Could not create {channelLabel} report thread: {ex}");
        return false;
    }
}

static SocketTextChannel? FindPlottyReportsChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "plottyreports");

static SocketTextChannel? FindEgg9000ReportChannel(SocketGuild guild) =>
    FindPlottyReportsChannel(guild) ?? FindNaughtyListChannel(guild);

static SocketTextChannel? FindNaughtyListChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) is "naughtylist" or "naughtlist");

static SocketTextChannel? FindGeneralChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "general");

static SocketTextChannel? FindPlottyQuestionsChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "plottyquestions");

static SocketTextChannel? FindModLogChannel(SocketGuild guild) =>
    guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "modlog");

static SocketRole? FindNewMemberPollRole(SocketGuild guild) =>
    guild.Roles.FirstOrDefault(r => NormalizeName(r.Name) == "newmemberpoll");

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

async Task HandleAdminDemeritAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can manage demerits.", ephemeral: true);
        return;
    }

    var action = GetString(command, "action");
    var amount = Math.Max(1, Convert.ToInt32(command.Data.Options.FirstOrDefault(o => o.Name == "amount")?.Value ?? 1));

    if(action == "add") {
        var member = ResolveGuildMemberOption(command, "member");
        if(member is null) {
            await command.RespondAsync("Select a server member when adding demerits.", ephemeral: true);
            return;
        }

        var reason = command.Data.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string;
        var added = await dataStore.AddDemeritsAsync(
            command.GuildId!.Value,
            member.Id,
            amount,
            reason ?? "Manual staff demerit",
            contractId: null,
            sourceKey: null);
        await command.RespondAsync($"Added `{added}` demerit(s) to {member.Mention}.", ephemeral: true);
        return;
    }

    if(action == "remove") {
        var target = ResolveDemeritTarget(command);
        if(target.UserId is null) {
            await command.RespondAsync("Select a server member or enter a Discord user ID from `/admin-demerits-view-all`.", ephemeral: true);
            return;
        }

        var removed = await dataStore.RemoveDemeritsAsync(command.GuildId!.Value, target.UserId.Value, amount);
        await command.RespondAsync($"Removed `{removed}` active demerit(s) from {target.Label}.", ephemeral: true);
        return;
    }

    await command.RespondAsync("Choose `add` or `remove` for the demerit action.", ephemeral: true);
}

async Task HandleAdminPlottySendDemeritAsync(SocketSlashCommand command) {
    var staffUser = command.User as SocketGuildUser;
    if(staffUser is null || !HasStaffRole(staffUser)) {
        await command.RespondAsync("Only members with the Staff role can send demerit notices.", ephemeral: true);
        return;
    }

    var member = ResolveGuildMemberOption(command, "member");
    if(member is null) {
        await command.RespondAsync("I can only send demerit notices to members in this server.", ephemeral: true);
        return;
    }

    var messageType = GetString(command, "message");
    var (notice, reason) = DemeritNoticeTemplate(messageType, manual: true);

    await dataStore.AddDemeritsAsync(
        command.GuildId!.Value,
        member.Id,
        1,
        reason,
        contractId: null,
        sourceKey: null);
    var activeDemeritCount = (await dataStore.GetActiveDemeritsAsync(command.GuildId.Value, member.Id)).Count;
    var fullNotice = $"{notice} You currently have {activeDemeritCount} demerits.";

    if(command.Channel is IMessageChannel channel) {
        await channel.SendMessageAsync(
            $"{member.Mention} {fullNotice}",
            allowedMentions: new AllowedMentions { UserIds = [member.Id] });
    }

    await command.RespondAsync($"Sent the `{messageType}` demerit notice to {member.Mention} and added 1 demerit.", ephemeral: true);
}

(string Notice, string Reason) DemeritNoticeTemplate(string messageType, bool manual) => messageType switch {
    "6hr" => (
        "You've failed to meet the guild requirement of joining a contract within 6 hours. 1 demerit has been added to your account.",
        manual ? "Manual staff notice: failed to join within 6 hours" : "EGG9000 report: failed to join within 6 hours"),
    "18hr" => (
        "You've failed to meet the guild requirement of 2q/hr after 18 hours. 1 demerit has been added to your account.",
        manual ? "Manual staff notice: under 2q/hr after 18 hours" : "EGG9000 report: under 2q/hr after 18 hours"),
    _ => (
        "1 demerit has been added to your account.",
        manual ? "Manual staff demerit notice" : "EGG9000 report demerit notice")
};

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
    AsyncBatch.SelectWithConcurrencyAsync(
        accounts.ToList(),
        MaxEggIncAccountConcurrency,
        async account => (account, await eggClient.GetBackupAsync(account.Eid)));

Task<IReadOnlyList<(RegisteredEggAccount Account, PlayerCoopLookupResult Lookup)>> GetAccountCoopLookupsAsync(
    IEnumerable<RegisteredEggAccount> accounts) =>
    AsyncBatch.SelectWithConcurrencyAsync(
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
            embeds.Add(BuildEggsLaidEmbed(backup, account, client.GetGuild(command.GuildId.Value)));
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

async Task HandleMyEggCountAsync(SocketSlashCommand command) {
    var showPublic = GetBool(command, "public");
    await command.DeferAsync(ephemeral: !showPublic);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync(
            "You do not have an EID registered yet. Run `/register-eid` first.",
            ephemeral: !showPublic);
        return;
    }

    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    var embeds = backups
        .Where(item => item.Backup is not null)
        .Select(item => BuildMyEggCountEmbed(item.Account, item.Backup!))
        .ToArray();

    if(embeds.Length == 0) {
        await command.FollowupAsync(
            "I could not pull your Egg Inc backup right now. Please try again in a bit.",
            ephemeral: !showPublic);
        return;
    }

    await command.FollowupAsync(
        text: accounts.Count > 1 ? $"Showing `{embeds.Length}` Egg Inc account(s) separately." : null,
        embeds: embeds,
        ephemeral: !showPublic,
        allowedMentions: AllowedMentions.None);
}

async Task HandleGoldenEggsAsync(SocketSlashCommand command) {
    var showPublic = GetBool(command, "public");
    await command.DeferAsync(ephemeral: !showPublic);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync(
            "You do not have an EID registered yet. Run `/register-eid` first.",
            ephemeral: !showPublic);
        return;
    }

    var capturedAt = DateTimeOffset.UtcNow;
    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    var currentSnapshots = backups
        .Where(item => item.Backup?.Game is not null)
        .Select(item => new GoldenEggSnapshot(
            command.GuildId.Value,
            item.Account.DiscordUserId,
            item.Account.EidHash,
            string.IsNullOrWhiteSpace(item.Account.EggName) ? item.Backup!.UserName : item.Account.EggName,
            item.Backup!.Game.GoldenEggsEarned,
            capturedAt))
        .ToList();

    if(currentSnapshots.Count == 0) {
        await command.FollowupAsync(
            "I could not pull your Golden Egg counters right now. Please try again in a bit.",
            ephemeral: !showPublic);
        return;
    }

    await dataStore.RecordGoldenEggSnapshotsAsync(currentSnapshots);
    var embeds = new List<Embed>();
    foreach(var current in currentSnapshots) {
        var history = await dataStore.GetGoldenEggSnapshotsAsync(command.GuildId.Value, current.EidHash);
        embeds.Add(BuildGoldenEggProgressEmbed(current, history, capturedAt));
    }

    await command.FollowupAsync(
        text: accounts.Count > 1 ? $"Showing `{embeds.Count}` Egg Inc account(s) separately." : null,
        embeds: embeds.ToArray(),
        ephemeral: !showPublic,
        allowedMentions: AllowedMentions.None);
}

async Task HandleEggMilestonesAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    var embeds = backups
        .Where(item => item.Backup is not null)
        .Select(item => BuildEggMilestonesEmbed(item.Account, item.Backup!))
        .ToArray();

    if(embeds.Length == 0) {
        await command.FollowupAsync("Plotty could not pull your Egg Inc backup right now. Please try again in a bit.", ephemeral: true);
        return;
    }

    await command.FollowupAsync(
        text: accounts.Count > 1 ? $"Showing milestones for `{embeds.Length}` Egg Inc account(s) tied to your Discord name." : null,
        embeds: embeds,
        ephemeral: true);
}

async Task HandleRivalryAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var memberOne = command.Data.Options.First(o => o.Name == "member-one").Value as SocketGuildUser;
    var memberTwo = command.Data.Options.First(o => o.Name == "member-two").Value as SocketGuildUser;
    if(memberOne is null || memberTwo is null) {
        await command.FollowupAsync("Pick two server members for the rivalry.", ephemeral: true);
        return;
    }

    var accountsOne = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, memberOne.Id);
    var accountsTwo = await dataStore.GetRegisteredAccountsAsync(command.GuildId.Value, memberTwo.Id);
    if(accountsOne.Count == 0 || accountsTwo.Count == 0) {
        var missing = new List<string>();
        if(accountsOne.Count == 0) {
            missing.Add(memberOne.DisplayName);
        }

        if(accountsTwo.Count == 0) {
            missing.Add(memberTwo.DisplayName);
        }

        await command.FollowupAsync($"{string.Join(" and ", missing)} need to register an EID before Plotty can compare them.", ephemeral: true);
        return;
    }

    var snapshotsOne = await BuildRivalrySnapshotsAsync(accountsOne);
    var snapshotsTwo = await BuildRivalrySnapshotsAsync(accountsTwo);
    if(snapshotsOne.Count == 0 || snapshotsTwo.Count == 0) {
        await command.FollowupAsync("Plotty could not pull enough backup data for that rivalry right now.", ephemeral: true);
        return;
    }

    var embed = BuildRivalryEmbed(memberOne, snapshotsOne, memberTwo, snapshotsTwo);
    await command.FollowupAsync(embed: embed, ephemeral: true);
}

async Task HandleEggFlexAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    var embeds = backups
        .Where(item => item.Backup?.Game is not null)
        .Select(item => BuildEggFlexEmbed(item.Account, item.Backup!, command.User))
        .ToArray();

    if(embeds.Length == 0) {
        await command.FollowupAsync("Plotty could not pull your Egg Inc backup right now. Please try again in a bit.", ephemeral: true);
        return;
    }

    await command.FollowupAsync(embeds: embeds, ephemeral: true);
}

async Task HandleContractMvpAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var lookups = await GetAccountCoopLookupsAsync(accounts.Take(10));
    var embeds = BuildUniqueActiveStatuses(lookups)
        .Select(item => BuildContractMvpEmbed(item.Status))
        .ToArray();

    if(embeds.Length == 0) {
        await command.FollowupAsync("Plotty could not find active co-op status data for your registered EID account(s).", ephemeral: true);
        return;
    }

    await command.FollowupAsync(embeds: embeds.Take(10).ToArray(), ephemeral: true);
}

async Task HandleContractPredictionsAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var currentContracts = (await eggClient.GetCurrentContractsAsync())
        .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
        .GroupBy(c => c.Identifier, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    var lookups = await GetAccountCoopLookupsAsync(accounts.Take(10));
    var embeds = BuildUniqueActiveStatuses(lookups)
        .Select(item => {
            var contract = FindContractDefinition(item.Lookup.Backup, item.Status.ContractIdentifier);
            if(contract is null &&
               !string.IsNullOrWhiteSpace(item.Status.ContractIdentifier) &&
               currentContracts.TryGetValue(item.Status.ContractIdentifier, out var currentContract)) {
                contract = currentContract;
            }

            return BuildContractPredictionEmbed(item.Status, contract);
        })
        .ToArray();

    if(embeds.Length == 0) {
        await command.FollowupAsync("Plotty could not find active co-op status data for your registered EID account(s).", ephemeral: true);
        return;
    }

    await command.FollowupAsync(embeds: embeds.Take(10).ToArray(), ephemeral: true);
}

async Task HandlePlottyAchievementsAsync(SocketSlashCommand command) {
    await command.DeferAsync(ephemeral: true);

    var accounts = await dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
    if(accounts.Count == 0) {
        await command.FollowupAsync("You do not have an EID registered yet. Run `/register-eid` first.", ephemeral: true);
        return;
    }

    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    var demerits = await dataStore.GetActiveDemeritsAsync(command.GuildId.Value, command.User.Id);
    var beverageStats = (await dataStore.GetBeerLeaderboardAsync(command.GuildId.Value))
        .FirstOrDefault(s => s.DiscordUserId == command.User.Id);
    var embeds = backups
        .Where(item => item.Backup?.Game is not null)
        .Select(item => BuildPlottyAchievementsEmbed(item.Account, item.Backup!, demerits.Count, beverageStats))
        .ToArray();

    if(embeds.Length == 0) {
        await command.FollowupAsync("Plotty could not pull your Egg Inc backup right now. Please try again in a bit.", ephemeral: true);
        return;
    }

    await command.FollowupAsync(embeds: embeds, ephemeral: true);
}

Embed BuildEggMilestonesEmbed(RegisteredEggAccount account, Backup backup) {
    var game = backup.Game;
    var soulEggs = GetSoulEggs(game);
    var prophecyEggs = (double)game.EggsOfProphecy;
    var earningsBonus = GetEstimatedEarningsBonus(backup);
    var currentRank = GetFarmerRank(earningsBonus);
    var nextRank = GetFarmerRankByOom(Math.Min(currentRank.Oom + 1, GetFarmerRanks().Length - 1));
    var nextRankEb = Math.Pow(10, nextRank.Oom) * 100d;
    var nextSeOom = soulEggs <= 0 ? 1d : Math.Pow(10, Math.Floor(Math.Log10(soulEggs)) + 1);
    var seNeeded = Math.Max(0, nextSeOom - soulEggs);
    var totals = BuildEggsLaidTotals(backup)
        .Where(total => total.Amount > 0)
        .OrderByDescending(total => total.Amount)
        .ToList();
    var topEgg = totals.FirstOrDefault();
    var activeShips = GetActiveShipMissions(backup).ToList();
    var activeContracts = backup.Contracts?.Contracts.Count(c => !c.Cancelled) ?? 0;
    var completedContracts = backup.Contracts?.Archive.Count ?? 0;

    var rankLine = currentRank.Oom >= nextRank.Oom
        ? $"Current rank: **{currentRank.Name}**. You're already at the top known farmer tier Plotty tracks."
        : $"Current rank: **{currentRank.Name}**. Next: **{nextRank.Name}** at about **{FormatEggs(nextRankEb)}% EB**.";

    var lines = new List<string> {
        rankLine,
        $"Estimated EB: **{FormatEggs(earningsBonus)}%**",
        $"Soul Eggs: **{FormatEggs(soulEggs)} SE**. Next SE order: **{FormatEggs(nextSeOom)}**, needs **{FormatEggs(seNeeded)}** more.",
        $"Prophecy Eggs: **{prophecyEggs:0} PE**",
        $"Contracts tracked: **{activeContracts} active** / **{completedContracts} completed**"
    };

    if(topEgg is not null) {
        var nextEggMilestone = NextNiceMilestone(topEgg.Amount);
        lines.Add($"Top eggs laid: **{topEgg.Name}** at **{FormatEggs(topEgg.Amount)}**. Next shiny number: **{FormatEggs(nextEggMilestone)}**.");
    }

    lines.Add(activeShips.Count == 0
        ? "Ships: no active ship mission found."
        : $"Ships: **{activeShips.Count} active**, next return **{FormatNullableTimestamp(activeShips.Min(s => s.ReturnAt))}**.");

    return new EmbedBuilder()
        .WithTitle($"Egg Milestones - {AccountDisplayName(account)}")
        .WithColor(Color.Gold)
        .WithDescription(string.Join("\n", lines))
        .WithFooter("Uses your latest registered Egg Inc backup. Registered EIDs are still stored separately.")
        .WithCurrentTimestamp()
        .Build();
}

async Task<IReadOnlyList<RivalrySnapshot>> BuildRivalrySnapshotsAsync(IReadOnlyList<RegisteredEggAccount> accounts) {
    var backups = await GetAccountBackupsAsync(accounts.Take(10));
    return backups
        .Where(item => item.Backup?.Game is not null)
        .Select(item => BuildRivalrySnapshot(item.Account, item.Backup!))
        .ToList();
}

RivalrySnapshot BuildRivalrySnapshot(RegisteredEggAccount account, Backup backup) {
    var game = backup.Game;
    var totals = BuildEggsLaidTotals(backup).Where(total => total.Amount > 0).ToList();
    var topEgg = totals.OrderByDescending(total => total.Amount).FirstOrDefault();
    var totalEggs = totals.Sum(total => total.Amount);
    var earningsBonus = GetEstimatedEarningsBonus(backup);
    return new RivalrySnapshot(
        account,
        AccountDisplayName(account),
        earningsBonus,
        GetSoulEggs(game),
        (double)game.EggsOfProphecy,
        GetFarmerRank(earningsBonus).Name,
        totalEggs,
        topEgg?.Name ?? "Unknown",
        topEgg?.Amount ?? 0,
        backup.Contracts?.Contracts.Count(c => !c.Cancelled) ?? 0,
        backup.Contracts?.Archive.Count ?? 0,
        GetActiveShipMissions(backup).Count());
}

Embed BuildRivalryEmbed(SocketGuildUser memberOne, IReadOnlyList<RivalrySnapshot> one, SocketGuildUser memberTwo, IReadOnlyList<RivalrySnapshot> two) {
    var bestOne = one.OrderByDescending(s => s.EarningsBonus).First();
    var bestTwo = two.OrderByDescending(s => s.EarningsBonus).First();
    var categories = new[] {
        CompareRivalry("EB", bestOne.EarningsBonus, bestTwo.EarningsBonus, "%", FormatEggs),
        CompareRivalry("SE", bestOne.SoulEggs, bestTwo.SoulEggs, " SE", FormatEggs),
        CompareRivalry("PE", bestOne.ProphecyEggs, bestTwo.ProphecyEggs, " PE", value => value.ToString("0")),
        CompareRivalry("Eggs Laid", bestOne.TotalEggsLaid, bestTwo.TotalEggsLaid, "", FormatEggs),
        CompareRivalry("Completed Contracts", bestOne.CompletedContracts, bestTwo.CompletedContracts, "", value => value.ToString("0")),
        CompareRivalry("Active Ships", bestOne.ActiveShips, bestTwo.ActiveShips, "", value => value.ToString("0"))
    };
    var winsOne = categories.Count(c => c.Winner == 1);
    var winsTwo = categories.Count(c => c.Winner == 2);
    var verdict = winsOne == winsTwo
        ? "This rivalry is currently too close for Plotty to call."
        : winsOne > winsTwo
            ? $"{memberOne.DisplayName} is holding the bragging rights for now."
            : $"{memberTwo.DisplayName} is holding the bragging rights for now.";

    var lines = categories.Select(c => {
        var winner = c.Winner switch {
            1 => memberOne.DisplayName,
            2 => memberTwo.DisplayName,
            _ => "Tie"
        };
        return $"**{c.Label}:** {c.Left} vs {c.Right} - {winner}";
    });

    return new EmbedBuilder()
        .WithTitle($"Friendly Rivalry - {memberOne.DisplayName} vs {memberTwo.DisplayName}")
        .WithColor(Color.Orange)
        .WithDescription(string.Join("\n", lines) + $"\n\n**Plotty verdict:** {verdict}")
        .AddField(memberOne.DisplayName, FormatRivalryAccountList(one), true)
        .AddField(memberTwo.DisplayName, FormatRivalryAccountList(two), true)
        .WithFooter("Uses each member's strongest EB account for category comparisons. EIDs are not combined.")
        .WithCurrentTimestamp()
        .Build();
}

static RivalryCategory CompareRivalry(
    string label,
    double left,
    double right,
    string suffix,
    Func<double, string> formatter) {
    var winner = Math.Abs(left - right) < 0.0001 ? 0 : left > right ? 1 : 2;
    return new RivalryCategory(label, $"{formatter(left)}{suffix}", $"{formatter(right)}{suffix}", winner);
}

static string FormatRivalryAccountList(IReadOnlyList<RivalrySnapshot> snapshots) =>
    string.Join("\n", snapshots
        .OrderByDescending(snapshot => snapshot.EarningsBonus)
        .Take(5)
        .Select(snapshot => $"**{snapshot.PlayerName}** - {FormatEggs(snapshot.EarningsBonus)}% EB, {snapshot.FarmerRank}, top egg {snapshot.TopEggName}"));

static double NextNiceMilestone(double value) {
    if(value <= 0) {
        return 1;
    }

    var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
    var normalized = value / magnitude;
    var next = normalized < 2.5 ? 2.5 : normalized < 5 ? 5 : normalized < 7.5 ? 7.5 : 10;
    return next * magnitude;
}

static string FormatNullableTimestamp(DateTimeOffset? value) =>
    value is null ? "unknown" : $"<t:{value.Value.ToUnixTimeSeconds()}:R>";

static IReadOnlyList<(RegisteredEggAccount Account, PlayerCoopLookupResult Lookup, ContractCoopStatusResponse Status)> BuildUniqueActiveStatuses(
    IReadOnlyList<(RegisteredEggAccount Account, PlayerCoopLookupResult Lookup)> lookups) {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var result = new List<(RegisteredEggAccount Account, PlayerCoopLookupResult Lookup, ContractCoopStatusResponse Status)>();
    foreach(var item in lookups) {
        foreach(var statusLookup in item.Lookup.StatusLookups.OrderByDescending(s => s.AcceptedAt)) {
            var status = statusLookup.Status;
            if(status.AllGoalsAchieved) {
                continue;
            }

            var key = $"{status.ContractIdentifier}:{status.CoopIdentifier}";
            if(seen.Add(key)) {
                result.Add((item.Account, item.Lookup, status));
            }
        }
    }

    return result;
}

Embed BuildEggFlexEmbed(RegisteredEggAccount account, Backup backup, IUser user) {
    var game = backup.Game;
    var eb = GetEstimatedEarningsBonus(backup);
    var rank = GetFarmerRank(eb);
    var totals = BuildEggsLaidTotals(backup).Where(total => total.Amount > 0).ToList();
    var topEgg = totals.OrderByDescending(total => total.Amount).FirstOrDefault();
    var activeContracts = backup.Contracts?.Contracts.Count(c => !c.Cancelled) ?? 0;
    var completedContracts = backup.Contracts?.Archive.Count ?? 0;
    var ships = GetActiveShipMissions(backup).ToList();
    var lines = new[] {
        $"**Farmer rank:** {rank.Name}",
        $"**Estimated EB:** {FormatEggs(eb)}%",
        $"**Soul Eggs:** {FormatEggs(GetSoulEggs(game))}",
        $"**Prophecy Eggs:** {game.EggsOfProphecy:0}",
        $"**Top egg laid:** {(topEgg is null ? "Unknown" : $"{topEgg.Name} - {FormatEggs(topEgg.Amount)}")}",
        $"**Contracts:** {activeContracts} active / {completedContracts} completed",
        $"**Ships:** {(ships.Count == 0 ? "No active missions" : $"{ships.Count} active, next return {FormatNullableTimestamp(ships.Min(s => s.ReturnAt))}")}"
    };

    return new EmbedBuilder()
        .WithTitle($"Egg Flex - {user.GlobalName ?? user.Username}")
        .WithColor(Color.Magenta)
        .WithDescription(string.Join("\n", lines))
        .WithFooter($"Account: {AccountDisplayName(account)}")
        .WithCurrentTimestamp()
        .Build();
}

Embed BuildFarmerRankUpEmbed(
    SocketGuildUser? member,
    string? eggName,
    FarmerRankSnapshot previous,
    FarmerRankSnapshot current) {
    var displayName = member?.DisplayName
        ?? (string.IsNullOrWhiteSpace(eggName) ? "A registered farmer" : eggName!.Trim());
    var accountLine = string.IsNullOrWhiteSpace(eggName)
        ? displayName
        : eggName!.Trim();

    return new EmbedBuilder()
        .WithTitle("Farmer Rank Up")
        .WithColor(Color.Gold)
        .WithDescription($"**{displayName}** reached **{current.RankName}**.")
        .AddField("Egg Inc Account", accountLine, true)
        .AddField("Previous Rank", previous.RankName, true)
        .AddField("Current Rank", current.RankName, true)
        .AddField("Current EB", $"{FormatEggs(current.EarningsBonus)}%", true)
        .WithFooter("Checked from registered EID backup data.")
        .WithCurrentTimestamp()
        .Build();
}

Embed BuildContractMvpEmbed(ContractCoopStatusResponse status) {
    var contributors = status.Contributors
        .Where(c => !string.IsNullOrWhiteSpace(c.UserName) || !string.IsNullOrWhiteSpace(c.UserId))
        .ToList();
    var highestRate = contributors.OrderByDescending(c => c.ContributionRate).FirstOrDefault();
    var biggestContribution = contributors.OrderByDescending(c => c.ContributionAmount).FirstOrDefault();
    var tokenHelper = contributors.OrderByDescending(c => c.BoostTokens + c.BoostTokensSpent).FirstOrDefault();
    var activeReporter = contributors
        .Where(c => c.RecentlyActive || c.Active)
        .OrderByDescending(c => c.ContributionRate)
        .FirstOrDefault();
    var underRequirement = contributors.Count(c => c.ContributionRate * 3600 < 2e15);
    var lines = new List<string> {
        MvpLine("Rate Rocket", highestRate, c => $"{FormatEggs(c.ContributionRate * 3600)}/hr"),
        MvpLine("Big Basket", biggestContribution, c => $"{FormatEggs(c.ContributionAmount)} contributed"),
        MvpLine("Token Helper", tokenHelper, c => $"{c.BoostTokens + c.BoostTokensSpent:N0} token(s) seen"),
        MvpLine("Fresh Sync Energy", activeReporter, c => $"{FormatEggs(c.ContributionRate * 3600)}/hr and reporting")
    };

    lines.Add(underRequirement == 0
        ? "Guild line: everyone currently visible is at or above 2q/hr."
        : $"Guild line: {underRequirement} visible player(s) are under 2q/hr.");

    return new EmbedBuilder()
        .WithTitle($"Contract MVP - {status.ContractIdentifier}")
        .WithColor(Color.Gold)
        .WithDescription(string.Join("\n", lines))
        .WithFooter("Co-op name hidden for privacy.")
        .WithCurrentTimestamp()
        .Build();
}

static string MvpLine(
    string badge,
    ContractCoopStatusResponse.Types.ContributionInfo? contributor,
    Func<ContractCoopStatusResponse.Types.ContributionInfo, string> detail) {
    if(contributor is null) {
        return $"**{badge}:** no visible player data";
    }

    var name = string.IsNullOrWhiteSpace(contributor.UserName) ? "(unknown)" : contributor.UserName;
    return $"**{badge}:** {name} - {detail(contributor)}";
}

Embed BuildContractPredictionEmbed(ContractCoopStatusResponse status, Contract? contract) {
    var currentRatePerSecond = status.Contributors.Sum(c => c.ContributionRate);
    var currentRatePerHour = currentRatePerSecond * 3600;
    var target = GetContractTargetAmount(contract, status.Grade);
    double? remaining = target is null ? null : Math.Max(0, target.Value - status.TotalAmount);
    TimeSpan? eta = remaining is null || currentRatePerSecond <= 0
        ? null
        : TimeSpan.FromSeconds(remaining.Value / currentRatePerSecond);
    var timeLeft = status.SecondsRemaining > 0 ? TimeSpan.FromSeconds(status.SecondsRemaining) : (TimeSpan?)null;
    var underTwoQ = status.Contributors.Count(c => c.ContributionRate * 3600 < 2e15);
    var reporting = status.AllMembersReporting ? "all members reporting" : "not all members reporting";
    var outcome = status.AllGoalsAchieved
        ? "Already complete."
        : eta is null || timeLeft is null
            ? currentRatePerHour <= 0 ? "No active production rate visible yet." : "Trending data is available, but Plotty cannot estimate finish time without a target."
            : eta <= timeLeft ? "Trending to finish before time runs out." : "Trending behind the visible timer.";

    var lines = new List<string> {
        $"**Current total:** {FormatEggs(status.TotalAmount)}",
        $"**Current rate:** {FormatEggs(currentRatePerHour)}/hr",
        $"**Reporting:** {reporting}",
        $"**Under 2q/hr:** {underTwoQ}",
        $"**Outlook:** {outcome}"
    };

    if(target is not null) {
        lines.Insert(1, $"**Target:** {FormatEggs(target.Value)}");
    }

    if(eta is not null) {
        var finishAt = DateTimeOffset.UtcNow.Add(eta.Value);
        lines.Add($"**Estimated finish:** <t:{finishAt.ToUnixTimeSeconds()}:f> (in about {FormatDuration(eta.Value)})");
    }

    if(timeLeft is not null) {
        lines.Add($"**Time left:** {FormatDuration(timeLeft.Value)}");
    }

    return new EmbedBuilder()
        .WithTitle($"Contract Prediction - {status.ContractIdentifier}")
        .WithColor(outcome.Contains("finish", StringComparison.OrdinalIgnoreCase) ? Color.Green : Color.Orange)
        .WithDescription(string.Join("\n", lines))
        .WithFooter("Co-op name hidden for privacy. Estimates use the currently visible sync data.")
        .WithCurrentTimestamp()
        .Build();
}

Embed BuildPlottyAchievementsEmbed(RegisteredEggAccount account, Backup backup, int activeDemerits, BeerStats? beverageStats) {
    var game = backup.Game;
    var eb = GetEstimatedEarningsBonus(backup);
    var completedContracts = backup.Contracts?.Archive.Count ?? 0;
    var activeShips = GetActiveShipMissions(backup).Count();
    var tokenWeek = WeeklyTokenLeaderboard.CurrentWeek(DateTimeOffset.UtcNow, WeeklyTokenLeaderboard.MountainTimeZone());
    var tokens = WeeklyTokenLeaderboard.CountTokens(backup, tokenWeek.Start, tokenWeek.End).TokensSent;
    var totals = BuildEggsLaidTotals(backup).Where(total => total.Amount > 0).ToList();
    var achievements = new List<string>();

    AddAchievement(achievements, "First Sync", true, "Registered and backup is readable.");
    AddAchievement(achievements, "2q Club", HasAnyRecentTwoQRate(backup), "Has a recent contract contribution at or above 2q/hr.");
    AddAchievement(achievements, "Token Saint", tokens >= 10, $"Sent {tokens:N0} token(s) this week.");
    AddAchievement(achievements, "Shipyard Regular", activeShips > 0, $"{activeShips} active ship mission(s).");
    AddAchievement(achievements, "Contract Veteran", completedContracts >= 10, $"{completedContracts} completed contract(s) tracked.");
    AddAchievement(achievements, "Beverage Local", (beverageStats?.BeersReceivedFromMembers ?? 0) >= 10, $"{beverageStats?.BeersReceivedFromMembers ?? 0} beverage(s) received.");
    AddAchievement(achievements, "Clean Clipboard", activeDemerits == 0, $"{activeDemerits} active demerit(s).");
    AddAchievement(achievements, "Egg Mountain", totals.Any(t => t.Amount >= 1e18), "Laid at least 1Q of one egg type.");
    AddAchievement(achievements, "Prophecy Collector", game.EggsOfProphecy >= 100, $"{game.EggsOfProphecy:0} PE.");
    AddAchievement(achievements, "EB Meteor", eb >= 1e20, $"{FormatEggs(eb)}% EB.");

    return new EmbedBuilder()
        .WithTitle($"Plotty Achievements - {AccountDisplayName(account)}")
        .WithColor(Color.Purple)
        .WithDescription(string.Join("\n", achievements))
        .WithFooter("Achievements are playful and recalculated from current Plotty data.")
        .WithCurrentTimestamp()
        .Build();
}

static void AddAchievement(List<string> lines, string name, bool unlocked, string detail) =>
    lines.Add($"{(unlocked ? "Unlocked" : "Locked")} **{name}** - {detail}");

static bool HasAnyRecentTwoQRate(Backup backup) {
    var contracts = BuildPlayerRateCandidates(backup).Take(5);
    return contracts.Any(c => c.ContractId.Length > 0 && c.CoopCode.Length > 0);
}

static double? GetContractTargetAmount(Contract? contract, Contract.Types.PlayerGrade grade) {
    if(contract is null) {
        return null;
    }

    var gradeGoals = GetGoalTargets(contract.GradeSpecs.Where(spec => spec.Grade == grade));
    if(gradeGoals.Count > 0) {
        return gradeGoals.Max();
    }

    var aaaGoals = GetGoalTargets(contract.GradeSpecs.Where(spec => spec.Grade == Contract.Types.PlayerGrade.GradeAaa));
    if(aaaGoals.Count > 0) {
        return aaaGoals.Max();
    }

    var anyGradeGoals = GetGoalTargets(contract.GradeSpecs);
    if(anyGradeGoals.Count > 0) {
        return anyGradeGoals.Max();
    }

    var legacyGoals = contract.Goals
        .Where(goal => goal.TargetAmount > 0)
        .Select(goal => goal.TargetAmount)
        .ToList();
    return legacyGoals.Count == 0 ? null : legacyGoals.Max();
}

static List<double> GetGoalTargets(IEnumerable<Contract.Types.GradeSpec> gradeSpecs) =>
    gradeSpecs
        .SelectMany(spec => spec.Goals)
        .Where(goal => goal.TargetAmount > 0)
        .Select(goal => goal.TargetAmount)
        .ToList();

static Contract? FindContractDefinition(Backup? backup, string contractId) {
    if(backup?.Contracts is null || string.IsNullOrWhiteSpace(contractId)) {
        return null;
    }

    return backup.Contracts.Contracts
        .Concat(backup.Contracts.Archive)
        .Where(c => c.Contract is not null)
        .Select(c => c.Contract)
        .FirstOrDefault(c => string.Equals(c.Identifier, contractId, StringComparison.OrdinalIgnoreCase));
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
        .AddTextInput(
            label: "Egg Inc display name, optional",
            customId: "egg-name",
            style: TextInputStyle.Short,
            placeholder: "Use this if Egg Inc sends Plotty a malformed name",
            minLength: 1,
            maxLength: 32,
            required: false)
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
    var manualEggName = CleanEggIncDisplayName(
        modal.Data.Components.FirstOrDefault(c => c.CustomId == "egg-name")?.Value);
    var validation = await eggClient.ValidateEggIdAsync(eid);
    if(!validation.IsValid) {
        await modal.FollowupAsync("Plotty could not validate that EID with Egg Inc. Please double-check it and try again.", ephemeral: true);
        return;
    }

    var validatedEggName = CleanEggIncDisplayName(validation.EggName);
    var eggName = manualEggName ?? validatedEggName;
    await dataStore.SaveRegisteredEidAsync(modal.GuildId.Value, modal.User.Id, eid, eggName);

    var accounts = await dataStore.GetRegisteredAccountsAsync(modal.GuildId.Value, modal.User.Id);
    var suffix = SecureText.Sha256(eid)[..8];
    var parseNote = validation.BackupParseLimited
        ? manualEggName is null
            ? " Egg Inc returned one malformed optional backup field, so I saved the EID without an Egg Inc display name for now. Re-run `/register-eid` with the same EID and fill in the optional display name if you want Plotty to show/match it by name."
            : " Egg Inc returned one malformed optional backup field, so I saved the display name you entered instead."
        : "";
    await modal.FollowupAsync(
        $"Saved your EID securely and tied it to your Discord name. You now have `{accounts.Count}` EID account(s) registered. Stored hash ending: `{suffix}`.{parseNote}",
        ephemeral: true);

    await SendRegistrationWelcomeAsync(modal.GuildId.Value, modal.User);
}

async Task HandleUnregisterEidAsync(SocketSlashCommand command) {
    var removed = await dataStore.RemoveRegisteredEidsForUserAsync(command.GuildId!.Value, command.User.Id);
    if(removed == 0) {
        await command.RespondAsync("You do not have any EIDs registered with Plotty in this server.", ephemeral: true);
        return;
    }

    var accountText = removed == 1 ? "EID account" : "EID accounts";
    await command.RespondAsync(
        $"Removed `{removed}` {accountText} from your Plotty registration in this server.",
        ephemeral: true);
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

async Task HandlePlottyFeaturesAsync(SocketSlashCommand command) {
    var builder = new EmbedBuilder()
        .WithTitle("Plotty Features")
        .WithColor(Color.Teal)
        .WithDescription("Here is what I can do right now.");

    AddFeatureFields(builder, "Registration & Privacy", [
        "`/register-eid` privately stores one or more Egg Inc IDs for your Discord account.",
        "`/unregister-eid` privately removes all Egg Inc IDs tied to your Discord account in this server.",
        "`/rates` privately shows your active contracts and recent completed contract count.",
        "`/player` privately shows recent contribution history for a registered player.",
        "`/eggs-laid` shows lifetime eggs laid by farm, including regular and virtue eggs.",
        "`/myeggcount` shows the remaining standard eggs for the 5Q and 10Q challenges, privately or publicly.",
        "`/goldeneggs` shows Golden Eggs earned over the past 24 hours, privately or publicly.",
        "`/egg-milestones` privately shows your next Egg Inc account milestones.",
        "`/rivalry` privately compares two registered members in a friendly stat showdown.",
        "`/egg-flex` privately shows a brag-card summary for your account.",
        "`/contract-mvp` privately awards playful MVP badges for active co-ops.",
        "`/contract-predictions` privately estimates active co-op health and finish pace.",
        "`/plotty-achievements` privately shows playful achievement badges."
    ]);
    AddFeatureFields(builder, "Contracts & Alerts", [
        "`/contract` looks up a specific contract and co-op code.",
        "`/mycontract` privately shows your active co-op players, current rates, Plot Chickens membership, and contract-wide members not joined yet.",
        "`/contract-late-notify` tells Staff you may be late joining an upcoming contract.",
        "Plotty watches `#i-am-late-today` for late notices.",
        "Background checks watch for 6-hour missing joins, EB rank-ups, ship returns, and first co-op finishes."
    ]);
    AddFeatureFields(builder, "Artifacts, Ships & Help", [
        "`/contract-artifacts` suggests current-contract artifact and stone sets from your inventory.",
        "`/ships` shows active ship missions and can DM you when one returns.",
        "`/help` answers Egg Inc questions and can use your registered player data when useful."
    ]);
    AddFeatureFields(builder, "Demerits", [
        "`/demerits-view` privately shows your active demerits.",
        "Staff can use `/add-demerit` and `/remove-demerit` to manage demerits.",
        "Staff can view all demerits and send 6hr/18hr demerit notices.",
        "Demerits expire automatically after 30 days."
    ]);
    AddFeatureFields(builder, "Beverages & Leaderboards", [
        "`/beverage-plotty` lets you give Plotty Water, LaCroix, Soda-Pop, Milk, Coffee, Tea, Beer, or Wine.",
        "`/beverage-user` lets members gift beverages to each other, optionally pinging the receiver.",
        "`/beverage-leader` privately shows beverage standings.",
        "`/plotty-poll` creates 12hr or 24hr reaction polls, with optional New Member Poll role ping.",
        "`/new-member-poll` creates a 12hr yes/no poll for a prospective member and pings the New Member Poll role.",
        "`/token-leaderboard` shows the weekly Tokie Awards for tokens sent."
    ]);
    AddFeatureFields(builder, "Plotty Personality", [
        "`/plotty-mood`, `/plotty-excuses`, and `/plotty-wisdom` generate Plotty-style replies.",
        "Mention Plotty for conversation replies.",
        "\"what the fox\" still triggers Plotty's fox response.",
        "Sarcastic chat replies are intentionally rare."
    ]);
    AddFeatureFields(builder, "Staff Tools", [
        "`/admin-dashboard`, `/admin-rates-all`, and `/admin-member-contract` support contract oversight.",
        "`/admin-list-members` and `/admin-e9k-compare` compare Discord, Plotty, and EGG9000 membership.",
        "`/admin-plotty-report` privately scrapes the latest EGG9000 AAA contract report.",
        "`/admin-plotty-speak` lets Staff speak as Plotty and logs usage to `#mod-log`."
    ]);

    var embed = builder
        .WithFooter("Admin commands are Staff restricted. This feature list is private to you.")
        .WithCurrentTimestamp()
        .Build();

    await command.RespondAsync(embed: embed, ephemeral: true);
}

static void AddFeatureFields(EmbedBuilder builder, string title, IEnumerable<string> lines) {
    var chunks = BuildDiscordFieldChunks(lines, maxLength: 1000);
    for(var index = 0; index < chunks.Count; index++) {
        builder.AddField(index == 0 ? title : $"{title} (continued)", chunks[index]);
    }
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
    var earningsBonus = GetEstimatedEarningsBonus(backup);
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

static double GetEstimatedEarningsBonus(Backup backup) {
    var game = backup.Game;
    var soulEggs = GetSoulEggs(game);
    var prophecyEggs = (double)game.EggsOfProphecy;
    var truthEggs = GetEggsOfTruth(backup);
    var soulFoodLevel = GetEpicResearchLevel(game, "soul_eggs");
    var prophecyBonusLevel = GetEpicResearchLevel(game, "prophecy_bonus");
    var soulEggBonus = soulFoodLevel + 10d;
    var prophecyEggBonus = ((prophecyBonusLevel + 5d) / 100d) + 1d;
    var truthEggBonus = Math.Pow(1.01d, truthEggs);
    return soulEggs * soulEggBonus * Math.Pow(prophecyEggBonus, prophecyEggs) * truthEggBonus;
}

static double GetEggsOfTruth(Backup backup) {
    return backup.Virtue?.EovEarned.Select(value => (double)value).Sum() ?? 0d;
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
        return $"**{name}** - {FormatEggs(p.ContributionRate * 3600)}/hr, {FormatEggs(p.ContributionAmount)} contributed{flag}";
    });

    builder.WithDescription(string.Join("\n", lines));
    return builder.Build();
}

Embed BuildMyContractEmbed(
    ContractCoopStatusResponse status,
    Egg9000ContractScrape? scrape,
    IReadOnlySet<string> plotChickenRosterNames,
    string? accountLabel) {
    var configuredGuildTag = NormalizeGuildTag(settings.Egg9000?.EffectiveGuildTag ?? "The Plot Chickens");
    var scrapePlayersByName = (scrape?.Players ?? [])
        .Where(player => NormalizeName(player.Name).Length > 0)
        .GroupBy(player => NormalizeName(player.Name), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

    var currentPlayers = status.Contributors
        .OrderByDescending(player => player.ContributionRate)
        .ToList();
    var currentNames = currentPlayers
        .Select(player => NormalizeName(player.UserName))
        .Where(name => name.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var currentLines = currentPlayers.Select(player => {
        var name = string.IsNullOrWhiteSpace(player.UserName) ? "(unknown)" : player.UserName;
        var membership = PlotChickenMembershipLabel(
            name,
            scrapePlayersByName,
            plotChickenRosterNames,
            configuredGuildTag);
        var state = !player.Active ? " - inactive" : player.TimeCheatDetected ? " - flagged" : "";
        return $"**{name}** - {FormatEggs(player.ContributionRate * 3600)}/hr - " +
               $"{FormatEggs(player.ContributionAmount)} contributed - Plot Chickens: **{membership}**{state}";
    }).ToList();

    var pendingLines = (scrape?.Players ?? [])
        .Where(player => NormalizeGuildTag(player.GuildTag) == configuredGuildTag)
        .Where(player => NormalizeName(player.Name).Length > 0)
        .GroupBy(player => NormalizeName(player.Name), StringComparer.OrdinalIgnoreCase)
        .Where(group => group.All(player => !player.Joined))
        .Where(group => !currentNames.Contains(group.Key))
        .Select(group => group.First().Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .Select(name => $"**{name}** - not joined - Plot Chickens: **yes**")
        .ToList();

    var title = string.IsNullOrWhiteSpace(accountLabel)
        ? $"My Contract - {status.ContractIdentifier}"
        : $"My Contract - {status.ContractIdentifier} ({accountLabel})";
    var builder = new EmbedBuilder()
        .WithTitle(title)
        .WithColor(Color.Gold)
        .WithDescription(
            $"Current co-op: `{currentPlayers.Count}` player(s) | " +
            $"Current total rate: **{FormatEggs(currentPlayers.Sum(player => player.ContributionRate) * 3600)}/hr**")
        .WithFooter("Co-op code hidden. Membership and pending status come from EGG9000; pending players are contract-wide because the scrape does not preserve co-op assignments.")
        .WithCurrentTimestamp();

    AddMyContractFields(builder, "Current co-op players", currentLines);
    if(scrape is null || (scrape.LooksLikeLoginPage && scrape.Players.Count == 0)) {
        builder.AddField("Plot Chickens not joined (contract-wide)", "EGG9000 membership data is unavailable right now.");
    } else if(pendingLines.Count == 0) {
        builder.AddField("Plot Chickens not joined (contract-wide)", "No Plot Chickens members are currently marked as not joined.");
    } else {
        AddMyContractFields(builder, "Plot Chickens not joined (contract-wide)", pendingLines);
    }

    return builder.Build();
}

static string PlotChickenMembershipLabel(
    string playerName,
    IReadOnlyDictionary<string, List<Egg9000ContractPlayer>> scrapePlayersByName,
    IReadOnlySet<string> plotChickenRosterNames,
    string configuredGuildTag) {
    var normalizedName = NormalizeName(playerName);
    if(scrapePlayersByName.TryGetValue(normalizedName, out var scrapePlayers)) {
        if(scrapePlayers.Any(player => NormalizeGuildTag(player.GuildTag) == configuredGuildTag)) {
            return "yes";
        }

        if(scrapePlayers.Any(player => !string.IsNullOrWhiteSpace(player.GuildTag))) {
            return "no";
        }
    }

    return plotChickenRosterNames.Contains(normalizedName) ? "yes" : "unknown";
}

static void AddMyContractFields(EmbedBuilder builder, string title, IEnumerable<string> lines) {
    var chunks = BuildDiscordFieldChunks(lines, maxLength: 1000);
    if(chunks.Count == 0) {
        builder.AddField(title, "No players found.");
        return;
    }

    for(var index = 0; index < chunks.Count; index++) {
        builder.AddField(index == 0 ? title : $"{title} (continued)", chunks[index]);
    }
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
        .ThenBy(g => DisplayNameForDemerits(guild, g.Key, g))
        .Select(g => {
            var nextExpiry = g.Min(d => d.ExpiresAt).LocalDateTime.ToString("yyyy-MM-dd");
            var recent = g.OrderByDescending(d => d.CreatedAt).First();
            var detail = string.IsNullOrWhiteSpace(recent.ContractId)
                ? recent.Reason
                : $"{recent.Reason} (`{recent.ContractId}`)";
            return $"{DisplayNameForDemerits(guild, g.Key, g)} - `{g.Count()}` active, next expires `{nextExpiry}` - {detail}";
        })
        .ToList();

    var text = "**Active Demerits**\n" + string.Join("\n", lines);
    return text.Length <= 1900 ? text : text[..1900] + "\n...";
}

static string DisplayNameForDemerits(SocketGuild? guild, ulong userId, IEnumerable<DemeritEntry>? entries = null) {
    var user = guild?.GetUser(userId);
    if(user is not null) {
        return user.Mention;
    }

    var playerName = entries?
        .Select(entry => entry.PlayerName)
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    return string.IsNullOrWhiteSpace(playerName)
        ? $"Discord user `{userId}`"
        : $"{playerName} (Discord user `{userId}`)";
}

SocketGuildUser? ResolveGuildMemberOption(SocketSlashCommand command, string optionName) {
    var user = command.Data.Options.FirstOrDefault(o => o.Name == optionName)?.Value as IUser;
    if(user is null || command.GuildId is null) {
        return null;
    }

    return client.GetGuild(command.GuildId.Value)?.GetUser(user.Id);
}

(ulong? UserId, string Label) ResolveDemeritTarget(SocketSlashCommand command) {
    var member = ResolveGuildMemberOption(command, "member");
    if(member is not null) {
        return (member.Id, member.Mention);
    }

    var rawUserId = command.Data.Options.FirstOrDefault(o => o.Name == "discord-user-id")?.Value as string;
    if(TryParseDiscordUserId(rawUserId, out var userId)) {
        var guildMember = command.GuildId is { } guildId
            ? client.GetGuild(guildId)?.GetUser(userId)
            : null;
        return guildMember is null
            ? (userId, $"Discord user `{userId}`")
            : (userId, guildMember.Mention);
    }

    return (null, "");
}

static bool TryParseDiscordUserId(string? value, out ulong userId) {
    userId = 0;
    if(string.IsNullOrWhiteSpace(value)) {
        return false;
    }

    var trimmed = value.Trim();
    if(trimmed.StartsWith("<@", StringComparison.Ordinal) && trimmed.EndsWith('>')) {
        trimmed = trimmed[2..^1].TrimStart('!');
    }

    return ulong.TryParse(trimmed, out userId);
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

static IReadOnlyList<string> BuildDiscordFieldChunks(IEnumerable<string> lines, int maxLength) {
    var chunks = new List<string>();
    var current = "";
    foreach(var line in lines) {
        var cleaned = string.IsNullOrWhiteSpace(line) ? "(blank)" : line.Trim();
        var candidate = string.IsNullOrWhiteSpace(current) ? cleaned : $"{current}\n{cleaned}";
        if(candidate.Length > maxLength) {
            if(!string.IsNullOrWhiteSpace(current)) {
                chunks.Add(current);
            }

            current = cleaned.Length <= maxLength ? cleaned : TrimDiscordMessage(cleaned, maxLength);
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

static SocketRole? FindStaffRole(SocketGuild guild) =>
    guild.Roles.FirstOrDefault(role => string.Equals(role.Name, "Staff", StringComparison.OrdinalIgnoreCase));

bool IsPlottyAdmin(SocketGuildUser user) =>
    plottyAdminUserIds.Contains(user.Id);

bool IsTokenLeaderboardExcluded(RegisteredEggAccount account, SocketGuild? guild) {
    var member = guild?.GetUser(account.DiscordUserId);
    var candidateNames = new[] {
        account.EggName,
        AccountDisplayName(account),
        member?.Username,
        member?.DisplayName,
        member?.GlobalName
    };

    var normalizedExcludedNames = tokenLeaderboardExcludedNames
        .Select(NormalizeName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return candidateNames
        .Select(name => NormalizeName(name))
        .Any(name => normalizedExcludedNames.Contains(name));
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

static string MentionOrFallback(ulong discordUserId, string fallbackName) =>
    discordUserId == 0 ? fallbackName : $"<@{discordUserId}>";

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

Embed BuildEggsLaidEmbed(Backup backup, RegisteredEggAccount? account = null, SocketGuild? guild = null) {
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

    var lines = BuildEggsLaidLines(totals.Where(x => x.Amount > 0), guild);

    if(lines.Count == 0) {
        builder.WithDescription("No eggs laid totals above zero were found.");
    } else {
        builder.WithDescription(TrimDiscordMessage(string.Join("\n", lines), 4000));
    }

    var currentFarmLines = backup.Farms
        .Where(f => f.EggsLaid > 0)
        .Take(10)
        .Select(f => {
            var label = !string.IsNullOrWhiteSpace(f.ContractId)
                ? $"{EggIconForName(EggDisplayName(f.EggType), guild)} **{EggDisplayName(f.EggType)}** contract `{f.ContractId}`"
                : $"{EggIconForName(EggDisplayName(f.EggType), guild)} **{EggDisplayName(f.EggType)}**";
            return $"{label} - {FormatEggs(f.EggsLaid)} currently laid";
        })
        .ToList();

    if(currentFarmLines.Count > 0) {
        builder.AddField("Current Farms", string.Join("\n", currentFarmLines));
    }

    return builder.Build();
}

Embed BuildMyEggCountEmbed(
    RegisteredEggAccount account,
    Backup backup) {
    const double fiveQ = 5e18;
    const double tenQ = 1e19;
    var totals = BuildEggsLaidTotals(backup)
        .Where(total => total.Group == EggsLaidGroup.Regular)
        .Take(19)
        .ToList();

    var tableLines = new List<string> { $"{"Egg",-8} {"5Q",6} {"10Q",6}" };
    tableLines.AddRange(totals.Select(total =>
        $"{CompactEggChallengeName(total.Name),-8} " +
        $"{EggChallengeRemaining(total.Amount, fiveQ),6} " +
        $"{EggChallengeRemaining(total.Amount, tenQ),6}"));

    var builder = new EmbedBuilder()
        .WithTitle($"Egg challenge progress - {AccountDisplayName(account)}")
        .WithColor(Color.Green)
        .WithFooter("Amounts show how many more eggs must be laid. A check mark means the target is complete.")
        .WithCurrentTimestamp();

    if(totals.Count == 0) {
        builder.WithDescription("No lifetime standard egg totals were found in this backup.");
    } else {
        builder.WithDescription($"```text\n{string.Join("\n", tableLines)}\n```");
    }

    return builder.Build();
}

static string EggChallengeRemaining(double laid, double target) {
    var remaining = target - Math.Max(0, laid);
    return remaining <= 0 ? "✓" : FormatEggs(remaining);
}

static string CompactEggChallengeName(string name) => name switch {
    "Rocket Fuel" => "RocketFu",
    "Super Material" => "SuperMat",
    "Dilithium" => "Dilithiu",
    "Terraform" => "Terrafor",
    "Antimatter" => "Antimatt",
    "Dark Matter" => "DarkMatt",
    "Enlightenment" => "Enlight",
    _ => name.Length <= 8 ? name : name[..8]
};

Embed BuildGoldenEggProgressEmbed(
    GoldenEggSnapshot current,
    IReadOnlyList<GoldenEggSnapshot> history,
    DateTimeOffset now) {
    var target = now.AddHours(-24);
    var fullBaseline = history
        .Where(snapshot => snapshot.CapturedAt <= target)
        .OrderByDescending(snapshot => snapshot.CapturedAt)
        .FirstOrDefault();
    var baseline = fullBaseline ?? history.OrderBy(snapshot => snapshot.CapturedAt).FirstOrDefault();
    var accountName = string.IsNullOrWhiteSpace(current.EggName) ? "Registered Player" : current.EggName;
    var builder = new EmbedBuilder()
        .WithTitle($"Golden Eggs - {accountName}")
        .WithColor(Color.Gold)
        .WithCurrentTimestamp();

    if(baseline is null || current.GoldenEggsEarned < baseline.GoldenEggsEarned) {
        return builder
            .WithDescription("I do not have a usable Golden Egg baseline for this account yet.")
            .WithFooter("Plotty will keep tracking the cumulative earned counter every 10 minutes.")
            .Build();
    }

    var gained = current.GoldenEggsEarned - baseline.GoldenEggsEarned;
    var elapsed = now - baseline.CapturedAt;
    if(fullBaseline is not null) {
        builder.WithDescription($"**{gained:N0} Golden Eggs** earned during the past 24 hours.");
        builder.AddField("Tracking window", $"{baseline.CapturedAt.LocalDateTime:g} to now", false);
    } else {
        var availableAt = baseline.CapturedAt.AddHours(24);
        builder.WithDescription(
            $"**{gained:N0} Golden Eggs** earned since tracking began " +
            $"({FormatTrackingDuration(elapsed)} of 24 hours recorded).");
        builder.AddField("Full 24-hour total available", availableAt.LocalDateTime.ToString("g"), false);
    }

    return builder
        .AddField("Lifetime earned counter", current.GoldenEggsEarned.ToString("N0"), false)
        .WithFooter("Gross earned Golden Eggs. Spending does not reduce this result.")
        .Build();
}

static string FormatTrackingDuration(TimeSpan elapsed) {
    if(elapsed.TotalHours >= 1) {
        return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }

    return $"{Math.Max(0, elapsed.Minutes)}m";
}

IReadOnlyList<string> BuildEggsLaidLines(IEnumerable<EggsLaidTotal> totals, SocketGuild? guild) {
    var orderedGroups = new[] {
        EggsLaidGroup.Regular,
        EggsLaidGroup.Virtue,
        EggsLaidGroup.Seasonal,
        EggsLaidGroup.Custom
    };
    var lines = new List<string>();
    foreach(var group in orderedGroups) {
        var groupLines = totals
            .Where(total => total.Group == group)
            .Select(total => $"{EggIconForName(total.Name, guild)} **{total.Name}** - {FormatEggs(total.Amount)}")
            .ToList();
        if(groupLines.Count == 0) {
            continue;
        }

        if(lines.Count > 0) {
            lines.Add("");
        }

        lines.AddRange(groupLines);
    }

    return lines;
}

static IReadOnlyList<EggsLaidTotal> BuildEggsLaidTotals(Backup backup) {
    var statsTotals = backup.Stats?.EggTotals?.ToList() ?? [];
    var totals = new List<EggsLaidTotal>();

    for(var i = 0; i < Math.Min(19, statsTotals.Count); i++) {
        totals.Add(new EggsLaidTotal(EggNameForStatsIndex(i), statsTotals[i], EggsLaidGroup.Regular));
    }

    for(var i = 20; i < Math.Min(25, statsTotals.Count); i++) {
        totals.Add(new EggsLaidTotal(EggNameForStatsIndex(i), statsTotals[i], EggsLaidGroup.Virtue));
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
            SumContractEggsLaid(contracts.Where(c => c.Contract?.Egg == egg)),
            EggsLaidGroup.Seasonal));
    }

    var customEggs = backup.Contracts?.CustomEggInfo ?? [];
    foreach(var customEgg in customEggs.Where(e => !string.IsNullOrWhiteSpace(e.Identifier))) {
        var name = string.IsNullOrWhiteSpace(customEgg.Name)
            ? $"Custom Egg ({customEgg.Identifier})"
            : customEgg.Name;
        totals.Add(new EggsLaidTotal(
            name,
            SumContractEggsLaid(contracts.Where(c =>
                string.Equals(c.Contract?.CustomEggId, customEgg.Identifier, StringComparison.OrdinalIgnoreCase))),
            EggsLaidGroup.Custom));
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

static string GetModalValue(SocketModal modal, string customId) =>
    modal.Data.Components.First(c => c.CustomId == customId).Value;

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

static string? CleanEggIncDisplayName(string? value) {
    if(string.IsNullOrWhiteSpace(value)) {
        return null;
    }

    var cleaned = Regex.Replace(value, @"\p{C}+", "").Trim();
    return string.IsNullOrWhiteSpace(cleaned)
        ? null
        : cleaned.Length <= 32 ? cleaned : cleaned[..32];
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
        "CRISPR",
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
    Egg.Immortality => "CRISPR",
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

string EggIconForName(string eggName, SocketGuild? guild) {
    var preferredEmojiName = EggEmojiNameForDisplayName(eggName);
    var preferredSource = FindEggIconSource(preferredEmojiName, guild);
    if(preferredSource is not null) {
        return preferredSource.Value.Emote.ToString();
    }

    var assetFile = EggAssetFileName(eggName) ?? "egg_unknown.png";
    var emojiName = assetFile[..assetFile.LastIndexOf('.')];
    var source = FindEggIconSource(emojiName, guild);
    if(source is not null) {
        return source.Value.Emote.ToString();
    }

    return "";
}

static string EggEmojiNameForDisplayName(string eggName) => NormalizeName(eggName) switch {
    "rocketfuel" => "egg_rocketfuel",
    "supermaterial" => "egg_supermaterial",
    "darkmatter" => "egg_darkmatter",
    "waterballoon" => "egg_waterballoon",
    "customegg" => "egg_unknown",
    "" => "egg_unknown",
    var normalized => $"egg_{normalized}"
};

(GuildEmote Emote, string GuildName)? FindEggIconSource(string emojiName, SocketGuild? preferredGuild) {
    var normalized = NormalizeName(emojiName);
    if(preferredGuild is not null) {
        var local = preferredGuild.Emotes.FirstOrDefault(e =>
            string.Equals(NormalizeName(e.Name), normalized, StringComparison.OrdinalIgnoreCase));
        if(local is not null) {
            return (local, preferredGuild.Name);
        }
    }

    foreach(var guild in client.Guilds.Where(g => preferredGuild is null || g.Id != preferredGuild.Id)) {
        var external = guild.Emotes.FirstOrDefault(e =>
            string.Equals(NormalizeName(e.Name), normalized, StringComparison.OrdinalIgnoreCase));
        if(external is not null) {
            return (external, guild.Name);
        }
    }

    return null;
}

static string? EggAssetFileName(string eggName) => NormalizeName(eggName) switch {
    "edible" => "egg_edible.png",
    "superfood" => "egg_superfood.png",
    "medical" => "egg_medical.png",
    "rocketfuel" => "egg_rocketfuel.png",
    "supermaterial" => "egg_supermaterial.png",
    "fusion" => "egg_fusion.png",
    "quantum" => "egg_quantum.png",
    "immortality" => "egg_immortality.png",
    "tachyon" => "egg_tachyon.png",
    "graviton" => "egg_graviton.png",
    "dilithium" => "egg_dilithium.png",
    "prodigy" => "egg_prodigy.png",
    "terraform" => "egg_terraform.png",
    "antimatter" => "egg_antimatter.png",
    "darkmatter" => "egg_darkmatter.png",
    "ai" => "egg_ai.png",
    "universe" => "egg_universe.png",
    "enlightenment" => "egg_enlightenment.png",
    "chocolate" => "egg_chocolate.png",
    "easter" => "egg_easter.png",
    "waterballoon" => "egg_waterballoon.png",
    "firework" => "egg_firework.png",
    "pumpkin" => "egg_pumpkin.png",
    _ => null
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

static string NormalizeGuildTag(string? value) {
    if(string.IsNullOrWhiteSpace(value)) {
        return "";
    }

    return NormalizeName(value.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal));
}

