using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Pangolin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// Locates the pangolin executable, downloading and verifying it from GitHub releases when needed.
/// </summary>
public partial class PangolinCliProvider
{
    private const string ReleasesApi = "https://api.github.com/repos/fosrl/cli/releases/";
    private const string LatestVersion = "latest";
    private const string DigestSuffix = ".sha256";

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PangolinCliProvider> _logger;
    private readonly string _cacheRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="PangolinCliProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public PangolinCliProvider(
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILogger<PangolinCliProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheRoot = Path.Combine(applicationPaths.DataPath, "pangolin", "bin");
    }

    /// <summary>
    /// Returns the path of a pangolin executable matching the configuration.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The absolute path to the executable.</returns>
    public async Task<string> GetExecutableAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(config.BinaryPath))
        {
            var path = config.BinaryPath.Trim();
            if (!File.Exists(path))
            {
                throw new TunnelConfigurationException($"No pangolin executable at {path}.");
            }

            return path;
        }

        if (config.UseSudo)
        {
            // Downloads land in a directory Jellyfin can write to. Letting sudo run them would let
            // anything running as the Jellyfin user swap the file and get root.
            throw new TunnelConfigurationException("Running through sudo requires an explicit binary path to a root-owned pangolin executable.");
        }

        var assetName = GetAssetName();
        var version = string.IsNullOrWhiteSpace(config.CliVersion) ? PluginConfiguration.DefaultCliVersion : config.CliVersion.Trim();
        var wantsLatest = string.Equals(version, LatestVersion, StringComparison.OrdinalIgnoreCase);

        if (!wantsLatest)
        {
            EnsureSafeTag(version);
            var pinned = Path.Combine(_cacheRoot, version, assetName);
            if (await IsCachedCopyIntactAsync(pinned, cancellationToken).ConfigureAwait(false))
            {
                return pinned;
            }
        }

        GitHubRelease release;
        try
        {
            release = await GetReleaseAsync(wantsLatest ? LatestVersion : "tags/" + version, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (wantsLatest)
        {
            var fallback = await FindNewestCachedCopyAsync(assetName, cancellationToken).ConfigureAwait(false);
            if (fallback is null)
            {
                throw;
            }

            _logger.LogWarning(ex, "Could not look up the latest Pangolin CLI release; using cached {Path}", fallback);
            return fallback;
        }

        EnsureSafeTag(release.TagName);
        var target = Path.Combine(_cacheRoot, release.TagName, assetName);
        if (await IsCachedCopyIntactAsync(target, cancellationToken).ConfigureAwait(false))
        {
            return target;
        }

        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.Ordinal))
            ?? throw new TunnelConfigurationException($"Pangolin CLI {release.TagName} has no {assetName} build. Set a binary path instead.");

        await DownloadAsync(asset, target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    private static string GetAssetName()
    {
        string os;
        if (OperatingSystem.IsLinux())
        {
            os = "linux";
        }
        else if (OperatingSystem.IsMacOS())
        {
            os = "darwin";
        }
        else
        {
            throw new TunnelConfigurationException("The Pangolin CLI can only run the tunnel in-process on Linux and macOS.");
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm32",
            Architecture.RiscV64 => "riscv64",
            var other => throw new TunnelConfigurationException($"No Pangolin CLI build for {other}. Set a binary path instead.")
        };

        return $"pangolin-cli_{os}_{arch}";
    }

    private static void EnsureSafeTag(string tag)
    {
        // The tag becomes a directory name, so keep it to plain release-style names.
        if (!SafeTagRegex().IsMatch(tag) || tag.Contains("..", StringComparison.Ordinal))
        {
            throw new TunnelConfigurationException($"'{tag}' is not a valid Pangolin CLI version.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
    }

    private static async Task<bool> IsCachedCopyIntactAsync(string path, CancellationToken cancellationToken)
    {
        var digestPath = path + DigestSuffix;
        if (!File.Exists(path) || !File.Exists(digestPath))
        {
            return false;
        }

        var expected = (await File.ReadAllTextAsync(digestPath, cancellationToken).ConfigureAwait(false)).Trim();
        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> FindNewestCachedCopyAsync(string assetName, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_cacheRoot))
        {
            return null;
        }

        var candidates = new DirectoryInfo(_cacheRoot)
            .EnumerateDirectories()
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => Path.Combine(d.FullName, assetName));

        foreach (var candidate in candidates)
        {
            if (await IsCachedCopyIntactAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<GitHubRelease> GetReleaseAsync(string selector, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi + selector);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Jellyfin-Plugin-Pangolin");

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new TunnelConfigurationException($"Pangolin CLI release '{selector}' does not exist.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("GitHub returned an empty release.");
    }

    private async Task DownloadAsync(GitHubAsset asset, string target, CancellationToken cancellationToken)
    {
        const string Sha256Prefix = "sha256:";
        if (asset.Digest is null || !asset.Digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new TunnelConfigurationException($"GitHub did not publish a SHA-256 digest for {asset.Name}, so it cannot be verified. Set a binary path instead.");
        }

        var expected = asset.Digest[Sha256Prefix.Length..];
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".download";

        _logger.LogInformation("Downloading Pangolin CLI from {Url}", asset.DownloadUrl);
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using (var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (destination.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var actual = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new HttpRequestException($"Checksum mismatch for {asset.Name}: expected {expected}, got {actual}.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(partial, ExecutableMode);
        }

        File.Move(partial, target, overwrite: true);
        await File.WriteAllTextAsync(target + DigestSuffix, actual, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Pangolin CLI saved to {Path} (sha256 {Digest})", target, actual);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeTagRegex();
}
