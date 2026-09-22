using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniLiberty.Web;

/// <summary>
/// Adds an "AniLiberty" entry to the web client's side menu by extending the served /web/config.json
/// ("menuLinks"), so users find the account page without knowing its URL. Nothing on disk is changed,
/// which keeps it working across Jellyfin/web updates.
/// </summary>
public sealed class MenuLinkStartupFilter : IStartupFilter
{
    private const string LinkName = "AniLiberty";

    private readonly ILogger<MenuLinkStartupFilter> _logger;

    public MenuLinkStartupFilter(ILogger<MenuLinkStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InjectAsync);
            next(app);
        };
    }

    private async Task InjectAsync(HttpContext context, Func<Task> next)
    {
        var isWebConfig = context.Request.Path.Value is { } path
                          && path.EndsWith("/web/config.json", StringComparison.OrdinalIgnoreCase);
        if (!isWebConfig
            || !HttpMethods.IsGet(context.Request.Method)
            || !(Plugin.Instance?.Configuration.ShowMenuLink ?? true))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Get the plain file, not a compressed or 304 response, so it can be edited.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        var bytes = buffer.ToArray();
        if (context.Response.StatusCode == StatusCodes.Status200OK)
        {
            try
            {
                bytes = Inject(bytes);
                context.Response.ContentLength = bytes.Length;
                context.Response.Headers.Remove("ETag");

                // The static file has only Last-Modified, so browsers cache it heuristically for hours and
                // never see the injected link (or a later change of it). Make them revalidate every time.
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl = "no-cache";
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "AniLiberty: can't add the menu link, web config.json isn't JSON");
            }
        }

        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static byte[] Inject(byte[] json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        if (root["menuLinks"] is not JsonArray links)
        {
            root["menuLinks"] = links = new JsonArray();
        }

        if (!links.Any(l => l?["name"]?.GetValue<string>() == LinkName))
        {
            // Relative to /web/, so it also works behind a base URL or reverse proxy path.
            links.Add(new JsonObject { ["name"] = LinkName, ["icon"] = "link", ["url"] = "../AniLiberty/Link" });
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
