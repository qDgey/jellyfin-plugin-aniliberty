using System.Globalization;
using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AniLiberty.Providers;

internal static class MetadataMapper
{
    private static bool PreferEnglish => Plugin.Instance?.Configuration.PreferEnglishTitle ?? false;

    public static void Fill(BaseItem item, Match match)
    {
        if (match.Release is { } r)
        {
            FillFromRelease(item, r);
        }
        else if (match.Shiki is { } s)
        {
            FillFromShikimori(item, s);
        }
    }

    public static List<PersonInfo> People(Match match)
    {
        var people = new List<PersonInfo>();
        if (match.Release?.Members is not { } members || !(Plugin.Instance?.Configuration.ImportTeam ?? true))
        {
            return people;
        }

        foreach (var group in members.Where(m => !string.IsNullOrWhiteSpace(m.Nickname)).GroupBy(m => m.Nickname!.Trim()))
        {
            var roles = string.Join(", ", group.Select(m => m.Role?.Description).Where(d => !string.IsNullOrEmpty(d)).Distinct());
            people.Add(new PersonInfo
            {
                Name = group.Key,
                Role = roles,
                Type = Jellyfin.Data.Enums.PersonKind.Actor,
            });
        }

        return people;
    }

    public static RemoteSearchResult ToSearchResult(Match match)
    {
        var result = new RemoteSearchResult { SearchProviderName = Plugin.ProviderName };
        if (match.Release is { } r)
        {
            result.Name = Title(r);
            result.ProductionYear = r.Year;
            result.Overview = r.Description;
            result.ImageUrl = AniLibertyClient.ImageUrl(r.Poster?.Src);
            SetIds(result, r);
        }
        else if (match.Shiki is { } s)
        {
            result.Name = Title(s) + " (Shikimori)";
            result.ProductionYear = ReleaseResolver.AiredYear(s);
            result.Overview = ShikimoriClient.CleanDescription(s.Description);
            result.ImageUrl = ShikimoriClient.ImageUrl(s.Image);
            result.SetProviderId(Plugin.ShikimoriKey, s.Id.ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    public static string Title(Release r)
    {
        var ru = r.Name?.Main;
        var en = r.Name?.English;
        return (PreferEnglish ? en ?? ru : ru ?? en) ?? r.Alias ?? r.Id.ToString(CultureInfo.InvariantCulture);
    }

    public static string Title(ShikiAnime s)
    {
        return (PreferEnglish ? s.Name ?? s.Russian : NullIfEmpty(s.Russian) ?? s.Name) ?? s.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static void FillFromRelease(BaseItem item, Release r)
    {
        item.Name = Title(r);
        item.OriginalTitle = PreferEnglish ? r.Name?.Main : r.Name?.English;
        item.Overview = r.Description?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        item.ProductionYear = r.Year;
        item.OfficialRating = r.AgeRating?.Label;

        var rating = r.Shikimori?.Rating ?? r.Mal?.Rating;
        if (rating is > 0)
        {
            item.CommunityRating = (float)rating.Value;
        }

        if (r.Genres is { Count: > 0 } genres)
        {
            item.Genres = genres.Select(g => g.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToArray()!;
        }

        if (r.AverageDurationOfEpisode is > 0 && item is not Series)
        {
            item.RunTimeTicks = TimeSpan.FromMinutes(r.AverageDurationOfEpisode.Value).Ticks;
        }

        if (item is Series series)
        {
            series.Status = r.IsOngoing ? SeriesStatus.Continuing : SeriesStatus.Ended;
        }

        SetIds(item, r);
    }

    private static void FillFromShikimori(BaseItem item, ShikiAnime s)
    {
        item.Name = Title(s);
        item.OriginalTitle = PreferEnglish ? s.Russian : s.Name;
        item.Overview = ShikimoriClient.CleanDescription(s.Description);
        item.ProductionYear = ReleaseResolver.AiredYear(s);
        item.OfficialRating = s.Rating switch
        {
            "g" => "0+",
            "pg" => "6+",
            "pg_13" => "12+",
            "r" => "16+",
            "r_plus" => "18+",
            "rx" => "18+",
            _ => null,
        };

        if (DateTime.TryParse(s.AiredOn, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var aired))
        {
            item.PremiereDate = aired;
        }

        if (DateTime.TryParse(s.ReleasedOn, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ended))
        {
            item.EndDate = ended;
        }

        if (float.TryParse(s.Score, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) && score > 0)
        {
            item.CommunityRating = score;
        }

        if (s.Genres is { Count: > 0 } genres)
        {
            item.Genres = genres.Where(g => g.Kind is null or "genre" or "theme" or "demographic")
                .Select(g => NullIfEmpty(g.Russian) ?? g.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToArray()!;
        }

        if (item is Series series)
        {
            series.Status = s.Status == "ongoing" ? SeriesStatus.Continuing : SeriesStatus.Ended;
        }

        item.SetProviderId(Plugin.ShikimoriKey, s.Id.ToString(CultureInfo.InvariantCulture));
        if (s.MyAnimeListId is > 0)
        {
            item.SetProviderId(Plugin.MalKey, s.MyAnimeListId.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void SetIds(IHasProviderIds target, Release r)
    {
        target.SetProviderId(Plugin.ProviderKey, r.Id.ToString(CultureInfo.InvariantCulture));
        if (r.Shikimori?.Id is > 0)
        {
            target.SetProviderId(Plugin.ShikimoriKey, r.Shikimori.Id.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (r.Mal?.Id is > 0)
        {
            target.SetProviderId(Plugin.MalKey, r.Mal.Id.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
