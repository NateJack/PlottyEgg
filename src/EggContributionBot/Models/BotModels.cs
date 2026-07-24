using Discord;
using EggContribBot.Proto;
using EggContribBot;

public sealed record RegisteredEid(ulong GuildId, ulong DiscordUserId, string EidHash, string EncryptedEid, string? EggName, DateTimeOffset UpdatedAt);
public sealed record RegisteredEggAccount(ulong DiscordUserId, string Eid, string? EggName, DateTimeOffset UpdatedAt) {
    public string EidHash => SecureText.Sha256(EggIncClient.NormalizeEggId(Eid));
}
public sealed record Egg9000LeaderboardItem(
    string? DiscordName,
    ulong DiscordId,
    string? EggIncName,
    double EarningsBonus,
    double SoulEggs,
    double EggsOfProphecy,
    double MER,
    double EggsOfTruth,
    double NumPrestiges);
public sealed record PlayerContractCandidate(string ContractId, string CoopCode, double AcceptedAt);
public sealed record ContractFarmSnapshot(int Index, Backup.Types.Simulation Farm);
public sealed record PlayerContractRate(string ContractId, double RatePerHour, double ContributionAmount);
public sealed record ArtifactCandidate(
    ArtifactInventoryItem Item,
    CompleteArtifact Artifact,
    ArtifactSpec.Types.Name Name,
    string DisplayName,
    ArtifactPurpose Purpose,
    double LayingMultiplier,
    double ShippingMultiplier,
    double TeamLayingMultiplier,
    double DeflectorBonus,
    string Reason,
    string? ImageUrl,
    IReadOnlyList<ArtifactSpec> Stones,
    int SlotCount,
    bool StonesChanged);
public sealed record StoneOption(ArtifactSpec Spec, string DisplayName, double Delta, string? ImageUrl);
public sealed record ArtifactSetSuggestion(string Label, IReadOnlyList<ArtifactCandidate> Set, double Score);
public sealed record CoopArtifactMemberSnapshot(
    string UserId,
    string UserName,
    bool IsRequestingPlayer,
    bool HasFarmInfo,
    IReadOnlyList<CompleteArtifact> EquippedArtifacts,
    double EggLayingRate,
    double ShippingRate,
    double DeflectorBonus,
    double TeamEarningsBonus,
    double ReportedEggLayingMultiplier,
    double ReportedEarningsMultiplier,
    DateTimeOffset? SyncedAt);
public sealed record CoopArtifactContext(
    string ContractId,
    IReadOnlyList<CoopArtifactMemberSnapshot> Members) {
    public CoopArtifactMemberSnapshot? RequestingPlayer => Members.FirstOrDefault(m => m.IsRequestingPlayer);
    public int ReportingMemberCount => Members.Count(m => m.HasFarmInfo);
    public int MissingReportCount => Members.Count - ReportingMemberCount;
    public int LiveProductionMemberCount => Members.Count(m => m.EggLayingRate > 0 && m.ShippingRate > 0);
    public double TotalDeflectorBonus => Members.Sum(m => m.DeflectorBonus);
    public double TeammateDeflectorBonus => Members.Where(m => !m.IsRequestingPlayer).Sum(m => m.DeflectorBonus);
    public double TeammateEarningsBonus => Members.Where(m => !m.IsRequestingPlayer).Sum(m => m.TeamEarningsBonus);
    public int TeammateDeflectorCount => Members.Count(m => !m.IsRequestingPlayer && m.DeflectorBonus > 0);
    public int TeammateEarningsArtifactCount => Members.Count(m => !m.IsRequestingPlayer && m.TeamEarningsBonus > 0);
}
public sealed record ArtifactSetEvaluation(
    double Score,
    double PlayerLayingRate,
    double PlayerShippingRate,
    double PlayerOutputRatio,
    double CoopOutputRatio,
    bool UsesLiveProduction);
