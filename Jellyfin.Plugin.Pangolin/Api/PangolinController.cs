using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Pangolin.Diagnostics;
using Jellyfin.Plugin.Pangolin.Tunnel;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Pangolin.Api;

/// <summary>
/// Tunnel status and diagnostics for the settings page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Pangolin")]
[Produces(MediaTypeNames.Application.Json)]
public class PangolinController : ControllerBase
{
    private readonly TunnelManager _tunnelManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PangolinController"/> class.
    /// </summary>
    /// <param name="tunnelManager">The tunnel manager.</param>
    public PangolinController(TunnelManager tunnelManager)
    {
        _tunnelManager = tunnelManager;
    }

    /// <summary>
    /// Gets the tunnel status.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Status returned.</response>
    /// <returns>The tunnel status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TunnelStatus>> GetStatus(CancellationToken cancellationToken)
    {
        return await _tunnelManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restarts the tunnel with the saved settings.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Tunnel restarted.</response>
    /// <returns>The tunnel status after the restart.</returns>
    [HttpPost("Restart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TunnelStatus>> Restart(CancellationToken cancellationToken)
    {
        await _tunnelManager.RestartAsync().ConfigureAwait(false);
        return await _tunnelManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tests whether Jellyfin can open a TCP (and optionally TLS) connection to a host.
    /// </summary>
    /// <param name="request">The host and port to test.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Test completed; see the result for success.</response>
    /// <returns>The test result.</returns>
    [HttpPost("Probe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProbeResult>> Probe([FromBody, Required] ProbeRequest request, CancellationToken cancellationToken)
    {
        return await EndpointProbe.ProbeAsync(request.Host.Trim(), request.Port, request.UseTls, cancellationToken).ConfigureAwait(false);
    }
}
