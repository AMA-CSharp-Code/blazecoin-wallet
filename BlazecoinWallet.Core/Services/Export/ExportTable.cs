namespace BlazecoinWallet.Core.Services.Export;

/// <summary>A format-agnostic tabular snapshot handed to
/// <see cref="ITransactionExportService"/>. The caller resolves all display
/// concerns (which columns are visible, header text, display units, labels) and
/// hands over plain typed cells; the export service only serializes.
///
/// Each cell is a boxed <c>decimal</c>, <c>int</c>, or <c>string</c> — the same
/// typed values the Transactions page produced. Numeric formats (XLSX) keep them
/// numeric; text formats (CSV/PDF) stringify decimals as <c>0.########</c> and
/// ints invariantly.</summary>
public sealed record ExportTable(
    string Title,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<object>> Rows);
