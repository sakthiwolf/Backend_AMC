using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using AmcTracker.Application.Validators;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

public class PlanServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private readonly IPlanRepository _plans = Substitute.For<IPlanRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly Customer _customer = new()
    {
        CustomerId = 7, CustomerName = "Asha", Phone = "+919876543210",
        InstallationDate = new DateOnly(2026, 10, 12), PlanType = PlanType.AMC, IsActive = true,
    };

    public PlanServiceTests()
    {
        _clock.Today.Returns(Today);
        _clock.UtcNow.Returns(new DateTime(2026, 10, 21, 5, 0, 0, DateTimeKind.Utc));
        _customers.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(_customer);
    }

    private PlanService Plans() => new(_plans, _customers, Substitute.For<IReminderRepository>(), _payments,
        _uow, _clock, new CreatePlanRequestValidator(_clock));

    private RenewalService Renewals() => new(_plans, _payments, _uow, _clock, new RenewPlanRequestValidator(_clock));

    // ---- Creating plans ----

    [Fact]
    public async Task Create_starts_on_the_installation_date_and_stores_the_amount_as_entered()
    {
        var dto = await Plans().CreateAsync(7, new CreatePlanRequest(PlanType.Monthly, 499.50m, null));

        Assert.Equal(new DateOnly(2026, 10, 12), dto.StartDate);
        Assert.Equal(new DateOnly(2026, 11, 12), dto.EndDate);
        Assert.Equal(new DateOnly(2026, 11, 5), dto.ReminderDate);
        Assert.Equal(499.50m, dto.Amount); // the monthly rate, never multiplied
        Assert.Equal(PlanStatus.Active, dto.Status);
        _plans.Received(1).Add(Arg.Any<Plan>());
    }

    [Fact]
    public async Task A_second_open_plan_of_the_same_type_is_409_overlapping_plan()
    {
        _plans.HasOpenPlanAsync(7, PlanType.AMC, null, Arg.Any<CancellationToken>()).Returns(true);

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Plans().CreateAsync(7, new CreatePlanRequest(PlanType.AMC, 100m, null)));

        Assert.Equal((409, "overlapping_plan"), (ex.StatusCode, ex.Code));
        _plans.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Invalid_input_is_422_with_field_details()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Plans().CreateAsync(7, new CreatePlanRequest(null, -1m, null)));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains(ex.Details!, d => d.Field == "planType");
        Assert.Contains(ex.Details!, d => d.Field == "amount");
    }

    // ---- Renewal ----

    /// <summary>A plan of <paramref name="type"/> that ends on <paramref name="end"/>, loaded as the repository would.</summary>
    private Plan PlanEndingOn(DateOnly end, PlanType type = PlanType.AMC, decimal amount = 12000m)
    {
        var start = type == PlanType.AMC ? end.AddYears(-1) : end.AddMonths(-1);
        var plan = Plan.Create(7, type, amount, start);
        plan.PlanId = 3;
        plan.Customer = _customer;
        _plans.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(plan);
        return plan;
    }

    private static RenewPlanRequest Pay(DateOnly paidOn, decimal amount = 12000m, PaymentMode mode = PaymentMode.UPI,
        string? note = null, DateOnly? startDate = null, decimal? planAmount = null) =>
        new(paidOn, amount, mode, note, startDate, planAmount);

    private Payment? _saved;

    private void CapturePayment() =>
        _payments.When(p => p.Add(Arg.Any<Payment>())).Do(c => _saved = c.Arg<Payment>());

    [Fact]
    public async Task Paid_after_the_previous_end_the_new_cycle_starts_on_the_payment_date()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12)); // expired

        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15)));

        Assert.Equal(new DateOnly(2026, 10, 15), result.Plan.StartDate);
        Assert.Equal(new DateOnly(2027, 10, 15), result.Plan.EndDate);
        Assert.Equal(new DateOnly(2027, 9, 15), result.Plan.ReminderDate);
    }

    [Fact]
    public async Task Paid_before_the_previous_end_the_new_cycle_starts_the_day_after_the_end()
    {
        PlanEndingOn(new DateOnly(2026, 10, 25)); // due: reminder 25 Sep

        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 20)));

        Assert.Equal(new DateOnly(2026, 10, 26), result.Plan.StartDate);
    }

    [Fact]
    public async Task Paid_on_the_previous_end_date_the_new_cycle_starts_the_day_after()
    {
        PlanEndingOn(new DateOnly(2026, 10, 21)); // ends today, still due

        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 21)));

        Assert.Equal(new DateOnly(2026, 10, 22), result.Plan.StartDate);
    }

    [Fact]
    public async Task The_payment_is_recorded_against_the_plan_that_was_paid_for()
    {
        var old = PlanEndingOn(new DateOnly(2026, 10, 12));
        CapturePayment();

        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), 11500.50m, PaymentMode.Bank, "  cheque cleared "));

        Assert.NotNull(_saved);
        Assert.Same(old, _saved!.Plan);
        Assert.Equal((new DateOnly(2026, 10, 15), 11500.50m, PaymentMode.Bank, "cheque cleared"),
            (_saved.PaidOn, _saved.Amount, _saved.Mode, _saved.Note));
        Assert.Equal((11500.50m, PaymentMode.Bank), (result.Payment.Amount, result.Payment.Mode));
        // The renewal and the payment are saved together, in one SaveChanges.
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_new_plan_copies_the_amount_unless_a_plan_amount_is_given()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12), amount: 12000m);
        var copied = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), amount: 9999m));
        Assert.Equal(12000m, copied.Plan.Amount); // the payment amount does not change the rate

        PlanEndingOn(new DateOnly(2026, 10, 12), amount: 12000m);
        var changed = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), planAmount: 13500m));
        Assert.Equal(13500m, changed.Plan.Amount);
    }

    [Fact]
    public async Task Renewing_an_active_plan_is_409_not_due_for_renewal_and_saves_nothing()
    {
        PlanEndingOn(new DateOnly(2027, 10, 12)); // reminder 12 Sep 2027: active today

        var ex = await Assert.ThrowsAsync<AppException>(() => Renewals().RenewAsync(3, Pay(Today)));

        Assert.Equal((409, "not_due_for_renewal"), (ex.StatusCode, ex.Code));
        Assert.Contains("1 month before it ends", ex.Message);
        _payments.DidNotReceiveWithAnyArgs().Add(default!);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task The_not_due_message_for_a_monthly_plan_says_seven_days()
    {
        PlanEndingOn(new DateOnly(2026, 11, 12), PlanType.Monthly); // reminder 5 Nov: active today
        var ex = await Assert.ThrowsAsync<AppException>(() => Renewals().RenewAsync(3, Pay(Today)));
        Assert.Equal("not_due_for_renewal", ex.Code);
        Assert.Contains("7 days before it ends", ex.Message);
    }

    [Fact]
    public async Task Renewing_twice_is_409_plan_already_renewed()
    {
        var plan = PlanEndingOn(new DateOnly(2026, 10, 12));
        plan.Renew(new DateOnly(2026, 10, 13));

        var ex = await Assert.ThrowsAsync<AppException>(() => Renewals().RenewAsync(3, Pay(Today)));

        Assert.Equal((409, "plan_already_renewed"), (ex.StatusCode, ex.Code));
    }

    [Fact]
    public async Task A_renewal_that_loses_a_race_on_the_unique_index_is_also_409()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12));
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns<int>(_ => throw new DuplicateKeyException(
            "Cannot insert duplicate key row in object 'dbo.Plans' with unique index 'IX_Plans_PreviousPlanId'.",
            new InvalidOperationException()));

        var ex = await Assert.ThrowsAsync<AppException>(() => Renewals().RenewAsync(3, Pay(Today)));

        Assert.Equal("plan_already_renewed", ex.Code);
    }

    [Fact]
    public async Task A_start_date_before_the_default_is_422_start_before_default()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12)); // paid 15 Oct: the default start is 15 Oct

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), startDate: new DateOnly(2026, 10, 14))));

        Assert.Equal((422, "start_before_default"), (ex.StatusCode, ex.Code));
        Assert.Equal("startDate", Assert.Single(ex.Details!).Field);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task A_start_date_after_the_default_is_accepted_and_the_default_itself_too()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12));
        var later = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), startDate: new DateOnly(2026, 11, 1)));
        Assert.Equal(new DateOnly(2026, 11, 1), later.Plan.StartDate);

        PlanEndingOn(new DateOnly(2026, 10, 12));
        var same = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), startDate: new DateOnly(2026, 10, 15)));
        Assert.Equal(new DateOnly(2026, 10, 15), same.Plan.StartDate);
    }

    [Fact]
    public async Task An_amc_renewal_ends_one_year_after_its_start()
    {
        PlanEndingOn(new DateOnly(2027, 2, 28).AddYears(-1)); // 28 Feb 2026: expired
        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 3, 1)));
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2027, 3, 1)), (result.Plan.StartDate, result.Plan.EndDate));
    }

    [Fact]
    public async Task A_monthly_renewal_ends_one_month_after_its_start_with_the_reminder_seven_days_before()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12), PlanType.Monthly, 499.50m); // expired
        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), 499.50m));
        Assert.Equal((new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 8)),
            (result.Plan.StartDate, result.Plan.EndDate, result.Plan.ReminderDate));
        Assert.Equal(499.50m, result.Plan.Amount);
    }

    [Fact]
    public async Task Month_end_starts_clamp_in_a_monthly_renewal()
    {
        PlanEndingOn(new DateOnly(2027, 1, 31), PlanType.Monthly); // paid on the end date -> starts 1 Feb
        Today_Is(new DateOnly(2027, 1, 31));
        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2027, 1, 31)));
        Assert.Equal((new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 1), new DateOnly(2027, 2, 22)),
            (result.Plan.StartDate, result.Plan.EndDate, result.Plan.ReminderDate));
    }

    private void Today_Is(DateOnly day) => _clock.Today.Returns(day);

    [Fact]
    public async Task A_renewal_updates_the_customers_dates()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12));
        await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15)));
        Assert.Equal(new DateOnly(2027, 10, 15), _customer.PlanEndDate);
    }

    // ---- Validation (422) ----

    [Fact]
    public async Task Payment_fields_are_checked()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12));
        var cases = new (RenewPlanRequest Request, string Field)[]
        {
            (Pay(new DateOnly(2026, 10, 15), amount: 0m), "amount"),
            (Pay(new DateOnly(2026, 10, 15), amount: -5m), "amount"),
            (Pay(new DateOnly(2026, 10, 15), amount: 10.555m), "amount"),
            (Pay(new DateOnly(2026, 10, 15), mode: (PaymentMode)99), "mode"),
            (Pay(Today.AddDays(1)), "paidOn"),
            (Pay(new DateOnly(2026, 10, 15), note: new string('x', 201)), "note"),
            (Pay(new DateOnly(2026, 10, 15), planAmount: -1m), "planAmount"),
            (new RenewPlanRequest(null, 100m, PaymentMode.Cash), "paidOn"),
            (new RenewPlanRequest(Today, null, PaymentMode.Cash), "amount"),
            (new RenewPlanRequest(Today, 100m, null), "mode"),
        };
        foreach (var (request, field) in cases)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => Renewals().RenewAsync(3, request));
            Assert.Equal(422, ex.StatusCode);
            Assert.Contains(ex.Details!, d => d.Field == field);
        }
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task A_payment_of_a_whole_rupee_and_a_zero_plan_amount_are_allowed()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12));
        var result = await Renewals().RenewAsync(3, Pay(new DateOnly(2026, 10, 15), 1m, planAmount: 0m));
        Assert.Equal(0m, result.Plan.Amount);
    }

    // ---- Payments list ----

    [Fact]
    public async Task Payments_come_from_the_repository_for_that_plan_only()
    {
        PlanEndingOn(new DateOnly(2026, 10, 12));
        _payments.ListForPlanAsync(3, Arg.Any<CancellationToken>()).Returns(
        [
            new Payment { PaymentId = 2, PlanId = 3, PaidOn = new DateOnly(2026, 10, 15), Amount = 12000m, Mode = PaymentMode.UPI },
        ]);

        var list = await Plans().GetPaymentsAsync(3);

        Assert.Equal(2, Assert.Single(list).PaymentId);
        await _payments.DidNotReceive().ListForPlanAsync(Arg.Is<int>(id => id != 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Payments_of_an_unknown_plan_is_404()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Plans().GetPaymentsAsync(999));
        Assert.Equal(404, ex.StatusCode);
    }
}
