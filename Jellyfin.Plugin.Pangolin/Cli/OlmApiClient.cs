using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// Talks to the control API a running Pangolin client serves on its Unix socket.
/// </summary>
public sealed class OlmApiClient : IDisposable
{
    /// <summary>
    /// The socket path the Pangolin CLI always uses on Linux and macOS.
    /// </summary>
    public const string SocketPath = "/var/run/olm.sock";

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="OlmApiClient"/> class.
    /// </summary>
    public OlmApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// Checks whether a Pangolin client is answering on the socket.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if a client responded.</returns>
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SocketPath))
        {
            return false;
        }

        try
        {
            using var response = await _httpClient.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the client's connection and peer status.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    public async Task<OlmStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<OlmStatus>(new Uri("/status", UriKind.Relative), cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The Pangolin client returned an empty status.");
    }

    /// <summary>
    /// Asks the client to tear down the tunnel, restore DNS and exit.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the request is acknowledged.</returns>
    public async Task RequestExitAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(new Uri("/exit", UriKind.Relative), null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
