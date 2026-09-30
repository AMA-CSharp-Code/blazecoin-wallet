using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BlazecoinWallet.Core.Services;          // INodeRpc
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlazecoinWallet.Maui.Services;

/// <summary>Default <see cref="IDaemonProcessManager"/>. "Is the daemon up?" is a raw
/// TCP connect to the RPC port — true the moment the daemon is listening, even mid
/// warmup (-28), which an RPC ping would report as not-ready and cause us to launch a
/// second, colliding daemon.</summary>
public sealed class DaemonProcessManager : IDaemonProcessManager
{
    private readonly INodeRpc _node;
    private readonly IConfiguration _config;
    private readonly ILogger<DaemonProcessManager>? _log;
    private Process? _proc;

    public bool WeStartedIt { get; private set; }

    public DaemonProcessManager(INodeRpc node, IConfiguration config, ILogger<DaemonProcessManager>? log = null)
    {
        _node = node;
        _config = config;
        _log = log;
    }

    public async Task EnsureRunningAsync()
    {
        var (host, port) = RpcEndpoint();

        // Something already listening on the RPC port?
        if (await IsListeningAsync(host, port))
        {
            // Verify it's actually blazecoind before trusting it — a local process
            // squatting the RPC port could otherwise be handed wallet RPC (including an
            // unlock passphrase). Only adopt if it identifies as a Blazecoin daemon.
            if (await LooksLikeBlazecoindAsync())
            {
                WeStartedIt = false;
                _log?.LogInformation("blazecoind already running on {Host}:{Port} — adopted (won't stop on exit).", host, port);
            }
            else
            {
                _log?.LogWarning("Port {Host}:{Port} is occupied but does NOT identify as a Blazecoin daemon — not adopting; RPC will fail until it's freed.", host, port);
            }
            return;
        }

        var exe = ResolveExePath();
        if (exe is null)
        {
            // No bundled/configured daemon to launch (e.g. the dev box runs it from a
            // script). Leave it; the startup screen will wait for it to appear.
            _log?.LogInformation("No {Exe} configured or found beside the app — not auto-launching.", DaemonPaths.DaemonFileName);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var dataDir = _config["Blazecoind:DataDir"];
            if (!string.IsNullOrWhiteSpace(dataDir)) psi.ArgumentList.Add($"-datadir={dataDir}");

            // Provenance walks a coin's ancestry through FOREIGN transactions, which
            // getrawtransaction can only serve with a transaction index; without one the
            // Provenance page traces nothing past a coin's own wallet tx (first Mac run,
            // 2026-09-24: every received coin came back unreadable, and the shipped conf
            // template never set it). Core builds the index in the background from genesis
            // the first time — minutes on this chain. A conf that says txindex=0 loses to
            // this argument, deliberately: the wallet's features depend on it.
            psi.ArgumentList.Add("-txindex=1");

            // macOS/Linux parity with the Windows installer, which lays the default
            // blazecoin.conf into the datadir and never overwrites one that exists; the
            // bundle carries the same template as Resources/blazecoin.conf.example.
            if (!OperatingSystem.IsWindows()) EnsureDefaultConf(dataDir);

            _proc = Process.Start(psi);
            WeStartedIt = _proc is not null;
            _log?.LogInformation("Launched blazecoind ({Exe}) — pid {Pid}.", exe, _proc?.Id);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to launch blazecoind ({Exe}).", exe);
            WeStartedIt = false;
        }
    }

