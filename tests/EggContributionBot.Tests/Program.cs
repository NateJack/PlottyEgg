using EggContribBot;
using EggContribBot.Proto;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

if(args is ["--audit-migration", var legacyJsonPath, var databasePath]) {
    await AuditMigrationAsync(legacyJsonPath, databasePath);
    return;
}

var tests = new (string Name, Func<Task> Run)[] {
    ("Discord settings parse guild and admin IDs", TestDiscordSettingsParsing),
    ("Egg Inc periodicals parse season score when player info is present", TestEggIncNativePeriodicalsSeasonScore),
    ("Egg Inc local worker maps season contract score", TestEggIncWorkerSeasonScore),
    ("Egg Inc signed player-info request matches Inspector transport", TestEggIncSignedPlayerInfoRequest),
    ("Egg Inc client learns newer versions from backups", TestEggIncLearnsClientVersionFromBackup),
    ("Egg Inc backup requests are coalesced and cached", TestEggIncBackupCache),
    ("EGG9000 leaderboard requests are cached", TestEgg9000LeaderboardCache),
    ("EGG9000 completed co-ops are recognized for report filtering", TestEgg9000CompletedCoopStatus),
    ("Plotty AI redacts private credentials from chat prompts", TestPlottyAiPromptRedaction),
    ("Plotty AI has a no-key local conversation fallback", TestPlottyAiLocalFallback),
    ("Plotty AI local fallback answers greetings naturally", TestPlottyAiLocalGreeting),
    ("DataStore stores multiple registered EIDs securely", TestRegisteredEids),
    ("DataStore unregisters EIDs when a server member leaves", TestRemoveRegisteredEidsForUser),
    ("DataStore removes legacy plaintext link records", TestLegacyPlaintextLinksRemoved),
    ("DataStore imports legacy JSON exactly once", TestLegacyJsonMigration),
    ("DataStore persists SQLite state across reopen", TestSqlitePersistence),
    ("DataStore tracks Golden Egg history per EID", TestGoldenEggSnapshots),
    ("DataStore serializes concurrent writes", TestConcurrentDataStoreWrites),
    ("DataStore supports concurrent reads while writing", TestConcurrentDataStoreReads),
    ("Demerits can be added, viewed, and removed", TestDemerits),
    ("Late notices filter by contract and expire window", TestContractLateNotices),
    ("Beer cooldowns can be enforced and bypassed", TestBeerCooldowns),
    ("Ship return notifications become due and can be marked sent", TestShipReturnNotifications),
    ("Pending polls persist until removed", TestPendingPolls),
    ("Monitor health records success and failure state", TestMonitorHealth),
    ("Co-op artifact reports include other members", TestCoopArtifactReports),
    ("Artifact scoring uses live co-op production", TestCoopArtifactScoring),
    ("Tokie Awards use Monday 10 AM weekly boundaries", TestTokenLeaderboardWeek),
    ("Tokie Awards count each weekly co-op once", TestTokenLeaderboardTokens)
};

static Task TestEgg9000CompletedCoopStatus() {
    AssertTrue(Egg9000Client.IsCompletedCoopStatus("Finished", 12), "finished status");
    AssertTrue(Egg9000Client.IsCompletedCoopStatus("Complete once everyone checks in", 3), "completed waiting for check-in");
    AssertTrue(Egg9000Client.IsCompletedCoopStatus("Time to complete 0m", 0), "expired co-op timer");
    AssertTrue(!Egg9000Client.IsCompletedCoopStatus("Time to complete 1h", 1), "active co-op");
    return Task.CompletedTask;
}

