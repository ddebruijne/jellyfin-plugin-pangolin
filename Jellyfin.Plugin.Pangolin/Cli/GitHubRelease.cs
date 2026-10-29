using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// The subset of a GitHub release the plugin needs.
/// </summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public IReadOnlyList<GitHubAsset> Assets { get; set; } = [];
}
