using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.AniLiberty.Api;

/// <summary>The stored token was rejected: the user has to link the account again.</summary>
public sealed class AccountUnauthorizedException : Exception
{
    public AccountUnauthorizedException(string message)
        : base(message)
    {
    }
}

public sealed record OtpTicket(string Code, DateTimeOffset? ExpiresAt, int RemainingSeconds);

public sealed record RemoteTimecode(string EpisodeId, double Time, bool IsWatched);

public sealed record TimecodeUpdate(string EpisodeId, double Time, bool IsWatched);

/// <summary>AniLiberty collection types, as the API names them.</summary>
public static class CollectionTypes
{
    public static readonly string[] All = { "WATCHING", "PLANNED", "WATCHED", "POSTPONED", "ABANDONED" };

    public static string Title(string type) => type switch
    {
        "WATCHING" => "Смотрю",
        "PLANNED" => "Запланировано",
        "WATCHED" => "Просмотрено",
        "POSTPONED" => "Отложено",
        "ABANDONED" => "Брошено",
        _ => type,
    };
}

/// <summary>Authenticated (per-user token) part of the AniLiberty API: OTP login, timecodes, favorites, collections.</summary>
public sealed class AccountClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _httpFactory;

    public AccountClient(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    private static string ApiUrl => (Plugin.Instance?.Configuration.SiteUrl ?? "https://aniliberty.top").TrimEnd('/') + "/api/v1";

    public static string LinkDeviceUrl => (Plugin.Instance?.Configuration.SiteUrl ?? "https://aniliberty.top").TrimEnd('/') + "/app/auth/otp/linkDevice";

    public async Task<OtpTicket> RequestOtpAsync(string deviceId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, "/accounts/otp/get", null, new { device_id = deviceId }, ct).ConfigureAwait(false);
        var root = doc!.RootElement;
        var otp = root.GetProperty("otp");
        var code = otp.GetProperty("code").ToString();
        DateTimeOffset? expires = otp.TryGetProperty("expired_at", out var e) && e.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
        var remaining = root.TryGetProperty("remaining_time", out var r) && r.TryGetInt32(out var sec) ? sec : 120;
        return new OtpTicket(code, expires, remaining);
    }

    /// <summary>Exchanges an accepted OTP for a session token; null while the user hasn't entered the code yet.</summary>
    public async Task<string?> TryLoginWithOtpAsync(string code, string deviceId, CancellationToken ct)
    {
        if (!int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return null;
        }

        try
        {
            using var doc = await SendAsync(HttpMethod.Post, "/accounts/otp/login", null, new { code = numeric, device_id = deviceId }, ct).ConfigureAwait(false);
            return doc?.RootElement.TryGetProperty("token", out var t) == true ? t.GetString() : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound
                                                  or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // Not accepted yet (or expired): the caller keeps polling until the ticket runs out.
            return null;
        }
    }

    public async Task<string?> GetNicknameAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/accounts/users/me/profile", token, null, ct).ConfigureAwait(false);
        var root = doc!.RootElement;
        foreach (var name in new[] { "nickname", "login" })
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                return v.GetString();
            }
        }

        return null;
    }

    public async Task LogoutAsync(string token, CancellationToken ct)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, "/accounts/users/auth/logout", token, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or AccountUnauthorizedException)
        {
            // Unlinking locally must work even when the token is already dead.
        }
    }

    /// <summary>All timecodes; items are [episode uuid, seconds, is_watched] tuples.</summary>
    public async Task<List<RemoteTimecode>> GetTimecodesAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/accounts/users/me/views/timecodes", token, null, ct).ConfigureAwait(false);
        var result = new List<RemoteTimecode>();
        foreach (var item in doc!.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 3)
            {
                continue;
            }

            var id = item[0].GetString();
            var time = item[1].ValueKind == JsonValueKind.Number ? item[1].GetDouble() : 0;
            var watched = item[2].ValueKind == JsonValueKind.True;
            if (!string.IsNullOrEmpty(id))
            {
                result.Add(new RemoteTimecode(id, time, watched));
            }
        }

        return result;
    }

    public async Task UpdateTimecodesAsync(string token, IReadOnlyCollection<TimecodeUpdate> updates, CancellationToken ct)
    {
        if (updates.Count == 0)
        {
            return;
        }

        var body = updates.Select(u => new { release_episode_id = u.EpisodeId, time = Math.Round(u.Time, 1), is_watched = u.IsWatched });
        using var _ = await SendAsync(HttpMethod.Post, "/accounts/users/me/views/timecodes", token, body, ct).ConfigureAwait(false);
    }

    public async Task DeleteTimecodesAsync(string token, IReadOnlyCollection<string> episodeIds, CancellationToken ct)
    {
        if (episodeIds.Count == 0)
        {
            return;
        }

        using var _ = await SendAsync(HttpMethod.Delete, "/accounts/users/me/views/timecodes", token, episodeIds.Select(id => new { release_episode_id = id }), ct).ConfigureAwait(false);
    }

    public async Task<HashSet<long>> GetFavoriteIdsAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/accounts/users/me/favorites/ids", token, null, ct).ConfigureAwait(false);
        return doc!.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt64()).ToHashSet();
    }

    public async Task AddFavoritesAsync(string token, IReadOnlyCollection<long> releaseIds, CancellationToken ct)
    {
        if (releaseIds.Count > 0)
        {
            using var _ = await SendAsync(HttpMethod.Post, "/accounts/users/me/favorites", token, releaseIds.Select(id => new { release_id = id }), ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveFavoritesAsync(string token, IReadOnlyCollection<long> releaseIds, CancellationToken ct)
    {
        if (releaseIds.Count > 0)
        {
            using var _ = await SendAsync(HttpMethod.Delete, "/accounts/users/me/favorites", token, releaseIds.Select(id => new { release_id = id }), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Release id → collection type; items are [release id, type] tuples.</summary>
    public async Task<Dictionary<long, string>> GetCollectionsAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/accounts/users/me/collections/ids", token, null, ct).ConfigureAwait(false);
        var result = new Dictionary<long, string>();
        foreach (var item in doc!.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2
                && item[0].ValueKind == JsonValueKind.Number && item[1].ValueKind == JsonValueKind.String)
            {
                result[item[0].GetInt64()] = item[1].GetString()!;
            }
        }

        return result;
    }

    public async Task SetCollectionsAsync(string token, IReadOnlyDictionary<long, string> releases, CancellationToken ct)
    {
        if (releases.Count > 0)
        {
            var body = releases.Select(p => new { release_id = p.Key, type_of_collection = p.Value });
            using var _ = await SendAsync(HttpMethod.Post, "/accounts/users/me/collections", token, body, ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveFromCollectionsAsync(string token, IReadOnlyCollection<long> releaseIds, CancellationToken ct)
    {
        if (releaseIds.Count > 0)
        {
            using var _ = await SendAsync(HttpMethod.Delete, "/accounts/users/me/collections", token, releaseIds.Select(id => new { release_id = id }), ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient(NamedClient.Default);
        for (var attempt = 0; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(method, ApiUrl + path);
            req.Headers.UserAgent.ParseAdd(Http.UserAgent);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.AcceptEncoding.ParseAdd("gzip");
            if (token is not null)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (body is not null)
            {
                req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            }

            try
            {
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && token is not null)
                {
                    throw new AccountUnauthorizedException($"AniLiberty rejected the token ({(int)resp.StatusCode}) for {path}");
                }

                if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < 3)
                {
                    await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt), ct).ConfigureAwait(false);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"AniLiberty {method} {path} → {(int)resp.StatusCode}", null, resp.StatusCode);
                }

                var raw = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using Stream stream = resp.Content.Headers.ContentEncoding.LastOrDefault() == "gzip"
                    ? new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress)
                    : raw;
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cts.Token).ConfigureAwait(false);
                return buffer.Length == 0 ? null : JsonDocument.Parse(buffer.ToArray());
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException && !ct.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
