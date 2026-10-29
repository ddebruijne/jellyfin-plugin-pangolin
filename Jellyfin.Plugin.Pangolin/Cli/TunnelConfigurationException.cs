using System;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// Raised when the tunnel cannot start because of a configuration problem that retrying will not fix.
/// </summary>
public class TunnelConfigurationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TunnelConfigurationException"/> class.
    /// </summary>
    public TunnelConfigurationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TunnelConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TunnelConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TunnelConfigurationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public TunnelConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
