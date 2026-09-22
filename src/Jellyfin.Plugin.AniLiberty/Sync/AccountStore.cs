using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Sync;

/// <summary>Last agreed state of one timecode, used for three-way merging.</summary>
public sealed class TimecodeState
{
    public double Time { get; set; }

    public bool Watched { get; set; }
}

/// <summary>AniLiberty account linked to one Jellyfin user.</summary>
public sealed class AccountLink
{
    public Guid UserId { get; set; }

    /// <summary>Stable device id presented to AniLiberty for this user's OTP logins.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();

    public string? Token { get; set; }

    public string? Nickname { get; set; }

    public DateTime? LinkedAt { get; set; }

    public DateTime? LastSyncAt { get; set; }

    public string? LastError { get; set; }

    public string? PendingCode { get; set; }

    public DateTime? PendingExpiresAt { get; set; }

    /// <summary>False until the first full merge after linking (local and remote are unioned, nothing is removed).</summary>
    public bool Merged { get; set; }

    // Snapshots of the state both sides agreed on after the last sync.
    public Dictionary<string, TimecodeState> Timecodes { get; set; } = new();

    public HashSet<long> Favorites { get; set; } = new();

    public Dictionary<long, string> Collections { get; set; } = new();

    /// <summary>Jellyfin playlist id per collection type.</summary>
    public Dictionary<string, Guid> Playlists { get; set; } = new();

    /// <summary>Release ids AniLiberty knows but the local library lacks (shown on the link page).</summary>
    public List<long> MissingFavorites { get; set; } = new();

    public bool IsLinked => !string.IsNullOrEmpty(Token);
}

/// <summary>Persists account links in the plugin data folder. Tokens never go to the (admin-visible) plugin XML config.</summary>
public sealed class AccountStore
{
    private readonly ILogger<AccountStore> _logger;
    private readonly object _sync = new();
    private Dictionary<Guid, AccountLink>? _links;

    public AccountStore(ILogger<AccountStore> logger)
    {
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "accounts.json");

    public AccountLink GetOrCreate(Guid userId)
    {
        lock (_sync)
        {
            var links = Load();
            if (!links.TryGetValue(userId, out var link))
            {
                links[userId] = link = new AccountLink { UserId = userId };
            }

            return link;
        }
    }

    public AccountLink? Get(Guid userId)
    {
        lock (_sync)
        {
            return Load().TryGetValue(userId, out var link) ? link : null;
        }
    }

    public IReadOnlyList<AccountLink> Linked()
    {
        lock (_sync)
        {
            return Load().Values.Where(l => l.IsLinked).ToList();
        }
    }

    /// <summary>Apply a change to a link and persist; callers mutate only inside this callback.</summary>
    public void Update(Guid userId, Action<AccountLink> change)
    {
        lock (_sync)
        {
            change(GetOrCreate(userId));
            Save();
        }
    }

    public void Remove(Guid userId)
    {
        lock (_sync)
        {
            if (Load().Remove(userId))
            {
                Save();
            }
        }
    }

    private Dictionary<Guid, AccountLink> Load()
    {
        if (_links is not null)
        {
            return _links;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<AccountLink>>(File.ReadAllText(FilePath)) ?? new List<AccountLink>();
                return _links = list.ToDictionary(l => l.UserId);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "AniLiberty: cannot read {File}; starting with no linked accounts", FilePath);
        }

        return _links = new Dictionary<Guid, AccountLink>();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_links!.Values.ToList()));
        File.Move(tmp, FilePath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            // Tokens are credentials: keep the file private to the jellyfin user.
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
