using System.Globalization;
using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Dtos;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AmcTracker.Infrastructure.Services;

public class ClosedXmlExcelExporter : IExcelExporter
{
    private static readonly string[] Headers =
        ["Customer", "Phone", "Project", "Plan", "Amount (₹)", "Start", "Ends", "Reminder on", "Status"];

    public byte[] Export(IReadOnlyList<PlanDto> rows, string title)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Plans");
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        for (var c = 0; c < Headers.Length; c++)
        {
            var cell = ws.Cell(3, c + 1);
            cell.Value = Headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        var r = 4;
        foreach (var p in rows)
        {
            ws.Cell(r, 1).Value = p.CustomerName;
            ws.Cell(r, 2).Value = p.Phone;
            ws.Cell(r, 3).Value = p.ProjectName ?? "";
            ws.Cell(r, 4).Value = p.PlanType.ToString();
            ws.Cell(r, 5).Value = p.Amount;
            ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(r, 6).Value = p.StartDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 7).Value = p.EndDate.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 8).Value = p.NotificationDate.ToDateTime(TimeOnly.MinValue);
            ws.Range(r, 6, r, 8).Style.DateFormat.Format = "dd mmm yyyy";
            ws.Cell(r, 9).Value = p.Status.ToString();
            r++;
        }
        ws.SheetView.FreezeRows(3);
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

public class QuestPdfExporter : IPdfExporter
{
    static QuestPdfExporter()
    {
        // QuestPDF Community licence: free below the annual revenue threshold in QuestPDF's terms.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Export(IReadOnlyList<PlanDto> rows, string title)
    {
        string D(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(9));
            page.Header().Text(title).FontSize(14).Bold();
            page.Content().PaddingTop(8).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3); c.RelativeColumn(2.4f); c.RelativeColumn(3);
                    c.RelativeColumn(1.3f); c.RelativeColumn(1.8f); c.RelativeColumn(2);
                    c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2);
                });
                string[] heads = ["Customer", "Phone", "Project", "Plan", "Amount (INR)", "Start", "Ends", "Reminder on", "Status"];
                t.Header(h =>
                {
                    foreach (var head in heads)
                        h.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text(head).Bold();
                });
                foreach (var p in rows)
                {
                    string[] cells =
                    [
                        p.CustomerName, p.Phone, p.ProjectName ?? "", p.PlanType.ToString(),
                        p.Amount.ToString("N2", CultureInfo.InvariantCulture),
                        D(p.StartDate), D(p.EndDate), D(p.NotificationDate), p.Status.ToString(),
                    ];
                    foreach (var text in cells)
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(text);
                }
            });
            page.Footer().AlignRight().Text(x =>
            {
                x.Span($"{rows.Count} plan(s)  |  Page ");
                x.CurrentPageNumber();
                x.Span(" of ");
                x.TotalPages();
            });
        })).GeneratePdf();
    }
}
