using System.ComponentModel;
using Discord.WebSocket;
using EggContribBot.Config;
using EggContribBot.Services;
using EggContribBot.Models;
using EggContribBot.Proto;

namespace EggContribBot.Commands;

public static class Helpers
{
    private static BotSettings? _settings;
    private static HashSet<ulong>? _botAdmins;

    private static EggIncClient? _eggClient;

    private static EggIncClient EggClient =>
        _eggClient ?? throw new InvalidOperationException(
            $"{nameof(Helpers)}.{nameof(Initialize)} must be called before use.");

    //private plottyAdminUserIds = settings.Discord.ParsedAdminUserIds.ToHashSet();
    //[MemberNotNull(nameof(_settings), nameof(_eggClient))]
    public static void Initialize(BotSettings botSettings, EggIncClient eggClient)
    {
        _settings = botSettings;
        _botAdmins = _settings.Discord.ParsedAdminUserIds.ToHashSet();
        // if (eggClient is null || botSettings is null)
        // {
        //     throw new Exception();
        // }
        _eggClient = eggClient;
    }
    public static string GetString(SocketSlashCommand command, string name) =>
        (string)command.Data.Options.First(o => o.Name == name).Value;
    
    public static bool GetBool(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value is bool value && value;

    public static string FormatEggs(double amount) {
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

    public static string NormalizeName(string? value) {
        if(string.IsNullOrWhiteSpace(value)) {
            return "";
        }

        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    public static bool HasStaffRole(SocketGuildUser user) =>
        // TODO: Configurable per guild. Uses guild config and RoleIDs (provide autocomplete)
        IsPlottyAdmin(user) ||
        user.Roles.Any(r => string.Equals(r.Name, "Staff", StringComparison.OrdinalIgnoreCase));
    
    private static bool IsPlottyAdmin(SocketGuildUser user)
    {
        if (_botAdmins is null)
        {
            return false;
        }
        return _botAdmins.Contains(user.Id);
    }

    // TODO: Fix the naming of this thing, configurable per guild (autofill)
    public static SocketTextChannel? FindPlottyQuestionsChannel(SocketGuild guild) =>
        guild.TextChannels.FirstOrDefault(c => NormalizeName(c.Name) == "plottyquestions");

    public static bool IsRegisteredContributor(
        ContractCoopStatusResponse.Types.ContributionInfo contributor,
        ISet<string> visibleUserIds,
        ISet<string> visibleUserNames) =>
        (!string.IsNullOrWhiteSpace(contributor.UserId) && visibleUserIds.Contains(EggIncClient.NormalizeEggId(contributor.UserId))) ||
        (!string.IsNullOrWhiteSpace(contributor.UserName) && visibleUserNames.Contains(NormalizeName(contributor.UserName)));

    public async static Task<(
    IReadOnlyDictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse> Statuses,
    int Failed,
    int SkippedOldContracts,
    int RecentContractCount)> GetRecentRegisteredStatusesAsync(IReadOnlyList<RegisteredEggAccount> accounts) {
        var now = DateTimeOffset.UtcNow;
        var recentContractIds = (await EggClient.GetCurrentContractsAsync())
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

    static Task<IReadOnlyList<(RegisteredEggAccount Account, PlayerCoopLookupResult Lookup)>> GetAccountCoopLookupsAsync(
    IEnumerable<RegisteredEggAccount> accounts) =>
    SelectWithConcurrencyAsync(
        accounts.ToList(),
        4, // TODO: Hard coded value is bad.
        async account => (account, await EggClient.GetPlayerCoopLookupAsync(account.Eid)));
}