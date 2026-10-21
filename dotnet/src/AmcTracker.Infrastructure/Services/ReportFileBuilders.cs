using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AmcTracker.Infrastructure.Services;

/// <summary>
/// Excel (ClosedXML, MIT). Company name, report title, date range and generated time above the table; then a bold,
/// frozen header row with filters. Dates are real date cells, money is a number cell with two decimals and the ₹
/// format (Indian grouping), phone numbers are text so they are never reformatted. No totals row.
/// </summary>
public class ExcelReportBuilder : IReportFileBuilder
{
    /// <summary>Row of the table's header. The company, title, range and generated time sit above it.</summary>
    public const int HeaderRow = 6;

    // ₹ with Indian grouping: 12,000.00 / 1,25,000.00 / 12,50,000.00 / 1,25,00,000.00
    private const string MoneyFormat =
        "[>=10000000]\"₹\"##\\,##\\,##\\,##0.00;[>=100000]\"₹\"##\\,##\\,##0.00;\"₹\"#,##0.00";

    public string Format => "xlsx";
    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string Extension => "xlsx";

    public byte[] Build(ReportTable table, DateTime generatedAtIst)
    {
        using var wb = new XLWorkbook();
        wb.Properties.Title = table.Title;
        wb.Properties.Company = "Genuine IT Solution";
        var ws = wb.AddWorksheet(SheetName(table.Title));

        ws.Cell(1, 1).Value = "Genuine IT Solution";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = table.Title;
        ws.Cell(2, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.FontSize = 12;
        ws.Cell(3, 1).Value = table.FilterText is null ? table.RangeText : $"{table.RangeText}  ·  {table.FilterText}";
        ws.Cell(4, 1).Value = $"Generated {ReportTables.DateTimeText(generatedAtIst)} IST";
        ws.Cell(4, 1).Style.Font.FontColor = XLColor.Gray;

        for (var c = 0; c < table.Columns.Count; c++)
        {
            var header = ws.Cell(HeaderRow, c + 1);
            header.Value = table.Columns[c].Header;
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Column(c + 1).Width = table.Columns[c].Width;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = HeaderRow + 1 + r;
            for (var c = 0; c < table.Columns.Count; c++) Write(ws.Cell(row, c + 1), table.Columns[c], table.Rows[r][c]);
        }

        var last = HeaderRow + table.Rows.Count;
        ws.Range(HeaderRow, 1, last, table.Columns.Count).SetAutoFilter();
        ws.SheetView.FreezeRows(HeaderRow);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Write(IXLCell cell, ReportColumn column, object? value)
    {
        if (value is null) return; // a blank cell, never a made-up value
        switch (column.Kind)
        {
            case ReportColumnKind.Phone:
                cell.Style.NumberFormat.Format = "@"; // text
                cell.SetValue((string)value);
                break;
            case ReportColumnKind.Date:
                cell.SetValue(((DateOnly)value).ToDateTime(TimeOnly.MinValue));
                cell.Style.DateFormat.Format = "dd mmm yyyy";
                break;
            case ReportColumnKind.DateTime:
                cell.SetValue((DateTime)value);
                cell.Style.DateFormat.Format = "dd mmm yyyy, hh:mm";
                break;
            case ReportColumnKind.Money:
                cell.SetValue((double)(decimal)value);
                cell.Style.NumberFormat.Format = MoneyFormat;
                break;
            case ReportColumnKind.Number:
                cell.SetValue((int)value);
                break;
            default:
                cell.SetValue((string)value);
                break;
        }
    }

    // Excel sheet names are at most 31 characters and cannot contain : \ / ? * [ ]
    private static string SheetName(string title) =>
        new string(title.Where(ch => !":\\/?*[]".Contains(ch)).ToArray())[..Math.Min(31, title.Length)];
}

/// <summary>
/// PDF (QuestPDF, Community licence as already set: the licence type is not changed here). A4 landscape; header with
/// "Genuine IT Solution", the report title, the date range and the generated time in IST; the same columns as the
/// screen; the page number in the footer. No totals row.
/// </summary>
public class PdfReportBuilder : IReportFileBuilder
{
    static PdfReportBuilder()
    {
        // QuestPDF Community licence: free below the annual revenue threshold in QuestPDF's terms.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public string Format => "pdf";
    public string ContentType => "application/pdf";
    public string Extension => "pdf";

    public byte[] Build(ReportTable table, DateTime generatedAtIst)
    {
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(8.5f));

            page.Header().PaddingBottom(8).Column(col =>
            {
                col.Item().Text("Genuine IT Solution").FontSize(14).Bold();
                col.Item().Text(table.Title).FontSize(12).SemiBold();
                col.Item().Text(table.FilterText is null ? table.RangeText : $"{table.RangeText}  ·  {table.FilterText}");
                col.Item().Text($"Generated {ReportTables.DateTimeText(generatedAtIst)} IST")
                    .FontSize(8).FontColor(Colors.Grey.Darken1);
            });

            page.Content().Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    foreach (var column in table.Columns) c.RelativeColumn(column.Weight);
                });
                t.Header(h =>
                {
                    foreach (var column in table.Columns)
                        Cell(h.Cell(), column).Background(Colors.Grey.Lighten2).Text(column.Header).Bold();
                });
                foreach (var row in table.Rows)
                {
                    for (var c = 0; c < table.Columns.Count; c++)
                    {
                        var column = table.Columns[c];
                        Cell(t.Cell(), column).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Text(Format_(column, row[c]));
                    }
                }
            });

            page.Footer().AlignRight().Text(x =>
            {
                x.Span("Page ");
                x.CurrentPageNumber();
                x.Span(" of ");
                x.TotalPages();
            });
        })).GeneratePdf();
    }

    private static IContainer Cell(IContainer cell, ReportColumn column)
    {
        var padded = cell.Padding(3);
        return column.Kind is ReportColumnKind.Money or ReportColumnKind.Number ? padded.AlignRight() : padded;
    }

    private static string Format_(ReportColumn column, object? value) => value switch
    {
        null => "",
        DateOnly d => ReportTables.Date(d),
        DateTime d => ReportTables.DateTimeText(d),
        decimal m => ReportTables.Rupees(m),
        _ => value.ToString() ?? "",
    };
}
