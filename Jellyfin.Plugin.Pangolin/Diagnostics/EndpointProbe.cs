using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Pangolin.Diagnostics;

/// <summary>
/// Checks whether Jellyfin can reach a host, and what certificate it presents, without sending application data.
/// </summary>
public static class EndpointProbe
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Resolves the host, opens a TCP connection and optionally performs a TLS handshake (LDAPS style, not StartTLS).
    /// </summary>
    /// <param name="host">The host name or IP address.</param>
    /// <param name="port">The TCP port.</param>
    /// <param name="useTls">Whether to perform a TLS handshake.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The probe result.</returns>
    public static async Task<ProbeResult> ProbeAsync(string host, int port, bool useTls, CancellationToken cancellationToken)
    {
        var result = new ProbeResult();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var step = "resolve " + host;

        try
        {
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            result.ResolvedAddresses = addresses.Select(a => a.ToString()).ToArray();
            if (addresses.Length == 0)
            {
                result.Error = $"{host} did not resolve to any address.";
                return result;
            }

            step = "connect";
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var started = Stopwatch.GetTimestamp();
            await socket.ConnectAsync(addresses, port, timeout.Token).ConfigureAwait(false);
            result.ConnectMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            result.ConnectedAddress = socket.RemoteEndPoint?.ToString();

            if (useTls)
            {
                step = "TLS handshake";
                await HandshakeAsync(socket, host, result, timeout.Token).ConfigureAwait(false);
            }

            result.Success = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Error = string.Create(CultureInfo.InvariantCulture, $"Timed out during {step} after {_timeout.TotalSeconds:0} seconds.");
        }
        catch (AuthenticationException) when (result.CertificateProblems.Count > 0)
        {
            result.Error = "The TLS handshake worked, but the server's certificate would be rejected.";
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException or IOException)
        {
            result.Error = $"Failed during {step}: {ex.Message}";
        }

        return result;
    }

    private static async Task HandshakeAsync(Socket socket, string host, ProbeResult result, CancellationToken cancellationToken)
    {
        var stream = new NetworkStream(socket, ownsSocket: false);
        await using (stream.ConfigureAwait(false))
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await using (ssl.ConfigureAwait(false))
            {
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    {
                        Describe(certificate, chain, errors, result);

                        // Validation stays on: an untrusted certificate fails the probe like it
                        // would fail the LDAP plugin, but the details are captured first.
                        return errors == SslPolicyErrors.None;
                    }
                };

                await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
                result.TlsProtocol = ssl.SslProtocol.ToString();
            }
        }
    }

    private static void Describe(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, ProbeResult result)
    {
        if (certificate is X509Certificate2 cert)
        {
            result.CertificateSubject = cert.Subject;
            result.CertificateIssuer = cert.Issuer;
            result.CertificateNotAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime());
            result.CertificateDnsNames = cert.Extensions
                .OfType<X509SubjectAlternativeNameExtension>()
                .SelectMany(san => san.EnumerateDnsNames())
                .ToArray();
        }
        else if (certificate is not null)
        {
            result.CertificateSubject = certificate.Subject;
            result.CertificateIssuer = certificate.Issuer;
        }

        var problems = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            problems.Add("The server did not present a certificate.");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            problems.Add("The certificate does not cover this host name. Connect using a name listed in the certificate (e.g. the resource alias) instead of an IP.");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var details = chain?.ChainStatus
                .Select(s => s.StatusInformation.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal) ?? [];
            problems.Add("The certificate chain is not trusted by this server: " + string.Join("; ", details));
        }

        result.CertificateProblems = problems;
    }
}