var failed = 0;
foreach(var test in tests) {
    try {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    } catch(Exception ex) {
        failed++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

if(failed > 0) {
    Environment.ExitCode = 1;
}

static Task TestDiscordSettingsParsing() {
    var settings = new DiscordSettings(
        Token: "token",
        GuildId: "100",
        GuildIds: ["100", "200", "not-a-number"],
        AdminUserIds: ["300", "bad", "400"]);
    var egg9000 = new Egg9000Settings("https://egg9000.com/", "secret");
    var openAi = new OpenAiSettings();

    AssertSequenceEqual([100UL, 100UL, 200UL], settings.ParsedGuildIds.ToArray(), "guild ids");
    AssertSequenceEqual([300UL, 400UL], settings.ParsedAdminUserIds.ToArray(), "admin ids");
    AssertTrue(egg9000.IsConfigured, "egg9000 api key configured");
    AssertEqual("https://egg9000.com/", egg9000.EffectiveBaseUrl, "egg9000 base url");
    AssertEqual("gpt-5-nano", openAi.EffectiveModel, "lowest-cost OpenAI model default");
    return Task.CompletedTask;
}

static async Task TestEggIncNativePeriodicalsSeasonScore() {
    var periodicals = new PeriodicalsResponse {
        ContractPlayerInfo = new ContractPlayerInfo {
            SeasonCxp = 5678,
            TotalCxp = 9012
        }
    };
    var authenticated = new AuthenticatedMessage {
        Message = ByteString.CopyFrom(periodicals.ToByteArray()),
        Compressed = false
    };
    var handler = new StubHttpMessageHandler(request => {
        AssertEqual("https://ctx-dot-auxbrainhome.appspot.com/ei/get_periodicals", request.RequestUri?.ToString(), "periodicals request URL");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(Convert.ToBase64String(authenticated.ToByteArray()))
        };
    });
    using var http = new HttpClient(handler) {
        BaseAddress = new Uri("https://www.auxbrain.com/"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    var client = new EggIncClient(new EggIncApiSettings(), http);

    var snapshot = await client.GetPlayerContractScoreSnapshotAsync("ei123", backup: null);

    AssertTrue(snapshot.PlayerInfo is not null, "native periodicals player info returned");
    AssertEqual(5678D, snapshot.PlayerInfo!.SeasonCxp, "native season contract score");
    AssertEqual(9012D, snapshot.PlayerInfo.TotalCxp, "native total contract score");
}

static async Task TestEggIncWorkerSeasonScore() {
    var handler = new StubHttpMessageHandler(request => {
        AssertEqual("http://127.0.0.1:8787/player_summary?eid=EI123", request.RequestUri?.ToString(), "worker request URL");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(
                """
                {"contracts":{"grade":{"id":5},"grade_progress":0.75,"grade_score":1234,"season_cxp":5678,"total_cxp":9012}}
                """,
                System.Text.Encoding.UTF8,
                "application/json")
        };
    });
    using var http = new HttpClient(handler) {
        BaseAddress = new Uri("https://www.auxbrain.com/"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    var client = new EggIncClient(
        new EggIncApiSettings(WorkerUrl: "http://127.0.0.1:8787"),
        http);

    var info = await client.GetContractPlayerInfoAsync("ei123");

    AssertTrue(info is not null, "worker player info returned");
    AssertEqual(5678D, info!.SeasonCxp, "season contract score");
    AssertEqual(9012D, info.TotalCxp, "total contract score");
    AssertEqual(1234D, info.GradeScore, "grade score");
    AssertEqual(Contract.Types.PlayerGrade.GradeAaa, info.Grade, "contract grade");
}

static async Task TestEggIncSignedPlayerInfoRequest() {
    const string salt = "inspector-parity-test";
    var responseInfo = new ContractPlayerInfo { SeasonCxp = 4321 };
    var responseEnvelope = new AuthenticatedMessage {
        Message = ByteString.CopyFrom(responseInfo.ToByteArray())
    };
    var handler = new StubHttpMessageHandler(request => {
        AssertEqual("https://www.auxbrain.com/ei_ctx/get_contract_player_info", request.RequestUri?.ToString(), "signed player-info URL");
        var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var encoded = form.Split('=', 2)[1];
        var envelope = AuthenticatedMessage.Parser.ParseFrom(Convert.FromBase64String(Uri.UnescapeDataString(encoded)));
        var requestInfo = BasicRequestInfo.Parser.ParseFrom(envelope.Message);

        AssertEqual("EI123", requestInfo.EiUserId, "signed EID");
        AssertEqual(75U, requestInfo.ClientVersion, "current client version");
        AssertEqual("1.37", requestInfo.Version, "current app version");
        AssertEqual("111358", requestInfo.Build, "current app build");
        AssertEqual("DROID", requestInfo.Platform, "Inspector platform");
        AssertEqual("en", requestInfo.DeviceLanguage, "Inspector device language");
        AssertEqual(InspectorSignature(envelope.Message.ToByteArray(), salt), envelope.Code, "Inspector signature");

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(Convert.ToBase64String(responseEnvelope.ToByteArray()))
        };
    });
    using var http = new HttpClient(handler) {
        BaseAddress = new Uri("https://www.auxbrain.com/"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    var client = new EggIncClient(new EggIncApiSettings(Salt: salt), http);

    var info = await client.GetContractPlayerInfoAsync("ei123");

    AssertTrue(info is not null, "signed player info returned");
    AssertEqual(4321D, info!.SeasonCxp, "signed player-info response parsed");
}

static string InspectorSignature(byte[] messageBytes, string phrase) {
    var mutated = messageBytes.ToArray();
    const uint magic = 0x3b9af419;
    if(mutated.Length > 0) {
        mutated[magic % (uint)mutated.Length] = 0x1b;
    }

    var saltHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.ASCII.GetBytes(phrase))).ToLowerInvariant();
    var combined = mutated.Concat(System.Text.Encoding.ASCII.GetBytes(saltHash)).ToArray();
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(combined)).ToLowerInvariant();
}

static async Task TestEggIncLearnsClientVersionFromBackup() {
    uint observedClientVersion = 0;
    var firstContact = new EggIncFirstContactResponse {
        Backup = new Backup { UserName = "Updated Player", Version = 76 }
    };
    var handler = new StubHttpMessageHandler(request => {
        if(request.RequestUri?.AbsolutePath.EndsWith("/ei/bot_first_contact", StringComparison.Ordinal) == true) {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent(Convert.ToBase64String(firstContact.ToByteArray()))
            };
        }

        var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var encoded = form.Split('=', 2)[1];
        var coopRequest = ContractCoopStatusRequest.Parser.ParseFrom(
            Convert.FromBase64String(Uri.UnescapeDataString(encoded)));
        observedClientVersion = coopRequest.ClientVersion;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(Convert.ToBase64String(new ContractCoopStatusResponse().ToByteArray()))
        };
    });
    using var http = new HttpClient(handler) {
        BaseAddress = new Uri("https://www.auxbrain.com/"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    var client = new EggIncClient(new EggIncApiSettings(), http);

    await client.GetBackupAsync("ei123");
    await client.GetCoopStatusAsync("contract", "coop");

    AssertEqual(76U, observedClientVersion, "learned client version");
}

static async Task TestEggIncBackupCache() {
    var requestCount = 0;
    var firstContact = new EggIncFirstContactResponse {
        Backup = new Backup { UserName = "Cached Player" }
    };
    var responseBody = Convert.ToBase64String(firstContact.ToByteArray());
    var handler = new StubHttpMessageHandler(_ => {
        Interlocked.Increment(ref requestCount);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(responseBody)
        };
    });
    using var http = new HttpClient(handler) {
        BaseAddress = new Uri("https://www.auxbrain.com/"),
        Timeout = TimeSpan.FromSeconds(2)
    };
    var client = new EggIncClient(new EggIncApiSettings(), http);

    var backups = await Task.WhenAll(Enumerable.Range(0, 12)
        .Select(_ => client.GetBackupAsync("ei123")));

    AssertEqual(1, requestCount, "first-contact request count");
    AssertTrue(backups.All(backup => backup?.UserName == "Cached Player"), "all callers receive cached backup");
}

static async Task TestEgg9000LeaderboardCache() {
    var requestCount = 0;
    var handler = new StubHttpMessageHandler(_ => {
        Interlocked.Increment(ref requestCount);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
        };
    });
    using var http = new HttpClient(handler);
    var client = new Egg9000Client(new Egg9000Settings("https://egg9000.test/", "test-key"), http);

    await client.GetLeaderboardAsync();
    await client.GetLeaderboardAsync();

    AssertEqual(1, requestCount, "EGG9000 leaderboard request count");
}

