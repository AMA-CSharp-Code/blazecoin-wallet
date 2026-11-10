using System.Globalization;

namespace BlazecoinWallet.Core.Services;

/// <summary>
/// An amount field a person types into: the text as shown, and the number it means.
/// <para>Why (bug-bounty report 2026-11-09): the desktop Send page used a browser number field.
/// Chromium decides which decimal mark such a field accepts from the app's display language and
/// silently DROPS the other one — on an English-language Windows, typing "1,5" (one and a half,
/// as most of Europe and Indonesia write it) left "15" in the field, ten times the amount. A
/// plain text field read through <see cref="AmountText"/> has one grammar on every machine: a
/// dot or a comma is the decimal mark, and anything else is refused out loud, never dropped.</para>
/// <para>After a successful read the text is rewritten in the wallet's own form ("1,5" becomes
/// "1.5") so the field shows what the wallet understood.</para>
/// </summary>
public sealed class TypedAmount
{
    /// <summary>BLZ has 8 decimal places; a ninth cannot be paid.</summary>
    public const int MaxDecimals = 8;

    public const string InvalidMessage =
        "That amount could not be read. Type it as a plain number such as 1.5 — digits with one decimal point " +
        "(a comma also works), up to 8 decimal places, no spaces and no thousands separators.";

    /// <summary>What the field shows.</summary>
    public string Text { get; private set; } = "";

    /// <summary>The number the text means; null while the field is empty or unreadable.</summary>
    public decimal? Value { get; private set; }

    /// <summary>True when something is typed and it is not an amount. Never true for an empty field.</summary>
    public bool IsInvalid { get; private set; }

    /// <summary>The user typed (or pasted) this.</summary>
    public void SetText(string? text)
    {
        var typed = text?.Trim() ?? "";
        if (typed.Length == 0) { Text = ""; Value = null; IsInvalid = false; return; }

        if (AmountText.TryParseTyped(typed, out var value) && decimal.Round(value, MaxDecimals) == value)
        {
            Value = value;
            Text = Format(value);
            IsInvalid = false;
            return;
        }

        Text = typed;          // left as typed, so the user can see and fix it
        Value = null;
        IsInvalid = true;
    }

    /// <summary>The app set the amount (a payment link, the step arrows, Send Max, a reset).</summary>
    public void SetValue(decimal? value)
    {
        Value = value;
        Text = value is { } v ? Format(v) : "";
        IsInvalid = false;
    }

    private static string Format(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
