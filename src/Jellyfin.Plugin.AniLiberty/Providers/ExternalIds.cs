using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniLiberty.Providers;

public sealed class AniLibertyExternalId : IExternalId
{
    public string ProviderName => Plugin.ProviderName;

    public string Key => Plugin.ProviderKey;

    public ExternalIdMediaType? Type => null;

    public bool Supports(IHasProviderIds item) => item is Series or Movie;
}

public sealed class ShikimoriExternalId : IExternalId
{
    public string ProviderName => "Shikimori";

    public string Key => Plugin.ShikimoriKey;

    public ExternalIdMediaType? Type => null;

    public bool Supports(IHasProviderIds item) => item is Series or Movie;
}

public sealed class ExternalUrlProvider : IExternalUrlProvider
{
    public string Name => Plugin.ProviderName;

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is not (Series or Movie))
        {
            yield break;
        }

        if (item.TryGetProviderId(Plugin.ProviderKey, out var id))
        {
            yield return AniLibertyClient.ReleasePageUrl(id);
        }

        if (item.TryGetProviderId(Plugin.ShikimoriKey, out var shiki))
        {
            yield return ShikimoriClient.SiteUrl + "/animes/" + shiki;
        }
    }
}
