using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Services;

public class ReportService(
    IPlanRepository plans, IClock clock, IExcelExporter excel, IPdfExporter pdf)
{
    public async Task<ReportResultDto> GetPlansAsync(
        DateOnly? from, DateOnly? to, PlanType? type, PlanStatus? status, DateBasis basis,
        CancellationToken ct = default)
    {
        var rows = await QueryAsync(from, to, type, status, basis, ct);
        return new ReportResultDto(from, to, basis, rows.Count, rows);
    }

    public async Task<ExportFile> ExportAsync(
        string? format, DateOnly? from, DateOnly? to, PlanType? type, PlanStatus? status, DateBasis basis,
        CancellationToken ct = default)
    {
        var f = (format ?? "").ToLowerInvariant();
        if (f is not ("xlsx" or "pdf"))
            throw new AppException(422, "validation_error", "Invalid or missing fields: format (xlsx or pdf).");
        if (from > to)
            throw new AppException(422, "validation_error", "Invalid or missing fields: from, to.");

        var rows = await QueryAsync(from, to, type, status, basis, ct);
        var title = $"AMC plans report - {clock.Today:dd MMM yyyy}";
        var stamp = clock.Today.ToString("yyyyMMdd");
        return f == "xlsx"
            ? new ExportFile(excel.Export(rows, title),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"plans-{stamp}.xlsx")
            : new ExportFile(pdf.Export(rows, title), "application/pdf", $"plans-{stamp}.pdf");
    }

    private async Task<IReadOnlyList<PlanDto>> QueryAsync(
        DateOnly? from, DateOnly? to, PlanType? type, PlanStatus? status, DateBasis basis, CancellationToken ct)
    {
        var today = clock.Today;
        var list = await plans.ListAsync(new PlanFilter(status, type, basis, from, to), today, ct);
        return list.OrderBy(p => p.EndDate).ThenBy(p => p.PlanId).Select(p => p.ToDto(today)).ToList();
    }
}
