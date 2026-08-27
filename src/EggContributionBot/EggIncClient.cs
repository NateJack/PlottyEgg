using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using EggContribBot.Proto;
using Google.Protobuf;

namespace EggContribBot;

public sealed class EggIncClient {
    private const string BaseAddress = "https://www.auxbrain.com/";
    private const string PeriodicalsAddress = "https://ctx-dot-auxbrainhome.appspot.com/ei/get_periodicals";
    private const string CallerUserId = "EI6291940968235008";
    private const string PeriodicalsReferenceUserId = "EI5482515761594368";
    private const string PeriodicalsPostUserId = "EI4765194876354560";
    private const uint DefaultClientVersion = 75;
    private const string AppVersion = "1.37";
    private const string AppBuild = "111358";
    private const string UserAgent = "egginc/1.37 CFNetwork/1410.1 Darwin/22.6.0";

    private readonly HttpClient _http;
    private readonly EggIncApiSettings _settings;
    private int _observedClientVersion = (int)DefaultClientVersion;
    private readonly SemaphoreSlim _currentContractsGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TimedRequest<Backup?>> _backupRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TimedRequest<ContractCoopStatusResponse?>> _coopStatusRequests = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<Contract>? _currentContractsCache;
    private DateTimeOffset _currentContractsCacheUntil;
    private static readonly TimeSpan BackupCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CoopStatusCacheLifetime = TimeSpan.FromSeconds(45);

