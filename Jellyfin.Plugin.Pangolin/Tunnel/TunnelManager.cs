using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Pangolin.Cli;
using Jellyfin.Plugin.Pangolin.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// Runs the Pangolin client for the lifetime of the server, restarting it when it exits or the settings change.
/// </summary>
public sealed partial class TunnelManager : IHostedService, IDisposable
{
    private const int MaxOutputLines = 200;

    private static readonly TimeSpan _minRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _stableRunTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _exitRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _exitRequestGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _terminateGrace = TimeSpan.FromSeconds(5);

    private readonly ILogger<TunnelManager> _logger;
    private readonly PangolinCliProvider _cliProvider;
    private readonly OlmApiClient _olmApi;

    // Serializes start/stop/restart. Held across awaits, so it can't be a Lock.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly Queue<string> _recentOutput = new();

    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorTask;

    // Everything below is guarded by _stateLock.
    private TunnelState _state = TunnelState.Stopped;
    private string? _message;
    private int? _processId;
    private DateTimeOffset? _startedAt;
    private int _restartCount;
    private int? _lastExitCode;
    private string? _executablePath;
    private string? _appliedFingerprint;
    private string? _secret;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunnelManager"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cliProvider">Provides the pangolin executable.</param>
    /// <param name="olmApi">Client for the running tunnel's control API.</param>
    public TunnelManager(ILogger<TunnelManager> logger, PangolinCliProvider cliProvider, OlmApiClient olmApi)
    {
        _logger = logger;
        _cliProvider = cliProvider;
        _olmApi = olmApi;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StartSupervisor();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        // Not cancellable: leaving the client running would leave routes and DNS behind.
        await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopSupervisorAsync().ConfigureAwait(false);
            SetState(TunnelState.Stopped, "Jellyfin is shutting down.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops the client if it is running and starts it again with the current settings.
    /// </summary>
    /// <returns>A task that completes once the new client has been launched.</returns>
    public async Task RestartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopSupervisorAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _restartCount = 0;
                _lastExitCode = null;
            }

            StartSupervisor();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Returns the tunnel state, including live peer status when the client is running.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status snapshot.</returns>
    public async Task<TunnelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        TunnelStatus status;
        lock (_stateLock)
        {
            status = new TunnelStatus
            {
                State = _state,
                Message = _message,
                ProcessId = _processId,
                StartedAt = _startedAt,
                RestartCount = _restartCount,
                LastExitCode = _lastExitCode,
                ExecutablePath = _executablePath,
                RecentOutput = [.. _recentOutput]
            };
        }

        if (status.ProcessId is null)
        {
            return status;
        }

        try
        {
            var client = await _olmApi.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            status.Connected = client.Connected;
            status.Registered = client.Registered;
            status.ClientVersion = client.Version;
            status.ClientError = client.Error?.Message;
            status.Peers = (client.Peers?.Values ?? [])
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new TunnelPeer
                {
                    Name = p.Name,
                    SiteId = p.SiteId,
                    Connected = p.Connected,
                    RttMilliseconds = Math.Round(p.RttNanoseconds / 1_000_000d, 1),
                    Endpoint = p.Endpoint,
                    Relayed = p.IsRelay,
                    Local = p.IsLocal,
                    LastSeen = p.LastSeen?.Year > 1 ? p.LastSeen : null
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            status.ClientError = "Client API not reachable yet: " + ex.Message;
        }

        return status;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _supervisorCts?.Dispose();
        _lifecycleLock.Dispose();
    }

    private static string Fingerprint(PluginConfiguration config)
    {
        // The settings that affect the client process. Saving only the connection test
        // fields shouldn't drop the tunnel.
        IEnumerable<string> modeParts = config.UserspaceMode
            ? ["userspace", .. PangolinCommand.BuildUserspaceArguments(config, string.Empty)]
            : ["system", config.BinaryPath.Trim(), config.CliVersion.Trim(), config.UseSudo ? "sudo" : "direct", .. PangolinCommand.BuildArguments(config)];
        IEnumerable<string> parts =
        [
            config.Enabled ? "enabled" : "disabled",
            config.Endpoint.Trim(),
            config.ClientId.Trim(),
            config.ClientSecret.Trim(),
            .. modeParts
        ];
        return string.Join('\n', parts);
    }

    private static string PrepareUserspaceSocketPath()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new TunnelConfigurationException("Userspace mode is only available on Linux. Use system mode instead.");
        }

