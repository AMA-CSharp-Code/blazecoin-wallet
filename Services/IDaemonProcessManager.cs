namespace BlazecoinWallet.Maui.Services;

/// <summary>Owns the local <c>blazecoind</c> process lifecycle for the desktop GUI:
/// launches it on startup if it isn't already running, and gracefully stops it when
/// the app closes — but ONLY if this app started it. A daemon that was already
/// running when the GUI launched (e.g. one kept up for solo-proxy mining) is adopted
/// for the session and left running on close.</summary>
public interface IDaemonProcessManager
{
    /// <summary>True if this app launched the daemon (so it should stop it on exit).
    /// False if the daemon was already running and merely adopted.</summary>
    bool WeStartedIt { get; }

    /// <summary>If no daemon is listening on the RPC port, launch one (from the
    /// configured/auto-resolved <c>blazecoind</c> executable — beside the app on
    /// Windows, inside the .app bundle on macOS). No-op if one is already up or no
    /// executable can be found.</summary>
    Task EnsureRunningAsync();

    /// <summary>If this app started the daemon, send a graceful <c>stop</c> and wait
    /// (bounded) for it to exit so LevelDB flushes cleanly. No-op otherwise.</summary>
    Task StopIfOwnedAsync();
}
