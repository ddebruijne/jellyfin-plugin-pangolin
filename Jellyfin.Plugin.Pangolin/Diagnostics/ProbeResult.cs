using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Pangolin.Diagnostics;

/// <summary>
/// Outcome of a connection test against a host and port.
/// </summary>
public sealed class ProbeResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the connection (and TLS handshake, if requested) succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the addresses the host name resolved to.
    /// </summary>
    public IReadOnlyList<string> ResolvedAddresses { get; set; } = [];

    /// <summary>
    /// Gets or sets the address the TCP connection was made to.
    /// </summary>
    public string? ConnectedAddress { get; set; }

    /// <summary>
    /// Gets or sets how long the TCP connection took, in milliseconds.
    /// </summary>
    public long? ConnectMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets the negotiated TLS protocol.
    /// </summary>
    public string? TlsProtocol { get; set; }

    /// <summary>
    /// Gets or sets the server certificate subject.
    /// </summary>
    public string? CertificateSubject { get; set; }

    /// <summary>
    /// Gets or sets the server certificate issuer.
    /// </summary>
    public string? CertificateIssuer { get; set; }

    /// <summary>
    /// Gets or sets the DNS names in the server certificate.
    /// </summary>
    public IReadOnlyList<string> CertificateDnsNames { get; set; } = [];

    /// <summary>
    /// Gets or sets when the server certificate expires.
    /// </summary>
    public DateTimeOffset? CertificateNotAfter { get; set; }

    /// <summary>
    /// Gets or sets why the certificate would be rejected by a client that validates it, if it would be.
    /// </summary>
    public IReadOnlyList<string> CertificateProblems { get; set; } = [];

    /// <summary>
    /// Gets or sets the failure description.
    /// </summary>
    public string? Error { get; set; }
}
