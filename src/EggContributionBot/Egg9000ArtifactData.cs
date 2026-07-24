using System.Text.Json;
using EggContribBot.Proto;

namespace EggContribBot;

public static class Egg9000ArtifactData {
    private static readonly Lazy<IReadOnlyDictionary<int, AfxFamilyData>> Families = new(LoadFamilies);

    public static double EffectDelta(ArtifactSpec spec) =>
        ResolveEffect(spec)?.Delta ?? 0;

    public static int SlotCount(ArtifactSpec spec) =>
        ResolveEffect(spec)?.Slots ?? 0;

    public static string? ProperName(ArtifactSpec spec) =>
        ResolveTier(spec)?.Name;

    public static string? IconFilename(ArtifactSpec spec) =>
        ResolveTier(spec)?.IconFilename;

    private static AfxTierData? ResolveTier(ArtifactSpec spec) {
        if(!Families.Value.TryGetValue((int)spec.Name, out var family)) {
            return null;
        }

        var tierNumber = IsStoneName(spec.Name) ? (int)spec.Level + 2 : (int)spec.Level + 1;
        return family.Tiers.TryGetValue(tierNumber, out var tier) ? tier : null;
    }

    private static AfxEffectData? ResolveEffect(ArtifactSpec spec) {
        var tier = ResolveTier(spec);
        if(tier is null || tier.Effects.Count == 0) {
            return null;
        }

        var rarity = IsStoneName(spec.Name) ? 0 : (int)spec.Rarity;
        return tier.Effects.FirstOrDefault(e => e.Rarity == rarity)
            ?? tier.Effects.FirstOrDefault(e => e.Rarity == 0)
            ?? tier.Effects[0];
    }

    private static IReadOnlyDictionary<int, AfxFamilyData> LoadFamilies() {
        var path = FindDataPath();
        if(path is null) {
            Console.WriteLine("Plotty could not find eiafx-data.json; artifact values will fall back to zero.");
            return new Dictionary<int, AfxFamilyData>();
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if(!document.RootElement.TryGetProperty("artifact_families", out var familyElements)) {
            return new Dictionary<int, AfxFamilyData>();
        }

        var result = new Dictionary<int, AfxFamilyData>();
        foreach(var familyElement in familyElements.EnumerateArray()) {
            if(!familyElement.TryGetProperty("afx_id", out var familyIdElement)) {
                continue;
            }

            var familyIds = new HashSet<int> { familyIdElement.GetInt32() };
            if(familyElement.TryGetProperty("child_afx_ids", out var childIds) && childIds.ValueKind == JsonValueKind.Array) {
                foreach(var child in childIds.EnumerateArray()) {
                    familyIds.Add(child.GetInt32());
                }
            }

            var tiers = new Dictionary<int, AfxTierData>();
            if(familyElement.TryGetProperty("tiers", out var tierElements) && tierElements.ValueKind == JsonValueKind.Array) {
                foreach(var tierElement in tierElements.EnumerateArray()) {
                    if(!tierElement.TryGetProperty("tier_number", out var tierNumberElement)) {
                        continue;
                    }

                    var effects = new List<AfxEffectData>();
                    if(tierElement.TryGetProperty("effects", out var effectElements) && effectElements.ValueKind == JsonValueKind.Array) {
                        foreach(var effectElement in effectElements.EnumerateArray()) {
                            var rarity = effectElement.TryGetProperty("afx_rarity", out var rarityElement) ? rarityElement.GetInt32() : 0;
                            var delta = effectElement.TryGetProperty("effect_delta", out var deltaElement) ? deltaElement.GetDouble() : 0;
                            int? slots = null;
                            if(effectElement.TryGetProperty("slots", out var slotsElement) && slotsElement.ValueKind == JsonValueKind.Number) {
                                slots = slotsElement.GetInt32();
                            }

                            effects.Add(new AfxEffectData(rarity, delta, slots));
                        }
                    }

                    tiers[tierNumberElement.GetInt32()] = new AfxTierData(
                        tierElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "",
                        tierElement.TryGetProperty("icon_filename", out var iconElement) ? iconElement.GetString() : null,
                        effects);
                }
            }

            var family = new AfxFamilyData(
                familyElement.TryGetProperty("name", out var familyNameElement) ? familyNameElement.GetString() ?? "" : "",
                familyElement.TryGetProperty("type", out var familyTypeElement) ? familyTypeElement.GetString() ?? "" : "",
                tiers);
            foreach(var familyId in familyIds) {
                result[familyId] = family;
            }
        }

        return result;
    }

    private static string? FindDataPath() {
        var candidates = new[] {
            Path.Combine(AppContext.BaseDirectory, "eiafx-data.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "eiafx-data.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "EggContributionBot", "eiafx-data.json")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsStoneName(ArtifactSpec.Types.Name name) =>
        name is ArtifactSpec.Types.Name.TachyonStone
            or ArtifactSpec.Types.Name.DilithiumStone
            or ArtifactSpec.Types.Name.ShellStone
            or ArtifactSpec.Types.Name.LunarStone
            or ArtifactSpec.Types.Name.SoulStone
            or ArtifactSpec.Types.Name.ProphecyStone
            or ArtifactSpec.Types.Name.QuantumStone
            or ArtifactSpec.Types.Name.TerraStone
            or ArtifactSpec.Types.Name.LifeStone
            or ArtifactSpec.Types.Name.ClarityStone;
}

public sealed record AfxFamilyData(string Name, string Type, IReadOnlyDictionary<int, AfxTierData> Tiers);
public sealed record AfxTierData(string Name, string? IconFilename, IReadOnlyList<AfxEffectData> Effects);
public sealed record AfxEffectData(int Rarity, double Delta, int? Slots);
