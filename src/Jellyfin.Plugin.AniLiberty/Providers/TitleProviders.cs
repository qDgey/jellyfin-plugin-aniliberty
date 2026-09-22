using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniLiberty.Providers;

public abstract class TitleProviderBase<TItem, TLookup> : IRemoteMetadataProvider<TItem, TLookup>, IHasOrder
    where TItem : BaseItem, IHasLookupInfo<TLookup>, new()
    where TLookup : ItemLookupInfo, new()
{
    private readonly ReleaseResolver _resolver;
    private readonly IHttpClientFactory _httpFactory;

    protected TitleProviderBase(ReleaseResolver resolver, IHttpClientFactory httpFactory)
    {
        _resolver = resolver;
        _httpFactory = httpFactory;
    }

    public string Name => Plugin.ProviderName;

    // Run before TMDb/TVDb so AniLiberty data wins for this library.
    public int Order => -1;

    protected abstract bool IsMovie { get; }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TLookup searchInfo, CancellationToken cancellationToken)
    {
        var matches = await _resolver.SearchAsync(searchInfo, IsMovie, cancellationToken).ConfigureAwait(false);
        return matches.Select(MetadataMapper.ToSearchResult);
    }

    public async Task<MetadataResult<TItem>> GetMetadata(TLookup info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<TItem>();
        var match = await _resolver.ResolveAsync(info, IsMovie, cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return result;
        }

        var item = new TItem();
        MetadataMapper.Fill(item, match);
        result.Item = item;
        result.HasMetadata = true;
        result.Provider = Plugin.ProviderName;
        result.ResultLanguage = "ru";
        foreach (var person in MetadataMapper.People(match))
        {
            result.AddPerson(person);
        }

        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return Http.GetImageAsync(_httpFactory, url, cancellationToken);
    }
}

public sealed class SeriesProvider : TitleProviderBase<Series, SeriesInfo>
{
    public SeriesProvider(ReleaseResolver resolver, IHttpClientFactory httpFactory)
        : base(resolver, httpFactory)
    {
    }

    protected override bool IsMovie => false;
}

public sealed class MovieProvider : TitleProviderBase<Movie, MovieInfo>
{
    public MovieProvider(ReleaseResolver resolver, IHttpClientFactory httpFactory)
        : base(resolver, httpFactory)
    {
    }

    protected override bool IsMovie => true;
}
