using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// An error reported by the olm API.
/// </summary>
public sealed class OlmError
{
    /// <summary>
    /// Gets or sets the error code.
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
