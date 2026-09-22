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
    };

    /// <summary>Significant words of a title for order-insensitive comparison.</summary>
    public static HashSet<string> Tokens(string? name)
    {
        // "S2", "TV2", "2nd Season", "Season 2" all mean sequel number 2.
        var words = Clean(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => SequelNumber().Match(w) is { Success: true } m ? m.Groups[1].Value.TrimStart('0') : w)
            .Where(w => w.Length > 0)
            .ToArray();
        var set = new HashSet<string>(words.Where(w => !StopWords.Contains(w)), StringComparer.Ordinal);
        return set.Count > 0 ? set : new HashSet<string>(words, StringComparer.Ordinal);
    }

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
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.');
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
