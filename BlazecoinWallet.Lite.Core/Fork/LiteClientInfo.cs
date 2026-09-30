using System.Reflection;

namespace BlazecoinWallet.Lite.Fork;

/// <summary>
/// What THIS lite build is: its version and the set of consensus forks whose rules it carries.
/// The §7.1 banner compares both against the gateway's announcement. The version is owned by the
/// UI assembly (<c>&lt;Version&gt;</c> in BlazecoinWallet.Lite.UI.csproj — the lite release
/// number that the website's Wallet page quotes); the layout sets it here at load so Lite.Core,
/// the Settings "Version" line and the banner all quote the same string, and the loaded UI
/// assembly is found by name as the fallback so the figure is right even before that call.
/// </summary>
public static class LiteClientInfo
{
    /// <summary>The assembly whose <c>&lt;Version&gt;</c> is the lite release number.</summary>
    public const string UiAssemblyName = "BlazecoinWallet.Lite.UI";

    private static string? _version;

    /// <summary>
    /// Forks whose rules this build carries, by codename (case-insensitive): Phoenix-413
    /// (activated 2026-08-26, verified bit-exact) and pqsig (P2PQH, H_Q = 4,250,000 chosen
    /// 2026-09-15; this build carries the lite fork rules — PqInputVerifier + BQ receive/send gated
    /// on BlazecoinChain.PqSigActivationHeight). A build that does NOT ship a fork's rules must not
    /// list it, or the Stopped state could never fire for exactly the client it exists to warn.
    /// </summary>
    public static IReadOnlySet<string> SupportedForks { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "phoenix413", "pqsig" };

    /// <summary>The running build's version (e.g. <c>2.0.5</c>): the value set by the head, else
    /// the loaded UI assembly's informational version, else Lite.Core's own.</summary>
    public static string Version => _version ?? FromAssembly(FindUiAssembly() ?? typeof(LiteClientInfo).Assembly);

    /// <summary>
    /// True on the Blazor WebAssembly head, where "reload" fetches the redeployed bundle (index.html
    /// is served no-cache, there is no service worker). On Android/iOS a reload changes nothing,
    /// so the banner's "Reload to update" hint is suppressed. Settable so component tests can
    /// exercise the web-head wording off-browser.
    /// </summary>
    public static bool IsWebHead { get; set; } = OperatingSystem.IsBrowser();

    /// <summary>Sets the build version (the layout does this from the UI assembly's metadata at load).
    /// A null/blank value is ignored so a bad attribute can't blank the banner's "vX".</summary>
    public static void SetVersion(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version)) _version = version.Trim();
    }

    /// <summary>Reads an assembly's informational version (without any <c>+commit</c> suffix),
    /// falling back to its assembly version's first three parts.</summary>
    public static string FromAssembly(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static Assembly? FindUiAssembly()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, UiAssemblyName, StringComparison.Ordinal));
        }
        catch { return null; }
    }
}
