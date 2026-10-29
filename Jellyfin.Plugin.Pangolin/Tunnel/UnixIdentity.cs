using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Pangolin.Tunnel;

/// <summary>
/// Reads the user ID the Jellyfin process runs as.
/// </summary>
internal static partial class UnixIdentity
{
    /// <summary>
    /// Gets the effective user ID of the current process.
    /// </summary>
    public static uint EffectiveUserId => GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial uint GetEffectiveUserId();
}
