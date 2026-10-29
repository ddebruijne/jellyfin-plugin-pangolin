using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Pangolin.Cli;

/// <summary>
/// Locates the pangolin-userspace helper shipped in the plugin folder.
/// </summary>
public static class UserspaceHelper
{
    /// <summary>
    /// Returns the path of the helper for this machine, making sure it is executable.
    /// </summary>
    /// <returns>The absolute path to the helper.</returns>
    public static string GetExecutablePath()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new TunnelConfigurationException("Userspace mode is only available on Linux. Use system mode instead.");
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            var other => throw new TunnelConfigurationException($"Userspace mode has no build for {other}. Use system mode instead.")
        };

        var assemblyPath = Plugin.Instance?.AssemblyFilePath ?? typeof(Plugin).Assembly.Location;
        var path = Path.Combine(Path.GetDirectoryName(assemblyPath) ?? string.Empty, "pangolin-userspace_linux_" + arch);
        if (!File.Exists(path))
        {
            throw new TunnelConfigurationException($"The userspace helper is missing ({path}). Reinstall the plugin.");
        }

        // Zip extraction doesn't always keep the executable bit.
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((mode & UnixFileMode.UserExecute) == 0)
        {
            File.SetUnixFileMode(path, mode | Execute);
        }

        return path;
    }
}
