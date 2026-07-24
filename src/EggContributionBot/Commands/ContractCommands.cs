using Discord;
using Discord.WebSocket;
using EggContribBot.Proto;
using Microsoft.VisualBasic;
namespace EggContribBot.Commands;

public class ContractCommands(EggIncClient eggClient) {
    private readonly EggIncClient _eggClient = eggClient;

    public async Task HandleContractAsync(SocketSlashCommand command) {
        await command.DeferAsync();

        var contractId = Helpers.GetString(command, "contract-id");
        var coopCode = Helpers.GetString(command, "coop-code");
        var status = await _eggClient.GetCoopStatusAsync(contractId, coopCode);

        if(status is null) {
            await command.FollowupAsync(
                $"Couldn't find co-op `{coopCode}` for contract `{contractId}`. Double check both values.");
            return;
        }

        await command.FollowupAsync(embed: BuildContributionEmbed(contractId, coopCode, status, showCoopCode: false));
    }

    static Embed? BuildContributionEmbed(
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
            .WithFooter($"Total contributed: {Helpers.FormatEggs(status.TotalAmount)} eggs")
            .WithCurrentTimestamp();

        var contributors = visibleUserIds is null && visibleUserNames is null
            ? status.Contributors
            : status.Contributors.Where(c =>
                (!string.IsNullOrWhiteSpace(c.UserId) && visibleUserIds?.Contains(EggIncClient.NormalizeEggId(c.UserId)) == true) ||
                (!string.IsNullOrWhiteSpace(c.UserName) && visibleUserNames?.Contains(Helpers.NormalizeName(c.UserName)) == true));

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
            return $"**{name}** - {Helpers.FormatEggs(p.ContributionRate * 3600)}/hr, {Helpers.FormatEggs(p.ContributionAmount)} contributed{flag}";
        });

        builder.WithDescription(string.Join("\n", lines));
        return builder.Build();
    }
}