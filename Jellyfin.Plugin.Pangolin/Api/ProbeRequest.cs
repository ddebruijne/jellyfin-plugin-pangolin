using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.Pangolin.Api;

/// <summary>
/// Connection test parameters.
/// </summary>
public sealed class ProbeRequest
{
    /// <summary>
    /// Gets or sets the host name or IP address to test.
    /// </summary>
    [Required]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TCP port.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 636;

    /// <summary>
    /// Gets or sets a value indicating whether to perform a TLS handshake.
    /// </summary>
    public bool UseTls { get; set; } = true;
}