static async Task AuditMigrationAsync(string legacyJsonPath, string databasePath) {
    var tableMap = new (string JsonProperty, string Table)[] {
        ("registeredEids", "registered_eids"),
        ("demerits", "demerits"),
        ("missingJoinAlerts", "missing_join_alerts"),
        ("firstCoopAwards", "first_coop_awards"),
        ("weeklyTokenLeaderboardPosts", "weekly_token_leaderboard_posts"),
        ("contractLateNotices", "contract_late_notices"),
        ("shipReturnNotifications", "ship_return_notifications"),
        ("beerStats", "beer_stats"),
        ("beerGiftLogs", "beer_gift_logs"),
        ("plottyMemories", "plotty_memories"),
        ("plottyConversations", "plotty_conversations")
    };

    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(legacyJsonPath));
    await using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(databasePath)};Mode=ReadOnly");
    await connection.OpenAsync();

    var mismatches = new List<string>();
    foreach(var (jsonProperty, table) in tableMap) {
        var jsonCount = document.RootElement.TryGetProperty(jsonProperty, out var array)
            ? array.GetArrayLength()
            : 0;
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        var databaseCount = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        Console.WriteLine($"{table}: JSON={jsonCount}, SQLite={databaseCount}");
        if(jsonCount != databaseCount) {
            mismatches.Add($"{table} ({jsonCount} != {databaseCount})");
        }
    }

    if(mismatches.Count > 0) {
        throw new InvalidOperationException($"Migration count mismatch: {string.Join(", ", mismatches)}");
    }
    Console.WriteLine("PASS Live JSON-to-SQLite migration counts match.");
}

static Task TestTokenLeaderboardWeek() {
    var mountain = TimeZoneInfo.CreateCustomTimeZone("Test Mountain", TimeSpan.FromHours(-7), "Test Mountain", "Test Mountain");
    var beforeMondayReset = new DateTimeOffset(2026, 1, 12, 16, 59, 0, TimeSpan.Zero);
    var afterMondayReset = new DateTimeOffset(2026, 1, 12, 17, 1, 0, TimeSpan.Zero);

    var before = WeeklyTokenLeaderboard.CurrentWeek(beforeMondayReset, mountain);
    var after = WeeklyTokenLeaderboard.CurrentWeek(afterMondayReset, mountain);

    AssertEqual(new DateTimeOffset(2026, 1, 5, 17, 0, 0, TimeSpan.Zero), before.Start, "week before reset");
    AssertEqual(new DateTimeOffset(2026, 1, 12, 17, 0, 0, TimeSpan.Zero), after.Start, "week after reset");
    AssertEqual(after.Start.AddDays(7), after.End, "week end");
    return Task.CompletedTask;
}

