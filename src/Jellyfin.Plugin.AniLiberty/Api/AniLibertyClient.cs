using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Api;

/// <summary>
/// AniLiberty API v1 client with a persisted torrent-name → release index.
/// Local folders are torrent downloads, so the magnet "dn" (torrent name) identifies the release exactly.
/// </summary>
public sealed class AniLibertyClient
{
    private static readonly TimeSpan ReleaseTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissRefreshInterval = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AniLibertyClient> _logger;
    private readonly ConcurrentDictionary<long, (DateTime At, Release? Release)> _releases = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    private TorrentIndex? _index;

    public AniLibertyClient(IHttpClientFactory httpFactory, ILogger<AniLibertyClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private static string SiteUrl => (Plugin.Instance?.Configuration.SiteUrl ?? "https://aniliberty.top").TrimEnd('/');

    private static string ApiUrl => SiteUrl + "/api/v1";

    public static string? ImageUrl(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }

        return relative.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? relative : SiteUrl + relative;
    }

    public static string ReleasePageUrl(string alias) => SiteUrl + "/anime/releases/release/" + alias;

    public async Task<Release?> GetReleaseAsync(long id, CancellationToken ct)
    {
        if (_releases.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < ReleaseTtl)
        {
            return cached.Release;
        }

        var release = await Http.GetJsonAsync<Release>(Client(), $"{ApiUrl}/anime/releases/{id}", ct).ConfigureAwait(false);
        _releases[id] = (DateTime.UtcNow, release);
        return release;
    }

