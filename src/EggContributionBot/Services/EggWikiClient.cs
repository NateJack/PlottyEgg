using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using EggContribBot.Models;

namespace EggContribBot.Services;
public sealed class EggWikiClient {
    private const string ApiBase = "https://egg-inc.fandom.com/api.php";
    private static readonly HttpClient Http = new();
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from",
        "how", "i", "in", "is", "it", "me", "of", "on", "or", "the", "to", "what", "when",
        "where", "which", "who", "why", "with", "you", "your"
    };

    static EggWikiClient() {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PlottyDiscordBot/1.0");
    }

    public async Task<WikiAnswer?> AnswerAsync(string question) {
        var title = await SearchAsync(question);
        if(string.IsNullOrWhiteSpace(title)) {
            return null;
        }

        var page = await GetExtractAsync(title);
        if(page is null || string.IsNullOrWhiteSpace(page.Value.Extract)) {
            return null;
        }

        var answer = BuildAnswer(question, page.Value.Extract);
        if(string.IsNullOrWhiteSpace(answer)) {
            return null;
        }

        return new WikiAnswer(
            page.Value.Title,
            $"https://egg-inc.fandom.com/wiki/{Uri.EscapeDataString(page.Value.Title.Replace(' ', '_'))}",
            answer);
    }

    private static async Task<string?> SearchAsync(string question) {
        var url = $"{ApiBase}?action=query&list=search&srsearch={Uri.EscapeDataString(question)}&srlimit=1&format=json&utf8=1";
        using var stream = await Http.GetStreamAsync(url);
        using var doc = await JsonDocument.ParseAsync(stream);
        var results = doc.RootElement.GetProperty("query").GetProperty("search");
        return results.GetArrayLength() == 0
            ? null
            : results[0].GetProperty("title").GetString();
    }

    private static async Task<(string Title, string Extract)?> GetExtractAsync(string title) {
        var url = $"{ApiBase}?action=query&prop=extracts&explaintext=1&redirects=1&format=json&titles={Uri.EscapeDataString(title)}&utf8=1";
        using var stream = await Http.GetStreamAsync(url);
        using var doc = await JsonDocument.ParseAsync(stream);
        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach(var page in pages.EnumerateObject()) {
            if(!page.Value.TryGetProperty("extract", out var extractElement)) {
                continue;
            }

            var resolvedTitle = page.Value.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString() ?? title
                : title;
            return (resolvedTitle, WebUtility.HtmlDecode(extractElement.GetString() ?? ""));
        }

        return null;
    }

    private static string BuildAnswer(string question, string extract) {
        var keywords = Regex.Matches(question.ToLowerInvariant(), "[a-z0-9]+")
            .Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sentences = SentenceSplit
            .Split(Regex.Replace(extract, @"\s+", " ").Trim())
            .Where(s => s.Length is > 30 and < 450)
            .Select(s => new {
                Text = s.Trim(),
                Score = keywords.Count(k => s.Contains(k, StringComparison.OrdinalIgnoreCase))
            })
            .OrderByDescending(s => s.Score)
            .ThenBy(s => extract.IndexOf(s.Text, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(s => s.Text)
            .ToList();

        if(sentences.Count == 0) {
            return TrimForDiscord(extract);
        }

        return TrimForDiscord(string.Join(" ", sentences));
    }

    private static string TrimForDiscord(string value) =>
        value.Length <= 1500 ? value : value[..1497] + "...";
}
