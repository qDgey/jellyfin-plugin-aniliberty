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

    /// <summary>
    /// Image download for Jellyfin. The AniLiberty CDN occasionally stalls a response indefinitely,
    /// which otherwise hits HttpClient's 100 s timeout and fails the whole item refresh.
    /// </summary>
    public static async Task<HttpResponseMessage> GetImageAsync(IHttpClientFactory factory, string url, CancellationToken ct)
    {
        var client = factory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        for (var attempt = 0; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            try
            {
                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode < 500 || attempt >= 2)
                {
                    return resp;
                }

                resp.Dispose();
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException && !ct.IsCancellationRequested && attempt < 2)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct).ConfigureAwait(false);
        }
    }

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