    public async Task StopIfOwnedAsync()
    {
        if (!WeStartedIt) return;

        // 1. Graceful via RPC — flushes LevelDB cleanly. IMPORTANT: 'stop' is REJECTED
        //    while the daemon is loading the block index (RPC error -28); it only takes
        //    once the node is warmed.
        try { await _node.ExecuteCommandAsync("stop"); }
        catch (Exception ex) { _log?.LogWarning(ex, "RPC stop failed/rejected (likely -28 warmup)."); }
        if (await WaitForExitAsync(TimeSpan.FromSeconds(12))) return;

        // 2. Still running — almost certainly mid-warmup, where RPC 'stop' can't reach it.
        //    A console CTRL_BREAK (Windows) or SIGTERM (macOS) triggers a GRACEFUL shutdown
        //    even during the load (the load loop checks ShutdownRequested()), so the node
        //    still flushes cleanly.
        var signalled = OperatingSystem.IsWindows() ? TrySendCtrlBreak() : TrySendSigTerm();
        if (signalled && await WaitForExitAsync(TimeSpan.FromSeconds(15))) return;

        // 3. Last resort — force-kill so we never orphan a daemon holding the datadir
        //    lock + ports. During warmup little is loaded, so next start just re-reads.
        try
        {
            _log?.LogInformation("Graceful stop + break signal didn't complete — terminating the daemon we started.");
            _proc?.Kill(entireProcessTree: true);
            await WaitForExitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { _log?.LogWarning(ex, "Force-stopping blazecoind failed."); }
    }

    // Sends a console CTRL_BREAK to the daemon we launched — a graceful shutdown even mid
    // block-index load, unlike RPC 'stop'. The daemon has its own (hidden) console from
    // CreateNoWindow; we briefly attach to it, mute our own ctrl handler so the signal
    // doesn't quit the GUI too, then broadcast to that console. Windows-only.
    private bool TrySendCtrlBreak()
    {
        if (_proc is null || _proc.HasExited || !OperatingSystem.IsWindows()) return false;
        bool muted = false, attached = false;
        try
        {
            // Mute OUR ctrl handler FIRST, before touching any console. The mute is a
            // process-wide setting independent of which console is attached, so doing it
            // up front closes the window where a CTRL_BREAK could land between
            // AttachConsole and the mute and quit the GUI too.
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            muted = true;

            FreeConsole();
            if (!AttachConsole((uint)_proc.Id)) return false;
            attached = true;

            // Group 0 = "every process sharing the calling process's console". The daemon
            // was launched with CreateNoWindow, so it owns a PRIVATE hidden console with
            // only itself in it; once we attach to that console, group 0 reaches the
            // daemon and nothing else. (Targeting its PID as an explicit process group
            // would require launching it with CREATE_NEW_PROCESS_GROUP, which .NET's
            // ProcessStartInfo can't request without a full CreateProcess P/Invoke — not
            // worth it when the private-console scoping already isolates the signal.)
            return GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, 0);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "CTRL_BREAK to blazecoind failed."); return false; }
        finally
        {
            if (attached) FreeConsole();
            if (muted) SetConsoleCtrlHandler(IntPtr.Zero, false); // restore our handler
        }
    }

    private const uint CTRL_BREAK_EVENT = 1;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr HandlerRoutine, bool Add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    // Unix twin of the CTRL_BREAK path (macOS / Mac Catalyst head, 2026-09-22): SIGTERM is
    // the daemon's graceful-stop signal and, like CTRL_BREAK, is honoured even mid
    // block-index load. Never reached on Windows.
    private bool TrySendSigTerm()
    {
        if (_proc is null || _proc.HasExited || OperatingSystem.IsWindows()) return false;
        try { return kill(_proc.Id, SIGTERM) == 0; }
        catch (Exception ex) { _log?.LogWarning(ex, "SIGTERM to blazecoind failed."); return false; }
    }

    private const int SIGTERM = 15;
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);

    private async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        if (_proc is null || _proc.HasExited) return true;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await _proc.WaitForExitAsync(cts.Token);
            return true;
        }
        catch { return _proc.HasExited; }
    }

    // Daemon executable path: explicit config wins; otherwise look where a bundled
    // install ships it (see the end of the method). Null → nothing to launch.
    // First run on macOS/Linux: copy the bundled conf template beside the node's data so the
    // node runs with the same defaults a Windows install gets (seed addnodes, fee floors,
    // dbcache, wallet=Primary autoload). Never touches an existing conf; best effort.
    private void EnsureDefaultConf(string? configuredDataDir)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(configuredDataDir) ? DaemonPaths.DefaultDataDir() : configuredDataDir;
            var conf = Path.Combine(dir, "blazecoin.conf");
            if (File.Exists(conf)) return;
            var template = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "blazecoin.conf.example"));
            if (!File.Exists(template)) return;
            Directory.CreateDirectory(dir);
            File.Copy(template, conf);
            _log?.LogInformation("Laid the default blazecoin.conf into {Dir} (first run).", dir);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not lay the default blazecoin.conf — the node runs on built-in defaults.");
        }
    }

    private string? ResolveExePath()
    {
        var configured = _config["Blazecoind:ExePath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Only honour an absolute LOCAL path. Reject relative paths and UNC
            // (\\server\share) so a tampered/odd config can't point the launch at a
            // remote share or a path resolved relative to the working directory.
            if (!Path.IsPathFullyQualified(configured)
                || configured.StartsWith(@"\\", StringComparison.Ordinal)
                || configured.StartsWith("//", StringComparison.Ordinal))
            {
                _log?.LogWarning("Ignoring non-absolute/UNC Blazecoind:ExePath: {Path}", configured);
                return null;
            }
            if (File.Exists(configured)) return configured;
            // Configured path missing (e.g. a dev path baked into config running on a
            // user machine) — fall through to the bundled-daemon lookup instead of
            // silently refusing to launch anything.
            _log?.LogWarning("Configured Blazecoind:ExePath does not exist ({Path}) — falling back to the app directory.", configured);
        }

        // Bundled daemon: beside the app on Windows (the installer lays it there); inside
        // the .app on macOS, where AppContext.BaseDirectory is the managed-code folder
        // and installer/Build-MacRelease.sh puts the node in Contents/MacOS. The extra
        // candidates simply don't exist on Windows.
        var name = DaemonPaths.DaemonFileName;
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "..", "MacOS"), Path.Combine(baseDir, "..", "Resources") })
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, name));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Confirms the RPC-port listener is really blazecoind (not a squatter) via an
    // authenticated getnetworkinfo before we trust/adopt it. A -28 warmup error still
    // proves a Bitcoin-Core-family RPC server, so that counts as ours.
    private async Task<bool> LooksLikeBlazecoindAsync()
    {
        try
        {
            var info = await _node.ExecuteCommandAsync("getnetworkinfo");
            return info.Contains("Blazecoin", StringComparison.OrdinalIgnoreCase);
        }
        catch (RpcException ex) when (ex.Code == -28)
        {
            return true;   // Core daemon mid-warmup (RPC up, returning "Loading…")
        }
        catch { return false; }
    }

    private (string host, int port) RpcEndpoint()
    {
        var url = _config["Blazecoind:RpcUrl"] ?? "http://127.0.0.1:55413";
        try { var u = new Uri(url); return (u.Host, u.Port); }
        catch { return ("127.0.0.1", 55413); }
    }

    private static async Task<bool> IsListeningAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            await connect.WaitAsync(TimeSpan.FromSeconds(2));
            return client.Connected;
        }
        catch { return false; }
    }
}
