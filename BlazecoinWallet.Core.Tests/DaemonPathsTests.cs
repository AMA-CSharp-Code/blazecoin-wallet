using BlazecoinWallet.Core.Services;

namespace BlazecoinWallet.Core.Tests;

/// <summary>DaemonPaths mirrors blazecoind's GetDefaultDataDir(): a rooted path whose
/// location is per-platform, and executable names that carry .exe only on Windows.
/// Asserts the branch for whatever OS hosts the test run (Windows on the dev box; the
/// macOS branch is what the Mac Catalyst head relies on for the RPC cookie).</summary>
public class DaemonPathsTests
{
    [Fact]
    public void default_datadir_is_rooted_and_named_for_v2()
    {
        var dir = DaemonPaths.DefaultDataDir();
        Assert.True(Path.IsPathRooted(dir));
        Assert.Contains("blazecoinv2.0", dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void default_datadir_matches_the_platform_branch()
    {
        var dir = DaemonPaths.DefaultDataDir();
        if (OperatingSystem.IsWindows())
        {
            // Legacy Roaming wins if present (this dev box), else Local — args.cpp's rule.
            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DaemonPaths.DataDirName);
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DaemonPaths.DataDirName);
            Assert.Equal(Directory.Exists(roaming) ? roaming : local, dir);
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            Assert.EndsWith(Path.Combine("Library", "Application Support", DaemonPaths.DataDirName), dir);
            Assert.DoesNotContain(".config", dir);   // the SpecialFolder.ApplicationData trap
        }
        else
        {
            Assert.Equal(".blazecoinv2.0", Path.GetFileName(dir));
        }
    }

    [Fact]
    public void executable_names_have_exe_only_on_windows()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        Assert.Equal("blazecoind" + exe, DaemonPaths.DaemonFileName);
        Assert.Equal("blazecoin-cli" + exe, DaemonPaths.CliFileName);
    }
}
