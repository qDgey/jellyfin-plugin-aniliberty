using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AniLiberty.Api;

public static partial class NameNormalizer
{
    [GeneratedRegex(@"\.(mkv|mp4|avi|m4v|ts|webm)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoExtension();

    // Everything from the group tag on is release info: "Title - AniLibria.TV [WEBRip 1080p]", "Title_[AniLibria_TV]_[...]".
    [GeneratedRegex(@"\s-?\s*\[?\s*anili(?:bria|berty)", RegexOptions.IgnoreCase)]
    private static partial Regex GroupTag();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^(?:s|tv|season)?(\d{1,2})(?:st|nd|rd|th)?$")]
    private static partial Regex SequelNumber();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    // AniLibria episode files: "86_Eighty_Six_[01]_[AniLibria_TV]_[WEBRip_1080p].mkv", "Title_[12.5]_[...]".
    [GeneratedRegex(@"\[(\d{1,4}(?:\.\d)?)(?:v\d)?(?:[ _]END)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketEpisode();

    [GeneratedRegex(@"(?:^|[\s_\-.])(?:ep?|episode|серия)?\s*(\d{1,4})(?:v\d)?(?=[\s_\-.\[]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LooseEpisode();

    /// <summary>Key for exact lookups of folder/file names against torrent names.</summary>
    public static string Exact(string name) => name.Trim().ToLowerInvariant();

    /// <summary>Title with release info stripped, lower-case, punctuation collapsed.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var s = VideoExtension().Replace(name.Trim(), string.Empty).Replace('_', ' ');
        s = GroupTag().Split(s)[0];
        s = Brackets().Replace(s, " ");
        s = RemoveDiacritics(s);
        return NonWord().Replace(s.ToLowerInvariant(), " ").Trim();
    }

    // Words that differ between AniLiberty file names and database titles without changing the title.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "no", "movie", "film", "gekijouban", "gekijououban", "ova", "ona", "oad", "special", "specials", "tv", "bd", "season",

        // Release/quality words (see QualityWords).
        "bdrip", "webrip", "hdtvrip", "dvdrip", "webdl", "web", "dl", "rip", "hdtv", "dvd", "blu", "ray",
        "hevc", "avc", "x264", "x265", "h264", "h265", "10bit", "hdr", "lq",
    };

    /// <summary>Release info some folders spell inline instead of in brackets ("Shingeki_no_Kyojin_BD-Rip_720p").</summary>
    private static readonly HashSet<string> QualityWords = new(StringComparer.Ordinal)
    {
        "bdrip", "webrip", "hdtvrip", "dvdrip", "webdl", "web", "dl", "rip", "hdtv", "dvd", "blu", "ray",
        "hevc", "avc", "x264", "x265", "h264", "h265", "10bit", "hdr", "lq", "bd",
    };

    // "Shiguang Dailiren II" is season 2.
    private static readonly Dictionary<string, string> RomanNumbers = new(StringComparer.Ordinal)
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8",
    };

    /// <summary>Significant words of a title, in order: stop words dropped, sequel numbers normalized.</summary>
    private static string[] Words(string? name)
    {
        // "S2", "TV2", "2nd Season", "Season 2", "II" all mean sequel number 2.
        var words = Clean(name).Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => RomanNumbers.TryGetValue(w, out var digit) ? digit : w)
            .Select(w => SequelNumber().Match(w) is { Success: true } m ? m.Groups[1].Value.TrimStart('0') : w)
            .Where(w => w.Length > 0)
            .ToArray();
        var significant = words.Where(w => !StopWords.Contains(w)).ToArray();
        return significant.Length > 0 ? significant : words;
    }

    /// <summary>Significant words of a title for order-insensitive comparison.</summary>
    public static HashSet<string> Tokens(string? name) => new(Words(name), StringComparer.Ordinal);

    /// <summary>
    /// True when two titles are the same, also when a database spells the words differently than AniLiberty
    /// ("Watashi ga Motete Dou Sunda" = "Watashi ga Motete Dousunda",
    /// "Dai Dai Dai Dai Daisuki" = "Daidaidaidaidaisuki").
    /// </summary>
    public static bool SameTitle(string? a, string? b)
    {
        return Tokens(a).SetEquals(Tokens(b)) || (Glued(a).Length > 0 && Glued(a) == Glued(b));
    }

    // Word boundaries differ between databases ("Daidaidaidaidaisuki" vs "Dai Dai Dai Dai Daisuki"),
    // so compare the significant words glued together.
    private static string Glued(string? name) => string.Concat(Words(name));

    /// <summary>Human-readable search query derived from a folder/file name.</summary>
    public static string SearchQuery(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var s = VideoExtension().Replace(name.Trim(), string.Empty).Replace('_', ' ');
        s = GroupTag().Split(s)[0];
        s = Brackets().Replace(s, " ");

        // Searching for the release info finds nothing, so drop it — but keep ordinary words like "no".
        var words = Regex.Split(s, @"\s+")
            .Where(w => w.Length > 0 && !IsQuality(NonWord().Replace(w.ToLowerInvariant(), " ").Trim()))
            .ToArray();
        return string.Join(' ', words.Length > 0 ? words : Regex.Split(s, @"\s+")).Trim(' ', '-', '.');
    }

    private static bool IsQuality(string word)
    {
        return QualityWords.Contains(word)
               || Regex.IsMatch(word, @"^\d{3,4}[pi]$", RegexOptions.IgnoreCase)
               || word.Split(' ').All(w => w.Length > 0 && QualityWords.Contains(w));
    }

    /// <summary>Episode number from the AniLiberty "[07]" tag only; null when the file doesn't follow that convention.</summary>
    public static double? BracketEpisodeNumber(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var m = BracketEpisode().Match(Path.GetFileNameWithoutExtension(fileName));
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public static double? EpisodeNumber(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        if (BracketEpisodeNumber(fileName) is { } bracketed)
        {
            return bracketed;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);

        // Fall back to the last standalone number that isn't a resolution/codec.
        var cleaned = Regex.Replace(name, @"\d{3,4}p|[xh]\.?26[45]|10bit|\[[^\]]*\]", " ", RegexOptions.IgnoreCase);
        var loose = LooseEpisode().Matches(cleaned);
        return loose.Count > 0 ? double.Parse(loose[^1].Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string RemoveDiacritics(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
