using System.Text;
using BlazecoinWallet.Core.Services.Export;

namespace BlazecoinWallet.Core.Tests;

/// <summary>ITransactionExportService serialization of a generic ExportTable.</summary>
public class TransactionExportServiceTests
{
    static TransactionExportServiceTests()
    {
        // QuestPDF requires a license to be set before generating a PDF (normally done
        // in MauiProgram). Set it here so the PDF test can run headless.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static ExportTable Table() => new ExportTable(
        "Blazecoin Transactions — 1 rows",
        new[] { "Date", "Amount" },
        new IReadOnlyList<object>[] { new object[] { "2026-01-01", 1.5m } });

    private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void csv_has_header_and_row()
    {
        var (ext, bytes) = new TransactionExportService().Build("csv", Table());
        Assert.Equal("csv", ext);
        var text = Text(bytes);
        Assert.Contains("Date,Amount", text);
        Assert.Contains("1.5", text);
    }

    [Fact]
    public void json_is_keyed_by_header()
    {
        var (ext, bytes) = new TransactionExportService().Build("json", Table());
        Assert.Equal("json", ext);
        var text = Text(bytes);
        Assert.Contains("\"Date\"", text);
        Assert.Contains("\"Amount\"", text);
    }

    [Fact]
    public void xlsx_and_pdf_produce_nonempty_bytes()
    {
        var svc = new TransactionExportService();
        Assert.NotEmpty(svc.Build("xlsx", Table()).Bytes);
        Assert.NotEmpty(svc.Build("pdf", Table()).Bytes);
    }

    [Fact]
    public void unknown_format_throws()
    {
        Assert.Throws<ArgumentException>(() => new TransactionExportService().Build("txt", Table()));
    }
}
