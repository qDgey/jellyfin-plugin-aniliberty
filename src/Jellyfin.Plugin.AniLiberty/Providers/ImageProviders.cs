using System.Globalization;
using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniLiberty.Providers;

public sealed class TitleImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly AniLibertyClient _client;
    private readonly ShikimoriClient _shikimori;
    private readonly IHttpClientFactory _httpFactory;

    public TitleImageProvider(AniLibertyClient client, ShikimoriClient shikimori, IHttpClientFactory httpFactory)
    {
        _client = client;
        _shikimori = shikimori;
        _httpFactory = httpFactory;
    }

    public string Name => Plugin.ProviderName;

    public int Order => -1;

    public bool Supports(BaseItem item) => item is Series or Movie;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var images = new List<RemoteImageInfo>();

        if (TryId(item, Plugin.ProviderKey, out var releaseId)
            && await _client.GetReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false) is { } release
            && AniLibertyClient.ImageUrl(release.Poster?.Src) is { } poster)
        {
            images.Add(new RemoteImageInfo { ProviderName = Name, Url = poster, Type = ImageType.Primary, Language = "ru" });
        }

        // Shikimori poster only for titles AniLiberty removed: it's throttled to ~1.4 rps, too slow for every title.
        if (images.Count == 0
            && (Plugin.Instance?.Configuration.EnableShikimoriFallback ?? true)
            && TryId(item, Plugin.ShikimoriKey, out var shikiId)
            && await _shikimori.GetAsync(shikiId, cancellationToken).ConfigureAwait(false) is { } shiki
            && ShikimoriClient.ImageUrl(shiki.Image) is { } shikiPoster)
        {
            images.Add(new RemoteImageInfo { ProviderName = Name, Url = shikiPoster, Type = ImageType.Primary });
        }

        return images;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return Http.GetImageAsync(_httpFactory, url, cancellationToken);
    }

    private static bool TryId(BaseItem item, string key, out long id)
    {
        id = 0;
        return item.TryGetProviderId(key, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }
}

public sealed class EpisodeImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly AniLibertyClient _client;
    private readonly IHttpClientFactory _httpFactory;

    public EpisodeImageProvider(AniLibertyClient client, IHttpClientFactory httpFactory)
    {
        _client = client;
        _httpFactory = httpFactory;
    }

    public string Name => Plugin.ProviderName;

    public int Order => -1;

    public bool Supports(BaseItem item) => item is Episode;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode episode
            || episode.Series is not { } series
            || !series.TryGetProviderId(Plugin.ProviderKey, out var idText)
            || !long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var releaseId))
        {
            return Enumerable.Empty<RemoteImageInfo>();
        }

        var number = NameNormalizer.BracketEpisodeNumber(episode.Path) ?? episode.IndexNumber;
        var release = await _client.GetReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false);
        var source = release?.Episodes?.FirstOrDefault(e => e.Ordinal is { } o && number is { } n && Math.Abs(o - n) < 0.01);
        var url = AniLibertyClient.ImageUrl(source?.Preview?.Src);
        return url is null
            ? Enumerable.Empty<RemoteImageInfo>()
            : new[] { new RemoteImageInfo { ProviderName = Name, Url = url, Type = ImageType.Primary } };
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return Http.GetImageAsync(_httpFactory, url, cancellationToken);
    }
}