    public EggIncClient(EggIncApiSettings? settings = null, HttpClient? httpClient = null) {
        _settings = settings ?? new EggIncApiSettings();
        _http = httpClient ?? new HttpClient(new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public Task<ContractCoopStatusResponse?> GetCoopStatusAsync(
        string contractId,
        string coopCode,
        CancellationToken cancellationToken = default) {
        var normalizedContractId = contractId.Trim().ToLowerInvariant();
        var normalizedCoopCode = coopCode.Trim().ToLowerInvariant();
        return GetCachedRequestAsync(
            _coopStatusRequests,
            $"{normalizedContractId}/{normalizedCoopCode}",
            CoopStatusCacheLifetime,
            () => FetchCoopStatusAsync(normalizedContractId, normalizedCoopCode),
            cancellationToken);
    }

    private async Task<ContractCoopStatusResponse?> FetchCoopStatusAsync(
        string contractId,
        string coopCode) {
        var request = new ContractCoopStatusRequest {
            ContractIdentifier = contractId,
            CoopIdentifier = coopCode,
            UserId = CallerUserId,
            ClientVersion = EffectiveClientVersion,
            ClientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Rinfo = new BasicRequestInfo {
                EiUserId = CallerUserId,
                ClientVersion = EffectiveClientVersion,
                Version = AppVersion,
                Build = AppBuild,
                Platform = "IOS",
                Country = "US",
                Language = "en",
                Debug = false
            }
        };

        var status = await PostCoopStatusAsync("ei/coop_status", request, useCoopStatusHeaders: true, CancellationToken.None);
        if(status is not null) {
            return status;
        }

        return await PostCoopStatusAsync("ei/coop_status_bot", request, useCoopStatusHeaders: false, CancellationToken.None);
    }

    private async Task<ContractCoopStatusResponse?> PostCoopStatusAsync(
        string path,
        ContractCoopStatusRequest request,
        bool useCoopStatusHeaders,
        CancellationToken cancellationToken) {
        var payloadBase64 = Convert.ToBase64String(request.ToByteArray());
        using var body = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", payloadBase64)
        ]);

        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        if(useCoopStatusHeaders) {
            req.Version = HttpVersion.Version20;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("*/*");
            req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            var sessionCookie = Environment.GetEnvironmentVariable("EGGINC_SESSION_COOKIE");
            if(!string.IsNullOrWhiteSpace(sessionCookie)) {
                req.Headers.Add("cookie", $"session={sessionCookie}");
            }
        } else {
            req.Headers.UserAgent.ParseAdd("Dalvik/2.1.0 (Linux; U; Android 9; SM-G960U1 Build/PPR1.180610.011)");
            req.Headers.AcceptEncoding.ParseAdd("gzip");
            req.Headers.Connection.ParseAdd("Keep-Alive");
        }

        HttpResponseMessage response;
        try {
            response = await _http.SendAsync(req, cancellationToken);
        } catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        } catch(Exception ex) {
            Console.WriteLine($"Egg Inc request {path} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        using var responseToDispose = response;

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

    public Task<Backup?> GetBackupAsync(string eggIncId, CancellationToken cancellationToken = default) {
        var normalized = NormalizeEggId(eggIncId);
        return GetCachedRequestAsync(
            _backupRequests,
            normalized,
            BackupCacheLifetime,
            () => FetchBackupAsync(normalized),
            cancellationToken);
    }

    private async Task<Backup?> FetchBackupAsync(string normalizedEggIncId) {
        var raw = await GetFirstContactBytesAsync(normalizedEggIncId, CancellationToken.None);
        if(raw is null) {
            return null;
        }

        try {
            var firstContact = EggIncFirstContactResponse.Parser.ParseFrom(raw);
            ObserveClientVersion(firstContact.Backup?.Version ?? 0);
            return firstContact.Backup;
        } catch(InvalidProtocolBufferException ex) {
            Console.WriteLine($"Could not parse Egg Inc backup for EID hash {SecureText.Sha256(normalizedEggIncId)[..8]}: {ex.Message}");
            return null;
        }
    }

    private static async Task<T> GetCachedRequestAsync<T>(
        ConcurrentDictionary<string, TimedRequest<T>> requests,
        string key,
        TimeSpan lifetime,
        Func<Task<T>> factory,
        CancellationToken cancellationToken) {
        while(true) {
            var now = DateTimeOffset.UtcNow;
            if(requests.TryGetValue(key, out var existing)) {
                if(now - existing.CreatedAt < lifetime) {
                    return await existing.Request.Value.WaitAsync(cancellationToken);
                }

                requests.TryRemove(new KeyValuePair<string, TimedRequest<T>>(key, existing));
            }

            var created = new TimedRequest<T>(
                now,
                new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
            var selected = requests.GetOrAdd(key, created);
            try {
                return await selected.Request.Value.WaitAsync(cancellationToken);
            } catch {
                requests.TryRemove(new KeyValuePair<string, TimedRequest<T>>(key, selected));
                throw;
            }
        }
    }

    public async Task<EggIdValidationResult> ValidateEggIdAsync(string eggIncId, CancellationToken cancellationToken = default) {
        var raw = await GetFirstContactBytesAsync(eggIncId, cancellationToken);
        if(raw is null) {
            return new EggIdValidationResult(false, null, false);
        }

        try {
            var firstContact = EggIncFirstContactResponse.Parser.ParseFrom(raw);
            return firstContact.Backup is null
                ? new EggIdValidationResult(false, null, false)
                : new EggIdValidationResult(true, firstContact.Backup.UserName, false);
        } catch(InvalidProtocolBufferException ex) when(ex.Message.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)) {
            Console.WriteLine($"Validated EID with limited backup parsing for hash {SecureText.Sha256(NormalizeEggId(eggIncId))[..8]}: {ex.Message}");
            return new EggIdValidationResult(true, null, true);
        } catch(InvalidProtocolBufferException ex) {
            Console.WriteLine($"Could not validate Egg Inc EID hash {SecureText.Sha256(NormalizeEggId(eggIncId))[..8]}: {ex.Message}");
            return new EggIdValidationResult(false, null, false);
        }
    }

    public async Task<PlayerContractScoreSnapshot> GetPlayerContractScoreSnapshotAsync(
        string eggIncId,
        Backup? backup,
        CancellationToken cancellationToken = default) {
        var normalized = NormalizeEggId(eggIncId);
        var request = new GetPeriodicalsRequest {
            UserId = normalized,
            CurrentClientVersion = EffectiveClientVersion
        };

        var response = await PostAuthenticatedAsync<PeriodicalsResponse>(PeriodicalsAddress, request, normalized, cancellationToken);
        var contractPlayerInfo = response?.ContractPlayerInfo;
        if(contractPlayerInfo is null || !contractPlayerInfo.HasSeasonCxp || contractPlayerInfo.SeasonCxp <= 0) {
            contractPlayerInfo = await GetContractPlayerInfoAsync(eggIncId, cancellationToken) ?? contractPlayerInfo;
        }

        var evaluations = (response?.Evaluations ?? [])
            .Concat(response?.ContractPlayerInfo?.UnreadEvaluations ?? [])
            .ToList();
        return new PlayerContractScoreSnapshot(evaluations, contractPlayerInfo);
    }

    public async Task<ContractPlayerInfo?> GetContractPlayerInfoAsync(
        string eggIncId,
        CancellationToken cancellationToken = default) {
        if(!_settings.IsConfigured) {
            return null;
        }

        if(_settings.HasWorkerAccess) {
            var workerResult = await GetContractPlayerInfoFromWorkerAsync(eggIncId, cancellationToken);
            if(workerResult is not null) {
                return workerResult;
            }
        }

        return _settings.HasDirectApiAccess
            ? await GetContractPlayerInfoDirectAsync(eggIncId, cancellationToken)
            : null;
    }

    private async Task<ContractPlayerInfo?> GetContractPlayerInfoFromWorkerAsync(
        string eggIncId,
        CancellationToken cancellationToken) {
        if(!Uri.TryCreate(_settings.EffectiveWorkerUrl, UriKind.Absolute, out var workerBaseUri)) {
            return null;
        }

        var normalized = NormalizeEggId(eggIncId);
        var requestUri = new Uri(workerBaseUri, $"player_summary?eid={Uri.EscapeDataString(normalized)}");
        try {
            using var response = await _http.GetAsync(requestUri, cancellationToken);
            if(!response.IsSuccessStatusCode) {
                return null;
            }

            var summary = await response.Content.ReadFromJsonAsync<EggIncWorkerPlayerSummary>(
                cancellationToken: cancellationToken);
            var contracts = summary?.Contracts;
            if(contracts is null) {
                return null;
            }

            var playerInfo = new ContractPlayerInfo {
                GradeScore = contracts.GradeScore,
                GradeProgress = contracts.GradeProgress,
                SeasonCxp = contracts.SeasonCxp,
                TotalCxp = contracts.TotalCxp
            };
            if(contracts.Grade?.Id is > 0) {
                playerInfo.Grade = (Contract.Types.PlayerGrade)contracts.Grade.Id.Value;
            }

            return playerInfo;
        } catch(HttpRequestException) {
            return null;
        } catch(TaskCanceledException) when(!cancellationToken.IsCancellationRequested) {
            return null;
        }
    }

    private async Task<ContractPlayerInfo?> GetContractPlayerInfoDirectAsync(
        string eggIncId,
        CancellationToken cancellationToken) {

        var normalized = NormalizeEggId(eggIncId);
        var info = BuildSignedBasicRequestInfo(normalized);
        var messageBytes = info.ToByteArray();
        var authenticatedBytes = WrapAuthenticatedMessage(messageBytes);

        using var body = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", Convert.ToBase64String(authenticatedBytes))
        ]);
        using var req = new HttpRequestMessage(HttpMethod.Post, "ei_ctx/get_contract_player_info") { Content = body };
        req.Headers.UserAgent.ParseAdd("Dalvik/2.1.0 (Linux; U; Android 9; SM-G960U1 Build/PPR1.180610.011)");
        req.Headers.AcceptEncoding.ParseAdd("gzip");
        req.Headers.Connection.ParseAdd("Keep-Alive");

        HttpResponseMessage response;
        try {
            response = await _http.SendAsync(req, cancellationToken);
        } catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        } catch(Exception ex) {
            Console.WriteLine($"Egg Inc contract-player request failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        using var responseToDispose = response;

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

        byte[] playerInfoBytes;
        try {
            var responseAuthMessage = AuthenticatedMessage.Parser.ParseFrom(raw);
            playerInfoBytes = responseAuthMessage.Compressed
                ? await DecompressAsync(responseAuthMessage.Message.ToByteArray(), cancellationToken)
                : responseAuthMessage.Message.ToByteArray();
        } catch(InvalidProtocolBufferException) {
            return null;
        }

        try {
            return ContractPlayerInfo.Parser.ParseFrom(playerInfoBytes);
        } catch(InvalidProtocolBufferException) {
            return null;
        }
    }

    private async Task<byte[]?> GetFirstContactBytesAsync(string eggIncId, CancellationToken cancellationToken = default) {
        var normalized = NormalizeEggId(eggIncId);
        var request = new EggIncFirstContactRequest {
            ClientVersion = EffectiveClientVersion,
            Platform = Platform.Droid,
            EiUserId = normalized,
            DeviceId = normalized,
            Username = "",
            Rinfo = BuildBasicRequestInfo(normalized, includeUserId: true)
        };

        var payloadBase64 = Convert.ToBase64String(request.ToByteArray());
        using var body = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", payloadBase64)
        ]);

