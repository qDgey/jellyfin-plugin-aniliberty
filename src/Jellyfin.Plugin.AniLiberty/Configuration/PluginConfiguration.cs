using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AniLiberty.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>AniLiberty site root; API lives under /api/v1 and images are relative to it.</summary>
    public string SiteUrl { get; set; } = "https://aniliberty.top";

    /// <summary>Shikimori site root, used when a release isn't found on AniLiberty.</summary>
    public string ShikimoriUrl { get; set; } = "https://shikimori.rip";

    public bool EnableShikimoriFallback { get; set; } = true;

    /// <summary>Use the English/romaji name as the title instead of the Russian one.</summary>
    public bool PreferEnglishTitle { get; set; }

    /// <summary>Add AniLiberty voice/timing team members as people.</summary>
    public bool ImportTeam { get; set; } = true;

    /// <summary>How often the torrent-name index is re-downloaded.</summary>
    public int TorrentIndexRefreshHours { get; set; } = 24;

    /// <summary>Default interval of the account sync task (users can still change the trigger in Scheduled tasks).</summary>
    public int AccountSyncIntervalMinutes { get; set; } = 15;

    /// <summary>Which twin of a release (AVC or HEVC folder) goes into the collection playlists.</summary>
    public bool PreferHevcInPlaylists { get; set; } = true;
}
