namespace BlazecoinWallet.Lite;

/// <summary>The user's chosen amount display unit, persisted per platform. Display-only — the
/// wallet always works in satoshis, so changing it never affects a signed transaction.</summary>
public interface IUnitSettings
{
    LiteUnit Unit { get; }
    void Set(LiteUnit unit);
}

/// <summary>Non-persistent default (dev/test); real heads register a persistent one.</summary>
public sealed class InMemoryUnitSettings : IUnitSettings
{
    public LiteUnit Unit { get; private set; } = LiteUnit.Blz;
    public void Set(LiteUnit unit) => Unit = unit;
}
