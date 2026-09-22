using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Sync;

/// <summary>
/// Two-way sync of one Jellyfin user with their AniLiberty account.
/// AniLiberty timecodes carry no timestamps, so "newer wins" is decided by three-way merging against the
/// state both sides agreed on at the previous sync: whichever side differs from that snapshot changed since.
/// </summary>
public sealed class SyncService
{
    // Local positions within this distance of the snapshot count as unchanged (clients report progress coarsely).
    private const double PositionToleranceSeconds = 30;

    private readonly AccountStore _store;
    private readonly AccountClient _account;
    private readonly LibraryIndex _index;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IPlaylistManager _playlists;
    private readonly ILibraryManager _library;
    private readonly ILogger<SyncService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SyncService(
        AccountStore store,
        AccountClient account,
        LibraryIndex index,
        IUserManager users,
        IUserDataManager userData,
        IPlaylistManager playlists,
        ILibraryManager library,
        ILogger<SyncService> logger)
    {
        _store = store;
        _account = account;
        _index = index;
        _users = users;
        _userData = userData;
        _playlists = playlists;
        _library = library;
        _logger = logger;
    }

    private SemaphoreSlim LockFor(Guid userId) => _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    // ---------- live pushes from playback / user actions ----------

    /// <summary>Send one episode's progress; called from playback events and "mark played" toggles.</summary>
    public async Task PushAsync(Guid userId, BaseItem item, double seconds, bool watched, CancellationToken ct)
    {
        if (_store.Get(userId) is not { IsLinked: true } link)
        {
            return;
        }

        var episodeId = await _index.EpisodeIdAsync(item, ct).ConfigureAwait(false);
        if (episodeId is null)
        {
            return;
        }

        var gate = LockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!watched && seconds < 1)
            {
                await _account.DeleteTimecodesAsync(link.Token!, new[] { episodeId }, ct).ConfigureAwait(false);
                _store.Update(userId, l => l.Timecodes.Remove(episodeId));
            }
            else
            {
                await _account.UpdateTimecodesAsync(link.Token!, new[] { new TimecodeUpdate(episodeId, seconds, watched) }, ct).ConfigureAwait(false);
                _store.Update(userId, l => l.Timecodes[episodeId] = new TimecodeState { Time = seconds, Watched = watched });
            }
        }
        catch (AccountUnauthorizedException ex)
        {
            MarkUnauthorized(userId, ex);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------- full sync ----------

    public async Task SyncAsync(Guid userId, CancellationToken ct)
    {
        if (_store.Get(userId) is not { IsLinked: true } link || _users.GetUserById(userId) is not { } user)
        {
            return;
        }

        var gate = LockFor(userId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await _index.GetAsync(ct).ConfigureAwait(false);
            var token = link.Token!;
            var initial = !link.Merged;

            var timecodes = await SyncTimecodesAsync(user, link, index, token, initial, ct).ConfigureAwait(false);
            var (favorites, missing) = await SyncFavoritesAsync(user, link, index, token, initial, ct).ConfigureAwait(false);
            var collections = await SyncCollectionsAsync(user, link, index, token, initial, ct).ConfigureAwait(false);

            _store.Update(userId, l =>
            {
                l.Timecodes = timecodes;
                l.Favorites = favorites;
                l.Collections = collections.State;
                l.Playlists = collections.Playlists;
                l.MissingFavorites = missing;
                l.Merged = true;
                l.LastSyncAt = DateTime.UtcNow;
                l.LastError = null;
            });
        }
        catch (AccountUnauthorizedException ex)
        {
            MarkUnauthorized(userId, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AniLiberty sync failed for user {User}", user.Username);
            _store.Update(userId, l => l.LastError = "Сайт недоступен: " + ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Dictionary<string, TimecodeState>> SyncTimecodesAsync(
        User user, AccountLink link, LibraryIndex.Snapshot index, string token, bool initial, CancellationToken ct)
    {
        var remote = (await _account.GetTimecodesAsync(token, ct).ConfigureAwait(false))
            .GroupBy(t => t.EpisodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var snapshot = link.Timecodes;
        var result = new Dictionary<string, TimecodeState>(StringComparer.OrdinalIgnoreCase);
        var push = new List<TimecodeUpdate>();
        var delete = new List<string>();
        int toLocal = 0;

        var userData = UserDataFor(user, index.ByEpisode.Values.SelectMany(v => v));

        var keys = new HashSet<string>(remote.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(snapshot.Keys);
        keys.UnionWith(index.ByEpisode.Keys);

        foreach (var id in keys)
        {
            remote.TryGetValue(id, out var r);
            snapshot.TryGetValue(id, out var s);
            var rState = r is null ? null : new TimecodeState { Time = r.Time, Watched = r.IsWatched };

            if (!index.ByEpisode.TryGetValue(id, out var items))
            {
                // Not in the library: nothing to reconcile, just remember AniLiberty's value.
                if (rState is not null)
                {
                    result[id] = rState;
                }

                continue;
            }

            var lState = LocalState(userData, items);
            TimecodeState? final;
            if (initial)
            {
                // First sync after linking: union, nothing is taken away from either side.
                final = Union(lState, rState);
            }
            else if (!Same(rState, s))
            {
                final = rState;
            }
            else if (!Same(lState, s))
            {
                final = lState;
            }
            else
            {
                final = s;
            }

            if (!Same(final, lState))
            {
                ApplyLocal(user, userData, items, final, ct);
                toLocal++;
            }

            if (!Same(final, rState))
            {
                if (final is null)
                {
                    delete.Add(id);
                }
                else
                {
                    push.Add(new TimecodeUpdate(id, final.Time, final.Watched));
                }
            }

            if (final is not null)
            {
                result[id] = final;
            }
        }

        foreach (var chunk in push.Chunk(100))
        {
            await _account.UpdateTimecodesAsync(token, chunk, ct).ConfigureAwait(false);
        }

        foreach (var chunk in delete.Chunk(100))
        {
            await _account.DeleteTimecodesAsync(token, chunk, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "AniLiberty sync {User}: timecodes remote={Remote} → local {ToLocal}, → remote {Push}+{Delete}",
            user.Username, remote.Count, toLocal, push.Count, delete.Count);
        return result;
    }

    private async Task<(HashSet<long> State, List<long> Missing)> SyncFavoritesAsync(
        User user, AccountLink link, LibraryIndex.Snapshot index, string token, bool initial, CancellationToken ct)
    {
        var remote = await _account.GetFavoriteIdsAsync(token, ct).ConfigureAwait(false);
        var snapshot = link.Favorites;
        var userData = UserDataFor(user, index.ByRelease.Values.SelectMany(v => v));
        var local = index.ByRelease
            .Where(p => p.Value.Any(i => userData.GetValueOrDefault(i.Id)?.IsFavorite == true))
            .Select(p => p.Key)
            .ToHashSet();

        // Releases missing from the library have no local opinion: treat them as unchanged locally.
        local.UnionWith(snapshot.Where(id => !index.ByRelease.ContainsKey(id)));

        HashSet<long> final;
        if (initial)
        {
            final = new HashSet<long>(local);
            final.UnionWith(remote);
        }
        else
        {
            final = new HashSet<long>(snapshot);
            final.UnionWith(remote.Except(snapshot));
            final.UnionWith(local.Except(snapshot));
            final.ExceptWith(snapshot.Except(remote));
            final.ExceptWith(snapshot.Except(local));
        }

        foreach (var (releaseId, items) in index.ByRelease)
        {
            var want = final.Contains(releaseId);
            foreach (var item in items)
            {
                var data = userData.GetValueOrDefault(item.Id);
                if (data is not null && data.IsFavorite != want)
                {
                    data.IsFavorite = want;
                    _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, ct);
                }
            }
        }

        await _account.AddFavoritesAsync(token, final.Except(remote).ToList(), ct).ConfigureAwait(false);
        await _account.RemoveFavoritesAsync(token, remote.Except(final).ToList(), ct).ConfigureAwait(false);

        var missing = final.Where(id => !index.ByRelease.ContainsKey(id)).OrderBy(id => id).ToList();
        _logger.LogInformation(
            "AniLiberty sync {User}: favorites {Count} ({Missing} not in library), → remote +{Add} -{Remove}",
            user.Username, final.Count, missing.Count, final.Count(id => !remote.Contains(id)), remote.Count(id => !final.Contains(id)));
        return (final, missing);
    }

    private async Task<(Dictionary<long, string> State, Dictionary<string, Guid> Playlists)> SyncCollectionsAsync(
        User user, AccountLink link, LibraryIndex.Snapshot index, string token, bool initial, CancellationToken ct)
    {
        var remote = await _account.GetCollectionsAsync(token, ct).ConfigureAwait(false);
        var snapshot = link.Collections;
        var playlists = await EnsurePlaylistsAsync(user, link).ConfigureAwait(false);
        var local = ReadPlaylists(playlists, index);

        var final = new Dictionary<long, string>();
        var keys = new HashSet<long>(remote.Keys);
        keys.UnionWith(snapshot.Keys);
        keys.UnionWith(local.Keys);
        foreach (var id in keys)
        {
            remote.TryGetValue(id, out var r);
            snapshot.TryGetValue(id, out var s);
            string? l = index.ByRelease.ContainsKey(id) ? local.GetValueOrDefault(id) : s;

            string? f;
            if (initial)
            {
                f = r ?? l;
            }
            else if (r != s)
            {
                f = r;
            }
            else if (l != s)
            {
                f = l;
            }
            else
            {
                f = s;
            }

            if (f is not null)
            {
                final[id] = f;
            }
        }

        await WritePlaylistsAsync(user, playlists, index, local, final, ct).ConfigureAwait(false);

        var set = final.Where(p => remote.GetValueOrDefault(p.Key) != p.Value).ToDictionary(p => p.Key, p => p.Value);
        await _account.SetCollectionsAsync(token, set, ct).ConfigureAwait(false);
        var removed = remote.Keys.Where(id => !final.ContainsKey(id)).ToList();
        await _account.RemoveFromCollectionsAsync(token, removed, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "AniLiberty sync {User}: collections {Count}, → remote {Set} set, {Removed} removed",
            user.Username, final.Count, set.Count, removed.Count);

        return (final, playlists);
    }

    // ---------- playlists mirror AniLiberty collections ----------

    private async Task<Dictionary<string, Guid>> EnsurePlaylistsAsync(User user, AccountLink link)
    {
        var result = new Dictionary<string, Guid>();
        foreach (var type in CollectionTypes.All)
        {
            if (link.Playlists.TryGetValue(type, out var id) && _library.GetItemById(id) is Playlist)
            {
                result[type] = id;
                continue;
            }

            var created = await _playlists.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = "AniLiberty: " + CollectionTypes.Title(type),
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = Array.Empty<Guid>(),
            }).ConfigureAwait(false);
            result[type] = Guid.Parse(created.Id);
        }

        return result;
    }

    /// <summary>Release → collection type as currently shown in the user's playlists.</summary>
    private Dictionary<long, string> ReadPlaylists(Dictionary<string, Guid> playlists, LibraryIndex.Snapshot index)
    {
        var result = new Dictionary<long, string>();
        foreach (var type in CollectionTypes.All)
        {
            if (_library.GetItemById(playlists[type]) is not Playlist playlist)
            {
                continue;
            }

            foreach (var (_, item) in playlist.GetManageableItems())
            {
                if (LibraryIndex.ReleaseId(item) is { } releaseId)
                {
                    result.TryAdd(releaseId, type);
                }
            }
        }

        return result;
    }

    private async Task WritePlaylistsAsync(
        User user,
        Dictionary<string, Guid> playlists,
        LibraryIndex.Snapshot index,
        Dictionary<long, string> local,
        Dictionary<long, string> final,
        CancellationToken ct)
    {
        foreach (var type in CollectionTypes.All)
        {
            if (_library.GetItemById(playlists[type]) is not Playlist playlist)
            {
                continue;
            }

            var entries = playlist.GetManageableItems().ToList();
            var stale = entries
                .Where(e => LibraryIndex.ReleaseId(e.Item2) is { } id && final.GetValueOrDefault(id) != type)
                .Select(e => e.Item1.ItemId?.ToString("N") ?? e.Item2.Id.ToString("N"))
                .ToList();
            if (stale.Count > 0)
            {
                await _playlists.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), stale).ConfigureAwait(false);
            }

            var present = entries.Select(e => LibraryIndex.ReleaseId(e.Item2)).OfType<long>().ToHashSet();
            var add = final
                .Where(p => p.Value == type && !present.Contains(p.Key) && index.ByRelease.ContainsKey(p.Key))
                .Select(p => PreferredCopy(index.ByRelease[p.Key]).Id)
                .ToList();
            if (add.Count > 0)
            {
                // Adding a series adds its episodes.
                await _playlists.AddItemToPlaylistAsync(playlist.Id, add, null, user.Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One copy per release in playlists: the HEVC twin if configured and present, else the first.</summary>
    private static BaseItem PreferredCopy(List<BaseItem> copies)
    {
        var preferHevc = Plugin.Instance?.Configuration.PreferHevcInPlaylists ?? true;
        return copies.FirstOrDefault(c => preferHevc == (c.Path?.Contains("HEVC", StringComparison.OrdinalIgnoreCase) ?? false))
               ?? copies[0];
    }

    // ---------- local state helpers ----------

    private Dictionary<Guid, UserItemData> UserDataFor(User user, IEnumerable<BaseItem> items)
    {
        var list = items.DistinctBy(i => i.Id).ToList();
        var result = new Dictionary<Guid, UserItemData>();
        foreach (var chunk in list.Chunk(1000))
        {
            foreach (var (id, data) in _userData.GetUserDataBatch(chunk, user))
            {
                result[id] = data;
            }
        }

        return result;
    }

    private static TimecodeState? LocalState(Dictionary<Guid, UserItemData> userData, List<BaseItem> items)
    {
        TimecodeState? best = null;
        foreach (var item in items)
        {
            var data = userData.GetValueOrDefault(item.Id);
            if (data is null)
            {
                continue;
            }

            if (data.Played)
            {
                return new TimecodeState { Watched = true, Time = Seconds(item.RunTimeTicks ?? 0) };
            }

            var pos = Seconds(data.PlaybackPositionTicks);
            if (pos >= 1 && (best is null || pos > best.Time))
            {
                best = new TimecodeState { Time = pos };
            }
        }

        return best;
    }

    private void ApplyLocal(User user, Dictionary<Guid, UserItemData> userData, List<BaseItem> items, TimecodeState? state, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var data = userData.GetValueOrDefault(item.Id) ?? _userData.GetUserData(user, item);
            if (data is null)
            {
                continue;
            }

            var watched = state?.Watched ?? false;
            var ticks = watched || state is null ? 0 : (long)(state.Time * TimeSpan.TicksPerSecond);
            if (data.Played == watched && data.PlaybackPositionTicks == ticks)
            {
                continue;
            }

            if (watched && !data.Played)
            {
                data.PlayCount = Math.Max(1, data.PlayCount);
                data.LastPlayedDate ??= DateTime.UtcNow;
            }

            data.Played = watched;
            data.PlaybackPositionTicks = ticks;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, ct);
        }
    }

    private static TimecodeState? Union(TimecodeState? a, TimecodeState? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return new TimecodeState { Watched = a.Watched || b.Watched, Time = Math.Max(a.Time, b.Time) };
    }

    private static bool Same(TimecodeState? a, TimecodeState? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.Watched == b.Watched && (a.Watched || Math.Abs(a.Time - b.Time) < PositionToleranceSeconds);
    }

    private static double Seconds(long ticks) => ticks / (double)TimeSpan.TicksPerSecond;

    private void MarkUnauthorized(Guid userId, Exception ex)
    {
        _logger.LogWarning("AniLiberty: token of user {User} rejected, unlinking: {Message}", userId, ex.Message);
        _store.Update(userId, l =>
        {
            l.Token = null;
            l.LastError = "AniLiberty отклонил вход — привяжите аккаунт заново";
        });
    }
}
