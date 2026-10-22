using System;

namespace BlazecoinWallet.Core.Services;

/// <summary>Display helpers for wallet transaction "category" values
/// (send / receive / generate / immature / orphan), shared by the Dashboard
/// "Recent Transactions" table and the full Transactions page so both colour-code
/// and label them identically.</summary>
public static class TxDisplay
{
    /// <summary>Friendly label for the Type column: "generate" shows as "Mined";
    /// everything else is shown as-is (the table CSS capitalises it).</summary>
    public static string TypeLabel(string? category) =>
        string.Equals(category, "generate", StringComparison.OrdinalIgnoreCase)
            ? "Mined"
            : category ?? "";

    /// <summary>CSS class that colours the Type / Amount cells by category:
    /// send=red, receive=green (the Blockchain "Synced" colour), generate(Mined)=azure,
    /// immature=hot pink, orphan=light grey. Anything unknown returns "" — left default.</summary>
    public static string ColourClass(string? category) => (category ?? "").ToLowerInvariant() switch
    {
        "send"     => "send-cell",
        "receive"  => "receive-cell",
        "generate" => "mined-cell",
        "immature" => "immature-cell",
        "orphan"   => "orphan-cell",
        _          => "",
    };
}
