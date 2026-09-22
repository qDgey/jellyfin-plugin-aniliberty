using System.Globalization;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Api;

/// <summary>A matched title: an AniLiberty release, or a Shikimori entry when AniLiberty has none.</summary>
public sealed record Match(Release? Release, ShikiAnime? Shiki);

public sealed class ReleaseResolver
{
    private static readonly string[] MovieKinds = { "movie" };

    // Not releases anyone has in a library: trailers and music videos Shikimori lists next to the real entries.
    private static readonly string[] NotContentKinds = { "pv", "cm", "music" };

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
            // Same-words matches on either site beat looser ones: AniLiberty search happily returns
            // spin-offs and sequels ("Shingeki! Kyojin Chuugakkou" for "Shingeki no Kyojin").
            var alHits = await _aniliberty.SearchAsync(query, ct).ConfigureAwait(false);
            IReadOnlyList<ShikiAnime> shHits = fallback ? await ShikimoriCandidatesAsync(query, ct).ConfigureAwait(false) : Array.Empty<ShikiAnime>();
            Func<Release, bool> alKind = r => isMovie == string.Equals(r.Type?.Value, "MOVIE", StringComparison.OrdinalIgnoreCase);
            Func<Release, bool> alYear = r => info.Year is null || r.Year == info.Year;
            Func<ShikiAnime, bool> shKind = a => isMovie == MovieKinds.Contains(a.Kind ?? string.Empty);
            shHits = shHits.Where(a => !NotContentKinds.Contains(a.Kind ?? string.Empty)).ToList();
            Func<ShikiAnime, bool> shYear = a => info.Year is null || AiredYear(a) == info.Year;

            foreach (var loose in new[] { false, true })
            {
                if (Pick(alHits, query, Titles, alKind, alYear, loose, trustSingleHit: false) is { } al
                    && await _aniliberty.GetReleaseAsync(al.Id, ct).ConfigureAwait(false) is { } release)
                {
                    _logger.LogDebug("AniLiberty: {Query} matched by search → release {Id}", query, release.Id);
                    return new Match(release, null);
                }

                if (Pick(shHits, query, Titles, shKind, shYear, loose, trustSingleHit: true) is { } sh)
                {
                    _logger.LogDebug("AniLiberty: {Query} matched on Shikimori → {Id}", query, sh.Id);
                    return new Match(null, await _shikimori.GetAsync(sh.Id, ct).ConfigureAwait(false) ?? sh);
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
            // Movie tree entries are symlinks to the torrent files; the target's name is the torrent name.
            if (LinkTarget(info.Path) is { } target)
            {
                if (seen.Add(Path.GetFileName(target)))
                {
                    yield return Path.GetFileName(target);
                }

                // Single-file torrent packed in a folder: the folder is the torrent. Grouping folders
                // ("2016", "Movies") are skipped by requiring a release-looking name.
                if (Path.GetFileName(Path.GetDirectoryName(target)) is { Length: > 0 } folder
                    && (folder.Contains("anili", StringComparison.OrdinalIgnoreCase) || folder.Contains('['))
                    && seen.Add(folder))
                {
                    yield return folder;
                }
            }

            // Series tree folders merge several torrent folders; their episode symlinks point into them.
            foreach (var torrentFolder in TorrentFoldersBehind(info.Path))
            {
                if (seen.Add(torrentFolder))
                {
                    yield return torrentFolder;
                }
            }

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

    /// <summary>Names of the torrent folders that a symlinked series folder (or its Season subfolder) points into.</summary>
    private static List<string> TorrentFoldersBehind(string path)
    {
        if (!Directory.Exists(path))
        {
            return new List<string>();
        }

        try
        {
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
                .Select(LinkTarget)
                .OfType<string>()
                .Select(t => Path.GetFileName(Path.GetDirectoryName(t)))
                .OfType<string>()
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Shikimori's search results only carry the main title, so a romaji-only database entry never matches an
    /// English folder name. Load the details of the first few hits, which include english titles and synonyms.
    /// </summary>
    private async Task<IReadOnlyList<ShikiAnime>> ShikimoriCandidatesAsync(string query, CancellationToken ct)
    {
        var hits = await _shikimori.SearchAsync(query, ct).ConfigureAwait(false);
        var result = new List<ShikiAnime>(hits.Count);
        foreach (var hit in hits.Take(5))
        {
            result.Add(await _shikimori.GetAsync(hit.Id, ct).ConfigureAwait(false) ?? hit);
        }

        result.AddRange(hits.Skip(5));

        // An abbreviation the databases don't know ("Watatabe") still resolves when the site is sure.
        if (result.Count == 1)
        {
            _logger.LogDebug("AniLiberty: {Query} has a single Shikimori hit {Id}", query, result[0].Id);
        }

        return result;
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Best search hit for a file-derived query, most to least certain: same words, then the query's words
    /// being a subset of the title ("Fairy Tail Dragon Cry" in "Fairy Tail Movie 2: Dragon Cry").
    /// Within each tier the expected kind (movie vs series) and year are preferred, not required:
    /// the movie library also holds OVAs and specials.
    /// </summary>
    internal static T? Pick<T>(
        IReadOnlyList<T> hits,
        string query,
        Func<T, IEnumerable<string>> titles,
        Func<T, bool> kindOk,
        Func<T, bool> yearOk,
        bool loose,
        bool trustSingleHit)
        where T : class
    {
        var q = NameNormalizer.Tokens(query);
        if (q.Count == 0 || hits.Count == 0)
        {
            return null;
        }

        IEnumerable<T> candidates;
        if (!loose)
        {
            // Closest first: a query without a season number should land on the season without one.
            candidates = hits
                .Where(x => titles(x).Any(t => NameNormalizer.SameTitle(t, query)))
                .OrderBy(x => titles(x).Select(t => Math.Abs(NameNormalizer.Tokens(t).Count - q.Count)).DefaultIfEmpty(0).Min());
        }
        else if (trustSingleHit && hits.Count == 1)
        {
            // The database knows exactly one title for this query: trust it (AniLiberty abbreviates names).
            candidates = hits;
        }
        else
        {
            // A one-word query ("Charlotte") is too weak to accept a longer title.
            if (q.Count < 2)
            {
                return null;
            }

            // Fewest extra words first, so "Ghost in the Shell" prefers the film over "... Arise".
            candidates = hits
                .Select(x => (Item: x, Extra: titles(x).Select(NameNormalizer.Tokens).Where(q.IsSubsetOf).Select(t => t.Count - q.Count).DefaultIfEmpty(-1).Max()))
                .Where(c => c.Extra >= 0)
                .OrderBy(c => c.Extra)
                .Select(c => c.Item);
        }

        var list = candidates.ToList();
        return list.FirstOrDefault(x => kindOk(x) && yearOk(x)) ?? list.FirstOrDefault(kindOk) ?? list.FirstOrDefault();
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