static Task TestTokenLeaderboardTokens() {
    var weekStart = new DateTimeOffset(2026, 1, 12, 17, 0, 0, TimeSpan.Zero);
    var weekEnd = weekStart.AddDays(7);
    var backup = new Backup { Contracts = new MyContracts() };
    backup.Contracts.Contracts.Add(new LocalContract {
        ContractIdentifier = "active-contract",
        CoopIdentifier = "coop-a",
        Accepted = true,
        TimeAccepted = weekStart.AddHours(2).ToUnixTimeSeconds()
    });
    backup.Contracts.Archive.Add(new LocalContract {
        ContractIdentifier = "completed-contract",
        CoopIdentifier = "coop-b",
        Accepted = true,
        TimeAccepted = weekStart.AddDays(1).ToUnixTimeSeconds(),
        Evaluation = new ContractEvaluation { GiftTokensSent = 7 }
    });
    backup.Contracts.Archive.Add(new LocalContract {
        ContractIdentifier = "old-contract",
        CoopIdentifier = "coop-c",
        Accepted = true,
        TimeAccepted = weekStart.AddDays(-1).ToUnixTimeSeconds(),
        Evaluation = new ContractEvaluation { GiftTokensSent = 100 }
    });
    backup.Farms.Add(new Backup.Types.Simulation {
        ContractId = "active-contract",
        BoostTokensGiven = 4
    });

    var result = WeeklyTokenLeaderboard.CountTokens(backup, weekStart, weekEnd);

    AssertEqual(11U, result.TokensSent, "weekly tokens sent");
    AssertEqual(2, result.ContractCount, "weekly contract count");
    return Task.CompletedTask;
}

static Task TestPlottyAiPromptRedaction() {
    var prompt = "Check EI1234567890123456 with sk-example_secret_key_123456 and egg_example_secret_key_123456.";
    var sanitized = PlottyAiClient.SanitizePrompt(prompt);

    AssertTrue(!sanitized.Contains("EI1234567890123456", StringComparison.Ordinal), "EID removed");
    AssertTrue(!sanitized.Contains("sk-example_secret_key_123456", StringComparison.Ordinal), "OpenAI key removed");
    AssertTrue(!sanitized.Contains("egg_example_secret_key_123456", StringComparison.Ordinal), "EGG9000 key removed");
    AssertTrue(sanitized.Contains("[private EID removed]", StringComparison.Ordinal), "EID placeholder present");
    return Task.CompletedTask;
}

