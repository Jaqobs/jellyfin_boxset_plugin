using Jellyfin.Plugin.TmdbBoxSets.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.TmdbBoxSets;

/// <summary>
/// Registers the plugin's services with the server's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // A single instance backs both the library-event listener and the scheduled task
        // so the two share one sync lock and never overlap.
        serviceCollection.AddSingleton<TmdbCollectionClient>();
        serviceCollection.AddSingleton<BoxSetSyncManager>();
        serviceCollection.AddSingleton<IHostedService>(
            provider => provider.GetRequiredService<BoxSetSyncManager>());
        serviceCollection.AddSingleton<IScheduledTask, SyncBoxSetsTask>();
    }
}
