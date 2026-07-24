using EggContribBot.Proto;

namespace EggContribBot.Services;
public static class WeeklyTokenLeaderboard {
    public static TimeZoneInfo MountainTimeZone() {
        try {
            return TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");
        } catch(TimeZoneNotFoundException) {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        }
    }

    public static (DateTimeOffset Start, DateTimeOffset End) CurrentWeek(
        DateTimeOffset now,
        TimeZoneInfo? mountainTimeZone = null) {
        var zone = mountainTimeZone ?? MountainTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var daysSinceMonday = ((int)localNow.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var localStartDate = localNow.Date.AddDays(-daysSinceMonday);
        var localStart = localStartDate.AddHours(10);
        if(localNow.DateTime < localStart) {
            localStart = localStart.AddDays(-7);
        }

        var start = LocalToOffset(localStart, zone);
        return (start, LocalToOffset(localStart.AddDays(7), zone));
    }

    public static string WeekKey(DateTimeOffset weekStart, TimeZoneInfo? mountainTimeZone = null) {
        var zone = mountainTimeZone ?? MountainTimeZone();
        return TimeZoneInfo.ConvertTime(weekStart, zone).ToString("yyyy-MM-dd");
    }

    public static (uint TokensSent, int ContractCount) CountTokens(
        Backup? backup,
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd) {
        if(backup?.Contracts is null) {
            return (0, 0);
        }

        var farmsByContract = backup.Farms
            .Where(f => !string.IsNullOrWhiteSpace(f.ContractId))
            .GroupBy(f => f.ContractId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Max(f => f.BoostTokensGiven),
                StringComparer.OrdinalIgnoreCase);

        var tokensByCoop = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach(var contract in backup.Contracts.Contracts.Concat(backup.Contracts.Archive)) {
            var contractId = LocalContractId(contract);
            if(string.IsNullOrWhiteSpace(contractId) || !WasAcceptedDuring(contract, weekStart, weekEnd)) {
                continue;
            }

            var coopCode = contract.CoopIdentifier ?? string.Empty;
            var key = $"{contractId}|{coopCode}";
            var tokens = contract.Evaluation?.GiftTokensSent ?? 0;
            if(farmsByContract.TryGetValue(contractId, out var activeTokens)) {
                tokens = Math.Max(tokens, activeTokens);
            }

            if(tokensByCoop.TryGetValue(key, out var existing)) {
                tokensByCoop[key] = Math.Max(existing, tokens);
            } else {
                tokensByCoop[key] = tokens;
            }
        }

        return (tokensByCoop.Values.Aggregate(0U, (total, value) => total + value), tokensByCoop.Count);
    }

    private static bool WasAcceptedDuring(
        LocalContract contract,
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd) {
        if(contract.TimeAccepted <= 0) {
            return false;
        }

        var acceptedAt = DateTimeOffset.FromUnixTimeSeconds((long)contract.TimeAccepted);
        return acceptedAt >= weekStart && acceptedAt < weekEnd;
    }

    private static string LocalContractId(LocalContract contract) =>
        !string.IsNullOrWhiteSpace(contract.ContractIdentifier)
            ? contract.ContractIdentifier
            : contract.Contract?.Identifier ?? string.Empty;

    private static DateTimeOffset LocalToOffset(DateTime localTime, TimeZoneInfo zone) {
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
