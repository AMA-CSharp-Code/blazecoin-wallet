namespace BlazecoinWallet.Core.Services;

/// <summary>Where <c>blazecoind</c> keeps its data when no <c>-datadir</c> is given, and
/// what its executables are called — a mirror of the daemon's <c>GetDefaultDataDir()</c>
/// (<c>src/common/args.cpp</c>). The wallet MUST resolve this identically to the daemon:
/// a zero-config install reads the RPC <c>.cookie</c> from here, and wallet removal
/// deletes a folder under here.
/// <list type="bullet">
/// <item>Windows: the legacy Roaming <c>%APPDATA%\BlazecoinV2.0</c> wins if it exists
///       (this dev box), else <c>%LOCALAPPDATA%\BlazecoinV2.0</c> (fresh installs).</item>
/// <item>macOS (incl. the Mac Catalyst head): <c>~/Library/Application Support/BlazecoinV2.0</c>.</item>
/// <item>Other Unix: <c>~/.blazecoinv2.0</c>.</item>
/// </list>
/// ⚠️ .NET maps <see cref="Environment.SpecialFolder.ApplicationData"/> to <c>~/.config</c>
/// and <see cref="Environment.SpecialFolder.LocalApplicationData"/> to <c>~/.local/share</c>
/// on macOS — NOT to Application Support — so the Windows branch cannot be reused there
/// (2026-09-22, found while building the macOS head).</summary>
public static class DaemonPaths
{
    public const string DataDirName = "BlazecoinV2.0";

    /// <summary>The daemon's default datadir on this platform (may not exist yet).</summary>
    public static string DefaultDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataDirName);
            if (Directory.Exists(roaming)) return roaming;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? "/";

        // OperatingSystem.IsMacOS() is FALSE under Mac Catalyst; the daemon, a plain
        // macOS binary, uses the same Application Support path in both cases.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return Path.Combine(home, "Library", "Application Support", DataDirName);

        return Path.Combine(home, ".blazecoinv2.0");
    }

    /// <summary>Platform file name of the node executable (<c>blazecoind.exe</c> / <c>blazecoind</c>).</summary>
    public static string DaemonFileName => OperatingSystem.IsWindows() ? "blazecoind.exe" : "blazecoind";

    /// <summary>Platform file name of the RPC client (<c>blazecoin-cli.exe</c> / <c>blazecoin-cli</c>).</summary>
    public static string CliFileName => OperatingSystem.IsWindows() ? "blazecoin-cli.exe" : "blazecoin-cli";
}
