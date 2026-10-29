using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Pangolin.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The Pangolin CLI release used when no version is configured.
    /// </summary>
    public const string DefaultCliVersion = "0.19.1";

    /// <summary>
    /// Gets or sets a value indicating whether the tunnel should be running.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Pangolin dashboard URL, e.g. https://pangolin.example.com.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the machine client ID.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the machine client secret.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an explicit path to the pangolin executable. Empty means download it.
    /// </summary>
    public string BinaryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Pangolin CLI release tag to download, or "latest".
    /// </summary>
    public string CliVersion { get; set; } = DefaultCliVersion;

    /// <summary>
    /// Gets or sets a value indicating whether to launch the CLI through <c>sudo -n</c>.
    /// </summary>
    public bool UseSudo { get; set; }

    /// <summary>
    /// Gets or sets the WireGuard interface name.
    /// </summary>
    public string InterfaceName { get; set; } = "pangolin";

    /// <summary>
    /// Gets or sets the tunnel MTU.
    /// </summary>
    public int Mtu { get; set; } = 1280;

    /// <summary>
    /// Gets or sets a value indicating whether the client takes over DNS so resource aliases resolve.
    /// </summary>
    public bool OverrideDns { get; set; } = true;

    /// <summary>
    /// Gets or sets comma-separated domain patterns handled by Pangolin DNS. Empty means all domains.
    /// </summary>
    public string MatchDomains { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets comma-separated upstream DNS servers used when DNS is overridden.
    /// </summary>
    public string UpstreamDns { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether DNS queries are sent through the tunnel.
    /// </summary>
    public bool TunnelDns { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether overlapping local routes win over tunnel routes.
    /// </summary>
    public bool PreferLocalRoutes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether relaying through the Pangolin server is disabled.
    /// </summary>
    public bool DisableRelay { get; set; }

    /// <summary>
    /// Gets or sets the CLI log level.
    /// </summary>
    public string LogLevel { get; set; } = "info";

    /// <summary>
    /// Gets or sets the host used by the connection test on the settings page.
    /// </summary>
    public string ProbeHost { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the port used by the connection test on the settings page.
    /// </summary>
    public int ProbePort { get; set; } = 636;

    /// <summary>
    /// Gets or sets a value indicating whether the connection test performs a TLS handshake.
    /// </summary>
    public bool ProbeUseTls { get; set; } = true;

    /// <summary>
    /// Returns the problems that prevent the tunnel from starting.
    /// </summary>
    /// <returns>A list of human readable problems; empty when the configuration is usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            problems.Add("Endpoint must be the http(s) URL of your Pangolin dashboard.");
        }

        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            problems.Add("Client ID and secret are required.");
        }

        if (Mtu is < 576 or > 9000)
        {
            problems.Add("MTU must be between 576 and 9000.");
        }

        if (string.IsNullOrWhiteSpace(InterfaceName) || InterfaceName.Trim().Length > 15)
        {
            problems.Add("Interface name must be 1 to 15 characters.");
        }

        if (UseSudo && string.IsNullOrWhiteSpace(BinaryPath))
        {
            problems.Add("Running through sudo requires an explicit binary path to a root-owned pangolin executable.");
        }

        return problems;
    }
}
