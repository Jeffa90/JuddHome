using Jellyfin.Plugin.JuddHome.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JuddHome;

/// <summary>
/// Registers JuddHome services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HealthService>();
        serviceCollection.AddSingleton<UserPreferencesStore>();
        serviceCollection.AddSingleton<WatchHistoryAnalyser>();
        serviceCollection.AddSingleton<RecommendationEngine>();
        serviceCollection.AddSingleton<CollectionOrganizer>();
        serviceCollection.AddHostedService<JuddHomeHostedService>();
    }
}
