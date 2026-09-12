namespace BlazecoinWallet.Core.Services.Export;

/// <summary>Serializes an <see cref="ExportTable"/> to a downloadable file body
/// in one of the supported formats (SOLID audit #7 — pulled out of
/// Transactions.razor so the heavy CSV/JSON/XLSX/PDF generation is testable and
/// the page just builds the table + drives the platform save dialog).</summary>
public interface ITransactionExportService
{
    /// <param name="format">"csv", "json", "xlsx", or "pdf" (case-insensitive).</param>
    /// <returns>The file extension and the serialized bytes.</returns>
    /// <exception cref="ArgumentException">Unknown format.</exception>
    (string Ext, byte[] Bytes) Build(string format, ExportTable table);
}
