using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.AniLiberty.Api;

/// <summary>One title on a user's AniList list.</summary>
public sealed record AniListEntry(long MediaId, long? MalId, int Progress, string? Status, int? Episodes);

public sealed record AniListViewer(long Id, string? Name);

/// <summary>
/// AniList GraphQL client. Titles are found by their MyAnimeList id, which AniLiberty provides for most
/// releases. Tokens belong to individual Jellyfin users (implicit grant, valid for a year).
/// </summary>
public sealed class AniListClient
{
    private const string Endpoint = "https://graphql.anilist.co";

    // AniList allows 90 requests per minute.
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(750);

    private readonly IHttpClientFactory _httpFactory;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly Dictionary<long, long?> _mediaByMal = new();
    private DateTime _last = DateTime.MinValue;

    public AniListClient(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    /// <summary>Where the user grants access; the redirect address is the one registered with the client id.</summary>
    public static string? AuthorizeUrl()
    {
        var clientId = Plugin.Instance?.Configuration.AniListClientId;
        return string.IsNullOrWhiteSpace(clientId)
            ? null
            : $"https://anilist.co/api/v2/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&response_type=token";
    }

    public async Task<AniListViewer?> GetViewerAsync(string token, CancellationToken ct)
    {
        const string Query = "{ Viewer { id name } }";
        using var doc = await SendAsync(Query, null, token, ct).ConfigureAwait(false);
        if (doc?.RootElement.GetProperty("data").TryGetProperty("Viewer", out var viewer) != true || viewer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new AniListViewer(viewer.GetProperty("id").GetInt64(), viewer.TryGetProperty("name", out var n) ? n.GetString() : null);
    }

    /// <summary>The user's whole anime list in one request.</summary>
    public async Task<List<AniListEntry>> GetListAsync(string token, long userId, CancellationToken ct)
    {
        const string Query = @"query ($userId: Int) {
  MediaListCollection(userId: $userId, type: ANIME) {
    lists { entries { mediaId progress status media { idMal episodes } } }
  }
}";
        using var doc = await SendAsync(Query, new { userId }, token, ct).ConfigureAwait(false);
        var result = new List<AniListEntry>();
        if (doc?.RootElement.GetProperty("data").TryGetProperty("MediaListCollection", out var collection) != true
            || collection.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var list in collection.GetProperty("lists").EnumerateArray())
        {
            foreach (var entry in list.GetProperty("entries").EnumerateArray())
            {
                var media = entry.GetProperty("media");
                result.Add(new AniListEntry(
                    entry.GetProperty("mediaId").GetInt64(),
                    media.TryGetProperty("idMal", out var mal) && mal.ValueKind == JsonValueKind.Number ? mal.GetInt64() : null,
                    entry.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                    entry.TryGetProperty("status", out var s) ? s.GetString() : null,
                    media.TryGetProperty("episodes", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null));
            }
        }

        return result;
    }

    /// <summary>AniList id of the title with this MyAnimeList id (null when AniList doesn't have it).</summary>
    public async Task<long?> GetMediaIdAsync(long malId, CancellationToken ct)
    {
        lock (_mediaByMal)
        {
            if (_mediaByMal.TryGetValue(malId, out var cached))
            {
                return cached;
            }
        }

        const string Query = "query ($idMal: Int) { Media(idMal: $idMal, type: ANIME) { id } }";
        long? id = null;
        using var doc = await SendAsync(Query, new { idMal = malId }, null, ct).ConfigureAwait(false);
        if (doc?.RootElement.GetProperty("data").TryGetProperty("Media", out var media) == true && media.ValueKind == JsonValueKind.Object)
        {
            id = media.GetProperty("id").GetInt64();
        }

        lock (_mediaByMal)
        {
            _mediaByMal[malId] = id;
        }

        return id;
    }

    public async Task SaveEntryAsync(string token, long mediaId, string status, int progress, CancellationToken ct)
    {
        const string Query = @"mutation ($mediaId: Int, $status: MediaListStatus, $progress: Int) {
  SaveMediaListEntry(mediaId: $mediaId, status: $status, progress: $progress) { id }
}";
        using var _ = await SendAsync(Query, new { mediaId, status, progress }, token, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> SendAsync(string query, object? variables, string? token, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            var client = _httpFactory.CreateClient(NamedClient.Default);
            for (var attempt = 0; ; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Headers.UserAgent.ParseAdd(Http.UserAgent);
                req.Headers.Accept.ParseAdd("application/json");
                if (token is not null)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { query, variables }),
                    Encoding.UTF8,
                    "application/json");

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && token is not null)
                {
                    throw new AccountUnauthorizedException($"AniList rejected the token ({(int)resp.StatusCode})");
                }

                if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                {
                    var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                    await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    // 404 from AniList means "no such title", which is a normal answer for a MAL id it doesn't know.
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    throw new HttpRequestException($"AniList returned {(int)resp.StatusCode}: {Truncate(body)}", null, resp.StatusCode);
                }

                return JsonDocument.Parse(body);
            }
        }
        finally
        {
            _last = DateTime.UtcNow;
            _throttle.Release();
        }
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;
}
