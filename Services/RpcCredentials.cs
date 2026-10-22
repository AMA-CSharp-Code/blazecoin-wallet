using System.Text;
using Microsoft.Extensions.Configuration;

namespace BlazecoinWallet.Maui.Services;

/// <summary>Supplies the daemon RPC basic-auth credentials. Prefers explicit
/// <c>rpcuser</c>/<c>rpcpassword</c> from config (back-compat / when a shared
/// setup needs them); otherwise reads Bitcoin Core's auto-generated <c>.cookie</c>
/// from the datadir — so a fresh install ships NO shared password (the cookie is a
/// fresh random secret per daemon start). Re-read on each call so a daemon restart
/// that rotates the cookie is picked up without rebuilding the HttpClient.</summary>
public sealed class RpcCredentials
{
    private readonly IConfiguration _config;
    private readonly string? _explicit;   // base64("user:pass") when explicit creds are configured

    public RpcCredentials(IConfiguration config)
    {
        _config = config;
        var u = config["Blazecoind:RpcUser"];
        var p = config["Blazecoind:RpcPassword"];
        if (!string.IsNullOrEmpty(u) && !string.IsNullOrEmpty(p))
            _explicit = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{u}:{p}"));
    }

    /// <summary>The base64 "user:pass" value for the HTTP Basic header, or null if no
    /// credentials are available (explicit unset AND no cookie yet).</summary>
    public string? GetBasicAuth()
    {
        if (_explicit is not null) return _explicit;
        try
        {
            var cookie = Path.Combine(DataDir(), ".cookie");
            if (!File.Exists(cookie)) return null;
            // The cookie file content is literally "__cookie__:<random>" — i.e. the
            // user:password pair Core expects; base64 it straight for Basic auth.
            var content = File.ReadAllText(cookie).Trim();
            return content.Length == 0 ? null : Convert.ToBase64String(Encoding.ASCII.GetBytes(content));
        }
        catch { return null; }
    }

    private string DataDir()
    {
        var dir = _config["Blazecoind:DataDir"];
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        return DefaultDataDir();
    }

    /// <summary>Mirror of blazecoind's <c>GetDefaultDataDir()</c> (args.cpp), per platform
    /// — see <see cref="BlazecoinWallet.Core.Services.DaemonPaths"/>. The wallet MUST
    /// resolve identically to the daemon or a zero-config install reads its
    /// <c>.cookie</c> from a directory the daemon never writes to.</summary>
    internal static string DefaultDataDir() => BlazecoinWallet.Core.Services.DaemonPaths.DefaultDataDir();
}
