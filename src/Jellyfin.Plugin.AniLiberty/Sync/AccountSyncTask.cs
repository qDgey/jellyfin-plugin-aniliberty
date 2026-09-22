using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AniLiberty.Sync;

/// <summary>Scheduled two-way sync for every linked account (Dashboard → Scheduled tasks → AniLiberty).</summary>
public sealed class AccountSyncTask : IScheduledTask
{
    private readonly AccountStore _store;
    private readonly SyncService _sync;
    private readonly LibraryIndex _index;

    public AccountSyncTask(AccountStore store, SyncService sync, LibraryIndex index)
    {
        _store = store;
        _sync = sync;
        _index = index;
    }

    public string Name => "Синхронизация аккаунтов AniLiberty";

    public string Key => "AniLibertyAccountSync";

    public string Description => "Прогресс просмотра, избранное и коллекции ↔ аккаунты AniLiberty пользователей.";

    public string Category => "AniLiberty";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var links = _store.Linked();
        if (links.Count == 0)
        {
            progress.Report(100);
            return;
        }

        // Pick up items added or re-identified since the last run.
        _index.Invalidate();
        for (var i = 0; i < links.Count; i++)
        {
            await _sync.SyncAsync(links[i].UserId, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / links.Count);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Math.Max(5, Plugin.Instance?.Configuration.AccountSyncIntervalMinutes ?? 15);
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks,
        };
    }
}
