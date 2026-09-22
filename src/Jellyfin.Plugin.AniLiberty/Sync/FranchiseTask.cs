using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Sync;

/// <summary>
/// Mirrors AniLiberty franchises as Jellyfin collections: seasons, movies and OVAs of one title in the site's
/// watch order ("Моя геройская академия" 1 → 2 → 3 → «Два героя» → 4 …). Collections are shared by all users.
/// Run daily by <see cref="FranchiseTask"/> and after every library scan by <see cref="FranchisePostScanTask"/>.
/// </summary>
public sealed class FranchiseBuilder
{
    public const string FranchiseKey = "AniLibertyFranchise";

    // A collection with one entry is just noise.
    private const int MinLocalReleases = 2;

    private readonly AniLibertyClient _client;
    private readonly LibraryIndex _index;
    private readonly ILibraryManager _library;
    private readonly ICollectionManager _collections;
    private readonly IProviderManager _providers;
    private readonly ILogger<FranchiseBuilder> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public FranchiseBuilder(
        AniLibertyClient client,
        LibraryIndex index,
        ILibraryManager library,
        ICollectionManager collections,
        IProviderManager providers,
        ILogger<FranchiseBuilder> logger)
    {
        _client = client;
        _index = index;
        _library = library;
        _collections = collections;
        _providers = providers;
        _logger = logger;
    }

    public async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // The daily task and the post-scan hook can overlap; one pass is enough.
        if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            progress.Report(100);
            return;
        }

        try
        {
            await BuildAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task BuildAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _index.Invalidate();
        var index = await _index.GetAsync(cancellationToken).ConfigureAwait(false);
        var franchises = await _client.GetFranchisesAsync(cancellationToken).ConfigureAwait(false);
        var existing = ExistingCollections();
        var preferEnglish = Plugin.Instance?.Configuration.PreferEnglishTitle ?? false;
        int created = 0, updated = 0;

        for (var i = 0; i < franchises.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / franchises.Count);

            var full = await _client.GetFranchiseAsync(franchises[i].Id, cancellationToken).ConfigureAwait(false);
            if (full?.FranchiseReleases is not { Count: > 0 } releases)
            {
                continue;
            }

            var items = releases
                .OrderBy(r => r.SortOrder)
                .Select(r => index.ByRelease.GetValueOrDefault(r.ReleaseId)?.FirstOrDefault(IsPrimaryVersion))
                .OfType<BaseItem>()
                .DistinctBy(item => item.Id)
                .ToList();
            if (items.Count < MinLocalReleases)
            {
                continue;
            }

            var name = (preferEnglish ? full.NameEnglish ?? full.Name : full.Name ?? full.NameEnglish) ?? full.Id;
            if (existing.TryGetValue(full.Id, out var boxSet))
            {
                if (await ApplyAsync(boxSet, name, items, cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }
            else
            {
                boxSet = await _collections.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = name,
                    IsLocked = true,
                    ProviderIds = new Dictionary<string, string> { [FranchiseKey] = full.Id },
                }).ConfigureAwait(false);
                await ApplyAsync(boxSet, name, items, cancellationToken).ConfigureAwait(false);
                created++;
            }

            await EnsurePosterAsync(boxSet, full, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("AniLiberty franchises: {Total} on the site, {Created} collections created, {Updated} updated", franchises.Count, created, updated);
        progress.Report(100);
    }

    // Episode/movie versions merged into one item keep their other files as separate items pointing to the primary.
    private static bool IsPrimaryVersion(BaseItem item) => item is not Video { PrimaryVersionId: not null };

    private Dictionary<string, BoxSet> ExistingCollections()
    {
        var result = new Dictionary<string, BoxSet>(StringComparer.OrdinalIgnoreCase);
        var boxSets = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
        });
        foreach (var item in boxSets.OfType<BoxSet>())
        {
            if (item.TryGetProviderId(FranchiseKey, out var id))
            {
                result[id] = item;
            }
        }

        return result;
    }

    /// <summary>Set name and members in franchise order; returns true when something changed.</summary>
    private static async Task<bool> ApplyAsync(BoxSet boxSet, string name, List<BaseItem> items, CancellationToken ct)
    {
        var wanted = items.Select(i => i.Id).ToList();
        var current = boxSet.LinkedChildren.Select(c => c.ItemId ?? Guid.Empty).ToList();
        if (current.SequenceEqual(wanted) && boxSet.Name == name && boxSet.DisplayOrder == "Default")
        {
            return false;
        }

        boxSet.Name = name;

        // "Default" keeps the linked order instead of sorting by premiere date.
        boxSet.DisplayOrder = "Default";
        boxSet.LinkedChildren = items
            .Select(i => new LinkedChild { ItemId = i.Id, Type = LinkedChildType.Manual })
            .ToArray();
        await boxSet.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        return true;
    }

    private async Task EnsurePosterAsync(BoxSet boxSet, Franchise franchise, CancellationToken ct)
    {
        var url = AniLibertyClient.ImageUrl(franchise.Image?.Preview ?? franchise.Image?.Thumbnail);
        if (url is null || boxSet.HasImage(ImageType.Primary))
        {
            return;
        }

        try
        {
            // A poster saved earlier may not be registered on the item yet: register it instead of downloading again.
            var existing = string.IsNullOrEmpty(boxSet.Path) ? null
                : Directory.EnumerateFiles(boxSet.Path, "folder.*").FirstOrDefault();
            if (existing is not null)
            {
                boxSet.SetImagePath(ImageType.Primary, existing);
            }
            else
            {
                await _providers.SaveImage(boxSet, url, ImageType.Primary, null, ct).ConfigureAwait(false);
            }

            await boxSet.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AniLiberty: poster for franchise {Name} failed", franchise.Name);
        }
    }
}

/// <summary>Scheduled entry point (Dashboard → Scheduled tasks → AniLiberty).</summary>
public sealed class FranchiseTask : IScheduledTask
{
    private readonly FranchiseBuilder _builder;

    public FranchiseTask(FranchiseBuilder builder)
    {
        _builder = builder;
    }

    public string Name => "Франшизы AniLiberty";

    public string Key => "AniLibertyFranchises";

    public string Description => "Коллекции из франшиз AniLiberty: сезоны, фильмы и OVA в порядке просмотра.";

    public string Category => "AniLiberty";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _builder.RunAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks,
        };
    }
}

/// <summary>Rebuilds franchise collections after each library scan, so new seasons land in them right away.</summary>
public sealed class FranchisePostScanTask : ILibraryPostScanTask
{
    private readonly FranchiseBuilder _builder;

    public FranchisePostScanTask(FranchiseBuilder builder)
    {
        _builder = builder;
    }

    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        => _builder.RunAsync(progress, cancellationToken);
}
