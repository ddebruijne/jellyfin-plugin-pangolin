using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// Snapshot of the tunnel for the settings page.
/// </summary>
public sealed class TunnelStatus
{
    /// <summary>
    /// Gets or sets the lifecycle state.
    /// </summary>
    public TunnelState State { get; set; }

    /// <summary>
    /// Gets or sets a human readable description of the state.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the ID of the running client process.
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    /// Gets or sets when the running client process started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets how many times the client was restarted after exiting.
    /// </summary>
    public int RestartCount { get; set; }

    /// <summary>
    /// Gets or sets the exit code of the last client process.
    /// </summary>
    public int? LastExitCode { get; set; }

    /// <summary>
    /// Gets or sets the executable in use.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the client reports being connected to Pangolin.
    /// </summary>
    public bool? Connected { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the client registered with Pangolin.
    /// </summary>
    public bool? Registered { get; set; }

    /// <summary>
    /// Gets or sets the version reported by the client.
    /// </summary>
    public string? ClientVersion { get; set; }

    /// <summary>
    /// Gets or sets the last error reported by the client, or why its API could not be read.
    /// </summary>
    public string? ClientError { get; set; }

    /// <summary>
    /// Gets or sets the Pangolin sites the client has tunnels to.
    /// </summary>
    public IReadOnlyList<TunnelPeer> Peers { get; set; } = [];

    /// <summary>
    /// Gets or sets the most recent output lines of the client.
    /// </summary>
    public IReadOnlyList<string> RecentOutput { get; set; } = [];
}
