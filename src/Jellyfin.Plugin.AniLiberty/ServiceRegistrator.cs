using Jellyfin.Plugin.AniLiberty.Api;
using Jellyfin.Plugin.AniLiberty.Sync;
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

        // Per-user AniLiberty account sync.
        serviceCollection.AddSingleton<AccountClient>();
        serviceCollection.AddSingleton<AccountStore>();
        serviceCollection.AddSingleton<LibraryIndex>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddHostedService<PlaybackSyncService>();
        serviceCollection.AddSingleton<FranchiseBuilder>();
    }
}
