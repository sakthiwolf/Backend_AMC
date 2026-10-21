using AmcTracker.Application.Abstractions;
using AmcTracker.Application.Common;
using AmcTracker.Application.Dtos;
using AmcTracker.Application.Services;
using AmcTracker.Application.Validators;
using AmcTracker.Domain.Entities;
using AmcTracker.Domain.Enums;
using NSubstitute;

namespace AmcTracker.UnitTests.Application;

public class CustomerServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 5, 0, 0, DateTimeKind.Utc);
    private readonly ICustomerRepository _repo = Substitute.For<ICustomerRepository>();
    private readonly IPlanRepository _plans = Substitute.For<IPlanRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly CustomerService _service;

    public CustomerServiceTests()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        clock.Today.Returns(DateOnly.FromDateTime(Now));
        _service = new CustomerService(_repo, _plans, _uow, clock,
            new CreateCustomerRequestValidator(clock), new UpdateCustomerRequestValidator(clock));
        _repo.SearchAsync(default, default, default, default, default)
            .ReturnsForAnyArgs(([], 0));
    }

    private static Customer Row(bool active = true) => new()
    {
        CustomerId = 7, CustomerName = "Asha", Phone = "+919876543210",
        InstallationDate = new DateOnly(2026, 1, 1), IsActive = active,
    };

    [Theory]
    [InlineData(null, 20)]
    [InlineData(0, 20)]
    [InlineData(500, 100)]
    [InlineData(100, 100)]
    [InlineData(5, 5)]
    public async Task Paging_defaults_to_20_and_is_capped_at_100(int? requested, int expected)
    {
        var result = await _service.SearchAsync(null, null, 1, requested);
        Assert.Equal(expected, result.PageSize);
        await _repo.Received().SearchAsync(null, null, 1, expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_page_below_one_becomes_page_one()
    {
        var result = await _service.SearchAsync(null, null, -3, 10);
        Assert.Equal(1, result.Page);
    }

    [Fact]
    public async Task Soft_delete_sets_IsActive_false_and_stamps_UpdatedAt()
    {
        var row = Row();
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(row);

        await _service.SoftDeleteAsync(7);

        Assert.False(row.IsActive);
        Assert.Equal(Now, row.UpdatedAt);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_an_already_inactive_customer_is_not_found()
    {
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(Row(active: false));
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.SoftDeleteAsync(7));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public async Task Unknown_ids_are_not_found()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.GetByIdAsync(99));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public async Task Create_trims_normalises_and_keeps_two_decimals()
    {
        Customer? saved = null;
        _repo.When(r => r.Add(Arg.Any<Customer>())).Do(c => saved = c.Arg<Customer>());

        var dto = await _service.CreateAsync(new CreateCustomerRequest(
            "  Asha  ", "+91 98765-43210", "  ", new DateOnly(2026, 10, 1), "", 1234.5m, null));

        Assert.Equal("Asha", saved!.CustomerName);
        Assert.Equal("+919876543210", saved.Phone);
        Assert.Null(saved.Location);
        Assert.Null(saved.ProjectName);
        Assert.Equal(Now, saved.CreatedAt);
        Assert.True(saved.IsActive);
        Assert.Equal("1234.50", dto.ProjectValue!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Invalid_input_throws_422_with_per_field_details_and_never_saves()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => _service.CreateAsync(
            new CreateCustomerRequest("", "9876543210", null, null, null, -1m, null)));

        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("validation_failed", ex.Code);
        var fields = ex.Details!.Select(d => d.Field).ToArray();
        Assert.Contains("customerName", fields);
        Assert.Contains("phone", fields);
        Assert.Contains("installationDate", fields);
        Assert.Contains("projectValue", fields);
        Assert.Equal("invalid_phone", ex.Details!.Single(d => d.Field == "phone").Code);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Update_stamps_UpdatedAt_with_the_current_utc_time()
    {
        var row = Row();
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(row);

        await _service.UpdateAsync(7, new UpdateCustomerRequest(
            "Asha Ltd", "+919876543210", null, new DateOnly(2026, 1, 1), null, null, true));

        Assert.Equal("Asha Ltd", row.CustomerName);
        Assert.True(row.WhatsAppOptOut);
        Assert.Equal(Now, row.UpdatedAt);
    }

    [Fact]
    public async Task Create_makes_the_first_plan_of_the_chosen_type()
    {
        Plan? plan = null;
        _plans.When(r => r.Add(Arg.Any<Plan>())).Do(c => plan = c.Arg<Plan>());

        var dto = await _service.CreateAsync(new CreateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 10, 12), null, 5000m, null, PlanType.Monthly));

        Assert.Equal(PlanType.Monthly, dto.PlanType);
        Assert.NotNull(plan);
        Assert.Equal(PlanType.Monthly, plan!.PlanType);
        Assert.Equal(new DateOnly(2026, 10, 12), plan.StartDate);
        Assert.Equal(new DateOnly(2026, 11, 12), plan.EndDate); // Monthly: a 1-month plan
        Assert.Equal(new DateOnly(2026, 11, 5), plan.ReminderDate);
        Assert.Equal(5000m, plan.Amount);
    }

    [Fact]
    public async Task Plan_type_defaults_to_Yearly_when_it_is_left_out()
    {
        Plan? plan = null;
        _plans.When(r => r.Add(Arg.Any<Plan>())).Do(c => plan = c.Arg<Plan>());

        var dto = await _service.CreateAsync(new CreateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 10, 12), null, null, null));

        Assert.Equal(PlanType.AMC, dto.PlanType);
        Assert.Equal(PlanType.AMC, plan!.PlanType);
        Assert.Equal(0m, plan.Amount);
    }

    [Fact]
    public async Task Switching_the_type_on_edit_changes_the_open_plan()
    {
        var row = Row();
        var plan = Plan.Create(7, PlanType.AMC, 100m, new DateOnly(2026, 1, 1));
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(row);
        _plans.GetCurrentForCustomerAsync(7, Arg.Any<PlanType?>(), Arg.Any<CancellationToken>()).Returns(plan);

        await _service.UpdateAsync(7, new UpdateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 1, 1), null, null, null, PlanType.Monthly));

        Assert.Equal(PlanType.Monthly, row.PlanType);
        Assert.Equal(PlanType.Monthly, plan.PlanType);
        Assert.Equal(new DateOnly(2026, 2, 1), plan.EndDate); // recalculated as a 1-month plan
        Assert.Equal(new DateOnly(2026, 1, 25), plan.ReminderDate);
        _plans.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Switching_to_a_type_that_already_has_an_open_plan_is_409_overlapping_plan()
    {
        var row = Row();
        var plan = Plan.Create(7, PlanType.AMC, 100m, new DateOnly(2026, 1, 1));
        plan.PlanId = 1;
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(row);
        _plans.GetCurrentForCustomerAsync(7, Arg.Any<PlanType?>(), Arg.Any<CancellationToken>()).Returns(plan);
        _plans.HasOpenPlanAsync(7, PlanType.Monthly, 1, Arg.Any<CancellationToken>()).Returns(true);

        var ex = await Assert.ThrowsAsync<AppException>(() => _service.UpdateAsync(7, new UpdateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 1, 1), null, null, null, PlanType.Monthly)));

        Assert.Equal((409, "overlapping_plan"), (ex.StatusCode, ex.Code));
        Assert.Equal(PlanType.AMC, plan.PlanType);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Editing_without_a_type_keeps_the_current_one()
    {
        var row = Row();
        row.PlanType = PlanType.Monthly;
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(row);
        _plans.GetCurrentForCustomerAsync(7, Arg.Any<PlanType?>(), Arg.Any<CancellationToken>())
            .Returns(Plan.Create(7, PlanType.Monthly, 0m, new DateOnly(2026, 1, 1)));

        await _service.UpdateAsync(7, new UpdateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 1, 1), null, null, null));

        Assert.Equal(PlanType.Monthly, row.PlanType);
    }

    [Fact]
    public async Task An_older_customer_without_a_plan_gets_one_when_edited()
    {
        var row = Row();
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(row);
        _plans.GetCurrentForCustomerAsync(7, Arg.Any<PlanType?>(), Arg.Any<CancellationToken>()).Returns((Plan?)null);

        await _service.UpdateAsync(7, new UpdateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 1, 1), null, null, null, PlanType.Monthly));

        _plans.Received(1).Add(Arg.Is<Plan>(p => p.PlanType == PlanType.Monthly));
    }

    [Fact]
    public async Task Create_calculates_the_end_date_and_next_reminder_date()
    {
        // The clock says 16 Oct 2026.
        var yearly = await _service.CreateAsync(new CreateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 10, 12), null, null, null, PlanType.AMC));
        Assert.Equal(new DateOnly(2027, 10, 12), yearly.PlanEndDate);
        Assert.Equal(new DateOnly(2027, 9, 12), yearly.ReminderDate); // 1 month before the end

        var monthly = await _service.CreateAsync(new CreateCustomerRequest(
            "Mohan", "+919876543211", null, new DateOnly(2026, 10, 12), null, null, null, PlanType.Monthly));
        Assert.Equal(new DateOnly(2026, 11, 12), monthly.PlanEndDate);
        Assert.Equal(new DateOnly(2026, 11, 5), monthly.ReminderDate); // 7 days before the end
    }

    [Fact]
    public async Task Changing_the_installation_date_moves_the_plan_and_the_dates()
    {
        var row = Row();
        var plan = Plan.Create(7, PlanType.AMC, 0m, new DateOnly(2026, 1, 1));
        _repo.GetByIdAsync(7, false, Arg.Any<CancellationToken>()).Returns(row);
        _plans.GetCurrentForCustomerAsync(7, Arg.Any<PlanType?>(), Arg.Any<CancellationToken>()).Returns(plan);

        var dto = await _service.UpdateAsync(7, new UpdateCustomerRequest(
            "Asha", "+919876543210", null, new DateOnly(2026, 10, 12), null, null, null));

        Assert.Equal(new DateOnly(2026, 10, 12), plan.StartDate);
        Assert.Equal(new DateOnly(2027, 10, 12), plan.EndDate);
        Assert.Equal(new DateOnly(2027, 9, 12), plan.ReminderDate);
        Assert.Equal(new DateOnly(2027, 10, 12), dto.PlanEndDate);
        Assert.Equal(new DateOnly(2027, 9, 12), dto.ReminderDate);
    }
}
