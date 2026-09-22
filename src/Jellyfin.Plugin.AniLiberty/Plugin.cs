using Jellyfin.Plugin.AniLiberty.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AniLiberty;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string ProviderName = "AniLiberty";
    public const string ProviderKey = "AniLiberty";
    public const string ShikimoriKey = "Shikimori";
    public const string MalKey = "MyAnimeList";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "AniLiberty Metadata";

    public override Guid Id => Guid.Parse("5b7c3f7e-2c1a-4d7e-9a57-7a1b0c0de001");

    public override string Description => "Metadata for local AniLibria/AniLiberty releases (with Shikimori fallback).";

    public string CacheDirectory => Path.Combine(ApplicationPaths.CachePath, "aniliberty");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
