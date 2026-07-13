using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using EggContribBot.Proto;
using Google.Protobuf;

namespace EggContribBot;

public sealed class EggIncClient {
    private const string BaseAddress = "https://www.auxbrain.com/";
    private const string CallerUserId = "EI6291940968235008";
    private const string PeriodicalsReferenceUserId = "EI5482515761594368";
    private const string PeriodicalsPostUserId = "EI4765194876354560";
    private const uint ClientVersion = 72;
    private const string AppVersion = "1.35.6";
    private const string AppBuild = "1.35.6.3";
    private const string UserAgent = "egginc/1.35.3.1 CFNetwork/1410.1 Darwin/22.6.0";

    private readonly HttpClient _http;

    public EggIncClient(HttpClient? httpClient = null) {
        _http = httpClient ?? new HttpClient(new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<ContractCoopStatusResponse?> GetCoopStatusAsync(
        string contractId,
        string coopCode,
        CancellationToken cancellationToken = default) {
        var request = new ContractCoopStatusRequest {
            ContractIdentifier = contractId,
            CoopIdentifier = coopCode.ToLowerInvariant(),
            UserId = CallerUserId,
            ClientVersion = ClientVersion,
            ClientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Rinfo = new BasicRequestInfo {
                EiUserId = CallerUserId,
                ClientVersion = ClientVersion,
                Version = AppVersion,
                Build = AppBuild,
                Platform = "IOS",
                Country = "US",
                Language = "en",
                Debug = false
            }
        };

        var status = await PostCoopStatusAsync("ei/coop_status", request, useCoopStatusHeaders: true, cancellationToken);
        if(status is not null) {
            return status;
        }

        return await PostCoopStatusAsync("ei/coop_status_bot", request, useCoopStatusHeaders: false, cancellationToken);
    }

    private async Task<ContractCoopStatusResponse?> PostCoopStatusAsync(
        string path,
        ContractCoopStatusRequest request,
        bool useCoopStatusHeaders,
        CancellationToken cancellationToken) {
        var payloadBase64 = Convert.ToBase64String(request.ToByteArray());
        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", payloadBase64)
        ]);
        var body = new ByteArrayContent(await form.ReadAsByteArrayAsync(cancellationToken)) {
            Headers = { ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded") }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        if(useCoopStatusHeaders) {
            req.Version = HttpVersion.Version20;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("*/*");
            req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            var sessionCookie = Environment.GetEnvironmentVariable("EGGINC_SESSION_COOKIE");
            req.Headers.Add("cookie", $"session={(!string.IsNullOrWhiteSpace(sessionCookie) ? sessionCookie : "9cd692e4-050e-4cb9-a305-993bd28441b2")}");
        } else {
            req.Headers.UserAgent.ParseAdd("Dalvik/2.1.0 (Linux; U; Android 9; SM-G960U1 Build/PPR1.180610.011)");
            req.Headers.AcceptEncoding.ParseAdd("gzip");
            req.Headers.Connection.ParseAdd("Keep-Alive");
        }

        HttpResponseMessage response;
        try {
            response = await _http.SendAsync(req, cancellationToken);
        } catch {
            return null;
        }

        if(!response.IsSuccessStatusCode) {
            return null;
        }

        var responseBase64 = await response.Content.ReadAsStringAsync(cancellationToken);
        byte[] raw;
        try {
            raw = Convert.FromBase64String(responseBase64);
        } catch(FormatException) {
            return null;
        }

        byte[] messageBytes;
        try {
            var authMessage = AuthenticatedMessage.Parser.ParseFrom(raw);
            messageBytes = authMessage.Compressed
                ? await DecompressAsync(authMessage.Message.ToByteArray(), cancellationToken)
                : authMessage.Message.ToByteArray();
        } catch(InvalidProtocolBufferException) {
            return null;
        }

        ContractCoopStatusResponse status;
        try {
            status = ContractCoopStatusResponse.Parser.ParseFrom(messageBytes);
        } catch(InvalidProtocolBufferException) {
            return null;
        }

        return string.Equals(status.CoopIdentifier, request.CoopIdentifier, StringComparison.OrdinalIgnoreCase)
            ? status
            : null;
    }

    public async Task<(string ContractId, ContractCoopStatusResponse Status)?> FindCoopStatusAsync(
        string coopCode,
        CancellationToken cancellationToken = default) {
        var contracts = await GetCurrentContractsAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candidates = contracts
            .Where(c => !string.IsNullOrWhiteSpace(c.Identifier))
            .Where(c => c.CoopAllowed)
            .OrderByDescending(c => c.ExpirationTime > now)
            .ThenByDescending(c => c.ExpirationTime)
            .ToList();

        foreach(var contract in candidates) {
            var status = await GetCoopStatusAsync(contract.Identifier, coopCode, cancellationToken);
            if(status is not null) {
                return (contract.Identifier, status);
            }
        }

        return null;
    }

    public async Task<Backup?> GetBackupAsync(string eggIncId, CancellationToken cancellationToken = default) {
        var normalized = NormalizeEggId(eggIncId);
        var request = new EggIncFirstContactRequest {
            ClientVersion = ClientVersion,
            Platform = Platform.Droid,
            EiUserId = normalized,
            DeviceId = normalized,
            Username = "",
            Rinfo = new BasicRequestInfo {
                EiUserId = normalized,
                ClientVersion = ClientVersion,
                Version = AppVersion,
                Build = AppBuild,
                Platform = "IOS",
                Country = "US",
                Language = "en",
                Debug = false
            }
        };

        var payloadBase64 = Convert.ToBase64String(request.ToByteArray());
        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", payloadBase64)
        ]);
        var body = new ByteArrayContent(await form.ReadAsByteArrayAsync(cancellationToken)) {
            Headers = { ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded") }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "ei/bot_first_contact") { Content = body };
        req.Headers.UserAgent.ParseAdd("Dalvik/2.1.0 (Linux; U; Android 9; SM-G960U1 Build/PPR1.180610.011)");
        req.Headers.AcceptEncoding.ParseAdd("gzip");
        req.Headers.Connection.ParseAdd("Keep-Alive");

        HttpResponseMessage response;
        try {
            response = await _http.SendAsync(req, cancellationToken);
        } catch {
            return null;
        }

        if(!response.IsSuccessStatusCode) {
            return null;
        }

        var responseBase64 = await response.Content.ReadAsStringAsync(cancellationToken);
        byte[] raw;
        try {
            raw = Convert.FromBase64String(responseBase64);
        } catch(FormatException) {
            return null;
        }

        var firstContact = EggIncFirstContactResponse.Parser.ParseFrom(raw);
        return firstContact.Backup;
    }

    public async Task<IReadOnlyList<(string ContractId, string CoopCode, ContractCoopStatusResponse Status)>> GetPlayerCoopStatusesAsync(
        string eggIncId,
        CancellationToken cancellationToken = default) {
        var lookup = await GetPlayerCoopLookupAsync(eggIncId, cancellationToken);
        return lookup.Statuses;
    }

    public async Task<PlayerCoopLookupResult> GetPlayerCoopLookupAsync(
        string eggIncId,
        CancellationToken cancellationToken = default) {
        var backup = await GetBackupAsync(eggIncId, cancellationToken);
        if(backup?.Contracts is null) {
            return new PlayerCoopLookupResult(
                [],
                [],
                BackupFound: backup is not null,
                ContractsFound: false,
                FarmCount: backup?.Farms.Count ?? 0,
                ContractFarmIds: [],
                LocalContractCount: 0,
                AcceptedLocalContractCount: 0,
                LocalContractIds: [],
                EmbeddedStatusCount: 0,
                EmbeddedStatusIds: [],
                CandidateIds: [],
                AttemptedLookups: []);
        }

        var localContracts = backup.Contracts.Contracts
            .Concat(backup.Contracts.Archive)
            .Where(c => c is not null)
            .ToList();

        var candidates = new List<(string ContractId, string CoopCode, double AcceptedAt)>();
        var attemptedLookups = new List<string>();
        var embeddedStatuses = backup.Contracts.CurrentCoopStatuses
            .Where(s => !string.IsNullOrWhiteSpace(s.ContractIdentifier) && !string.IsNullOrWhiteSpace(s.CoopIdentifier))
            .GroupBy(s => (ContractId: s.ContractIdentifier.ToLowerInvariant(), CoopCode: s.CoopIdentifier.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.First());

        foreach(var farm in backup.Farms.Where(f => !string.IsNullOrWhiteSpace(f.ContractId))) {
            var localContract = localContracts
                .Where(c => !c.Cancelled)
                .FirstOrDefault(c => string.Equals(GetContractId(c), farm.ContractId, StringComparison.OrdinalIgnoreCase));

            if(localContract is not null && !string.IsNullOrWhiteSpace(localContract.CoopIdentifier)) {
                candidates.Add((farm.ContractId, localContract.CoopIdentifier, localContract.TimeAccepted));
            }
        }

        candidates.AddRange(backup.Contracts.Contracts
            .Where(c => c.Accepted && !c.Cancelled)
            .Select(c => (ContractId: GetContractId(c), CoopCode: c.CoopIdentifier, AcceptedAt: c.TimeAccepted)));

        candidates.AddRange(embeddedStatuses.Values
            .Select(s => (ContractId: s.ContractIdentifier, CoopCode: s.CoopIdentifier, AcceptedAt: 0.0)));

        candidates = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.ContractId) && !string.IsNullOrWhiteSpace(c.CoopCode))
            .GroupBy(c => (ContractId: c.ContractId.ToLowerInvariant(), CoopCode: c.CoopCode.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(c => c.AcceptedAt).First())
            .OrderByDescending(c => c.AcceptedAt)
            .ToList();

        var results = new List<(string ContractId, string CoopCode, ContractCoopStatusResponse Status)>();
        var statusLookups = new List<PlayerCoopStatusLookup>();
        foreach(var candidate in candidates) {
            if(embeddedStatuses.TryGetValue(
                (candidate.ContractId.ToLowerInvariant(), candidate.CoopCode.ToLowerInvariant()),
                out var embeddedStatus)) {
                results.Add((candidate.ContractId, candidate.CoopCode, embeddedStatus));
                statusLookups.Add(new PlayerCoopStatusLookup(candidate.ContractId, candidate.CoopCode, embeddedStatus, candidate.AcceptedAt));
                attemptedLookups.Add($"{candidate.ContractId}/{candidate.CoopCode}: embedded");
                continue;
            }

            var status = await GetCoopStatusAsync(candidate.ContractId, candidate.CoopCode, cancellationToken);
            if(status is not null) {
                results.Add((candidate.ContractId, candidate.CoopCode, status));
                statusLookups.Add(new PlayerCoopStatusLookup(candidate.ContractId, candidate.CoopCode, status, candidate.AcceptedAt));
                attemptedLookups.Add($"{candidate.ContractId}/{candidate.CoopCode}: {status.ResponseStatus}");
            } else {
                attemptedLookups.Add($"{candidate.ContractId}/{candidate.CoopCode}: no response");
            }
        }

        return new PlayerCoopLookupResult(
            results,
            statusLookups,
            BackupFound: true,
            ContractsFound: true,
            FarmCount: backup.Farms.Count,
            ContractFarmIds: backup.Farms
                .Where(f => !string.IsNullOrWhiteSpace(f.ContractId))
                .Select(f => f.ContractId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList(),
            LocalContractCount: localContracts.Count,
            AcceptedLocalContractCount: backup.Contracts.Contracts.Count(c => c.Accepted && !c.Cancelled),
            LocalContractIds: localContracts
                .Select(GetContractId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList(),
            EmbeddedStatusCount: embeddedStatuses.Count,
            EmbeddedStatusIds: embeddedStatuses.Values
                .Select(s => $"{s.ContractIdentifier}/{s.CoopIdentifier}")
                .Take(10)
                .ToList(),
            CandidateIds: candidates
                .Select(c => $"{c.ContractId}/{c.CoopCode}")
                .Take(10)
                .ToList(),
            AttemptedLookups: attemptedLookups.Take(10).ToList());
    }

    private static string GetContractId(LocalContract contract) =>
        !string.IsNullOrWhiteSpace(contract.ContractIdentifier)
            ? contract.ContractIdentifier
            : contract.Contract?.Identifier ?? "";

    public async Task<IReadOnlyList<Contract>> GetCurrentContractsAsync(CancellationToken cancellationToken = default) {
        var request = new GetPeriodicalsRequest {
            UserId = PeriodicalsReferenceUserId,
            PiggyFull = false,
            PiggyFoundFull = false,
            SecondsFullRealtime = 2339576.17448521,
            SecondsFullGametime = 391564.659540082,
            SoulEggs = 570149167.28294,
            CurrentClientVersion = ClientVersion,
            Debug = false,
            Rinfo = new BasicRequestInfo {
                ClientVersion = ClientVersion,
                Version = AppVersion,
                Build = AppBuild,
                Platform = "IOS",
                Country = "US",
                Language = "en",
                Debug = false
            }
        };

        var response = await PostAuthenticatedAsync<PeriodicalsResponse>("ei/get_periodicals", request, PeriodicalsPostUserId, cancellationToken);
        return response?.Contracts?.Contracts?.ToList() ?? [];
    }

    private async Task<T?> PostAuthenticatedAsync<T>(
        string path,
        IMessage message,
        string userId,
        CancellationToken cancellationToken) where T : IMessage<T>, new() {
        var payloadBase64 = Convert.ToBase64String(message.ToByteArray());
        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", payloadBase64)
        ]);
        var body = new ByteArrayContent(await form.ReadAsByteArrayAsync(cancellationToken)) {
            Headers = { ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded") }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        req.Headers.UserAgent.ParseAdd("egginc/1.26.1.3 CFNetwork/1335.0.3 Darwin/21.6.0");
        req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        req.Headers.Connection.ParseAdd("Keep-Alive");

        HttpResponseMessage response;
        try {
            response = await _http.SendAsync(req, cancellationToken);
        } catch {
            return default;
        }

        if(!response.IsSuccessStatusCode) {
            return default;
        }

        var responseBase64 = await response.Content.ReadAsStringAsync(cancellationToken);
        byte[] raw;
        try {
            raw = Convert.FromBase64String(responseBase64);
        } catch(FormatException) {
            return default;
        }

        var authMessage = AuthenticatedMessage.Parser.ParseFrom(raw);
        var messageBytes = authMessage.Compressed
            ? await DecompressAsync(authMessage.Message.ToByteArray(), cancellationToken)
            : authMessage.Message.ToByteArray();

        return new MessageParser<T>(() => new T()).ParseFrom(messageBytes);
    }

    private static async Task<byte[]> DecompressAsync(byte[] bytes, CancellationToken cancellationToken) {
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using(var zlib = new ZLibStream(input, CompressionMode.Decompress)) {
            await zlib.CopyToAsync(output, cancellationToken);
        }
        return output.ToArray();
    }

    public static string NormalizeEggId(string eggIncId) => eggIncId.Trim().ToUpperInvariant();
}

public sealed record PlayerCoopLookupResult(
    IReadOnlyList<(string ContractId, string CoopCode, ContractCoopStatusResponse Status)> Statuses,
    IReadOnlyList<PlayerCoopStatusLookup> StatusLookups,
    bool BackupFound,
    bool ContractsFound,
    int FarmCount,
    IReadOnlyList<string> ContractFarmIds,
    int LocalContractCount,
    int AcceptedLocalContractCount,
    IReadOnlyList<string> LocalContractIds,
    int EmbeddedStatusCount,
    IReadOnlyList<string> EmbeddedStatusIds,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> AttemptedLookups);

public sealed record PlayerCoopStatusLookup(
    string ContractId,
    string CoopCode,
    ContractCoopStatusResponse Status,
    double AcceptedAt);
