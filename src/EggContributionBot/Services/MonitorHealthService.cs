using System.Collections.Concurrent;

namespace EggContribBot.Services;
public sealed record MonitorHealthSnapshot(
    string Name,
    ulong GuildId,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    int FailureCount);

public sealed class MonitorHealthService {
    private readonly ConcurrentDictionary<string, MonitorHealthSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public void ReportSuccess(string name, ulong guildId, DateTimeOffset? at = null) {
        var now = at ?? DateTimeOffset.UtcNow;
        _snapshots.AddOrUpdate(
            Key(name, guildId),
            _ => new MonitorHealthSnapshot(name, guildId, now, null, null, 0),
            (_, current) => current with {
                LastSuccessAt = now,
                LastError = null,
                FailureCount = 0
            });
    }

    public void ReportFailure(string name, ulong guildId, Exception ex, DateTimeOffset? at = null) {
        var now = at ?? DateTimeOffset.UtcNow;
        _snapshots.AddOrUpdate(
            Key(name, guildId),
            _ => new MonitorHealthSnapshot(name, guildId, null, now, ex.Message, 1),
            (_, current) => current with {
                LastFailureAt = now,
                LastError = ex.Message,
                FailureCount = current.FailureCount + 1
            });
    }

    public IReadOnlyList<MonitorHealthSnapshot> GetSnapshots(ulong guildId) =>
        _snapshots.Values
            .Where(s => s.GuildId == guildId)
            .OrderBy(s => s.Name)
            .ToList();

    private static string Key(string name, ulong guildId) => $"{guildId}:{name}";
}
