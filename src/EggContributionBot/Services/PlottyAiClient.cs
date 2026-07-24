using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EggContribBot.Models;
using EggContribBot.Config;

namespace EggContribBot.Services;
public sealed class PlottyAiClient {
    private const int MaxHistoryMessages = 10;
    private static readonly TimeSpan ConversationLifetime = TimeSpan.FromHours(6);
    private static readonly Regex EidPattern = new(@"\bEI\d{12,20}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OpenAiKeyPattern = new(@"\bsk-[A-Za-z0-9_-]{16,}\b", RegexOptions.Compiled);
    private static readonly Regex Egg9000KeyPattern = new(@"\begg_[A-Za-z0-9_-]{16,}\b", RegexOptions.Compiled);
    private static readonly Regex DiscordTokenPattern = new(
        @"\b[A-Za-z0-9_-]{20,30}\.[A-Za-z0-9_-]{5,10}\.[A-Za-z0-9_-]{20,}\b",
        RegexOptions.Compiled);

    private readonly OpenAiSettings? _settings;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), ConversationSession> _sessions = new();

    public PlottyAiClient(OpenAiSettings? settings, HttpClient? httpClient = null) {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public bool IsConfigured => _settings?.IsConfigured == true;

    public async Task<string?> GenerateReplyAsync(
        ulong guildId,
        ulong userId,
        string prompt,
        PlottyMemory memory,
        CancellationToken cancellationToken = default) {
        var cleanPrompt = SanitizePrompt(prompt);
        if(string.IsNullOrWhiteSpace(cleanPrompt)) {
            cleanPrompt = "I mentioned you without adding a message. Respond naturally and ask what I need.";
        }

        var session = _sessions.GetOrAdd((guildId, userId), _ => new ConversationSession());
        await session.Gate.WaitAsync(cancellationToken);
        try {
            var now = DateTimeOffset.UtcNow;
            if(now - session.LastUsedAt > ConversationLifetime) {
                session.Messages.Clear();
            }

            if(!IsConfigured) {
                var localReply = BuildLocalReply(cleanPrompt, session.Messages, memory);
                Remember(session, cleanPrompt, localReply, now);
                return localReply;
            }

            var input = session.Messages
                .Append(new ChatTurn("user", cleanPrompt))
                .Select(turn => new {
                    role = turn.Role,
                    content = turn.Content
                })
                .ToArray();

            var requestBody = new {
                model = _settings!.EffectiveModel,
                instructions = BuildInstructions(memory),
                input,
                store = false,
                reasoning = new { effort = GetReasoningEffort(_settings.EffectiveModel) },
                text = new { verbosity = "low" },
                max_output_tokens = 300
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(_settings.EffectiveBaseUrl), "responses"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.EffectiveApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if(!response.IsSuccessStatusCode) {
                Console.WriteLine($"Plotty AI request failed with HTTP {(int)response.StatusCode}.");
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var reply = ExtractOutputText(document.RootElement);
            if(string.IsNullOrWhiteSpace(reply)) {
                Console.WriteLine("Plotty AI returned no text.");
                return null;
            }

            reply = SanitizeReply(reply);
            Remember(session, cleanPrompt, reply, now);
            return reply;
        } catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested) {
            Console.WriteLine("Plotty AI request timed out.");
            return null;
        } catch(Exception ex) {
            Console.WriteLine($"Plotty AI request failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        } finally {
            session.Gate.Release();
        }
    }

    public static string SanitizePrompt(string value) {
        var sanitized = EidPattern.Replace(value ?? "", "[private EID removed]");
        sanitized = OpenAiKeyPattern.Replace(sanitized, "[private API key removed]");
        sanitized = Egg9000KeyPattern.Replace(sanitized, "[private API key removed]");
        sanitized = DiscordTokenPattern.Replace(sanitized, "[private Discord token removed]");
        return Regex.Replace(sanitized, @"\s+", " ").Trim();
    }

    private static void Remember(ConversationSession session, string prompt, string reply, DateTimeOffset now) {
        session.Messages.Add(new ChatTurn("user", prompt));
        session.Messages.Add(new ChatTurn("assistant", reply));
        if(session.Messages.Count > MaxHistoryMessages) {
            session.Messages.RemoveRange(0, session.Messages.Count - MaxHistoryMessages);
        }

        session.LastUsedAt = now;
    }

    private static string BuildInstructions(PlottyMemory memory) => $"""
        You are Plotty, a friendly member of an Egg Inc Discord guild.
        Speak naturally in first person. Never refer to yourself as Plotty in the third person.
        Answer the user's question directly and hold a normal, brief conversation.
        Be warm, lightly witty, and useful without forcing egg jokes.
        If you are unsure, give the best general answer you can, say what is uncertain, and ask one relevant question back.
        Never dodge a question by changing the subject.
        Never mention "ledger spark" or describe the exchange as "small talk".
        Do not claim you checked live Discord, Egg Inc, contract, EID, or guild data; this chat has no access to those systems.
        Never request an EID, API key, bot token, password, or other secret in chat.
        Do not impersonate Staff, issue moderation decisions, or claim an action was completed.
        Keep replies under 1,500 characters and do not ping @everyone or @here.
        The user has interacted with you {memory.TotalInteractions} time(s); use that only to choose a natural familiarity level.
        """;

    private static string GetReasoningEffort(string model) =>
        model.StartsWith("gpt-5-nano", StringComparison.OrdinalIgnoreCase)
            ? "minimal"
            : "none";

    private static string? ExtractOutputText(JsonElement root) {
        if(!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) {
            return null;
        }

        var parts = new List<string>();
        foreach(var item in output.EnumerateArray()) {
            if(!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) {
                continue;
            }

            foreach(var contentItem in content.EnumerateArray()) {
                if(contentItem.TryGetProperty("type", out var type) &&
                   type.GetString() == "output_text" &&
                   contentItem.TryGetProperty("text", out var text) &&
                   !string.IsNullOrWhiteSpace(text.GetString())) {
                    parts.Add(text.GetString()!);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string SanitizeReply(string reply) {
        var cleaned = reply
            .Replace("@everyone", "everyone", StringComparison.OrdinalIgnoreCase)
            .Replace("@here", "here", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return cleaned.Length <= 1800 ? cleaned : cleaned[..1797] + "...";
    }

    private static string BuildLocalReply(string prompt, IReadOnlyList<ChatTurn> history, PlottyMemory memory) {
        var topic = InferTopic(prompt, history);
        var lower = prompt.ToLowerInvariant();
        var isQuestion = LooksLikeQuestion(prompt);
        if(IsBareMention(prompt) || IsGreeting(prompt)) {
            return SanitizeReply(Pick([
                "Hey, I am here.",
                "Hi. I am awake and behaving at least a little.",
                "Hey there. What are we working on?",
                "Hi, good to see you.",
                "Hey. I am listening."
            ]));
        }

        if(IsThanks(prompt)) {
            return SanitizeReply(Pick([
                "You are welcome.",
                "Anytime.",
                "Glad I could help.",
                "Of course. I am here when you need me.",
                "You got it."
            ]));
        }

        var prefix = Pick([
            "I can work with that.",
            "I think so.",
            "Short version:",
            "Here is how I would look at it.",
            "I have a useful angle on that."
        ]);
        var familiarity = isQuestion && memory.TotalInteractions >= 10 && Random.Shared.Next(4) == 0
            ? " I remember this kind of question from you, so I will stay practical."
            : "";

        string answer;
        if(ContainsAny(lower, "contract", "coop", "co-op", "rate", "sync", "contribution")) {
            answer = "For contract questions, the practical answer is usually to check three things: whether the player joined the current co-op, whether the game has synced recently, and whether the displayed rate matches the latest server data. If the numbers look stale, syncing and waiting a few minutes can change the report.";
        } else if(ContainsAny(lower, "eid", "register", "registration", "account")) {
            answer = "For registration questions, the safest path is to register the EID privately with `/register-eid`, then use the bot commands that read the stored account. I should not ask for an EID in public chat.";
        } else if(ContainsAny(lower, "artifact", "artifacts", "stone", "stones", "boost")) {
            answer = "For artifacts, I would focus on the current contract goal first, then compare laying rate, shipping, deflector value, and useful stones. A good set is the one that improves the co-op now, not just the one with the prettiest raw bonus.";
        } else if(ContainsAny(lower, "ship", "ships", "mission", "returns")) {
            answer = "For ships, the useful check is the active mission, return time, capacity, and whether you want a private reminder when it lands. If timing matters, the reminder matters more than staring at the timer.";
        } else if(ContainsAny(lower, "demerit", "demerits", "late", "warning")) {
            answer = "For demerits or late notices, the cleanest move is to tell Staff early using the late-notify flow. Demerits are meant to track repeat issues, while late notices explain a specific contract timing problem.";
        } else if(ContainsAny(lower, "beer", "beverage", "water", "coffee", "tea", "wine", "milk", "soda")) {
            answer = "For beverages, I support the town economy with dignity and a questionable amount of enthusiasm. Beer and wine stay limited, while the gentler beverages are easier to hand around.";
        } else if(ContainsAny(lower, "help", "how do i", "how can i", "what should i")) {
            answer = "I would start with the command that owns the job: `/register-eid` for accounts, `/rates` for your contracts, `/player` for your history, `/eggs-laid` for farm totals, and `/contract-artifacts` for contract setup advice.";
        } else {
            answer = isQuestion
                ? $"My general answer is: treat `{topic}` as a signal to narrow the problem, check the newest available info, and avoid making decisions from stale data."
                : Pick([
                    "I hear you.",
                    "Fair.",
                    "That makes sense.",
                    "I am with you.",
                    "Noted."
                ]);
        }

        var followUp = !isQuestion && answer.Length < 40
            ? Pick([
                "What do you want to do next?",
                "Want me to look at anything specific?",
                "I can help if you give me a little more to go on.",
                "What should I check?"
            ])
            : isQuestion
            ? Pick([
                "What part do you want me to pin down first?",
                "Are you asking about your own account, a staff report, or the whole guild?",
                "Do you want the quick answer or the careful version?",
                "What detail do you already have in front of you?"
            ])
            : Pick([
                "What should I do with that from here?",
                "Do you want me to turn that into a command change?",
                "Should I treat this as a note, a bug, or a new feature?",
                "What outcome are you aiming for?"
            ]);

        return SanitizeReply($"{prefix}{familiarity} {answer} {followUp}");
    }

    private static bool IsBareMention(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("I mentioned you without adding a message. Respond naturally and ask what I need.", StringComparison.Ordinal);

    private static bool IsGreeting(string value) =>
        Regex.IsMatch(value.Trim(), @"^(hi|hello|hey|yo|sup|hiya|howdy|good morning|good afternoon|good evening)[!. ]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsThanks(string value) =>
        Regex.IsMatch(value.Trim(), @"^(thanks|thank you|ty|appreciate it|good bot|thx)[!. ]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string InferTopic(string prompt, IReadOnlyList<ChatTurn> history) {
        var words = Regex.Matches(prompt.ToLowerInvariant(), @"[a-z][a-z0-9'-]{2,}")
            .Select(m => m.Value)
            .Where(word => !StopWords.Contains(word))
            .GroupBy(word => word)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .Select(group => group.Key)
            .Take(3)
            .ToArray();

        if(words.Length > 0) {
            return string.Join(" ", words);
        }

        var previousUserTurn = history.LastOrDefault(turn => turn.Role == "user")?.Content;
        if(!string.IsNullOrWhiteSpace(previousUserTurn)) {
            return "the last thing we were discussing";
        }

        return "what you just sent";
    }

    private static bool LooksLikeQuestion(string value) {
        var lower = value.ToLowerInvariant();
        return lower.Contains('?') ||
               Regex.IsMatch(lower, @"\b(who|what|when|where|why|how|can|could|should|would|is|are|do|does|did)\b", RegexOptions.CultureInvariant);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) {
        "the", "and", "for", "you", "your", "that", "this", "with", "from", "have", "has",
        "had", "not", "but", "are", "was", "were", "can", "could", "should", "would", "what",
        "when", "where", "why", "how", "who", "does", "did", "will", "just", "about", "into",
        "like", "need", "want", "tell", "give", "show", "plotty", "please"
    };

    private static T Pick<T>(IReadOnlyList<T> values) =>
        values[Random.Shared.Next(values.Count)];

    private sealed class ConversationSession {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<ChatTurn> Messages { get; } = [];
        public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed record ChatTurn(string Role, string Content);
}
