using System.Text.RegularExpressions;
using EggContribBot;
using EggContribBot.Proto;

public static class CoopArtifactAnalyzer {
    public static CoopArtifactContext? Analyze(
        ContractCoopStatusResponse? status,
        RegisteredEggAccount account,
        string? backupUserName = null) {
        if(status is null) {
            return null;
        }

        var normalizedEid = EggIncClient.NormalizeEggId(account.Eid);
        var knownNames = new[] { account.EggName, backupUserName }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => NormalizeName(name!))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var contributors = status.Contributors.ToList();
        var requestingContributor = contributors.FirstOrDefault(contributor =>
            !string.IsNullOrWhiteSpace(contributor.UserId) &&
            string.Equals(
                EggIncClient.NormalizeEggId(contributor.UserId),
                normalizedEid,
                StringComparison.OrdinalIgnoreCase));
        if(requestingContributor is null) {
            var nameMatches = contributors
                .Where(contributor =>
                    !string.IsNullOrWhiteSpace(contributor.UserName) &&
                    knownNames.Contains(NormalizeName(contributor.UserName)))
                .ToList();
            if(nameMatches.Count == 1) {
                requestingContributor = nameMatches[0];
            }
        }

        var members = contributors
            .Select(contributor => BuildMember(contributor, ReferenceEquals(contributor, requestingContributor)))
            .ToList();

        return new CoopArtifactContext(status.ContractIdentifier, members);
    }

    public static ArtifactSetEvaluation EvaluateSet(
        CoopArtifactContext? context,
        IReadOnlyList<ArtifactCandidate> currentSet,
        IReadOnlyList<ArtifactCandidate> candidateSet) {
        var candidateMultipliers = ScoreMultipliers(candidateSet);
        var fallbackScore = Math.Min(candidateMultipliers.Laying, candidateMultipliers.Shipping) *
                            (1 + candidateMultipliers.Deflector);
        var player = context?.RequestingPlayer;
        if(context is null ||
           player is null ||
           player.EggLayingRate <= 0 ||
           player.ShippingRate <= 0) {
            return new ArtifactSetEvaluation(
                fallbackScore,
                0,
                0,
                1,
                1,
                UsesLiveProduction: false);
        }

        var productionMembers = context.Members
            .Where(member => member.EggLayingRate > 0 && member.ShippingRate > 0)
            .ToList();
        var baselineCoopOutput = productionMembers.Sum(CurrentOutput);
        if(baselineCoopOutput <= 0) {
            return new ArtifactSetEvaluation(
                fallbackScore,
                0,
                0,
                1,
                1,
                UsesLiveProduction: false);
        }

        var currentMultipliers = ScoreMultipliers(currentSet);
        var basePlayerLaying = player.EggLayingRate / Math.Max(1, currentMultipliers.Laying);
        var basePlayerShipping = player.ShippingRate / Math.Max(1, currentMultipliers.Shipping);
        var candidatePlayerLaying = basePlayerLaying * candidateMultipliers.Laying;
        var candidatePlayerShipping = basePlayerShipping * candidateMultipliers.Shipping;
        var candidatePlayerOutput = Math.Min(candidatePlayerLaying, candidatePlayerShipping);

        var candidateCoopOutput = 0d;
        foreach(var member in productionMembers) {
            if(member.IsRequestingPlayer) {
                candidateCoopOutput += candidatePlayerOutput;
                continue;
            }

            var currentReceivedDeflector = Math.Max(0, context.TotalDeflectorBonus - member.DeflectorBonus);
            var candidateReceivedDeflector = Math.Max(
                0,
                currentReceivedDeflector - player.DeflectorBonus + candidateMultipliers.Deflector);
            var adjustedLaying = member.EggLayingRate *
                                 ((1 + candidateReceivedDeflector) / (1 + currentReceivedDeflector));
            candidateCoopOutput += Math.Min(adjustedLaying, member.ShippingRate);
        }

        var baselinePlayerOutput = CurrentOutput(player);
        var playerOutputRatio = baselinePlayerOutput > 0 ? candidatePlayerOutput / baselinePlayerOutput : 1;
        var coopOutputRatio = candidateCoopOutput / baselineCoopOutput;

        return new ArtifactSetEvaluation(
            coopOutputRatio,
            candidatePlayerLaying,
            candidatePlayerShipping,
            playerOutputRatio,
            coopOutputRatio,
            UsesLiveProduction: true);
    }

    private static CoopArtifactMemberSnapshot BuildMember(
        ContractCoopStatusResponse.Types.ContributionInfo contributor,
        bool isRequestingPlayer) {
        var farmInfo = contributor.FarmInfo;
        var equippedArtifacts = farmInfo?.EquippedArtifacts.ToList() ?? [];
        var production = contributor.ProductionParams;
        var latestBuff = contributor.BuffHistory
            .OrderByDescending(buff => buff.ServerTimestamp)
            .FirstOrDefault();
        return new CoopArtifactMemberSnapshot(
            contributor.UserId,
            contributor.UserName,
            isRequestingPlayer,
            farmInfo is not null,
            equippedArtifacts,
            production?.Elr ?? 0,
            production?.Sr ?? 0,
            ArtifactBonus(equippedArtifacts, ArtifactSpec.Types.Name.TachyonDeflector),
            ArtifactBonus(equippedArtifacts, ArtifactSpec.Types.Name.ShipInABottle),
            latestBuff?.EggLayingRate ?? 1,
            latestBuff?.Earnings ?? 1,
            ParseTimestamp(farmInfo?.Timestamp ?? 0));
    }

    private static double ArtifactBonus(
        IEnumerable<CompleteArtifact> artifacts,
        ArtifactSpec.Types.Name name) =>
        artifacts
            .Where(artifact => artifact.Spec is not null && artifact.Spec.Name == name)
            .Sum(artifact => Math.Max(0, Egg9000ArtifactData.EffectDelta(artifact.Spec)));

    private static (double Laying, double Shipping, double Deflector) ScoreMultipliers(
        IReadOnlyList<ArtifactCandidate> set) =>
        (
            set.Aggregate(1d, (total, artifact) => total * artifact.LayingMultiplier),
            set.Aggregate(1d, (total, artifact) => total * artifact.ShippingMultiplier),
            set.Sum(artifact => artifact.DeflectorBonus)
        );

    private static double CurrentOutput(CoopArtifactMemberSnapshot member) =>
        Math.Min(member.EggLayingRate, member.ShippingRate);

    private static string NormalizeName(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]", "");

    private static DateTimeOffset? ParseTimestamp(double timestamp) {
        if(timestamp <= 0 || timestamp > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) {
            return null;
        }

        try {
            return DateTimeOffset.FromUnixTimeSeconds((long)timestamp);
        } catch(ArgumentOutOfRangeException) {
            return null;
        }
    }
}
