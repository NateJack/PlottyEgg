using EggContribBot;

var tests = new (string Name, Func<Task> Run)[] {
    ("Discord settings parse guild and admin IDs", TestDiscordSettingsParsing),
    ("DataStore stores multiple registered EIDs securely", TestRegisteredEids),
    ("Demerits can be added, viewed, and removed", TestDemerits),
    ("Late notices filter by contract and expire window", TestContractLateNotices),
    ("Beer cooldowns can be enforced and bypassed", TestBeerCooldowns),
    ("Ship return notifications become due and can be marked sent", TestShipReturnNotifications),
    ("Monitor health records success and failure state", TestMonitorHealth)
};

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

    AssertSequenceEqual([100UL, 100UL, 200UL], settings.ParsedGuildIds.ToArray(), "guild ids");
    AssertSequenceEqual([300UL, 400UL], settings.ParsedAdminUserIds.ToArray(), "admin ids");
    return Task.CompletedTask;
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

    AssertTrue(first.Accepted, "first beer accepted");
    AssertTrue(!second.Accepted && second.RetryAfter is not null, "second beer blocked by cooldown");
    AssertTrue(bypass.Accepted, "admin bypass beer accepted");
    AssertEqual(2, bypass.Stats.BeersGivenToBot, "bypass updates beer count");
    AssertEqual(1, bypass.Stats.BeersBoughtByBot, "bot buyback counted");
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

static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string label) {
    if(expected.Count != actual.Count || expected.Where((value, index) => !EqualityComparer<T>.Default.Equals(value, actual[index])).Any()) {
        throw new InvalidOperationException($"{label}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }
}

sealed class StoreFixture : IDisposable {
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plotty-tests", Guid.NewGuid().ToString("N"));

    public StoreFixture() {
        Directory.CreateDirectory(_dir);
        Store = new DataStore(Path.Combine(_dir, "state.json"), new SecureText(Path.Combine(_dir, "key.bin")));
    }

    public DataStore Store { get; }

    public void Dispose() {
        if(Directory.Exists(_dir)) {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
