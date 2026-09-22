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
        if (NameNormalizer.BracketEpisodeNumber(item.Path) is not { } number)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var index = (int)Math.Floor(number);
        if (item.IndexNumber == index)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        item.IndexNumber = index;
        item.IndexNumberEnd = null;
        item.ParentIndexNumber ??= 1;
        return Task.FromResult(ItemUpdateType.MetadataEdit);
    }
}
