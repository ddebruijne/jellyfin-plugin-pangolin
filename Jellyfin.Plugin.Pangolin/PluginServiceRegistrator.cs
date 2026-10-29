using Jellyfin.Plugin.Pangolin.Cli;
using Jellyfin.Plugin.Pangolin.Tunnel;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Pangolin;

/// <summary>
/// Registers the plugin's services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PangolinCliProvider>();
        serviceCollection.AddSingleton<OlmApiClient>();
        serviceCollection.AddSingleton<TunnelManager>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<TunnelManager>());
    }
}
