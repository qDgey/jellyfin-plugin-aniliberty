using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Sync;

/// <summary>
/// Keeps a user's AniList list in step with what they watch in Jellyfin: episode progress and list status
/// ("Смотрю"/"Просмотрено"). Titles are matched by the MyAnimeList id AniLiberty gives for a release.
/// AniList tracks one number per title, so progress is the highest watched episode.
/// </summary>
public sealed class AniListSync
{
    private readonly AniListClient _anilist;
    private readonly LibraryIndex _index;
    private readonly IUserDataManager _userData;
    private readonly ILogger<AniListSync> _logger;

    public AniListSync(AniListClient anilist, LibraryIndex index, IUserDataManager userData, ILogger<AniListSync> logger)
    {
        _anilist = anilist;
        _index = index;
        _userData = userData;
        _logger = logger;
    }

    /// <summary>Progress and status of one title, as both sides see it.</summary>
    private sealed record State(int Progress, string Status);

    public async Task SyncAsync(User user, AccountLink link, Action<Action<AccountLink>> update, CancellationToken ct)
    {
        if (link.AniListToken is not { Length: > 0 } token || link.AniListUserId is not { } anilistUserId)
        {
            return;
        }

        var index = await _index.GetAsync(ct).ConfigureAwait(false);
        var remoteEntries = await _anilist.GetListAsync(token, anilistUserId, ct).ConfigureAwait(false);
        var remote = new Dictionary<long, AniListEntry>();
        foreach (var entry in remoteEntries.Where(e => e.MalId is > 0))
        {
            remote[entry.MalId!.Value] = entry;
        }

        var snapshot = link.AniList;
        var local = LocalStates(user, index, out var episodesByMal);
        var initial = !link.AniListMerged;

        // A rebuilt library comes back without user data; that isn't the user resetting their list.
        var lost = snapshot.Count(p => local.TryGetValue(p.Key, out var l) && l.Progress == 0 && p.Value.Progress > 0);
        if (!initial && lost >= 5 && lost > snapshot.Count * 0.3)
        {
            _logger.LogWarning("AniList {User}: {Lost} titles lost their progress locally (library rebuilt?); merging without lowering anything", user.Username, lost);
            initial = true;
        }

        var result = new Dictionary<long, AniListState>();
        int pushed = 0, pulled = 0;
        foreach (var malId in local.Keys.Union(remote.Keys).Union(snapshot.Keys))
        {
            ct.ThrowIfCancellationRequested();
            // A title that isn't in the library has no local opinion: keep whatever both sides agreed on.
            var inLibrary = episodesByMal.ContainsKey(malId);
            var l = local.TryGetValue(malId, out var localState) ? localState
                : inLibrary ? null
                : snapshot.TryGetValue(malId, out var keep) ? new State(keep.Progress, keep.Status ?? string.Empty) : null;
            var r = remote.TryGetValue(malId, out var entry) ? new State(entry.Progress, entry.Status ?? string.Empty) : null;
            var s = snapshot.TryGetValue(malId, out var saved) ? new State(saved.Progress, saved.Status ?? string.Empty) : null;

            State? final;
            if (initial)
            {
                final = Best(l, r);
            }
            else if (!Same(r, s))
            {
                final = r;
            }
            else if (!Same(l, s))
            {
                final = l;
            }
            else
            {
                final = s;
            }

            if (final is null || final.Progress <= 0)
            {
                continue;
            }

            if (!Same(final, r))
            {
                var mediaId = entry?.MediaId ?? await _anilist.GetMediaIdAsync(malId, ct).ConfigureAwait(false);
                if (mediaId is { } id)
                {
                    await _anilist.SaveEntryAsync(token, id, final.Status, final.Progress, ct).ConfigureAwait(false);
                    pushed++;
                }
            }

            if ((Plugin.Instance?.Configuration.AniListPullProgress ?? true)
                && !Same(final, l)
                && episodesByMal.TryGetValue(malId, out var episodes))
            {
                pulled += MarkWatched(user, episodes, final.Progress, ct);
            }

            result[malId] = new AniListState { Progress = final.Progress, Status = final.Status };
        }

        update(l =>
        {
            l.AniList = result;
            l.AniListMerged = true;
        });
        _logger.LogInformation(
            "AniList sync {User}: {Titles} titles ({Remote} on the list), pushed {Pushed}, marked watched locally {Pulled}",
            user.Username, result.Count, remote.Count, pushed, pulled);
    }

    /// <summary>What Jellyfin knows: highest watched episode per title, and whether the release is finished.</summary>
    private Dictionary<long, State> LocalStates(User user, LibraryIndex.Snapshot index, out Dictionary<long, List<BaseItem>> episodesByMal)
    {
        var result = new Dictionary<long, State>();
        episodesByMal = new Dictionary<long, List<BaseItem>>();
        foreach (var (releaseId, episodes) in index.EpisodesByRelease)
        {
            if (!index.MalOfRelease.TryGetValue(releaseId, out var malId))
            {
                continue;
            }

            episodesByMal[malId] = episodes;
            var data = _userData.GetUserDataBatch(episodes, user);
            var watched = episodes
                .Where(e => data.GetValueOrDefault(e.Id)?.Played == true)
                .Select(e => e.IndexNumber ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            if (watched <= 0)
            {
                continue;
            }

            var total = episodes.Select(e => e.IndexNumber ?? 0).DefaultIfEmpty(0).Max();
            result[malId] = new State(watched, watched >= total ? "COMPLETED" : "CURRENT");
        }

        return result;
    }

    private int MarkWatched(User user, List<BaseItem> episodes, int progress, CancellationToken ct)
    {
        var changed = 0;
        var data = _userData.GetUserDataBatch(episodes, user);
        foreach (var episode in episodes.Where(e => e.IndexNumber is { } n && n <= progress))
        {
            var userData = data.GetValueOrDefault(episode.Id) ?? _userData.GetUserData(user, episode);
            if (userData is null || userData.Played)
            {
                continue;
            }

            userData.Played = true;
            userData.PlayCount = Math.Max(1, userData.PlayCount);
            userData.LastPlayedDate ??= DateTime.UtcNow;
            userData.PlaybackPositionTicks = 0;
            _userData.SaveUserData(user, episode, userData, UserDataSaveReason.Import, ct);
            changed++;
        }

        return changed;
    }

    private static State? Best(State? a, State? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return a.Progress >= b.Progress ? a : b;
    }

    private static bool Same(State? a, State? b) => a?.Progress == b?.Progress && a?.Status == b?.Status;
}
