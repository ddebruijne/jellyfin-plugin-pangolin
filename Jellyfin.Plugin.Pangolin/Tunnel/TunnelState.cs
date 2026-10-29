namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// Lifecycle state of the managed Pangolin client process.
/// </summary>
public enum TunnelState
{
    /// <summary>
    /// The tunnel is turned off in the settings.
    /// </summary>
    Disabled,

    /// <summary>
    /// The tunnel is not running.
    /// </summary>
    Stopped,

    /// <summary>
    /// The executable is being prepared or the process is starting.
    /// </summary>
    Starting,

    /// <summary>
    /// The client process is running.
    /// </summary>
    Running,

    /// <summary>
    /// The client process is being shut down.
    /// </summary>
    Stopping,

    /// <summary>
    /// The client exited or failed to start and will be retried.
    /// </summary>
    WaitingToRetry,

    /// <summary>
    /// The tunnel cannot start until the configuration is fixed.
    /// </summary>
    Failed
}
