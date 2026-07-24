using Discord;
using Discord.WebSocket;
using EggContribBot.Services;
using EggContribBot.Models;
using EggContribBot.Proto;

namespace EggContribBot.Commands;

class FunCommands(DataStore dataStore)
{
    private readonly EggWikiClient _wikiClient = new EggWikiClient();
    private readonly DataStore _dataStore = dataStore;

    private readonly EggIncClient _eggClient = new EggIncClient();

    public async Task HandleHelpAsync(SocketSlashCommand command) {
        await command.DeferAsync();

        var question = Helpers.GetString(command, "question");
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

        var answer = await _wikiClient.AnswerAsync(question);
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

        var accounts = await _dataStore.GetRegisteredAccountsAsync(command.GuildId!.Value, command.User.Id);
        var account = accounts.FirstOrDefault();
        if(account is null) {
            return new PersonalHelpAnswer(
                "Plotty Help - Registered EID Needed",
                "Plotty can answer personal PE/SE farmer-level questions after you run `/register-eid`.",
                "Personal backup unavailable until an EID is registered.",
                null);
        }

        var backup = await _eggClient.GetBackupAsync(account.Eid);
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
        var normalized = Helpers.NormalizeName(question);
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
                $"You are already **{currentRank.Name}**.\n\n**SE:** {Helpers.FormatEggs(soulEggs)}\n**PE:** {prophecyEggs:0}\n**Estimated EB:** {Helpers.FormatEggs(earningsBonus)}%",
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
            $"**Current SE:** {Helpers.FormatEggs(soulEggs)}",
            $"**Current PE:** {prophecyEggs:0}",
            $"**Estimated EB:** {Helpers.FormatEggs(earningsBonus)}%",
            $"**Target EB:** {Helpers.FormatEggs(targetEb)}%",
            "",
            $"With your current PE, you need about **{Helpers.FormatEggs(seNeededWithCurrentPe)} more SE**."
        };

        if(peOnlyNeeded >= 0) {
            lines.Add($"With no extra SE, you need about **{peOnlyNeeded} more PE**.");
        } else {
            lines.Add("With no extra SE, Plotty did not find a PE-only path within 300 additional PE.");
        }

        lines.Add("");
        if(unclaimedSoulEggs > 0 || unclaimedPe > 0) {
            lines.Add($"Unclaimed backup values not counted in active EB: `{Helpers.FormatEggs(unclaimedSoulEggs)}` SE and `{unclaimedPe}` PE.");
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
}