using Discord;
using Discord.WebSocket;
using EggContribBot.Services;
using EggContribBot.Proto;

namespace EggContribBot.Commands;

class AdminCommands(DataStore dataStore)
{
    private readonly DataStore _dataStore = dataStore;
    public async Task HandleAdminRemoveLateNotifyAsync(SocketSlashCommand command) {
        var staffUser = command.User as SocketGuildUser;
        if(staffUser is null || !Helpers.HasStaffRole(staffUser)) {
            await command.RespondAsync("Only members with the Staff role can remove late notices.", ephemeral: true);
            return;
        }

        var member = (SocketGuildUser)command.Data.Options.First(o => o.Name == "member").Value;
        var contractId = (command.Data.Options.FirstOrDefault(o => o.Name == "contract-id")?.Value as string)?.Trim();
        var removed = await _dataStore.RemoveContractLateNoticesAsync(
            command.GuildId!.Value,
            member.Id,
            string.IsNullOrWhiteSpace(contractId) ? null : contractId);

        var scope = string.IsNullOrWhiteSpace(contractId) ? "all active late notices" : $"active late notices for `{contractId}`";
        await command.RespondAsync($"Removed `{removed}` {scope} from {member.Mention}.", ephemeral: true);
    }

    public async Task HandleRatesAllAsync(SocketSlashCommand command) {
        var staffUser = command.User as SocketGuildUser;
        if(staffUser is null || !Helpers.HasStaffRole(staffUser)) {
            await command.RespondAsync("Only members with the Staff role can use admin rates.", ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);

        var accounts = await _dataStore.GetRegisteredEidsAsync(command.GuildId!.Value);
        if(accounts.Count == 0) {
            await command.FollowupAsync("No EIDs are registered in this server yet. Have players run `/register-eid` first.", ephemeral: true);
            return;
        }

        var registeredEggIds = accounts
            .Select(a => EggIncClient.NormalizeEggId(a.Eid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registeredNames = accounts
            .Select(a => Helpers.NormalizeName(a.EggName))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statusResult = await Helpers.GetRecentRegisteredStatusesAsync(accounts);
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

    Embed? BuildRegisteredContractEmbed(
        string contractId,
        IEnumerable<ContractCoopStatusResponse> statuses,
        ISet<string> visibleUserIds,
        ISet<string> visibleUserNames) {
        var coopStatuses = statuses.ToList();
        var players = coopStatuses
            .SelectMany(s => s.Contributors)
            .Where(c => Helpers.IsRegisteredContributor(c, visibleUserIds, visibleUserNames))
            .GroupBy(c => !string.IsNullOrWhiteSpace(c.UserId)
                ? $"id:{EggIncClient.NormalizeEggId(c.UserId)}"
                : $"name:{Helpers.NormalizeName(c.UserName)}")
            .Select(g => g.OrderByDescending(c => c.ContributionAmount).First())
            .OrderBy(c => c.ContributionRate)
            .ToList();

        if(players.Count == 0) {
            return null;
        }

        var lines = players.Select(p => {
            var name = string.IsNullOrWhiteSpace(p.UserName) ? "(unknown)" : p.UserName;
            var flag = !p.Active ? " inactive" : p.TimeCheatDetected ? " flagged" : "";
            return $"**{name}** - {Helpers.FormatEggs(p.ContributionRate * 3600)}/hr, {Helpers.FormatEggs(p.ContributionAmount)} contributed{flag}";
        });

        return new EmbedBuilder()
            .WithTitle(contractId)
            .WithColor(Color.Gold)
            .WithFooter($"Registered players across {coopStatuses.Count} co-op(s)")
            .WithDescription(string.Join("\n", lines))
            .WithCurrentTimestamp()
            .Build();
    }
}