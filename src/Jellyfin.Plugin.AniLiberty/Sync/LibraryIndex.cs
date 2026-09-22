using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Sync;

/// <summary>
/// Local items keyed by AniLiberty ids. AVC and HEVC twins of a release share ids, so every key maps to a list:
/// state pulled from AniLiberty is applied to all copies.
/// </summary>
public sealed class LibraryIndex
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(20);

    private readonly ILibraryManager _library;
    private readonly AniLibertyClient _client;
    private readonly ILogger<LibraryIndex> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _snapshot;

    public LibraryIndex(ILibraryManager library, AniLibertyClient client, ILogger<LibraryIndex> logger)
    {
        _library = library;
        _client = client;
        _logger = logger;
    }

    public sealed class Snapshot
    {
        public DateTime BuiltAt { get; init; }

        /// <summary>AniLiberty episode uuid → playable items (episodes, or movies via their single episode).</summary>
        public Dictionary<string, List<BaseItem>> ByEpisode { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Release id → series/movies.</summary>
        public Dictionary<long, List<BaseItem>> ByRelease { get; } = new();

        /// <summary>Series/movie item id → release id.</summary>
        public Dictionary<Guid, long> ReleaseOfTitle { get; } = new();

        /// <summary>Playable item id → AniLiberty episode uuid.</summary>
        public Dictionary<Guid, string> EpisodeOfItem { get; } = new();
    }

    public void Invalidate() => _snapshot = null;

    public async Task<Snapshot> GetAsync(CancellationToken ct)
    {
        if (_snapshot is { } s && DateTime.UtcNow - s.BuiltAt < MaxAge)
        {
            return s;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshot is { } again && DateTime.UtcNow - again.BuiltAt < MaxAge)
            {
                return again;
            }

            _snapshot = await BuildAsync(ct).ConfigureAwait(false);
            return _snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>AniLiberty episode uuid for a playable item, resolving movies through their release.</summary>
    public async Task<string?> EpisodeIdAsync(BaseItem item, CancellationToken ct)
    {
        if (item is Episode && item.TryGetProviderId(Plugin.ProviderKey, out var uuid) && Guid.TryParse(uuid, out _))
        {
            return uuid;
        }

        if (item is Movie && ReleaseId(item) is { } releaseId)
        {
            var release = await _client.GetReleaseAsync(releaseId, ct).ConfigureAwait(false);
            return release?.Episodes?.OrderBy(e => e.Ordinal ?? 0).FirstOrDefault()?.Id;
        }

        return null;
    }

    public static long? ReleaseId(BaseItem item)
    {
        var title = item is Episode episode ? episode.Series : item;
        return title is not null && title.TryGetProviderId(Plugin.ProviderKey, out var s)
               && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private async Task<Snapshot> BuildAsync(CancellationToken ct)
    {
        var snapshot = new Snapshot { BuiltAt = DateTime.UtcNow };
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
        });

        foreach (var item in items)
        {
            if (item is Episode)
            {
                if (item.TryGetProviderId(Plugin.ProviderKey, out var uuid) && Guid.TryParse(uuid, out _))
                {
                    Add(snapshot.ByEpisode, uuid, item);
                    snapshot.EpisodeOfItem[item.Id] = uuid;
                }

                continue;
            }

            if (ReleaseId(item) is not { } releaseId)
            {
                continue;
            }

            Add(snapshot.ByRelease, releaseId, item);
            snapshot.ReleaseOfTitle[item.Id] = releaseId;

            if (item is Movie && await EpisodeIdAsync(item, ct).ConfigureAwait(false) is { } movieEpisode)
            {
                Add(snapshot.ByEpisode, movieEpisode, item);
                snapshot.EpisodeOfItem[item.Id] = movieEpisode;
            }
        }

        _logger.LogInformation(
            "AniLiberty sync index: {Titles} titles, {Episodes} episodes",
            snapshot.ByRelease.Count,
            snapshot.ByEpisode.Count);
        return snapshot;
    }

    private static void Add<TKey>(Dictionary<TKey, List<BaseItem>> map, TKey key, BaseItem item)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = new List<BaseItem>();
        }

        list.Add(item);
    }
}
