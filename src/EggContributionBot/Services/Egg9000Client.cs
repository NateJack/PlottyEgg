using System.Net;
using System.Net.Http.Json;

namespace EggContribBot;

public sealed class Egg9000Client {
    private readonly Egg9000Settings? _settings;
    private readonly HttpClient _http;

    public Egg9000Client(Egg9000Settings? settings, HttpClient? httpClient = null) {
        _settings = settings;
        _http = httpClient ?? new HttpClient(new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        }) {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public bool IsConfigured => _settings?.IsConfigured == true;

    public async Task<IReadOnlyList<Egg9000LeaderboardItem>> GetLeaderboardAsync(CancellationToken cancellationToken = default) {
        if(_settings is null || !_settings.IsConfigured) {
            return [];
        }

        var baseUri = new Uri(_settings.EffectiveBaseUrl, UriKind.Absolute);
        var uri = new Uri(baseUri, "Home/LeaderboardJson");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Api-Key", _settings.EffectiveApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        if(!response.IsSuccessStatusCode) {
            return [];
        }

        return await response.Content.ReadFromJsonAsync<List<Egg9000LeaderboardItem>>(cancellationToken) ?? [];
    }
}
