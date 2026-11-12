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

    // Hardening note 2026-11-12 (round fourteen): a label that starts like a formula was written to
    // the CSV as it stood, so a spreadsheet opened it as a live formula. A label reaches the address
    // book from a scanned payment QR, so the text is not the user's own.
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil.test\",\"click\")", "\"'=HYPERLINK(\"\"http://evil.test\"\",\"\"click\"\")\"")]
    [InlineData("=2+2", "'=2+2")]
    [InlineData("+2+2", "'+2+2")]
    [InlineData("-2+2", "'-2+2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\t=2+2", "'\t=2+2")]
    [InlineData("\r=2+2", "\"'\r=2+2\"")]
    public void csv_text_that_starts_like_a_formula_is_written_as_text(string label, string expectedCell)
    {
        var table = new ExportTable("t", new[] { "Label", "Amount" },
            new IReadOnlyList<object>[] { new object[] { label, 1.5m } });

        Assert.Equal(expectedCell + ",1.5", RowAfterHeader(table));
    }

    private static string RowAfterHeader(ExportTable table)
    {
        var text = Text(new TransactionExportService().Build("csv", table).Bytes);
        return text[(text.IndexOf(Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length)..].TrimEnd('\r', '\n');
    }

    [Fact]
    public void csv_numbers_and_ordinary_text_are_left_alone()
    {
        // A negative amount is a number, typed or as text, and "Smith, John" only needs its quotes.
        var table = new ExportTable("t", new[] { "Label", "Amount", "Net", "Date" },
            new IReadOnlyList<object>[] { new object[] { "Smith, John", -1.5m, "-0.001", "2026-11-12" }, new object[] { "Alice", -3, "+2", "x-y" } });

        var text = Text(new TransactionExportService().Build("csv", table).Bytes);

        Assert.Contains("\"Smith, John\",-1.5,-0.001,2026-11-12", text);
        Assert.Contains("Alice,-3,+2,x-y", text);
        Assert.DoesNotContain("'", text);
    }

    [Fact]
    public void csv_headers_get_the_same_treatment()
    {
        var table = new ExportTable("t", new[] { "=cmd", "Amount" }, new IReadOnlyList<object>[] { new object[] { "a", 1m } });
        Assert.StartsWith("'=cmd,Amount", Text(new TransactionExportService().Build("csv", table).Bytes));
    }

    [Fact]
    public void xlsx_keeps_a_formula_looking_label_as_text()
    {
        var table = new ExportTable("t", new[] { "Label" }, new IReadOnlyList<object>[] { new object[] { "=2+2" } });

        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(new TransactionExportService().Build("xlsx", table).Bytes));
        var cell = wb.Worksheet(1).Cell(2, 1);

        Assert.False(cell.HasFormula);
        Assert.Equal("=2+2", cell.GetString());
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
