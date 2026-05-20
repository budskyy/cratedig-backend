using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SoundCloudService>();
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => {
        // For production: comma-separated list of allowed origins in ALLOWED_ORIGINS env var
        // For development: allow any origin
        var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS");
        if (!string.IsNullOrWhiteSpace(allowedOrigins))
        {
            policy.WithOrigins(allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference("/swagger", options => {
    options.WithTitle("CrateDig API");
    options.WithTheme(ScalarTheme.Default);
    options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
});
app.UseRouting();
app.UseCors();

// Health check for Render
app.MapGet("/", () => Results.Ok(new { status = "ok", service = "CrateDig API", swagger = "/swagger" }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/soundcloud/search", async (
    string q,
    int maxPlays,
    int? bpmMin,
    int? bpmMax,
    string? genre,
    int limit,
    SoundCloudService sc) =>
{
    var tracks = await sc.SearchTracksAsync(q, maxPlays, limit, genre);

    // Post-filter: max 7 minutes
    tracks = tracks.Where(t => t.Duration <= 420000).ToList();

    // Post-filter BPM if provided (keep tracks with null BPM)
    if (bpmMin.HasValue)
        tracks = tracks.Where(t => !t.Bpm.HasValue || t.Bpm >= bpmMin).ToList();
    if (bpmMax.HasValue)
        tracks = tracks.Where(t => !t.Bpm.HasValue || t.Bpm <= bpmMax).ToList();

    return Results.Ok(tracks);
}).WithName("SearchTracks");

app.MapGet("/api/soundcloud/related/{trackId}", async (
    long trackId,
    SoundCloudService sc) =>
{
    var tracks = await sc.GetRelatedTracksAsync(trackId);
    return Results.Ok(tracks);
}).WithName("GetRelatedTracks");

app.Run();

// ===================== SERVICE =====================

public class SoundCloudService
{
    private static readonly string[] KnownClientIds =
    [
        "iZIs9mchVcX5lhVRyQGGAYlNPVldzAoX",
        "a3e059563d7fd3372b49b37f00a00bcf",
        "2t9loNQH90kzJcsFCODdigxfp325aq4z",
        "6e4972f1a8a5e5c3e8a9b8e8e2e2e2e2"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SoundCloudService> _logger;

    public SoundCloudService(IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<SoundCloudService> logger)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string> GetClientIdAsync()
    {
        if (_cache.TryGetValue("sc_client_id", out string? cached) && cached != null)
            return cached;

        var extracted = await ExtractClientIdFromWebAsync();
        if (extracted != null)
        {
            _cache.Set("sc_client_id", extracted, TimeSpan.FromHours(6));
            return extracted;
        }

        foreach (var id in KnownClientIds)
        {
            if (await TestClientIdAsync(id))
            {
                _cache.Set("sc_client_id", id, TimeSpan.FromHours(2));
                return id;
            }
        }

        return KnownClientIds[0];
    }

    private async Task<string?> ExtractClientIdFromWebAsync()
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUserAgent);
            http.Timeout = TimeSpan.FromSeconds(15);

            var html = await http.GetStringAsync("https://soundcloud.com");

            // Method 1: hydration data
            var m1 = Regex.Match(html, @"""hydratable""\s*:\s*""apiClient"".*?""id""\s*:\s*""([a-zA-Z0-9_\-]{20,})""");
            if (m1.Success) return m1.Groups[1].Value;

            // Method 2: script files
            var scriptMatches = Regex.Matches(html, @"https://a-v2\.sndcdn\.com/assets/[^""'\s]+\.js");
            var scripts = scriptMatches
                .Select(m => m.Value)
                .Where(u => !u.Contains("vendor") && !u.Contains("framework"))
                .Distinct()
                .Take(8)
                .ToList();

            foreach (var scriptUrl in scripts)
            {
                try
                {
                    var js = await http.GetStringAsync(scriptUrl);
                    var m2 = Regex.Match(js, @"client_id\s*[=:]\s*""([a-zA-Z0-9]{32,})""");
                    if (m2.Success) return m2.Groups[1].Value;
                }
                catch { /* continue */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract client ID from web");
        }

        return null;
    }

    private async Task<bool> TestClientIdAsync(string clientId)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var response = await http.GetAsync($"https://api-v2.soundcloud.com/search/tracks?q=test&client_id={clientId}&limit=1");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<ScTrack>> SearchTracksAsync(string query, int maxPlays, int limit, string? genre)
    {
        var clientId = await GetClientIdAsync();
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUserAgent);

        var results = new List<ScTrack>();
        const int pageSize = 50;
        const int maxPages = 3;
        bool retried = false;

        for (int page = 0; page < maxPages && results.Count < limit; page++)
        {
            var url = $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={clientId}&limit={pageSize}&offset={page * pageSize}&linked_partitioning=1&filter.duration.from=60000&filter.duration.to=420000";

            if (!string.IsNullOrWhiteSpace(genre) && genre != "All Genres")
                url += $"&filter.genre={Uri.EscapeDataString(genre)}";

            var response = await http.GetAsync(url);

            if (!response.IsSuccessStatusCode && page == 0)
            {
                if (!retried)
                {
                    retried = true;
                    _cache.Remove("sc_client_id");
                    clientId = await GetClientIdAsync();
                    url = url.Replace($"client_id={clientId}", $"client_id={clientId}");
                    // Rebuild URL with new clientId
                    url = $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={clientId}&limit={pageSize}&offset=0&linked_partitioning=1&filter.duration.from=60000&filter.duration.to=420000";
                    if (!string.IsNullOrWhiteSpace(genre) && genre != "All Genres")
                        url += $"&filter.genre={Uri.EscapeDataString(genre)}";
                    response = await http.GetAsync(url);
                }
                if (!response.IsSuccessStatusCode) break;
            }

            var content = await response.Content.ReadAsStringAsync();
            var searchResult = JsonSerializer.Deserialize<ScSearchResult>(content, JsonOptions);
            var collection = searchResult?.Collection ?? [];

            var filtered = collection
                .Where(t => t.PlaybackCount.HasValue && t.PlaybackCount <= maxPlays && t.PlaybackCount > 0
                            && !string.IsNullOrWhiteSpace(t.PermalinkUrl))
                .ToList();

            results.AddRange(filtered);

            if (collection.Count < pageSize) break;
        }

        return results.Take(limit).ToList();
    }

    public async Task<List<ScTrack>> GetRelatedTracksAsync(long trackId)
    {
        var clientId = await GetClientIdAsync();
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUserAgent);

        var response = await http.GetAsync($"https://api-v2.soundcloud.com/tracks/{trackId}/related?client_id={clientId}&limit=50");
        if (!response.IsSuccessStatusCode) return [];

        var content = await response.Content.ReadAsStringAsync();
        var searchResult = JsonSerializer.Deserialize<ScSearchResult>(content, JsonOptions);

        return (searchResult?.Collection ?? [])
            .Where(t => t.PlaybackCount > 0 && !string.IsNullOrWhiteSpace(t.PermalinkUrl) && t.Duration <= 420000)
            .Take(12)
            .ToList();
    }
}

// ===================== MODELS =====================

public class ScSearchResult
{
    [JsonPropertyName("collection")]
    public List<ScTrack>? Collection { get; set; }
}

public class ScTrack
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("permalink_url")] public string? PermalinkUrl { get; set; }
    [JsonPropertyName("artwork_url")] public string? ArtworkUrl { get; set; }
    [JsonPropertyName("playback_count")] public int? PlaybackCount { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("bpm")] public int? Bpm { get; set; }
    [JsonPropertyName("genre")] public string? Genre { get; set; }
    [JsonPropertyName("tag_list")] public string? TagList { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("user")] public ScUser? User { get; set; }
    [JsonPropertyName("waveform_url")] public string? WaveformUrl { get; set; }
    [JsonPropertyName("stream_url")] public string? StreamUrl { get; set; }
}

public class ScUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("permalink_url")] public string? PermalinkUrl { get; set; }
}
