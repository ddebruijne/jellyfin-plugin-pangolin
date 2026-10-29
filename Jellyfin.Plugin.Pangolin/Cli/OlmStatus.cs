using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// Response of the olm API's /status endpoint.
/// </summary>
public sealed class OlmStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether the client is connected to Pangolin.
    /// </summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the client registered with Pangolin.
    /// </summary>
    [JsonPropertyName("registered")]
    public bool Registered { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Pangolin terminated the session.
    /// </summary>
    [JsonPropertyName("terminated")]
    public bool Terminated { get; set; }

    /// <summary>
    /// Gets or sets the client version.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the agent that started the client.
    /// </summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    /// <summary>
    /// Gets or sets the organization ID.
    /// </summary>
    [JsonPropertyName("orgId")]
    public string? OrgId { get; set; }

    /// <summary>
    /// Gets or sets the per-site peer status, keyed by site ID.
    /// </summary>
    [JsonPropertyName("peers")]
    public IReadOnlyDictionary<string, OlmPeerStatus>? Peers { get; set; }

    /// <summary>
    /// Gets or sets the last error reported by the client.
    /// </summary>
    [JsonPropertyName("error")]
    public OlmError? Error { get; set; }
}
