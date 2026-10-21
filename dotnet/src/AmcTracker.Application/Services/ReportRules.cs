using AmcTracker.Application.Common;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Application.Services;

/// <summary>Input rules shared by every report and the export (CLAUDE.md 4.6). Every failure is a 422.</summary>
internal static class ReportRules
{
    public const int MaxRangeDays = 366;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxExportRows = 20_000;
    public const int MaxErrorLength = 200;

    public const string RangeTooLargeMessage = "The date range can be at most 366 days.";
    public const string InvalidRangeMessage = "From must be on or before To.";

    /// <summary>
    /// The date range, defaulting to the current calendar month (in Asia/Kolkata, from <paramref name="today"/>).
    /// <c>from</c> after <c>to</c> is 422 invalid_range; more than 366 days from first to last is 422 range_too_large.
    /// </summary>
    public static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var f = from ?? firstOfMonth;
        var t = to ?? firstOfMonth.AddMonths(1).AddDays(-1);
        if (f > t) throw new AppException(422, "invalid_range", InvalidRangeMessage);
        if (t.DayNumber - f.DayNumber > MaxRangeDays)
            throw new AppException(422, "range_too_large", RangeTooLargeMessage);
        return (f, t);
    }

    /// <summary>AMC, Monthly or all (any case). Null or empty means all.</summary>
    public static PlanType? Type(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var t in Enum.GetValues<PlanType>())
            if (t.ToString().Equals(type.Trim(), StringComparison.OrdinalIgnoreCase)) return t;
        throw Invalid("type (AMC, Monthly or all)");
    }

    /// <summary>A plan status name (any case). Null, empty or "all" means any.</summary>
    public static PlanStatus? PlanStatus(string? status)
    {
        if (IsAny(status)) return null;
        foreach (var s in Enum.GetValues<PlanStatus>())
            if (s.ToString().Equals(status!.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        throw Invalid("status (Active, DueForRenewal, Expired or Renewed)");
    }

    /// <summary>Pending, Sent or Failed (any case). Null, empty or "all" means any.</summary>
    public static ReminderStatus? ReminderStatus(string? status)
    {
        if (IsAny(status)) return null;
        foreach (var s in Enum.GetValues<ReminderStatus>())
            if (s.ToString().Equals(status!.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        throw Invalid("status (Pending, Sent or Failed)");
    }

    /// <summary>Page from 1; page size 1 to 200 (default 50).</summary>
    public static (int Page, int Size) Paging(int? page, int? pageSize)
    {
        var p = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (p < 1) throw Invalid("page (1 or more)");
        if (size is < 1 or > MaxPageSize) throw Invalid($"pageSize (1 to {MaxPageSize})");
        return (p, size);
    }

    public static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max];

    private static bool IsAny(string? status) =>
        string.IsNullOrWhiteSpace(status) || status.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

    private static AppException Invalid(string what) =>
        new(422, "validation_error", $"Invalid or missing fields: {what}.");
}
