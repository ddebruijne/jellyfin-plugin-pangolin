using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// Status of the tunnel to one Pangolin site.
/// </summary>
public sealed class OlmPeerStatus
{
    /// <summary>
    /// Gets or sets the site ID.
    /// </summary>
    [JsonPropertyName("siteId")]
    public int SiteId { get; set; }

    /// <summary>
    /// Gets or sets the site name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the peer answers pings.
    /// </summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>
    /// Gets or sets the round trip time in nanoseconds.
    /// </summary>
    [JsonPropertyName("rtt")]
    public long RttNanoseconds { get; set; }

    /// <summary>
    /// Gets or sets when the peer was last seen.
    /// </summary>
    [JsonPropertyName("lastSeen")]
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>
    /// Gets or sets the WireGuard endpoint in use.
    /// </summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether traffic is relayed through the Pangolin server.
    /// </summary>
    [JsonPropertyName("isRelay")]
    public bool IsRelay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the peer was reached over the local network.
    /// </summary>
    [JsonPropertyName("isLocal")]
    public bool IsLocal { get; set; }
}
