using System.Globalization;
using System.Reflection;
using Jellyfin.Plugin.AniLiberty.Api;
using Jellyfin.Plugin.AniLiberty.Sync;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Web;

/// <summary>
/// Per-user AniLiberty account linking. The page is served anonymously (it's static); every API call runs
/// as the Jellyfin user whose token the page sends, so users only ever see and change their own link.
/// </summary>
[ApiController]
[Route("AniLiberty")]
public sealed class AccountController : ControllerBase
{
    private readonly IAuthorizationContext _auth;
    private readonly AccountStore _store;
    private readonly AccountClient _account;
    private readonly AniListClient _anilist;
    private readonly SyncService _sync;
    private readonly LibraryIndex _index;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IAuthorizationContext auth,
        AccountStore store,
        AccountClient account,
        AniListClient anilist,
        SyncService sync,
        LibraryIndex index,
        ILogger<AccountController> logger)
    {
        _auth = auth;
        _store = store;
        _account = account;
        _anilist = anilist;
        _sync = sync;
        _index = index;
        _logger = logger;
    }

    [HttpGet("Link")]
    [AllowAnonymous]
    [Produces("text/html")]
    public ContentResult LinkPage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.AniLiberty.Web.link.html")!;
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    }

    [HttpGet("Account")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> GetStatus()
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        return Status(_store.Get(userId));
    }

    /// <summary>Start linking: get a one-time code the user enters on the AniLiberty site.</summary>
    [HttpPost("Account/Otp")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> RequestCode(CancellationToken ct)
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        var link = _store.GetOrCreate(userId);
        var ticket = await _account.RequestOtpAsync(link.DeviceId, ct).ConfigureAwait(false);
        _store.Update(userId, l =>
        {
            l.PendingCode = ticket.Code;
            l.PendingExpiresAt = (ticket.ExpiresAt?.UtcDateTime) ?? DateTime.UtcNow.AddSeconds(ticket.RemainingSeconds);
        });
        return Status(_store.Get(userId));
    }

    /// <summary>Polled by the page while the code is shown; completes the link once the user accepted it on the site.</summary>
    [HttpPost("Account/Otp/Check")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> CheckCode(CancellationToken ct)
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        var link = _store.Get(userId);
        if (link?.PendingCode is not { } code || link.PendingExpiresAt < DateTime.UtcNow)
        {
            return Status(link);
        }

        var token = await _account.TryLoginWithOtpAsync(code, link.DeviceId, ct).ConfigureAwait(false);
        if (token is null)
        {
            return Status(link);
        }

        string? nickname = null;
        try
        {
            nickname = await _account.GetNicknameAsync(token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or AccountUnauthorizedException)
        {
            _logger.LogWarning(ex, "AniLiberty: profile lookup failed after linking");
        }

        _store.Update(userId, l =>
        {
            l.Token = token;
            l.Nickname = nickname;
            l.LinkedAt = DateTime.UtcNow;
            l.PendingCode = null;
            l.PendingExpiresAt = null;
            l.LastError = null;

            // A new account starts with a fresh union merge (history is imported, nothing removed).
            l.Merged = false;
            l.Timecodes.Clear();
            l.Favorites.Clear();
            l.Collections.Clear();
        });
        _logger.LogInformation("AniLiberty: user {User} linked account {Nickname}", userId, nickname);

        StartSync(userId);
        return Status(_store.Get(userId));
    }

    /// <summary>Finish AniList linking: the page sends the token AniList put in the url fragment.</summary>
    [HttpPost("Account/AniList")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> LinkAniList([FromBody] AniListTokenRequest request, CancellationToken ct)
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request?.Token))
        {
            return BadRequest();
        }

        var viewer = await _anilist.GetViewerAsync(request.Token, ct).ConfigureAwait(false);
        if (viewer is null)
        {
            return BadRequest();
        }

        _store.Update(userId, l =>
        {
            l.AniListToken = request.Token;
            l.AniListUserId = viewer.Id;
            l.AniListName = viewer.Name;
            l.AniListLinkedAt = DateTime.UtcNow;
            l.AniListMerged = false;
            l.AniList.Clear();
            l.LastError = null;
        });
        _logger.LogInformation("AniList: user {User} linked account {Name}", userId, viewer.Name);

        StartSync(userId);
        return Status(_store.Get(userId));
    }

    [HttpDelete("Account/AniList")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> UnlinkAniList()
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        _store.Update(userId, l =>
        {
            l.AniListToken = null;
            l.AniListUserId = null;
            l.AniListName = null;
            l.AniListLinkedAt = null;
            l.AniListMerged = false;
            l.AniList.Clear();
        });
        return Status(_store.Get(userId));
    }

    /// <summary>
    /// What the browser found out about favourites the server itself cannot fetch: AniLiberty hides some
    /// releases from us (geo or otherwise), so the page looks them up and reports the titles back. A title
    /// the library already has under the same MyAnimeList id stops being "missing".
    /// </summary>
    [HttpPost("Account/Favorites/Resolved")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> ResolveFavorites([FromBody] IReadOnlyList<ResolvedFavorite> resolved, CancellationToken ct)
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        if (_store.Get(userId) is null || resolved.Count == 0)
        {
            return Status(_store.Get(userId));
        }

        var index = await _index.GetAsync(ct).ConfigureAwait(false);
        _store.Update(userId, l =>
        {
            foreach (var item in resolved.Where(r => l.MissingFavorites.Contains(r.Id)))
            {
                if (item.Mal is { } mal && index.ByMal.ContainsKey(mal))
                {
                    l.MissingFavorites.Remove(item.Id);
                    l.MissingFavoriteNames.Remove(item.Id.ToString(CultureInfo.InvariantCulture));
                }
                else if (!string.IsNullOrWhiteSpace(item.Name))
                {
                    l.MissingFavoriteNames[item.Id.ToString(CultureInfo.InvariantCulture)] = item.Name!;
                }
            }
        });
        return Status(_store.Get(userId));
    }

    [HttpPost("Account/Sync")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> SyncNow()
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        StartSync(userId);
        return Status(_store.Get(userId));
    }

    [HttpDelete("Account")]
    [Authorize]
    public async Task<ActionResult<AccountStatus>> Unlink(CancellationToken ct)
    {
        var userId = await CurrentUserAsync().ConfigureAwait(false);
        if (_store.Get(userId)?.Token is { } token)
        {
            await _account.LogoutAsync(token, ct).ConfigureAwait(false);
        }

        // Keep the device id and playlist ids so relinking reuses them; drop the credentials and snapshots.
        _store.Update(userId, l =>
        {
            l.Token = null;
            l.Nickname = null;
            l.LinkedAt = null;
            l.LastSyncAt = null;
            l.LastError = null;
            l.PendingCode = null;
            l.Merged = false;
            l.Timecodes.Clear();
            l.Favorites.Clear();
            l.Collections.Clear();
            l.MissingFavorites.Clear();
        });
        return Status(_store.Get(userId));
    }

    private void StartSync(Guid userId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _sync.SyncAsync(userId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AniLiberty: sync after link failed");
            }
        });
    }

    private async Task<Guid> CurrentUserAsync()
    {
        var info = await _auth.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return info.UserId;
    }

    private static AccountStatus Status(AccountLink? link) => new()
    {
        Linked = link?.IsLinked ?? false,
        Nickname = link?.Nickname,
        LinkedAt = link?.LinkedAt,
        LastSyncAt = link?.LastSyncAt,
        LastError = link?.LastError,
        PendingCode = link?.PendingExpiresAt > DateTime.UtcNow ? link.PendingCode : null,
        PendingExpiresAt = link?.PendingExpiresAt > DateTime.UtcNow ? link.PendingExpiresAt : null,
        LinkDeviceUrl = AccountClient.LinkDeviceUrl,
        SiteUrl = (Plugin.Instance?.Configuration.SiteUrl ?? "https://aniliberty.top").TrimEnd('/'),
        AniListLinked = link?.IsAniListLinked ?? false,
        AniListName = link?.AniListName,
        AniListTitles = link?.AniList.Count ?? 0,
        AniListAuthorizeUrl = AniListClient.AuthorizeUrl(),
        SyncedEpisodes = link?.Timecodes.Count ?? 0,
        Favorites = link?.Favorites.Count ?? 0,
        Collections = link?.Collections.Count ?? 0,
        MissingFavorites = link?.MissingFavorites ?? new List<long>(),
        MissingFavoriteNames = link?.MissingFavoriteNames ?? new Dictionary<string, string>(),
    };
}

