using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AniLiberty.Api;

/// <summary>Shikimori API client, used when a release isn't found on AniLiberty.</summary>
public sealed partial class ShikimoriClient
{
    // Shikimori allows 5 rps / 90 rpm.
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(700);

    private readonly IHttpClientFactory _httpFactory;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly ConcurrentDictionary<long, ShikiAnime?> _details = new();
    private DateTime _last = DateTime.MinValue;

    public ShikimoriClient(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public static string SiteUrl => (Plugin.Instance?.Configuration.ShikimoriUrl ?? "https://shikimori.rip").TrimEnd('/');

    public static string? ImageUrl(ShikiImage? image)
    {
        var src = image?.Original;
        if (string.IsNullOrEmpty(src) || src.Contains("missing_", StringComparison.Ordinal))
        {
            return null;
        }

        return src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? src : SiteUrl + src;
    }

    public async Task<IReadOnlyList<ShikiAnime>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ShikiAnime>();
        }

        var url = $"{SiteUrl}/api/animes?search={Uri.EscapeDataString(query)}&limit=10&censored=false";
        return await GetAsync<List<ShikiAnime>>(url, ct).ConfigureAwait(false) ?? new List<ShikiAnime>();
    }

    public async Task<ShikiAnime?> GetAsync(long id, CancellationToken ct)
    {
        if (_details.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var anime = await GetAsync<ShikiAnime>($"{SiteUrl}/api/animes/{id}", ct).ConfigureAwait(false);
        if (anime is not null && anime.Genres is not { Count: > 0 })
        {
            // Some mirrors (shikimori.rip) leave genres out of the REST answer; GraphQL still has them.
            anime.Genres = await GetGenresAsync(id, ct).ConfigureAwait(false);
        }

        _details[id] = anime;
        return anime;
    }

    private async Task<List<ShikiGenre>?> GetGenresAsync(long id, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            var query = "{ animes(ids: \"" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\") { genres { name russian kind } } }";
            using var req = new HttpRequestMessage(HttpMethod.Post, SiteUrl + "/api/graphql")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { query }), System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.UserAgent.ParseAdd(Http.UserAgent);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _httpFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default).SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), cancellationToken: cts.Token).ConfigureAwait(false);
            var animes = doc.RootElement.GetProperty("data").GetProperty("animes");
            if (animes.GetArrayLength() == 0)
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<List<ShikiGenre>>(animes[0].GetProperty("genres").GetRawText(), Http.Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _last = DateTime.UtcNow;
            _throttle.Release();
        }
    }

    /// <summary>Strip Shikimori BBCode ([character=1]Name[/character], [i]…[/i]) from descriptions.</summary>
    public static string? CleanDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return description;
        }

        var s = BbCode().Replace(description, string.Empty);
        return s.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    [GeneratedRegex(@"\[/?[a-z_]+(?:=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbCode();

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            try
            {
                return await Http.GetJsonAsync<T>(_httpFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default), url, ct).ConfigureAwait(false);
            }
            finally
            {
                _last = DateTime.UtcNow;
            }
        }
        finally
        {
            _throttle.Release();
        }
    }
}
