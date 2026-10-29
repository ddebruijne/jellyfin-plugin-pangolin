using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Pangolin.Configuration;

namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// Builds the command line for the Pangolin CLI (system mode) or the userspace helper.
/// </summary>
public static class PangolinCommand
{
    // Settings the CLI falls back to when a flag isn't passed. Jellyfin's own environment
    // (LOG_LEVEL in a compose file, say) must not leak into the tunnel.
    private static readonly string[] _inheritedSettingVariables =
    [
        "MTU", "DNS", "UPSTREAM_DNS", "MATCH_DOMAINS_DNS", "LOG_LEVEL", "INTERFACE", "HTTP_ADDR",
        "PING_INTERVAL", "PING_TIMEOUT", "OVERRIDE_DNS", "TUNNEL_DNS", "DISABLE_RELAY",
        "PREFER_LOCAL_ROUTES", "DISABLE_ROUTES_AND_ALIASES", "SUBNET_ROUTER", "DISABLE_HOLEPUNCH",
        "PANGOLIN_ORG"
    ];

    /// <summary>
    /// Creates the start info for the Pangolin CLI in system mode.
    /// </summary>
    /// <param name="executable">Path to the pangolin executable.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The process start info.</returns>
    public static ProcessStartInfo CreateStartInfo(string executable, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var startInfo = CreateBaseStartInfo();
        if (config.UseSudo)
        {
            // -n fails instead of prompting. The sudoers rule has to keep the PANGOLIN_* variables (env_keep).
            startInfo.FileName = "sudo";
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(executable);
        }
        else
        {
            startInfo.FileName = executable;
        }

        foreach (var argument in BuildArguments(config))
        {
            startInfo.ArgumentList.Add(argument);
        }

        SetEnvironment(startInfo, config);
        return startInfo;
    }

    /// <summary>
    /// Creates the start info for the userspace helper.
    /// </summary>
    /// <param name="executable">Path to the pangolin-userspace helper.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="socketPath">Where the helper should serve its control API.</param>
    /// <returns>The process start info.</returns>
    public static ProcessStartInfo CreateUserspaceStartInfo(string executable, PluginConfiguration config, string socketPath)
    {
        ArgumentNullException.ThrowIfNull(config);

        var startInfo = CreateBaseStartInfo();
        startInfo.FileName = executable;
        foreach (var argument in BuildUserspaceArguments(config, socketPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        SetEnvironment(startInfo, config);
        return startInfo;
    }

    /// <summary>
    /// Builds the userspace helper's command line arguments.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="socketPath">Where the helper should serve its control API.</param>
    /// <returns>The arguments.</returns>
    public static IReadOnlyList<string> BuildUserspaceArguments(PluginConfiguration config, string socketPath)
    {
        ArgumentNullException.ThrowIfNull(config);

        var arguments = new List<string>
        {
            "--socket=" + socketPath,
            "--mtu=" + config.Mtu.ToString(CultureInfo.InvariantCulture),
            "--log-level=" + (string.IsNullOrWhiteSpace(config.LogLevel) ? "info" : config.LogLevel.Trim()),
            "--disable-relay=" + Flag(config.DisableRelay)
        };

        var upstreamDns = SplitList(config.UpstreamDns);
        if (upstreamDns.Length > 0)
        {
            arguments.Add("--upstream-dns=" + string.Join(',', upstreamDns));
        }

        if (PortForward.TryParseAll(config.Forwards, out var forwards, out _))
        {
            arguments.AddRange(forwards.Select(f => "--forward=" + f));
        }

        return arguments;
    }

    /// <summary>
    /// Builds the command line arguments, starting with the fixed <c>up client --attach</c> prefix.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The arguments.</returns>
    public static IReadOnlyList<string> BuildArguments(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var arguments = new List<string>
        {
            "up",
            "client",
            "--attach",
            "--interface-name=" + config.InterfaceName.Trim(),
            "--mtu=" + config.Mtu.ToString(CultureInfo.InvariantCulture),
            "--log-level=" + (string.IsNullOrWhiteSpace(config.LogLevel) ? "info" : config.LogLevel.Trim()),
            "--override-dns=" + Flag(config.OverrideDns),
            "--tunnel-dns=" + Flag(config.TunnelDns),
            "--prefer-local-routes=" + Flag(config.PreferLocalRoutes),
            "--disable-relay=" + Flag(config.DisableRelay)
        };

        var matchDomains = SplitList(config.MatchDomains);
        if (matchDomains.Length > 0)
        {
            arguments.Add("--match-domains=" + string.Join(',', matchDomains));
        }

        var upstreamDns = SplitList(config.UpstreamDns);
        if (upstreamDns.Length > 0)
        {
            arguments.Add("--upstream-dns=" + string.Join(',', upstreamDns));
        }

        return arguments;
    }

    private static ProcessStartInfo CreateBaseStartInfo()
    {
        return new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private static void SetEnvironment(ProcessStartInfo startInfo, PluginConfiguration config)
    {
        foreach (var name in _inheritedSettingVariables)
        {
            startInfo.Environment.Remove(name);
        }

        // Passed through the environment so the secret doesn't show up in `ps`.
        startInfo.Environment["PANGOLIN_ENDPOINT"] = config.Endpoint.Trim();
        startInfo.Environment["PANGOLIN_CLIENT_ID"] = config.ClientId.Trim();
        startInfo.Environment["PANGOLIN_CLIENT_SECRET"] = config.ClientSecret.Trim();
        startInfo.Environment["NO_COLOR"] = "1";
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static string[] SplitList(string value)
    {
        return value
            .Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