public sealed class AniListTokenRequest
{
    public string? Token { get; set; }
}

public sealed class AccountStatus
{
    public bool AniListLinked { get; init; }

    public string? AniListName { get; init; }

    public int AniListTitles { get; init; }

    /// <summary>Null when no AniList client id is configured by the administrator.</summary>
    public string? AniListAuthorizeUrl { get; init; }

    public bool Linked { get; init; }

    public string? Nickname { get; init; }

    public DateTime? LinkedAt { get; init; }

    public DateTime? LastSyncAt { get; init; }

    public string? LastError { get; init; }

    public string? PendingCode { get; init; }

    public DateTime? PendingExpiresAt { get; init; }

    public string LinkDeviceUrl { get; init; } = string.Empty;

    public string SiteUrl { get; init; } = string.Empty;

    public int SyncedEpisodes { get; init; }

    public int Favorites { get; init; }

    public int Collections { get; init; }

    public List<long> MissingFavorites { get; init; } = new();

    /// <summary>Titles of the missing favourites; an id without a title is one AniLiberty won't show us.</summary>
    public Dictionary<string, string> MissingFavoriteNames { get; init; } = new();
}

/// <summary>One favourite the browser could look up on AniLiberty while the server could not.</summary>
public sealed class ResolvedFavorite
{
    public long Id { get; init; }

    public long? Mal { get; init; }

    public string? Name { get; init; }
}