static async Task TestPlottyAiLocalFallback() {
    var client = new PlottyAiClient(null);
    var reply = await client.GenerateReplyAsync(
        1,
        10,
        "Can you help with my contract rate?",
        new PlottyMemory(1, 10, 10, 5, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    AssertTrue(!string.IsNullOrWhiteSpace(reply), "local fallback reply present");
    AssertTrue(reply!.Contains("I", StringComparison.Ordinal), "reply uses first person");
    AssertTrue(reply.Contains("contract", StringComparison.OrdinalIgnoreCase), "reply answers contract topic");
}

static async Task TestPlottyAiLocalGreeting() {
    var client = new PlottyAiClient(null);
    var reply = await client.GenerateReplyAsync(
        1,
        10,
        "hi",
        new PlottyMemory(1, 10, 10, 5, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    AssertTrue(!string.IsNullOrWhiteSpace(reply), "greeting reply present");
    AssertTrue(!reply!.Contains("last thing we were discussing", StringComparison.OrdinalIgnoreCase), "greeting does not use stale topic");
    AssertTrue(!reply.Contains("new feature", StringComparison.OrdinalIgnoreCase), "greeting does not ask workflow question");
    AssertTrue(!reply.Contains("useful thread", StringComparison.OrdinalIgnoreCase), "greeting does not sound like analysis");
}

static async Task TestRegisteredEids() {
    using var fixture = new StoreFixture();
    await fixture.Store.SaveRegisteredEidAsync(1, 10, "ei_first", "First");
    await fixture.Store.SaveRegisteredEidAsync(1, 10, "ei_second", "Second");

    var accounts = await fixture.Store.GetRegisteredAccountsAsync(1, 10);

    AssertEqual(2, accounts.Count, "registered account count");
    AssertTrue(accounts.Any(a => a.Eid == "EI_FIRST" && a.EggName == "First"), "first account present");
    AssertTrue(accounts.Any(a => a.Eid == "EI_SECOND" && a.EggName == "Second"), "second account present");
    AssertTrue(accounts.All(a => a.EidHash.Length == 64), "hashes are sha256 hex");
}

static async Task TestRemoveRegisteredEidsForUser() {
    using var fixture = new StoreFixture();
    await fixture.Store.SaveRegisteredEidAsync(1, 10, "ei_first", "First");
    await fixture.Store.SaveRegisteredEidAsync(1, 10, "ei_second", "Second");
    await fixture.Store.SaveRegisteredEidAsync(1, 11, "ei_other", "Other");
    await fixture.Store.SaveRegisteredEidAsync(2, 10, "ei_other_guild", "Other Guild");

    var removed = await fixture.Store.RemoveRegisteredEidsForUserAsync(1, 10);
    var removedAgain = await fixture.Store.RemoveRegisteredEidsForUserAsync(1, 10);
    var leavingUserAccounts = await fixture.Store.GetRegisteredAccountsAsync(1, 10);
    var sameGuildOtherUser = await fixture.Store.GetRegisteredAccountsAsync(1, 11);
    var otherGuildSameUser = await fixture.Store.GetRegisteredAccountsAsync(2, 10);

    AssertEqual(2, removed, "removed leaving user's accounts");
    AssertEqual(0, removedAgain, "second removal is empty");
    AssertEqual(0, leavingUserAccounts.Count, "leaving user account count");
    AssertEqual(1, sameGuildOtherUser.Count, "same guild other user account count");
    AssertEqual(1, otherGuildSameUser.Count, "other guild same user account count");
}

static async Task TestLegacyPlaintextLinksRemoved() {
    using var fixture = new StoreFixture();
    await File.WriteAllTextAsync(fixture.StatePath, """
        {
          "links": [
            {
              "guildId": 1,
              "discordUserId": 10,
              "eggId": "EI_SECRET",
              "eggName": "Secret"
            }
          ],
          "registeredEids": []
        }
        """);

    await fixture.Store.RemoveLegacyPlaintextLinksAsync();
    var json = await File.ReadAllTextAsync(fixture.StatePath);

    AssertTrue(!json.Contains("EI_SECRET", StringComparison.OrdinalIgnoreCase), "legacy plaintext EID removed");
    AssertTrue(!json.Contains("\"links\"", StringComparison.OrdinalIgnoreCase), "legacy links section removed");
}

static async Task TestLegacyJsonMigration() {
    using var fixture = new StoreFixture(initializeStore: false);
    var secureText = new SecureText(fixture.KeyPath);
    var encryptedEid = secureText.Encrypt("EI_MIGRATED");
    var eidHash = SecureText.Sha256("EI_MIGRATED");
    var now = DateTimeOffset.UtcNow;
    await File.WriteAllTextAsync(fixture.StatePath, $$"""
        {
          "registeredEids": [
            {
              "guildId": 1,
              "discordUserId": 10,
              "eidHash": "{{eidHash}}",
              "encryptedEid": "{{encryptedEid}}",
              "eggName": "Migrated",
              "updatedAt": "{{now:O}}"
            }
          ],
          "demerits": [
            {
              "id": "migration-demerit",
              "guildId": 1,
              "discordUserId": 10,
              "amount": 1,
              "reason": "migration test",
              "contractId": null,
              "playerName": "Migrated",
              "sourceKey": null,
              "createdAt": "{{now:O}}",
              "expiresAt": "{{now.AddDays(30):O}}",
              "removedAt": null
            }
          ]
        }
        """);

    fixture.CreateStore();
    var accounts = await fixture.Store.GetRegisteredAccountsAsync(1, 10);
    var demerits = await fixture.Store.GetActiveDemeritsAsync(1, 10);
    AssertEqual(1, accounts.Count, "migrated account count");
    AssertEqual("EI_MIGRATED", accounts[0].Eid, "migrated EID");
    AssertEqual(1, demerits.Count, "migrated demerit count");
    AssertTrue(File.Exists(fixture.Store.DatabasePath), "SQLite database created");

    fixture.ReopenStore();
    await File.WriteAllTextAsync(fixture.StatePath, """{ "registeredEids": [] }""");
    accounts = await fixture.Store.GetRegisteredAccountsAsync(1, 10);
    AssertEqual(1, accounts.Count, "legacy JSON is not reimported");
}

static async Task TestSqlitePersistence() {
    using var fixture = new StoreFixture();
    await fixture.Store.SaveRegisteredEidAsync(1, 10, "ei_persisted", "Persisted");
    await fixture.Store.RecordContractLateNoticeAsync(1, 10, "contract-a", "later", "testing");
    await fixture.Store.TryAddPlottyBeerAsync(1, 10, botBuysBack: true);

    fixture.ReopenStore();
    var accounts = await fixture.Store.GetRegisteredAccountsAsync(1, 10);
    var lateUsers = await fixture.Store.GetActiveContractLateNoticeUserIdsAsync(1, "contract-a");
    var leaderboard = await fixture.Store.GetBeerLeaderboardAsync(1);

    AssertEqual(1, accounts.Count, "reopened account count");
    AssertTrue(lateUsers.Contains(10), "reopened late notice");
    AssertEqual(1, leaderboard.Single().BeersBoughtByBot, "reopened beverage count");
}

static async Task TestGoldenEggSnapshots() {
    using var fixture = new StoreFixture();
    var now = DateTimeOffset.UtcNow;
    await fixture.Store.RecordGoldenEggSnapshotsAsync([
        new GoldenEggSnapshot(1, 10, "hash-a", "Primary", 1_000, now.AddHours(-25)),
        new GoldenEggSnapshot(1, 10, "hash-a", "Primary", 1_250, now),
        new GoldenEggSnapshot(1, 10, "hash-b", "Alt", 500, now)
    ]);

    fixture.ReopenStore();
    var primary = await fixture.Store.GetGoldenEggSnapshotsAsync(1, "hash-a");
    var alt = await fixture.Store.GetGoldenEggSnapshotsAsync(1, "hash-b");

    AssertEqual(2, primary.Count, "primary EID snapshot count");
    AssertEqual(1_000UL, primary[0].GoldenEggsEarned, "oldest earned counter");
    AssertEqual(1_250UL, primary[1].GoldenEggsEarned, "latest earned counter");
    AssertEqual(1, alt.Count, "alternate EID remains separate");
}

static async Task TestConcurrentDataStoreWrites() {
    using var fixture = new StoreFixture();
    await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
        fixture.Store.AddDemeritsAsync(1, 10, 1, $"concurrent-{index}", null, null)));

    var active = await fixture.Store.GetActiveDemeritsAsync(1, 10);
    AssertEqual(20, active.Count, "concurrent demerit count");
}

static async Task TestConcurrentDataStoreReads() {
    using var fixture = new StoreFixture();
    await fixture.Store.SaveRegisteredEidAsync(1, 10, "ei_reader", "Reader");
    var reads = Enumerable.Range(0, 20)
        .Select(_ => fixture.Store.GetRegisteredEidsAsync(1));
    var write = fixture.Store.AddDemeritsAsync(1, 10, 1, "parallel", null, null);

    var results = await Task.WhenAll(reads);
    await write;

    AssertTrue(results.All(accounts => accounts.Count == 1), "concurrent reads return registered account");
}

static async Task TestDemerits() {
    using var fixture = new StoreFixture();
    var added = await fixture.Store.AddDemeritsAsync(1, 10, 2, "test", "contract", "source");
    var active = await fixture.Store.GetActiveDemeritsAsync(1, 10);

    AssertEqual(2, added, "added count");
    AssertEqual(2, active.Count, "active count after add");

    var removed = await fixture.Store.RemoveDemeritsAsync(1, 10, 1);
    active = await fixture.Store.GetActiveDemeritsAsync(1, 10);

    AssertEqual(1, removed, "removed count");
    AssertEqual(1, active.Count, "active count after remove");
}

static async Task TestContractLateNotices() {
    using var fixture = new StoreFixture();
    await fixture.Store.RecordContractLateNoticeAsync(1, 10, "contract-a", "tonight", "note");
    await fixture.Store.RecordContractLateNoticeAsync(1, 20, null, null, null);

    var contractA = await fixture.Store.GetActiveContractLateNoticeUserIdsAsync(1, "contract-a");
    var contractB = await fixture.Store.GetActiveContractLateNoticeUserIdsAsync(1, "contract-b");

    AssertTrue(contractA.SetEquals([10UL, 20UL]), "specific plus general notice match contract-a");
    AssertTrue(contractB.SetEquals([20UL]), "general notice matches contract-b");

    var removed = await fixture.Store.RemoveContractLateNoticesAsync(1, 10, "contract-a");
    contractA = await fixture.Store.GetActiveContractLateNoticeUserIdsAsync(1, "contract-a");

    AssertEqual(1, removed, "removed late notices");
    AssertTrue(contractA.SetEquals([20UL]), "specific notice removed");
}

static async Task TestBeerCooldowns() {
    using var fixture = new StoreFixture();
    var first = await fixture.Store.TryAddPlottyBeerAsync(1, 10, botBuysBack: false);
    var second = await fixture.Store.TryAddPlottyBeerAsync(1, 10, botBuysBack: false);
    var bypass = await fixture.Store.TryAddPlottyBeerAsync(1, 10, botBuysBack: true, bypassCooldown: true);
    var firstGift = await fixture.Store.TryGiftBeerAsync(1, 10, 20);
    var blockedGift = await fixture.Store.TryGiftBeerAsync(1, 10, 20);
    var bypassGift = await fixture.Store.TryGiftBeerAsync(1, 10, 20, bypassCooldown: true);

    AssertTrue(first.Accepted, "first beer accepted");
    AssertTrue(!second.Accepted && second.RetryAfter is not null, "second beer blocked by cooldown");
    AssertTrue(bypass.Accepted, "admin bypass beer accepted");
    AssertEqual(2, bypass.Stats.BeersGivenToBot, "bypass updates beer count");
    AssertEqual(1, bypass.Stats.BeersBoughtByBot, "bot buyback counted");
    AssertTrue(firstGift.Accepted, "first member beverage gift accepted");
    AssertTrue(!blockedGift.Accepted && blockedGift.RetryAfter is not null && blockedGift.RetryAfter.Value <= TimeSpan.FromHours(1), "member beverage gift is blocked for up to one hour");
    AssertTrue(bypassGift.Accepted, "unlimited beverage bypass gift accepted");
}

static async Task TestShipReturnNotifications() {
    using var fixture = new StoreFixture();
    var now = DateTimeOffset.UtcNow;
    var notification = new ShipReturnNotification(1, 10, "hash", "mission", "Henerprise", now.AddMinutes(-1), now.AddHours(-1), null);

    await fixture.Store.UpsertShipReturnNotificationAsync(notification);
    var due = await fixture.Store.GetDueShipReturnNotificationsAsync(1, now);

    AssertEqual(1, due.Count, "due notification count");
    await fixture.Store.MarkShipReturnNotificationSentAsync(notification.Key, now);
    due = await fixture.Store.GetDueShipReturnNotificationsAsync(1, now.AddMinutes(1));

    AssertEqual(0, due.Count, "sent notification no longer due");
}

static async Task TestPendingPolls() {
    using var fixture = new StoreFixture();
    var now = DateTimeOffset.UtcNow;
    var poll = new PendingPoll(
        "poll:123",
        1,
        2,
        123,
        "general",
        "Test poll",
        null,
        ["One", "Two"],
        ["1", "2"],
        now.AddMinutes(-1),
        now.AddHours(-1));

    await fixture.Store.SavePendingPollAsync(poll);
    fixture.ReopenStore();
    var due = await fixture.Store.GetDuePendingPollsAsync(1, now);
    AssertEqual(1, due.Count, "persisted due poll count");
    AssertSequenceEqual(poll.Options, due[0].Options, "persisted poll options");

    await fixture.Store.RemovePendingPollAsync(poll.Key);
    due = await fixture.Store.GetDuePendingPollsAsync(1, now);
    AssertEqual(0, due.Count, "removed due poll count");
}

static Task TestMonitorHealth() {
    var health = new MonitorHealthService();
    var now = DateTimeOffset.UtcNow;

    health.ReportFailure("ship-returns", 1, new InvalidOperationException("network failed"), now);
    var failed = health.GetSnapshots(1).Single();
    AssertEqual("ship-returns", failed.Name, "monitor name");
    AssertEqual(1, failed.FailureCount, "failure count");
    AssertEqual("network failed", failed.LastError, "last error");

    health.ReportSuccess("ship-returns", 1, now.AddMinutes(1));
    var recovered = health.GetSnapshots(1).Single();
    AssertEqual(0, recovered.FailureCount, "failure count reset");
    AssertEqual(null, recovered.LastError, "last error cleared");
    AssertTrue(recovered.LastSuccessAt > recovered.LastFailureAt, "success recorded after failure");
    return Task.CompletedTask;
}

static Task TestCoopArtifactReports() {
    var status = new ContractCoopStatusResponse { ContractIdentifier = "contract-a" };
    var player = new ContractCoopStatusResponse.Types.ContributionInfo {
        UserId = "ei_player",
        UserName = "Player",
        FarmInfo = new PlayerFarmInfo { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
        ProductionParams = new FarmProductionParams { Elr = 200, Sr = 100 }
    };
    player.FarmInfo.EquippedArtifacts.Add(new CompleteArtifact {
        Spec = new ArtifactSpec { Name = ArtifactSpec.Types.Name.QuantumMetronome }
    });

    var teammate = new ContractCoopStatusResponse.Types.ContributionInfo {
        UserId = "ei_teammate",
        UserName = "Teammate",
        FarmInfo = new PlayerFarmInfo { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
        ProductionParams = new FarmProductionParams { Elr = 100, Sr = 200 }
    };
    teammate.FarmInfo.EquippedArtifacts.Add(new CompleteArtifact {
        Spec = new ArtifactSpec { Name = ArtifactSpec.Types.Name.TachyonDeflector }
    });

    status.Contributors.Add(player);
    status.Contributors.Add(teammate);
    status.Contributors.Add(new ContractCoopStatusResponse.Types.ContributionInfo {
        UserId = "ei_unsynced",
        UserName = "Unsynced"
    });

    var account = new RegisteredEggAccount(10, "EI_PLAYER", "Player", DateTimeOffset.UtcNow);
    var context = CoopArtifactAnalyzer.Analyze(status, account, "Player");

    AssertTrue(context is not null, "co-op artifact context created");
    AssertEqual(3, context!.Members.Count, "co-op contributor count");
    AssertEqual(2, context.ReportingMemberCount, "artifact reporting member count");
    AssertEqual(1, context.MissingReportCount, "missing artifact report count");
    AssertEqual("ei_player", context.RequestingPlayer?.UserId, "requesting player matched");
    AssertTrue(
        context.Members.Single(member => member.UserId == "ei_teammate").EquippedArtifacts.Any(
            artifact => artifact.Spec.Name == ArtifactSpec.Types.Name.TachyonDeflector),
        "teammate deflector captured");
    return Task.CompletedTask;
}

static Task TestCoopArtifactScoring() {
    var player = TestMember("ei_player", isPlayer: true, laying: 200, shipping: 100);
    var teammate = TestMember("ei_teammate", isPlayer: false, laying: 100, shipping: 200);
    var context = new CoopArtifactContext("contract-a", [player, teammate]);
    var currentSet = new[] { TestArtifact(2, 1, 0) };
    var balancedSet = new[] { TestArtifact(2, 2, 0) };

    var balanced = CoopArtifactAnalyzer.EvaluateSet(context, currentSet, balancedSet);
    AssertTrue(balanced.UsesLiveProduction, "live production scoring used");
    AssertApproximately(1.5, balanced.CoopOutputRatio, 0.0001, "balanced set co-op output ratio");

    var neutralContext = new CoopArtifactContext(
        "contract-a",
        [TestMember("ei_player", true, 100, 100), teammate]);
    var deflectorSet = new[] { TestArtifact(1, 1, 0.5) };
    var deflector = CoopArtifactAnalyzer.EvaluateSet(neutralContext, [], deflectorSet);
    AssertApproximately(1.25, deflector.CoopOutputRatio, 0.0001, "deflector co-op output ratio");
    return Task.CompletedTask;
}

static CoopArtifactMemberSnapshot TestMember(
    string userId,
    bool isPlayer,
    double laying,
    double shipping) =>
    new(
        userId,
        userId,
        isPlayer,
        HasFarmInfo: true,
        EquippedArtifacts: [],
        EggLayingRate: laying,
        ShippingRate: shipping,
        DeflectorBonus: 0,
        TeamEarningsBonus: 0,
        ReportedEggLayingMultiplier: 1,
        ReportedEarningsMultiplier: 1,
        SyncedAt: DateTimeOffset.UtcNow);

static ArtifactCandidate TestArtifact(double laying, double shipping, double deflector) {
    var spec = new ArtifactSpec { Name = deflector > 0
        ? ArtifactSpec.Types.Name.TachyonDeflector
        : ArtifactSpec.Types.Name.QuantumMetronome };
    var artifact = new CompleteArtifact { Spec = spec };
    return new ArtifactCandidate(
        new ArtifactInventoryItem { Artifact = artifact, Quantity = 1 },
        artifact,
        spec.Name,
        spec.Name.ToString(),
        deflector > 0 ? ArtifactPurpose.TeamLaying : ArtifactPurpose.Laying,
        laying,
        shipping,
        1 + deflector,
        deflector,
        "test",
        null,
        [],
        0,
        false);
}

static void AssertEqual<T>(T expected, T actual, string label) {
    if(!EqualityComparer<T>.Default.Equals(expected, actual)) {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}

static void AssertTrue(bool condition, string label) {
    if(!condition) {
        throw new InvalidOperationException(label);
    }
}

static void AssertApproximately(double expected, double actual, double tolerance, string label) {
    if(Math.Abs(expected - actual) > tolerance) {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}

static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string label) {
    if(expected.Count != actual.Count || expected.Where((value, index) => !EqualityComparer<T>.Default.Equals(value, actual[index])).Any()) {
        throw new InvalidOperationException($"{label}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }
}

sealed class StoreFixture : IDisposable {
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plotty-tests", Guid.NewGuid().ToString("N"));

    public StoreFixture(bool initializeStore = true) {
        Directory.CreateDirectory(_dir);
        StatePath = Path.Combine(_dir, "state.json");
        KeyPath = Path.Combine(_dir, "key.bin");
        Store = null!;
        if(initializeStore) {
            CreateStore();
        }
    }

    public DataStore Store { get; private set; }
    public string StatePath { get; }
    public string KeyPath { get; }

    public void CreateStore() {
        Store = new DataStore(StatePath, new SecureText(KeyPath));
    }

    public void ReopenStore() {
        Store.Dispose();
        CreateStore();
    }

    public void Dispose() {
        Store?.Dispose();
        if(Directory.Exists(_dir)) {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responder(request));
}
