using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Sync;

/// <summary>
/// Pushes local changes to AniLiberty as they happen: playback progress (throttled), stops, and "mark played" toggles.
/// Works for every Jellyfin client, since playback reporting goes through the server.
/// Favorites and playlist edits have no reliable events and are picked up by the periodic sync.
/// </summary>
public sealed class PlaybackSyncService : IHostedService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);
    private const double WatchedFraction = 0.9;

    private readonly ISessionManager _sessions;
    private readonly IUserDataManager _userData;
    private readonly SyncService _sync;
    private readonly AccountStore _store;
    private readonly ILogger<PlaybackSyncService> _logger;
    private readonly ConcurrentDictionary<(Guid User, Guid Item), DateTime> _lastSent = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pendingSyncs = new();

    public PlaybackSyncService(
        ISessionManager sessions,
        IUserDataManager userData,
        SyncService sync,
        AccountStore store,
        ILogger<PlaybackSyncService> logger)
    {
        _sessions = sessions;
        _userData = userData;
        _sync = sync;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        _userData.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        _userData.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.IsPaused || e.Item is null || e.PlaybackPositionTicks is not { } ticks)
        {
            return;
        }

        foreach (var user in e.Users)
        {
            var key = (user.Id, e.Item.Id);
            var now = DateTime.UtcNow;
            if (_lastSent.TryGetValue(key, out var last) && now - last < ProgressInterval)
            {
                continue;
            }

            _lastSent[key] = now;
            Fire(user.Id, e.Item, ticks, playedToCompletion: false);
        }
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        foreach (var user in e.Users)
        {
            _lastSent.TryRemove((user.Id, e.Item.Id), out _);
            Fire(user.Id, e.Item, e.PlaybackPositionTicks ?? 0, e.PlayedToCompletion);
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        switch (e.SaveReason)
        {
            case UserDataSaveReason.TogglePlayed when e.Item is not null:
                Fire(e.UserId, e.Item, e.UserData.Played ? e.Item.RunTimeTicks ?? 0 : 0, e.UserData.Played);
                break;
            case UserDataSaveReason.UpdateUserRating:
                // Favorite toggled: reconcile soon rather than waiting for the scheduled sync.
                ScheduleSync(e.UserId, TimeSpan.FromSeconds(10));
                break;
        }
    }

    private void Fire(Guid userId, BaseItem item, long positionTicks, bool playedToCompletion)
    {
        if (_store.Get(userId) is not { IsLinked: true })
        {
            return;
        }

        var seconds = positionTicks / (double)TimeSpan.TicksPerSecond;
        var runtime = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
        var watched = playedToCompletion || (runtime > 0 && seconds / runtime >= WatchedFraction);
        _ = Task.Run(async () =>
        {
            try
            {
                await _sync.PushAsync(userId, item, watched ? Math.Max(seconds, runtime) : seconds, watched, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AniLiberty: failed to push progress of {Item}", item.Name);
            }
        });
    }

    private void ScheduleSync(Guid userId, TimeSpan delay)
    {
        if (_store.Get(userId) is not { IsLinked: true })
        {
            return;
        }

        var cts = new CancellationTokenSource();
        if (_pendingSyncs.TryRemove(userId, out var previous))
        {
            previous.Cancel();
        }

        _pendingSyncs[userId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                // Debounce: toggling several favorites in a row triggers a single sync.
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await _sync.SyncAsync(userId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AniLiberty: deferred sync failed");
            }
        });
    }
}
