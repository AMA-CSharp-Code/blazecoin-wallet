using System.Globalization;
using System.Text;
using System.Text.Json;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace BlazecoinWallet.Core.Services.Export;

/// <summary>Default <see cref="ITransactionExportService"/>. The CSV/JSON/XLSX/PDF
/// generation moved verbatim out of Transactions.razor, operating on the generic
/// <see cref="ExportTable"/> instead of the page's instance state. Cell
/// stringification (for CSV/PDF) matches the page's old <c>CellString</c> exactly:
/// decimals as <c>0.########</c> invariant, ints invariant, everything else
/// <c>ToString()</c>.</summary>
public sealed class TransactionExportService : ITransactionExportService
{
    public (string Ext, byte[] Bytes) Build(string format, ExportTable table) => format switch
    {
        "csv"  => ("csv",  Encoding.UTF8.GetBytes(BuildCsv(table))),
        "json" => ("json", Encoding.UTF8.GetBytes(BuildJson(table))),
        "xlsx" => ("xlsx", BuildXlsx(table)),
        "pdf"  => ("pdf",  BuildPdf(table)),
        _ => throw new ArgumentException($"Unknown export format '{format}'.", nameof(format)),
    };

    private static string CellString(object v) => v switch
    {
        decimal d => d.ToString("0.########", CultureInfo.InvariantCulture),
        int n     => n.ToString(CultureInfo.InvariantCulture),
        _         => v?.ToString() ?? "",
    };

    private static string BuildCsv(ExportTable table)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", table.Headers.Select(CsvEscape)));
        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(",", row.Select(c => CsvEscape(CellString(c)))));
        return sb.ToString();
    }

    private static string CsvEscape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string BuildJson(ExportTable table)
    {
        var rows = table.Rows.Select(row =>
        {
            var dict = new Dictionary<string, object>(table.Headers.Count);
            for (int j = 0; j < table.Headers.Count; j++)
                dict[table.Headers[j]] = row[j];
            return dict;
        });
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    private static byte[] BuildXlsx(ExportTable table)
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Transactions");
        for (int j = 0; j < table.Headers.Count; j++)
            ws.Cell(1, j + 1).Value = table.Headers[j];
        ws.Row(1).Style.Font.Bold = true;
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            for (int j = 0; j < row.Count; j++)
            {
                var cell = ws.Cell(i + 2, j + 1);
                switch (row[j])
                {
                    case decimal d: cell.Value = d; break;
                    case int n:     cell.Value = n; break;
                    case string s:  cell.Value = s; break;
                }
            }
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // QuestPDF is initialised HERE, on first PDF export, not at app startup: its native library
    // (libQuestPdfSkia) ships for macOS 15+ only, and an eager touch in MauiProgram killed the whole
    // app on older Macs before any UI appeared (Intel Mac, 2026-09-24). A native library that
    // won't load must cost the user the PDF format, not the wallet.
    private static byte[] BuildPdf(ExportTable table)
    {
        try
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;   // required before generating; idempotent
            return BuildPdfCore(table);
        }
        catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException or BadImageFormatException
                                   || ex.InnerException is DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException(
                "PDF export isn't available on this system: the PDF engine's native library could not be loaded"
                + " (on macOS it needs macOS 15 or newer). CSV, JSON and XLSX exports still work.", ex);
        }
    }

    private static byte[] BuildPdfCore(ExportTable table)
    {
        return QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A4.Landscape());
                page.Margin(18);
                page.DefaultTextStyle(t => t.FontSize(8));
                page.Header().PaddingBottom(6)
                    .Text(table.Title).SemiBold().FontSize(12);
                page.Content().Table(t =>
                {
                    t.ColumnsDefinition(d => { foreach (var _ in table.Headers) d.RelativeColumn(); });
                    foreach (var h in table.Headers)
                        t.Cell().Background("#222222").Padding(3)
                            .Text(h).FontColor("#ffffff").SemiBold();
                    foreach (var row in table.Rows)
                        foreach (var c in row)
                            t.Cell().BorderBottom(0.5f).BorderColor("#dddddd").Padding(3)
                                .Text(CellString(c));
                });
                page.Footer().AlignRight().Text(t => { t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
            });
        }).GeneratePdf();
    }
}