        using var req = new HttpRequestMessage(HttpMethod.Post, "ei/bot_first_contact") { Content = body };
        req.Headers.UserAgent.ParseAdd("Dalvik/2.1.0 (Linux; U; Android 9; SM-G960U1 Build/PPR1.180610.011)");
        req.Headers.AcceptEncoding.ParseAdd("gzip");
        req.Headers.Connection.ParseAdd("Keep-Alive");

        HttpResponseMessage response;
        try {
            response = await _http.SendAsync(req, cancellationToken);
        } catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        } catch(Exception ex) {
            Console.WriteLine($"Egg Inc first-contact request failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        using var responseToDispose = response;

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

        return raw;
    }

    public async Task<PlayerCoopLookupResult> GetPlayerCoopLookupAsync(
        string eggIncId,
        CancellationToken cancellationToken = default) {
        var backup = await GetBackupAsync(eggIncId, cancellationToken);
        if(backup?.Contracts is null) {
            return new PlayerCoopLookupResult(
                [],
                [],
                backup,
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
        var localContractsById = localContracts
            .Where(c => !c.Cancelled)
            .Select(c => (Contract: c, ContractId: GetContractId(c)))
            .Where(c => !string.IsNullOrWhiteSpace(c.ContractId))
            .GroupBy(c => c.ContractId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => c.Contract.TimeAccepted).First().Contract,
                StringComparer.OrdinalIgnoreCase);

        var candidates = new List<(string ContractId, string CoopCode, double AcceptedAt)>();
        var attemptedLookups = new List<string>();
        var embeddedStatuses = backup.Contracts.CurrentCoopStatuses
            .Where(s => !string.IsNullOrWhiteSpace(s.ContractIdentifier) && !string.IsNullOrWhiteSpace(s.CoopIdentifier))
            .GroupBy(s => (ContractId: s.ContractIdentifier.ToLowerInvariant(), CoopCode: s.CoopIdentifier.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.First());

        foreach(var farm in backup.Farms.Where(f => !string.IsNullOrWhiteSpace(f.ContractId))) {
            if(localContractsById.TryGetValue(farm.ContractId, out var localContract) &&
               !string.IsNullOrWhiteSpace(localContract.CoopIdentifier)) {
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

        var lookupResults = await LookupCandidateStatusesAsync(candidates, embeddedStatuses, cancellationToken);
        var results = new List<(string ContractId, string CoopCode, ContractCoopStatusResponse Status)>();
        var statusLookups = new List<PlayerCoopStatusLookup>();
        foreach(var lookupResult in lookupResults) {
            var candidate = lookupResult.Candidate;
            attemptedLookups.Add(lookupResult.Attempt);
            if(lookupResult.Status is null) {
                continue;
            }

            results.Add((candidate.ContractId, candidate.CoopCode, lookupResult.Status));
            statusLookups.Add(new PlayerCoopStatusLookup(candidate.ContractId, candidate.CoopCode, lookupResult.Status, candidate.AcceptedAt));
        }

        return new PlayerCoopLookupResult(
            results,
            statusLookups,
            backup,
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

    private async Task<IReadOnlyList<CandidateStatusResult>> LookupCandidateStatusesAsync(
        IReadOnlyList<(string ContractId, string CoopCode, double AcceptedAt)> candidates,
        IReadOnlyDictionary<(string ContractId, string CoopCode), ContractCoopStatusResponse> embeddedStatuses,
        CancellationToken cancellationToken) {
        if(candidates.Count == 0) {
            return [];
        }

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = candidates.Select(async candidate => {
            if(embeddedStatuses.TryGetValue(
                (candidate.ContractId.ToLowerInvariant(), candidate.CoopCode.ToLowerInvariant()),
                out var embeddedStatus)) {
                return new CandidateStatusResult(
                    candidate,
                    embeddedStatus,
                    $"{candidate.ContractId}/{candidate.CoopCode}: embedded");
            }

            await gate.WaitAsync(cancellationToken);
            ContractCoopStatusResponse? status;
            try {
                status = await GetCoopStatusAsync(candidate.ContractId, candidate.CoopCode, cancellationToken);
            } finally {
                gate.Release();
            }

            return status is null
                ? new CandidateStatusResult(candidate, null, $"{candidate.ContractId}/{candidate.CoopCode}: no response")
                : new CandidateStatusResult(candidate, status, $"{candidate.ContractId}/{candidate.CoopCode}: {status.ResponseStatus}");
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<IReadOnlyList<Contract>> GetCurrentContractsAsync(CancellationToken cancellationToken = default) {
        var now = DateTimeOffset.UtcNow;
        if(_currentContractsCache is not null && _currentContractsCacheUntil > now) {
            return _currentContractsCache;
        }

        await _currentContractsGate.WaitAsync(cancellationToken);
        try {
            now = DateTimeOffset.UtcNow;
            if(_currentContractsCache is not null && _currentContractsCacheUntil > now) {
                return _currentContractsCache;
            }

            _currentContractsCache = await FetchCurrentContractsAsync(cancellationToken);
            _currentContractsCacheUntil = _currentContractsCache.Count == 0
                ? now.AddSeconds(30)
                : now.AddMinutes(5);
            return _currentContractsCache;
        } finally {
            _currentContractsGate.Release();
        }
    }

    private async Task<IReadOnlyList<Contract>> FetchCurrentContractsAsync(CancellationToken cancellationToken = default) {
        var request = new GetPeriodicalsRequest {
            UserId = PeriodicalsReferenceUserId,
            PiggyFull = false,
            PiggyFoundFull = false,
            SecondsFullRealtime = 2339576.17448521,
            SecondsFullGametime = 391564.659540082,
            SoulEggs = 570149167.28294,
            CurrentClientVersion = EffectiveClientVersion,
            Debug = false,
            Rinfo = new BasicRequestInfo {
                ClientVersion = EffectiveClientVersion,
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
        using var body = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", payloadBase64)
        ]);

        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        if(!string.Equals(path, PeriodicalsAddress, StringComparison.OrdinalIgnoreCase)) {
            req.Headers.UserAgent.ParseAdd("egginc/1.26.1.3 CFNetwork/1335.0.3 Darwin/21.6.0");
            req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
            req.Headers.Connection.ParseAdd("Keep-Alive");
        }

        HttpResponseMessage response;
        try {
            response = await _http.SendAsync(req, cancellationToken);
        } catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
            throw;
        } catch(Exception ex) {
            Console.WriteLine($"Egg Inc request {path} failed: {ex.GetType().Name}: {ex.Message}");
            return default;
        }
        using var responseToDispose = response;

        if(!response.IsSuccessStatusCode) {
            Console.WriteLine($"Egg Inc request {path} returned HTTP {(int)response.StatusCode}.");
            return default;
        }

        var responseBase64 = await response.Content.ReadAsStringAsync(cancellationToken);
        byte[] raw;
        try {
            raw = Convert.FromBase64String(responseBase64);
        } catch(FormatException) {
            Console.WriteLine($"Egg Inc request {path} returned a non-base64 response.");
            return default;
        }

        try {
            var authMessage = AuthenticatedMessage.Parser.ParseFrom(raw);
            var messageBytes = authMessage.Compressed
                ? await DecompressAsync(authMessage.Message.ToByteArray(), cancellationToken)
                : authMessage.Message.ToByteArray();

            return new MessageParser<T>(() => new T()).ParseFrom(messageBytes);
        } catch(InvalidProtocolBufferException ex) {
            Console.WriteLine($"Egg Inc request {path} returned invalid protobuf: {ex.Message}");
            return default;
        }
    }

    private static async Task<byte[]> DecompressAsync(byte[] bytes, CancellationToken cancellationToken) {
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using(var zlib = new ZLibStream(input, CompressionMode.Decompress)) {
            await zlib.CopyToAsync(output, cancellationToken);
        }
        return output.ToArray();
    }

    private BasicRequestInfo BuildBasicRequestInfo(string userId, bool includeUserId) {
        var info = new BasicRequestInfo {
            ClientVersion = EffectiveClientVersion,
            Version = AppVersion,
            Build = AppBuild,
            Platform = "IOS",
            Country = "US",
            Language = "en",
            Debug = false
        };
        if(includeUserId) {
            info.EiUserId = userId;
        }

        return info;
    }

    private BasicRequestInfo BuildSignedBasicRequestInfo(string userId) => new() {
        EiUserId = userId,
        ClientVersion = EffectiveClientVersion,
        Version = AppVersion,
        Build = AppBuild,
        Platform = "DROID",
        Country = "US",
        Language = "en",
        DeviceLanguage = "en",
        Debug = false
    };

    private byte[] WrapAuthenticatedMessage(byte[] messageBytes) => new AuthenticatedMessage {
        Message = ByteString.CopyFrom(messageBytes),
        Code = GetSignedRequestHash(messageBytes)
    }.ToByteArray();

    private string GetSignedRequestHash(byte[] messageBytes) {
        var salt = _settings.EffectiveSalt
            ?? throw new InvalidOperationException("Egg Inc API salt is not configured.");
        var hashInput = messageBytes.ToArray();
        const uint magic = 0x3b9af419;
        if(hashInput.Length > 0) {
            hashInput[magic % (uint)hashInput.Length] = 0x1b;
        }
        var saltHash = Sha256Hex(Encoding.ASCII.GetBytes(salt));
        var saltedBytes = Encoding.ASCII.GetBytes(saltHash);
        return Sha256Hex(hashInput.Concat(saltedBytes).ToArray());
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private uint EffectiveClientVersion => (uint)Volatile.Read(ref _observedClientVersion);

    private void ObserveClientVersion(uint version) {
        if(version == 0 || version > 1000) {
            return;
        }

        while(true) {
            var current = Volatile.Read(ref _observedClientVersion);
            if(version <= current) {
                return;
            }

            if(Interlocked.CompareExchange(ref _observedClientVersion, (int)version, current) == current) {
                Console.WriteLine($"Egg Inc client version updated from {current} to {version} based on a player backup.");
                return;
            }
        }
    }

    public static string NormalizeEggId(string eggIncId) => eggIncId.Trim().ToUpperInvariant();

    private sealed record CandidateStatusResult(
        (string ContractId, string CoopCode, double AcceptedAt) Candidate,
        ContractCoopStatusResponse? Status,
        string Attempt);

    private sealed record TimedRequest<T>(DateTimeOffset CreatedAt, Lazy<Task<T>> Request);

    private sealed record EggIncWorkerPlayerSummary(
        [property: JsonPropertyName("contracts")] EggIncWorkerContracts? Contracts);

    private sealed record EggIncWorkerContracts(
        [property: JsonPropertyName("grade")] EggIncWorkerGrade? Grade,
        [property: JsonPropertyName("grade_progress")] double GradeProgress,
        [property: JsonPropertyName("grade_score")] double GradeScore,
        [property: JsonPropertyName("season_cxp")] double SeasonCxp,
        [property: JsonPropertyName("total_cxp")] double TotalCxp);

    private sealed record EggIncWorkerGrade(
        [property: JsonPropertyName("id")] int? Id);
}

public sealed record PlayerCoopLookupResult(
    IReadOnlyList<(string ContractId, string CoopCode, ContractCoopStatusResponse Status)> Statuses,
    IReadOnlyList<PlayerCoopStatusLookup> StatusLookups,
    Backup? Backup,
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

public sealed record EggIdValidationResult(bool IsValid, string? EggName, bool BackupParseLimited);