public sealed record WikiAnswer(string Title, string Url, string Answer);
public sealed record PersonalHelpAnswer(string Title, string Answer, string Source, string? ImageUrl);
public sealed record FarmerRankInfo(int Oom, string Name);
public sealed record DashboardResult(string Message, IReadOnlyList<Embed> Embeds, IReadOnlyList<DashboardPlayerRow> Rows);
public sealed record DashboardPlayerRow(
    string ContractId,
    ulong DiscordUserId,
    string PlayerName,
    double RatePerHour,
    double ContributionAmount,
    bool Active,
    bool TimeCheatDetected,
    DashboardCategory Category,
    string Reason);
public sealed record AutoDemeritCandidate(ulong DiscordUserId, string ContractId, double RatePerHour, string PlayerName, string SourceKey);
public sealed record MissingJoinAccountSnapshot(RegisteredEggAccount Account, IReadOnlySet<string> JoinedContractIds);
public sealed record MissingJoinMember(ulong DiscordUserId, string Label, IReadOnlyList<RegisteredEggAccount> Accounts);
public sealed record MissingJoinAlert(string Key, DateTimeOffset PostedAt);
public sealed record FirstCoopAward(string Key, ulong GuildId, string ContractId, string CoopCode, DateTimeOffset AwardedAt);
public sealed record WeeklyTokenLeaderboardPost(ulong GuildId, string WeekKey, DateTimeOffset PostedAt);
public sealed record TokenLeaderboardEntry(ulong DiscordUserId, string PlayerName, uint TokensSent, int ContractCount);
public sealed record FirstCoopCandidate(
    RegisteredEggAccount Account,
    string ContractId,
    string CoopCode,
    ContractCoopStatusResponse Status,
    double AcceptedAt);
public sealed record ContractLateNotice(
    ulong GuildId,
    ulong DiscordUserId,
    string? ContractId,
    string? Eta,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
public sealed record ShipMissionSnapshot(
    MissionInfo Mission,
    string Key,
    string ShipName,
    string StatusName,
    string DurationTypeName,
    string MissionTypeName,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ReturnAt);
public sealed record ShipReturnNotification(
    ulong GuildId,
    ulong DiscordUserId,
    string EidHash,
    string MissionKey,
    string ShipName,
    DateTimeOffset ReturnAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NotifiedAt) {
    public string Key => $"{GuildId}:{DiscordUserId}:{EidHash}:{MissionKey}";
}
public sealed record EggsLaidTotal(string Name, double Amount);
public sealed record BeerStats(
    ulong GuildId,
    ulong DiscordUserId,
    int BeersGivenToBot,
    int BeersBoughtByBot,
    DateTimeOffset FirstBeerAt,
    DateTimeOffset LastBeerAt,
    DateTimeOffset? LastBotBeerAt,
    int BeersReceivedFromMembers = 0,
    int BeersGivenToMembers = 0,
    DateTimeOffset? LastMemberBeerReceivedAt = null,
    DateTimeOffset? LastMemberBeerGivenAt = null);
public sealed record BeerAttemptResult(bool Accepted, BeerStats Stats, TimeSpan? RetryAfter);
public sealed record BeerGiftLog(ulong GuildId, ulong GiverDiscordUserId, ulong RecipientDiscordUserId, DateTimeOffset GiftedAt);
public sealed record PlottyMemory(
    ulong GuildId,
    ulong DiscordUserId,
    int TotalInteractions,
    int Mentions,
    int QuestionsDodged,
    int SarcasmDetected,
    int FoxMoments,
    int Beers,
    int Registrations,
    int MoodChecks,
    int Excuses,
    int WisdomRequests,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
public sealed record PlottyConversationState(
    ulong GuildId,
    ulong DiscordUserId,
    string LastTopic,
    int Turns,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
public sealed record DemeritEntry(
    string Id,
    ulong GuildId,
    ulong DiscordUserId,
    int Amount,
    string Reason,
    string? ContractId,
    string? PlayerName,
    string? SourceKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RemovedAt);

public enum DashboardCategory {
    NoSync = 0,
    LikelyUnboosted = 1,
    BelowThreshold = 2,
    Flagged = 3,
    Healthy = 4
}

public enum ArtifactPurpose {
    TeamLaying = 0,
    Laying = 1,
    Shipping = 2,
    Capacity = 3,
    TeamEarnings = 4,
    Boosting = 5,
    InternalHatchery = 6,
    StoneCarrier = 7
}
