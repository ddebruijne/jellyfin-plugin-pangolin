using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// A downloadable file attached to a GitHub release.
/// </summary>
internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the digest GitHub computed at upload, formatted as "sha256:&lt;hex&gt;".
    /// </summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}
