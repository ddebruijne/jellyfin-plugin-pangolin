using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// Sends POSIX signals, which <see cref="System.Diagnostics.Process"/> has no API for.
/// </summary>
internal static partial class UnixSignals
{
    // Same value on Linux and macOS.
    private const int SigTerm = 15;

    /// <summary>
    /// Sends SIGTERM to a process. When the process is sudo, sudo relays it to the command.
    /// </summary>
    /// <param name="pid">The process ID.</param>
    /// <returns><c>true</c> if the signal was delivered.</returns>
    public static bool Terminate(int pid) => Kill(pid, SigTerm) == 0;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int signal);
}
