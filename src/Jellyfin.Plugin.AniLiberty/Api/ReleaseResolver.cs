using System.Globalization;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Api;

/// <summary>A matched title: an AniLiberty release, or a Shikimori entry when AniLiberty has none.</summary>
public sealed record Match(Release? Release, ShikiAnime? Shiki);

public sealed class ReleaseResolver
{
    private static readonly string[] MovieKinds = { "movie" };

    private readonly AniLibertyClient _aniliberty;
    private readonly ShikimoriClient _shikimori;
    private readonly ILogger<ReleaseResolver> _logger;

    public ReleaseResolver(AniLibertyClient aniliberty, ShikimoriClient shikimori, ILogger<ReleaseResolver> logger)
    {
        _aniliberty = aniliberty;
        _shikimori = shikimori;
        _logger = logger;
    }

    public async Task<Match?> ResolveAsync(ItemLookupInfo info, bool isMovie, CancellationToken ct)
    {
        if (TryGetId(info.ProviderIds, Plugin.ProviderKey, out var releaseId))
        {
            var byId = await _aniliberty.GetReleaseAsync(releaseId, ct).ConfigureAwait(false);
            if (byId is not null)
            {
                return new Match(byId, null);
            }
        }

        var names = CandidateNames(info).ToList();

        foreach (var name in names)
        {
            var id = await _aniliberty.FindByTorrentNameAsync(name, ct).ConfigureAwait(false);
            if (id is { } found && await _aniliberty.GetReleaseAsync(found, ct).ConfigureAwait(false) is { } release)
            {
                _logger.LogDebug("AniLiberty: {Name} matched torrent name → release {Id}", name, found);
                return new Match(release, null);
            }
        }

        var fallback = Plugin.Instance?.Configuration.EnableShikimoriFallback ?? true;
        if (fallback && TryGetId(info.ProviderIds, Plugin.ShikimoriKey, out var shikiId))
        {
            var shiki = await _shikimori.GetAsync(shikiId, ct).ConfigureAwait(false);
            if (shiki is not null)
            {
                return new Match(null, shiki);
            }
        }

        foreach (var query in names.Select(NameNormalizer.SearchQuery).Where(q => q.Length > 0).Distinct())
        {
            var release = await SearchAniLibertyAsync(query, info.Year, ct).ConfigureAwait(false);
            if (release is not null)
            {
                _logger.LogDebug("AniLiberty: {Query} matched by search → release {Id}", query, release.Id);
                return new Match(release, null);
            }

            if (fallback)
            {
                var shiki = await SearchShikimoriAsync(query, info.Year, isMovie, ct).ConfigureAwait(false);
                if (shiki is not null)
                {
                    _logger.LogDebug("AniLiberty: {Query} matched on Shikimori → {Id}", query, shiki.Id);
                    return new Match(null, await _shikimori.GetAsync(shiki.Id, ct).ConfigureAwait(false) ?? shiki);
                }
            }
        }

        _logger.LogInformation("AniLiberty: no match for {Names}", string.Join(" | ", names));
        return null;
    }

    public async Task<IReadOnlyList<Match>> SearchAsync(ItemLookupInfo info, bool isMovie, CancellationToken ct)
    {
        var results = new List<Match>();
        var direct = await ResolveAsync(info, isMovie, ct).ConfigureAwait(false);
        if (direct is not null)
        {
            results.Add(direct);
        }

        var query = NameNormalizer.SearchQuery(info.Name);
        foreach (var r in await _aniliberty.SearchAsync(query, ct).ConfigureAwait(false))
        {
            if (results.All(m => m.Release?.Id != r.Id))
            {
                results.Add(new Match(r, null));
            }
        }

        if (Plugin.Instance?.Configuration.EnableShikimoriFallback ?? true)
        {
            foreach (var s in await _shikimori.SearchAsync(query, ct).ConfigureAwait(false))
            {
                if (results.All(m => m.Shiki?.Id != s.Id && m.Release?.Shikimori?.Id != s.Id))
                {
                    results.Add(new Match(null, s));
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> CandidateNames(ItemLookupInfo info)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(info.Path))
        {
            var trimmed = info.Path.TrimEnd('/', '\\');
            var file = Path.GetFileName(trimmed);
            if (seen.Add(file))
            {
                yield return file;
            }

            // Movies inside their own folder: the folder is the torrent.
            if (Path.HasExtension(file) && Path.GetFileName(Path.GetDirectoryName(trimmed)) is { Length: > 0 } dir && seen.Add(dir))
            {
                yield return dir;
            }
        }

        if (!string.IsNullOrEmpty(info.Name) && seen.Add(info.Name))
        {
            yield return info.Name;
        }
    }

    private async Task<Release?> SearchAniLibertyAsync(string query, int? year, CancellationToken ct)
    {
        var key = NameNormalizer.Clean(query);
        var hits = (await _aniliberty.SearchAsync(query, ct).ConfigureAwait(false))
            .Where(r => Titles(r).Any(t => NameNormalizer.Clean(t) == key))
            .ToList();
        var best = hits.FirstOrDefault(r => year is null || r.Year == year) ?? hits.FirstOrDefault();
        return best is null ? null : await _aniliberty.GetReleaseAsync(best.Id, ct).ConfigureAwait(false);
    }

    private async Task<ShikiAnime?> SearchShikimoriAsync(string query, int? year, bool isMovie, CancellationToken ct)
    {
        var key = NameNormalizer.Clean(query);
        var hits = await _shikimori.SearchAsync(query, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return null;
        }

        bool KindOk(ShikiAnime a) => isMovie == MovieKinds.Contains(a.Kind ?? string.Empty);
        bool YearOk(ShikiAnime a) => year is null || AiredYear(a) == year;
        bool Exact(ShikiAnime a) => Titles(a).Any(t => NameNormalizer.Clean(t) == key);

        return hits.FirstOrDefault(a => Exact(a) && KindOk(a) && YearOk(a))
               ?? hits.FirstOrDefault(a => Exact(a) && KindOk(a))
               ?? hits.FirstOrDefault(Exact)
               // Shikimori's own ranking is good; only trust it when the title at least starts with the query.
               ?? hits.FirstOrDefault(a => KindOk(a) && Titles(a).Any(t => NameNormalizer.Clean(t).StartsWith(key, StringComparison.Ordinal)));
    }

    public static IEnumerable<string> Titles(Release r)
    {
        return new[] { r.Name?.English, r.Name?.Alternative, r.Name?.Main, r.Alias?.Replace('-', ' ') }
            .Where(t => !string.IsNullOrWhiteSpace(t))!;
    }

    public static IEnumerable<string> Titles(ShikiAnime a)
    {
        return new[] { a.Name, a.Russian }
            .Concat(a.English ?? new List<string?>())
            .Concat(a.Synonyms ?? new List<string?>())
            .Where(t => !string.IsNullOrWhiteSpace(t))!;
    }

    public static int? AiredYear(ShikiAnime a)
    {
        return DateTime.TryParse(a.AiredOn, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.Year : null;
    }

    private static bool TryGetId(Dictionary<string, string>? ids, string key, out long id)
    {
        id = 0;
        return ids is not null && ids.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }
}
