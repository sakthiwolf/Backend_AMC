using System.Linq.Expressions;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;

namespace AmcTracker.Domain.Services;

/// <summary>
/// The plan status rules (CLAUDE.md 4.2) written as expressions, so the database can apply them in one query
/// instead of loading every plan. This is the only place the rules appear in query form: the plans list and
/// the dashboard both use it, so a count and the list it opens cannot disagree.
/// <see cref="PlanStatusCalculator"/> is the in-memory form of the same rules. A test compares every status
/// from these expressions with <see cref="Plan.StatusOn"/> for each day around the boundaries, so any
/// difference fails the build.
/// <para>
/// Status is never stored: it comes from <c>today</c> (the Asia/Kolkata date from <c>IClock</c>), the plan's
/// dates, and whether a renewal exists.
/// </para>
/// </summary>
public static class PlanSpec
{
    /// <summary>Plans that have <paramref name="status"/> on <paramref name="today"/>.</summary>
    public static Expression<Func<Plan, bool>> HasStatus(PlanStatus status, DateOnly today) => status switch
    {
        PlanStatus.Renewed => p => p.Renewal != null,
        PlanStatus.Active => p => p.Renewal == null && today < p.ReminderDate,
        PlanStatus.DueForRenewal => p => p.Renewal == null && p.ReminderDate <= today && today <= p.EndDate,
        PlanStatus.Expired => p => p.Renewal == null && today > p.EndDate,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown plan status."),
    };

    /// <summary>The status of each plan on <paramref name="today"/>, for grouping and counting in one query.</summary>
    public static Expression<Func<Plan, PlanStatus>> StatusOf(DateOnly today) => p =>
        p.Renewal != null ? PlanStatus.Renewed
        : today < p.ReminderDate ? PlanStatus.Active
        : today <= p.EndDate ? PlanStatus.DueForRenewal
        : PlanStatus.Expired;

    /// <summary>Not renewed, and ending from <paramref name="today"/> through today + <paramref name="days"/> (both inclusive).</summary>
    public static Expression<Func<Plan, bool>> EndsWithin(DateOnly today, int days)
    {
        var last = today.AddDays(days);
        return p => p.Renewal == null && p.EndDate >= today && p.EndDate <= last;
    }

    /// <summary>Not renewed, with the reminder date from <paramref name="today"/> through today + <paramref name="days"/>.</summary>
    public static Expression<Func<Plan, bool>> RemindsWithin(DateOnly today, int days)
    {
        var last = today.AddDays(days);
        return p => p.Renewal == null && p.ReminderDate >= today && p.ReminderDate <= last;
    }

    /// <summary>Plans that need action now: DueForRenewal, or a reminder date within the next <paramref name="days"/> days.</summary>
    public static Expression<Func<Plan, bool>> NeedsAction(DateOnly today, int days) =>
        Or(HasStatus(PlanStatus.DueForRenewal, today), RemindsWithin(today, days));

    /// <summary>a || b over the same plan.</summary>
    public static Expression<Func<Plan, bool>> Or(Expression<Func<Plan, bool>> a, Expression<Func<Plan, bool>> b)
    {
        var plan = a.Parameters[0];
        var right = new ReplaceParameter(b.Parameters[0], plan).Visit(b.Body)!;
        return Expression.Lambda<Func<Plan, bool>>(Expression.OrElse(a.Body, right), plan);
    }

    private sealed class ReplaceParameter(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
