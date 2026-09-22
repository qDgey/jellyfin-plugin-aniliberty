using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.AniLiberty.Providers;

/// <summary>
/// Jellyfin's parser reads leading title digits as the episode ("100-man_no_Inochi_[03]_..." → 100).
/// AniLibria files carry the real number in brackets, so correct the item before remote providers run.
/// </summary>
public sealed class EpisodeNumberFixer : ICustomMetadataProvider<Episode>, IPreRefreshProvider
{
    public string Name => Plugin.ProviderName;

    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        var update = ItemUpdateType.None;

        // Files sit directly in the release folder; without a season Jellyfin files them under "Season unknown".
        if (item.ParentIndexNumber is null && item.Series is { } series
            && string.Equals(Path.GetDirectoryName(item.Path), series.Path?.TrimEnd('/'), StringComparison.Ordinal))
        {
            item.ParentIndexNumber = 1;
            update = ItemUpdateType.MetadataEdit;
        }

        if (NameNormalizer.BracketEpisodeNumber(item.Path) is { } number && item.IndexNumber != (int)Math.Floor(number))
        {
            item.IndexNumber = (int)Math.Floor(number);
            item.IndexNumberEnd = null;
            update = ItemUpdateType.MetadataEdit;
        }

        return Task.FromResult(update);
    }
}
