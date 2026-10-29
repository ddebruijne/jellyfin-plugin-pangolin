using System;

namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// Tunnel to one Pangolin site.
/// </summary>
public sealed class TunnelPeer
{
    /// <summary>
    /// Gets or sets the site name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the site ID.
    /// </summary>
    public int SiteId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the site answers pings.
    /// </summary>
    public bool Connected { get; set; }

    /// <summary>
    /// Gets or sets the round trip time in milliseconds.
    /// </summary>
    public double RttMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets the WireGuard endpoint in use.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether traffic is relayed through the Pangolin server.
    /// </summary>
    public bool Relayed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the site was reached over the local network.
    /// </summary>
    public bool Local { get; set; }

    /// <summary>
    /// Gets or sets when the site was last seen.
    /// </summary>
    public DateTimeOffset? LastSeen { get; set; }
}