        // Under /tmp rather than Jellyfin's data folder: that can be a network or host-shared
        // mount where Unix sockets don't work.
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var directory = Path.Combine(Path.GetTempPath(), string.Create(CultureInfo.InvariantCulture, $"jellyfin-pangolin-{UnixIdentity.EffectiveUserId}"));
        try
        {
            Directory.CreateDirectory(directory, OwnerOnly);

            // Throws if another user created the directory, so nobody else can swap the socket.
            File.SetUnixFileMode(directory, OwnerOnly);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TunnelConfigurationException($"{directory} belongs to another user. Remove it and restart the tunnel.", ex);
        }

        return Path.Combine(directory, "olm.sock");
    }

    private static LogLevel GetLogLevel(string line)
    {
        if (line.StartsWith("sudo:", StringComparison.Ordinal))
        {
            return LogLevel.Error;
        }

        var match = LogLevelRegex().Match(line);
        if (!match.Success)
        {
            return LogLevel.Information;
        }

        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "WARN" or "WARNING" => LogLevel.Warning,
            "ERROR" or "FATAL" => LogLevel.Error,
            _ => LogLevel.Information
        };
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        if (e is PluginConfiguration config)
        {
            lock (_stateLock)
            {
                if (string.Equals(Fingerprint(config), _appliedFingerprint, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        _ = ApplyConfigurationChangeAsync();
    }

    private async Task ApplyConfigurationChangeAsync()
    {
        try
        {
            _logger.LogInformation("Pangolin settings changed, restarting the tunnel");
            await RestartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply new Pangolin settings");
        }
    }

    private void StartSupervisor()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            SetState(TunnelState.Failed, "The plugin is not initialised.");
            return;
        }

        lock (_stateLock)
        {
            _appliedFingerprint = Fingerprint(config);
        }

        if (!config.Enabled)
        {
            SetState(TunnelState.Disabled, "Turned off in the plugin settings.");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            SetState(TunnelState.Failed, "Windows is not supported. The Pangolin CLI can only run the tunnel as a Windows service there; install the Pangolin client on the host instead.");
            return;
        }

        var problems = config.Validate();
        if (problems.Count > 0)
        {
            SetState(TunnelState.Failed, string.Join(' ', problems));
            return;
        }

        SetState(TunnelState.Starting, "Starting the Pangolin client.");
        var cts = new CancellationTokenSource();
        _supervisorCts = cts;
        _supervisorTask = Task.Run(() => SuperviseAsync(config, cts.Token), CancellationToken.None);
    }

    private async Task StopSupervisorAsync()
    {
        var cts = _supervisorCts;
        var task = _supervisorTask;
        _supervisorCts = null;
        _supervisorTask = null;
        if (cts is null || task is null)
        {
            return;
        }

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the supervisor is waiting between attempts.
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task SuperviseAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var retryDelay = _minRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var attemptStarted = Stopwatch.GetTimestamp();
            string failure;
            try
            {
                var exitCode = await RunClientAsync(config, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Pangolin client exited with code {ExitCode}", exitCode);
                failure = string.Create(CultureInfo.InvariantCulture, $"The Pangolin client exited with code {exitCode}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TunnelConfigurationException ex)
            {
                _logger.LogError(ex, "Pangolin tunnel cannot start");
                SetState(TunnelState.Failed, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pangolin tunnel failed");
                failure = ex.Message;
            }

            if (Stopwatch.GetElapsedTime(attemptStarted) >= _stableRunTime)
            {
                retryDelay = _minRetryDelay;
            }

            lock (_stateLock)
            {
                _restartCount++;
            }

            SetState(TunnelState.WaitingToRetry, string.Create(CultureInfo.InvariantCulture, $"{failure} Retrying in {retryDelay.TotalSeconds:0} s."));
            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, _maxRetryDelay.Ticks));
        }
    }

    private async Task<int> RunClientAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _secret = config.ClientSecret.Trim();
        }

        var (executable, startInfo) = config.UserspaceMode
            ? await PrepareUserspaceAsync(config, cancellationToken).ConfigureAwait(false)
            : await PrepareSystemAsync(config, cancellationToken).ConfigureAwait(false);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += OnClientOutput;
        process.ErrorDataReceived += OnClientOutput;

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (config.UseSudo)
        {
            throw new TunnelConfigurationException("sudo is not available here (the linuxserver.io image doesn't include it). Turn off \"Launch through sudo\" and run Jellyfin as root instead: PUID=0 and PGID=0 on linuxserver.io.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new TunnelConfigurationException($"Could not run {process.StartInfo.FileName}: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_stateLock)
        {
            _processId = process.Id;
            _startedAt = DateTimeOffset.UtcNow;
            _executablePath = executable;
        }

        _logger.LogInformation("Started Pangolin client (pid {Pid}) from {Path}", process.Id, executable);
        SetState(TunnelState.Running, "The Pangolin client is running.");

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                _lastExitCode = process.ExitCode;
            }

            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopClientAsync(process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                _processId = null;
                _startedAt = null;
            }
        }
    }

    private async Task<(string Executable, ProcessStartInfo StartInfo)> PrepareUserspaceAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        SetState(TunnelState.Starting, "Starting the userspace tunnel.");
        var executable = UserspaceHelper.GetExecutablePath();
        var socketPath = PrepareUserspaceSocketPath();
        _olmApi.SocketPath = socketPath;

        // This socket belongs to this plugin, so anything answering on it is a helper left
        // over from a Jellyfin crash. It still holds the forwarded ports; shut it down first.
        if (await _olmApi.IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Stopping a userspace helper left over from a previous run");
            await _olmApi.RequestExitAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < 20 && await _olmApi.IsRunningAsync(cancellationToken).ConfigureAwait(false); i++)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        return (executable, PangolinCommand.CreateUserspaceStartInfo(executable, config, socketPath));
    }

    private async Task<(string Executable, ProcessStartInfo StartInfo)> PrepareSystemAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        SetState(TunnelState.Starting, "Preparing the Pangolin CLI.");

        // The CLI exits immediately unless it runs as root, so say why up front instead of retrying forever.
        if (!config.UseSudo && UnixIdentity.EffectiveUserId != 0)
        {
            throw new TunnelConfigurationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Jellyfin runs as user ID {UnixIdentity.EffectiveUserId}, but system mode needs root to create the tunnel interface. Switch to userspace mode, or run Jellyfin as root (PUID=0 and PGID=0 on linuxserver.io), or use \"Launch through sudo\" on a bare-metal install."));
        }

        var executable = await _cliProvider.GetExecutableAsync(config, cancellationToken).ConfigureAwait(false);
        _olmApi.SocketPath = OlmApiClient.SystemSocketPath;

        if (await _olmApi.IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Another Pangolin client is already running on this host (it owns {OlmApiClient.SystemSocketPath}). Only one can run at a time.");
        }

        return (executable, PangolinCommand.CreateStartInfo(executable, config));
    }

    private async Task StopClientAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        SetState(TunnelState.Stopping, "Stopping the Pangolin client.");
        _logger.LogInformation("Stopping Pangolin client (pid {Pid})", process.Id);

        // The client's own exit endpoint removes routes and restores DNS, and works even
        // when it runs as root through sudo.
        try
        {
            using var requestTimeout = new CancellationTokenSource(_exitRequestTimeout);
            await _olmApi.RequestExitAsync(requestTimeout.Token).ConfigureAwait(false);
            if (await WaitForExitAsync(process, _exitRequestGrace).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pangolin client did not accept an exit request");
        }

        if (!OperatingSystem.IsWindows()
            && UnixSignals.Terminate(process.Id)
            && await WaitForExitAsync(process, _terminateGrace).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogWarning("Pangolin client (pid {Pid}) did not shut down, killing it. If DNS stops resolving afterwards, run `pangolin reset-dns`", process.Id);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            _logger.LogWarning(ex, "Could not kill the Pangolin client");
        }
    }

    private void OnClientOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = AnsiEscapeRegex().Replace(e.Data, string.Empty).TrimEnd();
        lock (_stateLock)
        {
            // olm prints its whole tunnel config, secret included, at debug level.
            if (_secret is { Length: > 0 })
            {
                line = line.Replace(_secret, "********", StringComparison.Ordinal);
            }

            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > MaxOutputLines)
            {
                _recentOutput.Dequeue();
            }
        }

        _logger.Log(GetLogLevel(line), "pangolin: {Line}", line);
    }

    private void SetState(TunnelState state, string? message)
    {
        lock (_stateLock)
        {
            _state = state;
            _message = message;
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"^\s*(DEBUG|INFO|WARN(?:ING)?|ERROR|FATAL)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LogLevelRegex();
}
