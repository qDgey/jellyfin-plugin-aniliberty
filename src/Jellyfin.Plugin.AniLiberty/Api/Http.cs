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

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>GET JSON; returns default on 404, retries on 429/5xx and stalled responses.</summary>
    public static async Task<T?> GetJsonAsync<T>(HttpClient client, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // AniLiberty (behind Cloudflare) sometimes sends headers and then stalls the body forever.
            // HttpClient.Timeout doesn't cover reading the body, so bound the whole request ourselves.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Accept.ParseAdd("application/json");

                // Uncompressed bodies from AniLiberty stall ~15% of the time; compressed ones don't.
                req.Headers.AcceptEncoding.ParseAdd("gzip");
                req.Headers.AcceptEncoding.ParseAdd("br");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);

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

                await using var raw = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);

                // Decompress ourselves when the handler didn't (it strips Content-Encoding when it does).
                var encoding = resp.Content.Headers.ContentEncoding.LastOrDefault();
                await using Stream stream = encoding switch
                {
                    "gzip" => new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress),
                    "br" => new System.IO.Compression.BrotliStream(raw, System.IO.Compression.CompressionMode.Decompress),
                    _ => raw,
                };
                return await JsonSerializer.DeserializeAsync<T>(stream, Json, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException
                                       && !ct.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