    public async Task<IReadOnlyList<Release>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<Release>();
        }

        var url = $"{ApiUrl}/app/search/releases?query={Uri.EscapeDataString(query)}";
        return await Http.GetJsonAsync<List<Release>>(Client(), url, ct).ConfigureAwait(false) ?? new List<Release>();
    }

    public async Task<IReadOnlyList<Franchise>> GetFranchisesAsync(CancellationToken ct)
    {
        return await Http.GetJsonAsync<List<Franchise>>(Client(), $"{ApiUrl}/anime/franchises", ct).ConfigureAwait(false) ?? new List<Franchise>();
    }

    /// <summary>One franchise with its releases (in AniLiberty's watch order via <see cref="FranchiseRelease.SortOrder"/>).</summary>
    public Task<Franchise?> GetFranchiseAsync(string id, CancellationToken ct)
    {
        return Http.GetJsonAsync<Franchise>(Client(), $"{ApiUrl}/anime/franchises/{Uri.EscapeDataString(id)}", ct);
    }

    /// <summary>Find a release by the exact torrent name (folder name or single-file name).</summary>
    public async Task<long?> FindByTorrentNameAsync(string name, CancellationToken ct)
    {
        var index = await GetIndexAsync(forceIfOlderThan: null, ct).ConfigureAwait(false);
        if (index.TryFind(name, out var id))
        {
            return id;
        }

        // New torrents appear daily; refresh an index that's more than an hour old on a miss.
        index = await GetIndexAsync(forceIfOlderThan: MissRefreshInterval, ct).ConfigureAwait(false);
        return index.TryFind(name, out id) ? id : null;
    }

    private HttpClient Client() => _httpFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);

    private async Task<TorrentIndex> GetIndexAsync(TimeSpan? forceIfOlderThan, CancellationToken ct)
    {
        var maxAge = TimeSpan.FromHours(Math.Max(1, Plugin.Instance?.Configuration.TorrentIndexRefreshHours ?? 24));
        if (forceIfOlderThan is { } f && f < maxAge)
        {
            maxAge = f;
        }

        if (_index is not null && DateTime.UtcNow - _index.BuiltAt < maxAge)
        {
            return _index;
        }

        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_index is not null && DateTime.UtcNow - _index.BuiltAt < maxAge)
            {
                return _index;
            }

            var file = IndexFile();
            if (_index is null && file is not null && File.Exists(file))
            {
                try
                {
                    await using var fs = File.OpenRead(file);
                    _index = await JsonSerializer.DeserializeAsync<TorrentIndex>(fs, cancellationToken: ct).ConfigureAwait(false);
                    if (_index is not null && DateTime.UtcNow - _index.BuiltAt < maxAge)
                    {
                        return _index;
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    _logger.LogWarning(ex, "Ignoring unreadable AniLiberty torrent index {File}", file);
                }
            }

            try
            {
                var fresh = await DownloadIndexAsync(ct).ConfigureAwait(false);
                _index = fresh;
                if (file is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await using var fs = File.Create(file);
                    await JsonSerializer.SerializeAsync(fs, fresh, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                // Keep serving the stale index rather than failing every lookup while the site is down.
                _logger.LogWarning(ex, "Failed to refresh AniLiberty torrent index");
                if (_index is null)
                {
                    throw;
                }

                _index.BuiltAt = DateTime.UtcNow - maxAge + MissRefreshInterval;
            }

            return _index;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<TorrentIndex> DownloadIndexAsync(CancellationToken ct)
    {
        var index = new TorrentIndex { BuiltAt = DateTime.UtcNow };
        var client = Client();
        var page = 1;
        int totalPages;
        do
        {
            var url = $"{ApiUrl}/anime/torrents?page={page}&limit=50";
            var data = await Http.GetJsonAsync<Paged<Torrent>>(client, url, ct).ConfigureAwait(false)
                       ?? throw new HttpRequestException("Empty torrent page " + page);
            foreach (var t in data.Data)
            {
                if (t.Release is null)
                {
                    continue;
                }

                var dn = MagnetDisplayName(t.Magnet);
                if (!string.IsNullOrEmpty(dn))
                {
                    index.Exact[NameNormalizer.Exact(dn)] = t.Release.Id;
                    index.AddClean(NameNormalizer.Clean(dn), t.Release.Id);
                }

                index.AddClean(NameNormalizer.Clean(t.Release.Name?.English), t.Release.Id);
                index.AddClean(NameNormalizer.Clean(t.Release.Name?.Alternative), t.Release.Id);
                index.AddClean(NameNormalizer.Clean(t.Release.Alias?.Replace('-', ' ')), t.Release.Id);
            }

            totalPages = data.Meta?.Pagination?.TotalPages ?? page;
            page++;
        }
        while (page <= totalPages);

        _logger.LogInformation("AniLiberty torrent index built: {Exact} torrent names, {Clean} titles", index.Exact.Count, index.Clean.Count);
        return index;
    }

    private static string? MagnetDisplayName(string? magnet)
    {
        if (string.IsNullOrEmpty(magnet))
        {
            return null;
        }

        var q = magnet.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return null;
        }

        foreach (var part in magnet[(q + 1)..].Split('&'))
        {
            if (part.StartsWith("dn=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part[3..].Replace('+', ' '));
            }
        }

        return null;
    }

    private static string? IndexFile()
    {
        var dir = Plugin.Instance?.CacheDirectory;
        return dir is null ? null : Path.Combine(dir, "torrent-index.json");
    }
}

public sealed class TorrentIndex
{
    public DateTime BuiltAt { get; set; }

    public Dictionary<string, long> Exact { get; set; } = new();

    /// <summary>Cleaned title → release ids (several when titles collide).</summary>
    public Dictionary<string, List<long>> Clean { get; set; } = new();

    public void AddClean(string key, long id)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!Clean.TryGetValue(key, out var ids))
        {
            Clean[key] = ids = new List<long>();
        }

        if (!ids.Contains(id))
        {
            ids.Add(id);
        }
    }

    public bool TryFind(string name, out long id)
    {
        if (Exact.TryGetValue(NameNormalizer.Exact(name), out id))
        {
            return true;
        }

        if (Clean.TryGetValue(NameNormalizer.Clean(name), out var ids) && ids.Count == 1)
        {
            id = ids[0];
            return true;
        }

        id = 0;
        return false;
    }
}
