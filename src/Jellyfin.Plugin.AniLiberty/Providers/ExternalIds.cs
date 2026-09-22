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

// Jellyfin labels every link with its provider's Name, so each site needs its own provider.

public sealed class MyAnimeListExternalId : IExternalId
{
    public string ProviderName => "MyAnimeList";

    public string Key => Plugin.MalKey;

    public ExternalIdMediaType? Type => null;

    public bool Supports(IHasProviderIds item) => item is Series or Movie;
}

public sealed class ExternalUrlProvider : IExternalUrlProvider
{
    public string Name => Plugin.ProviderName;

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Series or Movie && item.TryGetProviderId(Plugin.ProviderKey, out var id))
        {
            yield return AniLibertyClient.ReleasePageUrl(id);
        }
    }
}

public sealed class ShikimoriExternalUrlProvider : IExternalUrlProvider
{
    public string Name => "Shikimori";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Series or Movie && item.TryGetProviderId(Plugin.ShikimoriKey, out var id))
        {
            yield return ShikimoriClient.SiteUrl + "/animes/" + id;
        }
    }
}

public sealed class MyAnimeListExternalUrlProvider : IExternalUrlProvider
{
    public string Name => "MyAnimeList";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Series or Movie && item.TryGetProviderId(Plugin.MalKey, out var id))
        {
            yield return "https://myanimelist.net/anime/" + id;
        }
    }
}
