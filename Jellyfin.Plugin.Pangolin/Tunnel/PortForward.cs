using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// A local TCP port that userspace mode forwards to a host on the Pangolin network.
/// </summary>
/// <param name="ListenHost">The local address to listen on.</param>
/// <param name="ListenPort">The local port to listen on.</param>
/// <param name="TargetHost">The resource host name, alias or IP.</param>
/// <param name="TargetPort">The resource port.</param>
public sealed record PortForward(string ListenHost, int ListenPort, string TargetHost, int TargetPort)
{
    /// <summary>
    /// Parses one forward per line as <c>LISTEN=HOST:PORT</c>, where LISTEN is a port (bound to 127.0.0.1) or host:port.
    /// Blank lines and lines starting with # are ignored.
    /// </summary>
    /// <param name="text">The configured forwards.</param>
    /// <param name="forwards">The parsed forwards.</param>
    /// <param name="error">Why parsing failed, if it did.</param>
    /// <returns><c>true</c> if every line is valid.</returns>
    public static bool TryParseAll(string? text, out IReadOnlyList<PortForward> forwards, out string? error)
    {
        var parsed = new List<PortForward>();
        forwards = parsed;
        error = null;

        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0
                || !TryParseEndpoint(line[..separator].Trim(), "127.0.0.1", out var listenHost, out var listenPort)
                || !TryParseEndpoint(line[(separator + 1)..].Trim(), null, out var targetHost, out var targetPort))
            {
                error = $"'{line}' is not a valid forward. Use LOCALPORT=HOST:PORT, e.g. 6360=ldap.home.internal:636.";
                return false;
            }

            parsed.Add(new PortForward(listenHost, listenPort, targetHost, targetPort));
        }

        return true;
    }

    /// <summary>
    /// Formats the forward the way the userspace helper expects it.
    /// </summary>
    /// <returns>The forward as LISTENHOST:PORT=HOST:PORT.</returns>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{ListenHost}:{ListenPort}={TargetHost}:{TargetPort}");

    private static bool TryParseEndpoint(string value, string? defaultHost, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var colon = value.LastIndexOf(':');
        string portText;
        if (colon < 0)
        {
            if (defaultHost is null)
            {
                return false;
            }

            host = defaultHost;
            portText = value;
        }
        else
        {
            host = value[..colon].Trim();
            portText = value[(colon + 1)..].Trim();
        }

        return host.Length > 0
            && !host.Contains(' ', StringComparison.Ordinal)
            && int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }
}
