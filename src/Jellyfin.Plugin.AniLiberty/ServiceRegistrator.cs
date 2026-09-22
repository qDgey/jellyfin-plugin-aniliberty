using Jellyfin.Plugin.AniLiberty.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AniLiberty;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<AniLibertyClient>();
        serviceCollection.AddSingleton<ShikimoriClient>();
        serviceCollection.AddSingleton<ReleaseResolver>();
    }
}
