namespace BlazecoinWallet.Lite;

/// <summary>The animated background skins the wallet can paint behind its UI.</summary>
public enum LiteSkin
{
    /// <summary>Plain dark background, no animation.</summary>
    None,

    /// <summary>The V2.0 desktop wallet's "Pixelated Coins" grid (coin-skin.js).</summary>
    PixelatedCoins,

    /// <summary>The website User-account page's spinning-axe pattern (axe-skin.js).</summary>
    AxePattern,

    /// <summary>The V2.0 desktop wallet's "Digital Fire Fighters" scrolling art rows,
    /// mobile-tuned to a curated image set (firefighter-skin.js).</summary>
    DigitalFireFighters,

    /// <summary>Pick one of the animated skins at random on each launch.</summary>
    Random,
}

/// <summary>
/// Which animated background skin is shown behind the wallet UI. Purely cosmetic, persisted
/// per platform. Default = <see cref="LiteSkinResolver.Default"/> (Digital Fire Fighters since
/// 2026-08-19; was Random); users on a battery
/// budget (or who just prefer plain) switch it in Settings, and every skin self-disables
/// under <c>prefers-reduced-motion</c> and pauses while the app is backgrounded.
/// </summary>
public interface ISkinSettings
{
    LiteSkin Skin { get; }
    void SetSkin(LiteSkin skin);
}

/// <summary>Resolves the user's skin choice to a concrete paintable skin —
/// <see cref="LiteSkin.Random"/> rolls one of the animated skins, fresh on each call
/// (so each launch, and each re-pick in Settings, can land differently).</summary>
public static class LiteSkinResolver
{
    /// <summary>The skin a fresh install (or an unrecognised stored value) gets. ONE definition —
    /// every persistent store (Droid/iOS Preferences, browser localStorage, in-memory) falls back
    /// to this rather than naming a skin itself, so the default cannot drift between heads.
    /// Digital Fire Fighters since 2026-08-19 (Andrew's call; was Random).</summary>
    public const LiteSkin Default = LiteSkin.DigitalFireFighters;

    private static readonly LiteSkin[] Animated =
        [LiteSkin.PixelatedCoins, LiteSkin.AxePattern, LiteSkin.DigitalFireFighters];

    public static LiteSkin Resolve(LiteSkin choice) =>
        choice == LiteSkin.Random ? Animated[System.Random.Shared.Next(Animated.Length)] : choice;
}

/// <summary>Non-persistent default (dev/test); real heads register a persistent one.</summary>
public sealed class InMemorySkinSettings : ISkinSettings
{
    public LiteSkin Skin { get; private set; } = LiteSkinResolver.Default;
    public void SetSkin(LiteSkin skin) => Skin = skin;
}
