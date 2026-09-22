using System.Globalization;
using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniLiberty.Providers;

public sealed class EpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly AniLibertyClient _client;
    private readonly IHttpClientFactory _httpFactory;

    public EpisodeProvider(AniLibertyClient client, IHttpClientFactory httpFactory)
    {
        _client = client;
        _httpFactory = httpFactory;
    }

    public string Name => Plugin.ProviderName;

    public int Order => -1;

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
    }

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();
        var number = EpisodeNumber(info);
        var hasSeries = info.SeriesProviderIds.ContainsKey(Plugin.ProviderKey) || info.SeriesProviderIds.ContainsKey(Plugin.ShikimoriKey);
        if (number is null || !hasSeries)
        {
            return result;
        }

        var episode = new Episode
        {
            IndexNumber = (int)Math.Floor(number.Value),
            ParentIndexNumber = info.ParentIndexNumber ?? 1,
            Name = "Серия " + number.Value.ToString(CultureInfo.InvariantCulture),
        };

        var source = await FindAsync(info, number.Value, cancellationToken).ConfigureAwait(false);
        if (source is not null)
        {
            var name = (Plugin.Instance?.Configuration.PreferEnglishTitle ?? false)
                ? source.NameEnglish ?? source.Name
                : source.Name ?? source.NameEnglish;
            if (!string.IsNullOrWhiteSpace(name))
            {
                episode.Name = name.Trim();
            }

            if (source.Duration is > 0)
            {
                episode.RunTimeTicks = TimeSpan.FromSeconds(source.Duration.Value).Ticks;
            }

            if (!string.IsNullOrEmpty(source.Id))
            {
                episode.SetProviderId(Plugin.ProviderKey, source.Id);
            }
        }

        result.Item = episode;
        result.HasMetadata = true;
        result.Provider = Plugin.ProviderName;
        result.ResultLanguage = "ru";
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return Http.GetImageAsync(_httpFactory, url, cancellationToken);
    }

    internal async Task<ReleaseEpisode?> FindAsync(EpisodeInfo info, double number, CancellationToken ct)
    {
        if (!info.SeriesProviderIds.TryGetValue(Plugin.ProviderKey, out var idText)
            || !long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var releaseId))
        {
            return null;
        }

        var release = await _client.GetReleaseAsync(releaseId, ct).ConfigureAwait(false);
        return release?.Episodes?.FirstOrDefault(e => e.Ordinal is { } o && Math.Abs(o - number) < 0.01);
    }

    /// <summary>
    /// AniLibria files are "Title_[07]_[AniLibria_TV]_[WEBRip_1080p].mkv"; the bracketed number is authoritative,
    /// Jellyfin's generic parser can pick up digits from the title (e.g. "86_Eighty_Six").
    /// </summary>
    internal static double? EpisodeNumber(EpisodeInfo info)
    {
        return NameNormalizer.BracketEpisodeNumber(info.Path) ?? info.IndexNumber ?? NameNormalizer.EpisodeNumber(info.Path);
    }
}
