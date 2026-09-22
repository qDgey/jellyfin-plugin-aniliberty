using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.AniLiberty.Api;

internal static class Http
{
    public const string UserAgent = "Jellyfin-Plugin-AniLiberty/0.1 (+https://github.com/jellyfin)";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>GET JSON; returns default on 404, retries on 429/5xx.</summary>
    public static async Task<T?> GetJsonAsync<T>(HttpClient client, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < 3)
            {
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            resp.EnsureSuccessStatusCode();

            // Unknown aliases return an HTML "Not Found" page with 200 on some mirrors.
            if (resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == false)
            {
                return default;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false);
        }
    }
}
